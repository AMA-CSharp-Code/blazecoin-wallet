using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The read path counts only what the wallet has PROVEN (bug-bounty report 2026-11-06). Before,
/// chain verification ran at spend time and at one restore gate; balance, the balance breakdown,
/// the payment watcher and history took a gateway's rows as served, so a made-up confirmed coin
/// was shown as spendable balance, announced as "Received", and listed as income.
/// <para>Every test here runs the production stack the way the web wallet wires it — the real
/// <see cref="IndexerDataService"/> over a scripted gateway on the wire, the real
/// <see cref="LiteTxVerifier"/>, no header feed — and honest coins come from
/// <see cref="MinedBlockFixture"/>, a block genuinely mined at the minimum difficulty.</para>
/// </summary>
public partial class ReadPathVerificationTests
{
    private static readonly string FakeTxId = new('e', 64);     // a transaction that exists nowhere
    private const long FakeSats = 77_700_000_000;                // 777 BLZ

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    /// <summary>A gateway on the wire. It lists whatever coins, summaries and history rows a test
    /// scripts — true or not — and serves the raw transaction and merkle proof of the fixture
    /// block's transactions only (anything else: 404), unless told to withhold them.
    /// <para>Thread-safe on purpose: the wallet checks coins on a background queue, so a test
    /// that re-scripts the gateway mid-flight (clearing <see cref="Withheld"/>, say) does so
    /// while that queue may be reading it. With plain collections that was a data race — an
    /// occasional wrong answer, and once a test host that never finished (2026-11-06).</para></summary>
    private sealed class Gateway : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> Coins = new(StringComparer.Ordinal);
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> History = new(StringComparer.Ordinal);
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Summaries = new(StringComparer.Ordinal);
        public readonly ConcurrentSet Withheld = new();
        /// <summary>txid asked for → the txid whose raw transaction is served in its place.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Substitute = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A case-insensitive set of txids that tolerates a reader and a writer at once.</summary>
        public sealed class ConcurrentSet
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _items = new(StringComparer.OrdinalIgnoreCase);
            public void Add(string item) => _items[item] = 0;
            public bool Contains(string item) => _items.ContainsKey(item);
            public void Clear() => _items.Clear();
        }
        public int RawCalls, ProofCalls;
        public string? BroadcastHex;

        public void Coin(string address, string txId, int vout, long sats, int confs = 25, bool coinbase = false)
        {
            var row = $$"""{"txId":"{{txId}}","outputIndex":{{vout}},"amount":{{sats}},"confirmations":{{confs}},"blockHeight":{{(confs > 0 ? F.BlockHeight : 0)}},"isCoinbase":{{(coinbase ? "true" : "false")}}}""";
            Coins.AddOrUpdate(address, [row], (_, rows) => [.. rows, row]);
            Used(address);
        }

        public void Row(string address, string txId, long amount, long height = F.BlockHeight)
        {
            var row = $$"""{"txId":"{{txId}}","blockHeight":{{height}},"timestamp":"2026-10-01T00:00:00Z","amount":{{amount}},"type":"{{(amount >= 0 ? "received" : "sent")}}"}""";
            History.AddOrUpdate(address, [row], (_, rows) => [.. rows, row]);
            Used(address);
        }

        public void Used(string address) =>
            Summaries.TryAdd(address, """{"balance":0,"totalReceived":1,"totalSent":0,"txCount":1}""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var parts = path.TrimEnd('/').Split('/');

            if (path.EndsWith("/utxos"))
                return Json("[" + string.Join(",", Coins.TryGetValue(parts[^2], out var c) ? c : []) + "]");
            if (path.EndsWith("/transactions"))
            {
                var rows = History.TryGetValue(parts[^2], out var h) ? h : [];
                return Json($$"""{"page":1,"pageSize":25,"total":{{rows.Length}},"items":[{{string.Join(",", rows)}}]}""");
            }
            if (path.EndsWith("/raw"))
            {
                Interlocked.Increment(ref RawCalls);
                var hex = Withheld.Contains(parts[^2]) ? null
                    : F.RawHex(Substitute.TryGetValue(parts[^2], out var other) ? other : parts[^2]);
                return hex == null ? NotFound() : Json($$"""{"txId":"{{parts[^2]}}","hex":"{{hex}}"}""");
            }
            if (path.EndsWith("/proof"))
            {
                Interlocked.Increment(ref ProofCalls);
                var proof = Withheld.Contains(parts[^2]) ? null : F.ProofHex(parts[^2]);
                return proof == null ? NotFound() : Json($$"""{"txId":"{{parts[^2]}}","proof":"{{proof}}"}""");
            }
            if (path.EndsWith("/api/tx/broadcast"))
            {
                BroadcastHex = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.GetProperty("hex").GetString();
                return Json("""{"txId":"cafe"}""");
            }
            if (path.Contains("/address/"))
                return Summaries.TryGetValue(parts[^1], out var s) ? Json(s) : NotFound();
            return NotFound();
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage NotFound() =>
            new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json") };
    }

    private sealed class Wallet(LiteWalletService service, Gateway gateway, IWalletStateStore store)
    {
        public LiteWalletService Service => service;
        public Gateway Gateway => gateway;
        public IWalletStateStore Store => store;
        public DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>The production gateway-mode wiring (verifier present, header sync omitted — the
    /// web head's shape). The gateway is scripted BEFORE the restore so its discovery sees the
    /// addresses a test uses.</summary>
    private static async Task<Wallet> OpenAsync(Action<Gateway>? script = null, IWalletStateStore? store = null)
    {
        var gateway = new Gateway();
        gateway.Used(F.Receive0);
        script?.Invoke(gateway);
        var indexer = new IndexerDataService(
            new HttpClient(gateway) { BaseAddress = new Uri("https://gw.test/") }, NullLogger<IndexerDataService>.Instance);
        store ??= new InMemoryWalletStateStore();
        Wallet? wallet = null;
        var service = GatewayWallet(indexer, store, () => wallet!.Now, guardOnClock: false);
        wallet = new Wallet(service, gateway, store);
        await service.RestoreWalletAsync(F.Mnemonic);
        return wallet;
    }

    /// <summary>The gateway-mode wallet service over one client, as the composition root wires it
    /// (the client is also who a second opinion on spends and address use is asked of) — with the
    /// read-path queue and the spent-coin guard built HERE and handed in, unpaced and on the
    /// test's clock, instead of being reached for inside the service afterwards.</summary>
    private static LiteWalletService GatewayWallet(IndexerDataService indexer, IWalletStateStore store, Func<DateTime> clock,
        bool guardOnClock = true)
    {
        var verifier = new LiteTxVerifier(indexer);
        var checks = new ChainCheckQueue { Pace = TimeSpan.Zero, Clock = clock };
        var spends = new SpentCoinGuard(indexer, verifier);
        if (guardOnClock) spends.Clock = clock;
        return new LiteWalletService(new MemoryVault(), indexer, indexer, null, null, verifier, store, null, indexer, checks, spends);
    }

    private static async Task PollAsync(IncomingPaymentWatcher watcher) => await watcher.PollOnceAsync();

    // ════════════════════════ the report's scenario ════════════════════════

    [Fact]
    public async Task A_fabricated_confirmed_coin_is_not_balance()
    {
        var w = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 25);
            gw.Summaries[F.Receive0] = $$"""{"balance":{{FakeSats}},"totalReceived":{{FakeSats}},"totalSent":0,"txCount":1}""";
        });

        Assert.Equal(0, await w.Service.GetSpendableAsync());            // was 77,700,000,000

        var breakdown = await w.Service.GetBalanceBreakdownAsync();
        Assert.Equal(0, breakdown.Spendable);                            // was 77,700,000,000
        Assert.Equal(0, breakdown.ImmatureCoinbase);
        Assert.Equal(0, breakdown.UnconfirmedIncoming);
        Assert.Equal(FakeSats, breakdown.Unverified);                    // shown as a claim, on its own line

        var coin = Assert.Single(await w.Service.GetAllUtxosAsync());
        Assert.False(coin.Verified);
    }

    [Fact]
    public async Task A_fabricated_immature_coinbase_is_not_shown_as_maturing_either()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 5, coinbase: true));

        var breakdown = await w.Service.GetBalanceBreakdownAsync();

        Assert.Equal(0, breakdown.ImmatureCoinbase);
        Assert.Equal(FakeSats, breakdown.Unverified);
    }

    [Fact]
    public async Task A_fabricated_confirmed_coin_is_never_announced_as_received()
    {
        var w = await OpenAsync();
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);                                        // baseline: an empty wallet

        w.Gateway.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 25);    // the gateway starts lying
        for (var i = 0; i < 4; i++)
        {
            await PollAsync(watcher);
            w.Now += TimeSpan.FromMinutes(11);                           // past every back-off
        }

        Assert.Empty(events);                                            // was one event, 25 confirmations
    }

    [Fact]
    public async Task A_fabricated_received_row_is_listed_as_not_verified()
    {
        var w = await OpenAsync(gw => gw.Row(F.Receive0, FakeTxId, FakeSats));

        var row = Assert.Single(await w.Service.GetHistoryAsync());

        Assert.Equal(FakeTxId, row.TxId);
        Assert.Equal("received", row.Type);
        Assert.Equal(LiteHistoryCheck.Unverified, row.Check);            // was indistinguishable from income
    }

    [Fact]
    public async Task A_fabricated_coin_no_longer_blocks_sending_the_real_ones()
    {
        // The made-up coin is the largest, so it used to be selected first and failed every
        // send at signing ("Could not verify your coins"). It is now passed over.
        var w = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, FakeTxId, 0, FakeSats);
            gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000);
        });

        var sent = await w.Service.SendAsync(F.Receive1, 100_000_000);

        Assert.True(sent.Success, sent.Error);
        var tx = Transaction.Parse(w.Gateway.BroadcastHex!, BlazecoinNetwork.Instance);
        Assert.Equal(F.TxId(0), Assert.Single(tx.Inputs).PrevOut.Hash.ToString());
        Assert.Equal(LiteTxId.OfHex(w.Gateway.BroadcastHex), sent.TxId);
    }

    [Fact]
    public async Task With_only_a_fabricated_coin_a_send_fails_before_anything_is_signed()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats));

        var sent = await w.Service.SendAsync(F.Receive1, 100_000_000);
        var max = await w.Service.SendMaxAsync(F.Receive1);

        Assert.False(sent.Success);
        Assert.Contains("Could not verify your coins against the chain", sent.Error);
        Assert.False(max.Success);
        Assert.Contains("Could not verify your coins against the chain", max.Error);
        Assert.Null(w.Gateway.BroadcastHex);
    }

    [Fact]
    public async Task An_honest_empty_gateway_shows_nothing()
    {
        var w = await OpenAsync();
        using var watcher = new IncomingPaymentWatcher(w.Service);
        IncomingPayment? fired = null;
        watcher.PaymentReceived += p => fired = p;
        await PollAsync(watcher);
        await PollAsync(watcher);

        Assert.Equal(0, await w.Service.GetSpendableAsync());
        Assert.Equal(new LiteBalanceBreakdown(0, 0, 0), await w.Service.GetBalanceBreakdownAsync());
        Assert.Empty(await w.Service.GetHistoryAsync());
        Assert.Null(fired);
    }

    // ════════════════════════ honest coins still count ════════════════════════

    [Fact]
    public async Task A_real_coin_is_proven_inside_the_first_balance_read_and_only_once()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000));

        Assert.Equal(1_000_000_000, await w.Service.GetSpendableAsync());   // no waiting, no second read
        Assert.True(Assert.Single(await w.Service.GetAllUtxosAsync()).Verified);
        Assert.Equal(1_000_000_000, (await w.Service.GetBalanceBreakdownAsync()).Spendable);
        Assert.Equal(0, (await w.Service.GetBalanceBreakdownAsync()).Unverified);

        Assert.Equal(1, w.Gateway.RawCalls);                                // checked once, then remembered
        Assert.Equal(1, w.Gateway.ProofCalls);
    }

    [Fact]
    public async Task A_proven_coin_is_worth_what_the_chain_says_not_what_the_gateway_says()
    {
        // The transaction really pays 10 BLZ; the gateway's row says 5,000.
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 500_000_000_000));

        Assert.Equal(1_000_000_000, await w.Service.GetSpendableAsync());
        Assert.Equal(1_000_000_000, Assert.Single(await w.Service.GetAllUtxosAsync()).Utxo.Amount);
    }

    [Fact]
    public async Task A_real_coin_that_pays_someone_else_is_not_this_wallets()
    {
        // A genuine, mined transaction — but its output pays a stranger. Listed under our address.
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(11), 0, 5_000_000_000));

        var breakdown = await w.Service.GetBalanceBreakdownAsync();

        Assert.Equal(0, breakdown.Spendable);
        Assert.Equal(5_000_000_000, breakdown.Unverified);
    }

    [Fact]
    public async Task An_output_index_the_transaction_does_not_have_is_not_a_coin()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 7, 1_000_000_000));

        Assert.Equal(0, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task An_unconfirmed_coin_is_incoming_and_nothing_is_fetched_for_it()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000, confs: 0));

        var breakdown = await w.Service.GetBalanceBreakdownAsync();

        Assert.Equal(1_000_000_000, breakdown.UnconfirmedIncoming);     // labelled unconfirmed, as before
        Assert.Equal(0, breakdown.Unverified);
        Assert.Equal(0, breakdown.Spendable);
        Assert.Equal(0, w.Gateway.RawCalls);                             // nothing to prove until it is mined
    }

    [Fact]
    public async Task Coins_on_several_addresses_are_each_proven_against_their_own_address()
    {
        var w = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000);
            gw.Coin(F.Receive1, F.TxId(10), 0, 250_000_000);
            gw.Coin(F.Receive1, F.TxId(1), 0, 900_000_000);              // really pays receive[0], listed under [1]
        });

        var coins = await w.Service.GetAllUtxosAsync();

        Assert.Equal(1_250_000_000, await w.Service.GetSpendableAsync());
        Assert.False(coins.Single(c => c.Utxo.TxId == F.TxId(1)).Verified);
    }

    [Fact]
    public async Task More_coins_than_one_read_checks_settle_in_the_background()
    {
        var w = await OpenAsync(gw =>
        {
            for (var i = 0; i < 10; i++) gw.Coin(F.Receive0, F.TxId(i), 0, (10 - i) * 100_000_000L);
        });
        var changes = 0;
        w.Service.VerificationChanged += () => Interlocked.Increment(ref changes);

        var first = await w.Service.GetBalanceBreakdownAsync();
        // The six largest are proven inside the read; the rest are a claim until the queue gets to them.
        Assert.True(first.Spendable >= 4_500_000_000, $"spendable {first.Spendable}");
        Assert.Equal(5_500_000_000, first.Spendable + first.Unverified);

        await w.Service.WhenVerificationIdleAsync();
        var settled = await w.Service.GetBalanceBreakdownAsync();

        Assert.Equal(5_500_000_000, settled.Spendable);
        Assert.Equal(0, settled.Unverified);
        Assert.True(changes >= 1);                                       // the page is told to re-read
        Assert.Equal(10, w.Gateway.RawCalls);                            // every coin checked exactly once
    }

    // ════════════════════════ a check that fails is tried again ════════════════════════

    [Fact]
    public async Task A_coin_whose_proof_is_not_available_yet_counts_once_it_is()
    {
        var w = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000);
            gw.Withheld.Add(F.TxId(0));
        });

        Assert.Equal(0, await w.Service.GetSpendableAsync());
        Assert.Equal(1_000_000_000, (await w.Service.GetBalanceBreakdownAsync()).Unverified);

        w.Gateway.Withheld.Clear();
        Assert.Equal(0, await w.Service.GetSpendableAsync());            // inside the back-off: not hammered

        w.Now += TimeSpan.FromSeconds(11);
        Assert.Equal(1_000_000_000, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task A_failing_check_backs_off_instead_of_asking_on_every_read()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats));

        for (var i = 0; i < 5; i++) await w.Service.GetBalanceBreakdownAsync();
        Assert.Equal(1, w.Gateway.RawCalls);

        w.Now += TimeSpan.FromSeconds(11);                               // first back-off (10 s) is over
        await w.Service.GetBalanceBreakdownAsync();
        await w.Service.GetBalanceBreakdownAsync();
        Assert.Equal(2, w.Gateway.RawCalls);

        w.Now += TimeSpan.FromSeconds(11);                               // the second is 20 s
        await w.Service.GetBalanceBreakdownAsync();
        Assert.Equal(2, w.Gateway.RawCalls);
    }

    [Fact]
    public async Task A_send_checks_its_coins_at_once_whatever_the_back_off_says()
    {
        var w = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000);
            gw.Withheld.Add(F.TxId(0));
        });
        Assert.Equal(0, await w.Service.GetSpendableAsync());            // failed, now backing off
        w.Gateway.Withheld.Clear();

        var sent = await w.Service.SendAsync(F.Receive1, 100_000_000);   // the user is waiting on this coin

        Assert.True(sent.Success, sent.Error);
    }

    // ════════════════════════ remembered across restarts ════════════════════════

    [Fact]
    public async Task Proven_coins_are_remembered_so_a_restart_does_not_verify_them_again()
    {
        var store = new InMemoryWalletStateStore();
        var first = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);
        Assert.Equal(1_000_000_000, await first.Service.GetSpendableAsync());
        Assert.Contains(F.TxId(0), await store.GetVerifiedCoinsAsync());

        // A new session over the same store; this time the gateway cannot produce the proof at all.
        var second = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000);
            gw.Withheld.Add(F.TxId(0));
        }, store);

        Assert.Equal(1_000_000_000, await second.Service.GetSpendableAsync());
        Assert.Equal(0, second.Gateway.RawCalls);
    }

    [Fact]
    public async Task What_is_remembered_is_the_proven_value_and_only_for_that_address()
    {
        var store = new InMemoryWalletStateStore();
        var first = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);
        await first.Service.GetSpendableAsync();

        var second = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 900_000_000_000);          // same outpoint, an inflated claim
            gw.Coin(F.Receive1, F.TxId(0), 0, 1_000_000_000);            // same outpoint under another address
            gw.Withheld.Add(F.TxId(0));
        }, store);

        Assert.Equal(1_000_000_000, await second.Service.GetSpendableAsync());   // the proven value, once
    }

    [Fact]
    public async Task A_read_that_returns_no_coins_does_not_forget_the_proofs()
    {
        var store = new InMemoryWalletStateStore();
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);
        await w.Service.GetSpendableAsync();
        var remembered = await store.GetVerifiedCoinsAsync();

        w.Gateway.Coins.Clear();                                         // an outage looks like an empty wallet
        await w.Service.GetSpendableAsync();

        Assert.Equal(remembered, await store.GetVerifiedCoinsAsync());
    }

    [Fact]
    public async Task Resetting_the_wallet_forgets_what_was_proven()
    {
        var store = new InMemoryWalletStateStore();
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);
        await w.Service.GetSpendableAsync();

        await w.Service.ResetAsync();

        Assert.Null(await store.GetVerifiedCoinsAsync());
        Assert.False(w.Service.Checks!.TryGetVerified($"c:{F.TxId(0)}:0:{F.Receive0}", out _));
    }

    [Fact]
    public async Task A_garbled_remembered_set_is_ignored()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync($"junk;{FakeTxId}:0:{F.Receive0}:-5;{FakeTxId}:x:{F.Receive0}:5;short:0:a:5;;");

        var w = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats), store);

        Assert.Equal(0, await w.Service.GetSpendableAsync());
    }

    // ════════════════════════ the payment watcher ════════════════════════

    [Fact]
    public async Task A_real_confirmed_payment_is_announced_once_proven()
    {
        var w = await OpenAsync();
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);

        w.Gateway.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000, confs: 3);
        await PollAsync(watcher);
        await PollAsync(watcher);

        var payment = Assert.Single(events);                              // once, not once per poll
        Assert.Equal(F.TxId(0), payment.TxId);
        Assert.Equal(1_000_000_000, payment.AmountSatoshis);
        Assert.Equal(3, payment.Confirmations);
    }

    [Fact]
    public async Task A_payment_that_cannot_be_proven_yet_is_announced_when_it_can_not_lost()
    {
        var w = await OpenAsync();
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);

        w.Gateway.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000, confs: 1);
        w.Gateway.Withheld.Add(F.TxId(0));                               // the proof is not served yet
        await PollAsync(watcher);
        Assert.Empty(events);

        w.Gateway.Withheld.Clear();
        w.Now += TimeSpan.FromSeconds(11);
        await PollAsync(watcher);

        Assert.Equal(F.TxId(0), Assert.Single(events).TxId);
    }

    [Fact]
    public async Task Coins_that_were_already_there_do_not_announce_themselves_when_they_verify()
    {
        // At start-up every coin is unproven for a moment; the first poll must still record them.
        var w = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000);
            gw.Withheld.Add(F.TxId(0));
        });
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);                                        // baseline, coin unproven

        w.Gateway.Withheld.Clear();
        w.Now += TimeSpan.FromSeconds(11);
        await PollAsync(watcher);                                        // now proven

        Assert.Empty(events);
        Assert.Equal(1_000_000_000, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task A_transaction_with_two_outputs_to_the_wallet_is_announced_once_with_both()
    {
        var w = await OpenAsync();
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);

        w.Gateway.Coin(F.Receive0, F.TxId(12), 0, 30_000_000, confs: 2);
        w.Gateway.Coin(F.Receive0, F.TxId(12), 1, 20_000_000, confs: 2);
        await PollAsync(watcher);
        await PollAsync(watcher);

        Assert.Equal(50_000_000, Assert.Single(events).AmountSatoshis);
    }

    [Fact]
    public async Task An_unconfirmed_sighting_is_still_announced_as_unconfirmed()
    {
        // Nothing can be proven about a transaction that is not mined; the toast for it says
        // "Incoming · unconfirmed" (security audit S2) and that stays as it was.
        var w = await OpenAsync();
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);

        w.Gateway.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 0);
        await PollAsync(watcher);

        Assert.Equal(0, Assert.Single(events).Confirmations);
    }

    // ════════════════════════ history ════════════════════════

    [Fact]
    public async Task A_real_received_row_is_verified()
    {
        var w = await OpenAsync(gw => gw.Row(F.Receive0, F.TxId(0), 1_000_000_000));

        var row = Assert.Single(await w.Service.GetHistoryAsync());

        Assert.Equal(LiteHistoryCheck.Verified, row.Check);
    }

    [Fact]
    public async Task A_row_claiming_more_than_the_transaction_pays_is_not_verified()
    {
        // The transaction is real and mined, and pays the wallet 10 BLZ — the row says 2,000.
        var w = await OpenAsync(gw => gw.Row(F.Receive0, F.TxId(0), 200_000_000_000));

        Assert.Equal(LiteHistoryCheck.Unverified, Assert.Single(await w.Service.GetHistoryAsync()).Check);
    }

    [Fact]
    public async Task A_row_for_a_real_transaction_that_pays_someone_else_is_not_verified()
    {
        var w = await OpenAsync(gw => gw.Row(F.Receive0, F.TxId(11), 5_000_000_000));

        Assert.Equal(LiteHistoryCheck.Unverified, Assert.Single(await w.Service.GetHistoryAsync()).Check);
    }

    [Fact]
    public async Task A_received_row_across_two_outputs_is_verified_against_their_sum()
    {
        var w = await OpenAsync(gw => gw.Row(F.Receive0, F.TxId(12), 50_000_000));

        Assert.Equal(LiteHistoryCheck.Verified, Assert.Single(await w.Service.GetHistoryAsync()).Check);
    }

    [Fact]
    public async Task Sent_rows_are_not_checked_and_unmined_rows_say_unconfirmed()
    {
        var w = await OpenAsync(gw =>
        {
            gw.Row(F.Receive0, new string('a', 64), -300_000_000);
            gw.Row(F.Receive0, new string('b', 64), 400_000_000, height: 0);
        });

        var rows = await w.Service.GetHistoryAsync();

        Assert.Equal(LiteHistoryCheck.NotChecked, rows.Single(r => r.Amount < 0).Check);
        Assert.Equal(LiteHistoryCheck.Unconfirmed, rows.Single(r => r.Amount > 0).Check);
        Assert.Equal(0, w.Gateway.RawCalls);
    }

    [Fact]
    public async Task Rows_beyond_what_one_read_checks_say_verifying_and_then_settle()
    {
        var w = await OpenAsync(gw =>
        {
            for (var i = 0; i < 9; i++) gw.Row(F.Receive0, F.TxId(i), (10 - i) * 100_000_000L);
        });

        var first = await w.Service.GetHistoryAsync();
        Assert.Equal(9, first.Count);
        Assert.All(first, r => Assert.Contains(r.Check, new[] { LiteHistoryCheck.Verified, LiteHistoryCheck.Pending }));
        Assert.True(first.Count(r => r.Check == LiteHistoryCheck.Verified) >= 4);

        await w.Service.WhenVerificationIdleAsync();

        Assert.All(await w.Service.GetHistoryAsync(), r => Assert.Equal(LiteHistoryCheck.Verified, r.Check));
    }

    // ════════════════════════ a source that is authoritative ════════════════════════

    private sealed class OwnNode : ILiteWalletData
    {
        public bool SupportsHistory => true;
        public bool SupportsChainVerification => false;
        public Task<LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default) =>
            Task.FromResult<LiteAddressSummary?>(address == F.Receive0 ? new LiteAddressSummary(FakeSats, FakeSats, 0, 1) : null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiteChainUtxo>>(address == F.Receive0
                ? [new LiteChainUtxo(FakeTxId, 0, FakeSats, 25, F.BlockHeight, false)] : []);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiteHistoryEntry>>(address == F.Receive0
                ? [new LiteHistoryEntry(FakeTxId, F.BlockHeight, DateTime.UnixEpoch, FakeSats, "received")] : []);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default) =>
            Task.FromResult(LiteBroadcastResult.Ok(new string('c', 64)));
    }

    [Fact]
    public async Task The_users_own_node_is_taken_at_its_word()
    {
        // Personal-node mode wires no verifier: the user's own chainstate is the authority.
        var service = new LiteWalletService(new MemoryVault(), new OwnNode());
        await service.RestoreWalletAsync(F.Mnemonic);

        Assert.Null(service.Checks);
        Assert.Equal(FakeSats, await service.GetSpendableAsync());
        Assert.True(Assert.Single(await service.GetAllUtxosAsync()).Verified);
        Assert.Equal(0, (await service.GetBalanceBreakdownAsync()).Unverified);
        Assert.Equal(LiteHistoryCheck.NotChecked, Assert.Single(await service.GetHistoryAsync()).Check);
    }

    // ════════════════════════ the verifier's receipt check ════════════════════════

    private sealed class FixtureReader : IChainReader
    {
        public bool ServeProof = true;
        public bool SupportsHistory => false;
        public bool SupportsChainVerification => true;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult(F.RawHex(txId));
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult(ServeProof ? F.ProofHex(txId) : null);
    }

    [Fact]
    public async Task Receipt_check_reports_what_the_transaction_really_pays_the_wallet()
    {
        var verifier = new LiteTxVerifier(new FixtureReader());
        string[] owned = [F.Receive0, F.Receive1];

        var exact = await verifier.VerifyReceiptAsync(F.TxId(12), owned, 50_000_000, F.BlockHeight);
        Assert.True(exact.Verified);
        Assert.Equal(50_000_000, exact.RealSatoshis);
        Assert.True(exact.IncludedInBlock);

        // A net amount below the outputs (the wallet also spent coins in it) is still consistent.
        Assert.True((await verifier.VerifyReceiptAsync(F.TxId(12), owned, 10_000_000, F.BlockHeight)).Verified);

        var inflated = await verifier.VerifyReceiptAsync(F.TxId(12), owned, 50_000_001, F.BlockHeight);
        Assert.False(inflated.Verified);
        Assert.Equal(50_000_000, inflated.RealSatoshis);
        Assert.Contains("does not pay this wallet", inflated.Error);

        var notOurs = await verifier.VerifyReceiptAsync(F.TxId(11), owned, 1, F.BlockHeight);
        Assert.False(notOurs.Verified);
        Assert.Equal(0, notOurs.RealSatoshis);

        var unknown = await verifier.VerifyReceiptAsync(FakeTxId, owned, 1, F.BlockHeight);
        Assert.False(unknown.Verified);
        Assert.Contains("Could not fetch", unknown.Error);
    }

    [Fact]
    public async Task Receipt_check_needs_the_transaction_to_be_mined()
    {
        var verifier = new LiteTxVerifier(new FixtureReader { ServeProof = false });

        var v = await verifier.VerifyReceiptAsync(F.TxId(0), [F.Receive0], 1_000_000_000, F.BlockHeight);

        Assert.False(v.Verified);
        Assert.Contains("No merkle inclusion proof", v.Error);
    }

    // ════════════════════════ the fixture ════════════════════════

    /// <summary>
    /// Finds <see cref="MinedBlockFixture.Nonce"/> again after the fixture's transactions change.
    /// Does nothing unless BLZ_GRIND=1; then it writes the nonce to the file named by
    /// BLZ_GRIND_OUT and fails with the nonce in its message. Takes a few minutes.
    /// </summary>
    [Fact]
    public void Regrind_the_fixture_nonce()
    {
        if (Environment.GetEnvironmentVariable("BLZ_GRIND") != "1") return;
        Assert.Fail($"MinedBlockFixture.Nonce = {Grind(F.Block.Header)}");
    }

    /// <summary>The same for <see cref="SpendBlockFixture"/> (BLZ_GRIND=spend).</summary>
    [Fact]
    public void Regrind_the_spend_fixture_nonce()
    {
        if (Environment.GetEnvironmentVariable("BLZ_GRIND") != "spend") return;
        Assert.Fail($"SpendBlockFixture.Nonce = {Grind(SpendBlockFixture.Block.Header)}");
    }

    [Fact]
    public void The_spend_fixture_block_is_really_mined()
    {
        Assert.True(BlazecoinPoW.MeetsTarget(SpendBlockFixture.Block.Header),
            "SpendBlockFixture.Nonce no longer fits the block — regrind it (see the fixture).");
    }

    private static long Grind(BlockHeader blockHeader)
    {
        var template = blockHeader.ToBytes();
        long found = -1;
        Parallel.For(0L, uint.MaxValue, (nonce, loop) =>
        {
            var header = (byte[])template.Clone();
            BitConverter.TryWriteBytes(header.AsSpan(76, 4), (uint)nonce);
            var scrypt = Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(header, header, N: 1024, r: 1, p: 1, dkLen: 32);
            var hash = new System.Numerics.BigInteger(scrypt, isUnsigned: true, isBigEndian: false);
            if (hash <= BlazecoinDifficulty.CompactToTarget(F.Bits))
            {
                Interlocked.CompareExchange(ref found, nonce, -1);
                loop.Stop();
            }
        });

        var outFile = Environment.GetEnvironmentVariable("BLZ_GRIND_OUT");
        if (!string.IsNullOrEmpty(outFile)) File.WriteAllText(outFile, found.ToString());
        return found;
    }

    [Fact]
    public void The_fixture_block_is_really_mined()
    {
        Assert.True(BlazecoinPoW.MeetsTarget(F.Block.Header),
            "MinedBlockFixture.Nonce no longer fits the block — regrind it (see the fixture).");
        Assert.Equal(F.Bits, F.Block.Header.Bits.ToCompact());
        Assert.Equal(13, F.Count);
    }
}

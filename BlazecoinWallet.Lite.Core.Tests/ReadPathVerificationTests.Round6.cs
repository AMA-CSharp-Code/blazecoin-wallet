using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;
using S = BlazecoinWallet.Lite.Tests.SpendBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Bug-bounty round six (2026-11-06): a coin the wallet has PROVEN was created can have been
/// spent since, and nothing the listing gateway serves shows that. A gateway that simply kept a
/// spent coin in its /utxos answer — forging nothing — had it counted as spendable balance and
/// announced as a payment. A spend, unlike an absence, can be proven: so each proven coin is put
/// to a SECOND gateway, and a spend it can show takes the coin out.
/// <para>Two gateways on the wire here (<c>a.test</c>, the one the wallet reads from, and
/// <c>b.test</c>), the production client and verifier, and real proofs both ways: coins from
/// <see cref="MinedBlockFixture"/>, the transactions that spend them from <see cref="SpendBlockFixture"/>.</para>
/// </summary>
public partial class ReadPathVerificationTests
{
    /// <summary>One gateway's scripted view of the chain. Thread-safe: checks run on background queues.</summary>
    private sealed class Node
    {
        public readonly ConcurrentDictionary<string, string[]> Coins = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, byte> UsedAddresses = new(StringComparer.Ordinal);
        /// <summary>"txid:vout" → the mined transaction this gateway says spent it.</summary>
        public readonly ConcurrentDictionary<string, (string Spender, long Height)> Spends = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Transactions whose raw hex and proof this gateway will not serve.</summary>
        public readonly Gateway.ConcurrentSet Withheld = new();
        /// <summary>False = an older gateway (or a full Indexer site): the spends question is a 404.</summary>
        public volatile bool ServesSpends = true;
        /// <summary>True = unreachable.</summary>
        public volatile bool Down;
        public int SpendCalls, RawCalls, ProofCalls;
        public string? BroadcastHex;

        public void Coin(string address, string txId, int vout, long sats, int confs = 25)
        {
            var row = $$"""{"txId":"{{txId}}","outputIndex":{{vout}},"amount":{{sats}},"confirmations":{{confs}},"blockHeight":{{(confs > 0 ? F.BlockHeight : 0)}},"isCoinbase":false}""";
            Coins.AddOrUpdate(address, [row], (_, rows) => [.. rows, row]);
            Used(address);
        }

        public void Used(string address) => UsedAddresses[address] = 0;

        public void Spent(string txId, int vout, string spender, long height = S.BlockHeight) =>
            Spends[$"{txId}:{vout}"] = (spender, height);

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("unreachable");
            var path = request.RequestUri!.AbsolutePath;
            var parts = path.TrimEnd('/').Split('/');

            if (path.EndsWith("/outputs/spends"))
            {
                Interlocked.Increment(ref SpendCalls);
                if (!ServesSpends) return NotFound();
                var asked = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?')).Split('=', 2)[1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries);
                var items = asked.Select(a =>
                {
                    var bits = a.Split(':');
                    var spent = Spends.TryGetValue(a, out var s);
                    return $$"""{"txId":"{{bits[0]}}","vout":{{bits[1]}},"known":true,"spentTxId":{{(spent ? $"\"{s.Spender}\"" : "null")}},"spentHeight":{{(spent ? s.Height.ToString() : "null")}}}""";
                });
                return Json($$"""{"height":{{S.BlockHeight + 10}},"items":[{{string.Join(",", items)}}]}""");
            }
            if (path.EndsWith("/utxos"))
                return Json("[" + string.Join(",", Coins.TryGetValue(parts[^2], out var c) ? c : []) + "]");
            if (path.EndsWith("/transactions"))
                return Json("""{"page":1,"pageSize":25,"total":0,"items":[]}""");
            if (path.EndsWith("/raw"))
            {
                Interlocked.Increment(ref RawCalls);
                var hex = Withheld.Contains(parts[^2]) ? null : F.RawHex(parts[^2]) ?? S.RawHex(parts[^2]);
                return hex == null ? NotFound() : Json($$"""{"txId":"{{parts[^2]}}","hex":"{{hex}}"}""");
            }
            if (path.EndsWith("/proof"))
            {
                Interlocked.Increment(ref ProofCalls);
                var proof = Withheld.Contains(parts[^2]) ? null : F.ProofHex(parts[^2]) ?? S.ProofHex(parts[^2]);
                return proof == null ? NotFound() : Json($$"""{"txId":"{{parts[^2]}}","proof":"{{proof}}"}""");
            }
            if (path.EndsWith("/api/tx/broadcast"))
            {
                BroadcastHex = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.GetProperty("hex").GetString();
                return Json("""{"txId":"cafe"}""");
            }
            if (path.Contains("/address/"))
                return UsedAddresses.ContainsKey(parts[^1])
                    ? Json("""{"balance":0,"totalReceived":1,"totalSent":0,"txCount":1}""")
                    : NotFound();
            return NotFound();
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage NotFound() =>
            new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json") };
    }

    /// <summary>The wire: requests for <c>a.test</c> reach node A, everything else node B.</summary>
    private sealed class Fleet : HttpMessageHandler
    {
        public readonly Node A = new(), B = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            (request.RequestUri!.Host == "a.test" ? A : B).HandleAsync(request, ct);
    }

    private sealed class Pair(LiteWalletService service, IndexerDataService indexer, Fleet fleet, IWalletStateStore store)
    {
        public LiteWalletService Service => service;
        public IndexerDataService Indexer => indexer;
        public Node A => fleet.A;
        public Node B => fleet.B;
        public IWalletStateStore Store => store;
        public DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    }

    private static readonly Uri GatewayA = new("https://a.test/");
    private static readonly Uri GatewayB = new("https://b.test/");

    /// <summary>The production gateway-mode wiring WITH a failover list — what every shipped head
    /// has: the wallet reads from a.test (the first, sticky gateway) and can ask b.test.</summary>
    private static async Task<Pair> OpenPairAsync(Action<Node, Node>? script = null, IWalletStateStore? store = null)
    {
        var fleet = new Fleet();
        fleet.A.Used(F.Receive0);
        fleet.B.Used(F.Receive0);
        script?.Invoke(fleet.A, fleet.B);
        var failover = new GatewayFailoverHandler([GatewayA.ToString(), GatewayB.ToString()], fleet);
        var indexer = new IndexerDataService(new HttpClient(failover) { BaseAddress = failover.Primary }, NullLogger<IndexerDataService>.Instance);
        store ??= new InMemoryWalletStateStore();
        var service = new LiteWalletService(new MemoryVault(), indexer, verifier: new LiteTxVerifier(indexer), stateStore: store);
        var pair = new Pair(service, indexer, fleet, store);
        service.Checks!.Pace = TimeSpan.Zero;
        service.Checks.Clock = () => pair.Now;
        service.Spends!.Clock = () => pair.Now;
        await service.RestoreWalletAsync(F.Mnemonic);
        return pair;
    }

    /// <summary>The report's gateway: it lists the 10 BLZ coin, serves its genuine transaction and
    /// proof, and says nothing of the transaction that spent it. Gateway B knows the spend.</summary>
    private static void ScriptResurrectedCoin(Node a, Node b)
    {
        a.Coin(F.Receive0, F.TxId(0), 0, TenBlz);
        b.Spent(F.TxId(0), 0, S.TxId(0));
    }

    // ════════════════════════ the report's scenario ════════════════════════

    [Fact]
    public async Task A_spent_coin_the_gateway_still_lists_is_not_balance()
    {
        var w = await OpenPairAsync(ScriptResurrectedCoin);

        Assert.Equal(0, await w.Service.GetSpendableAsync());            // was 1,000,000,000

        var breakdown = await w.Service.GetBalanceBreakdownAsync();
        Assert.Equal(0, breakdown.Spendable);                            // was 1,000,000,000
        Assert.Equal(0, breakdown.Unverified);                           // not "being checked": it is known
        Assert.Equal(TenBlz, breakdown.ProvenSpent);                     // its own line: the gateway is wrong

        var coin = Assert.Single(await w.Service.GetAllUtxosAsync());
        Assert.False(coin.Verified);                                     // was Verified=True
        Assert.True(coin.ProvenSpent);
    }

    [Fact]
    public async Task A_spent_coin_the_gateway_starts_listing_is_never_announced_as_received()
    {
        var w = await OpenPairAsync((_, b) => b.Spent(F.TxId(0), 0, S.TxId(0)));
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);                                        // baseline: an empty wallet

        w.A.Coin(F.Receive0, F.TxId(0), 0, TenBlz);                      // the gateway resurrects the coin
        for (var i = 0; i < 4; i++)
        {
            await PollAsync(watcher);
            await w.Service.WhenVerificationIdleAsync();
            w.Now += TimeSpan.FromMinutes(11);                           // past every back-off and re-check
        }

        Assert.Empty(events);                                            // was PaymentReceived, 25 confirmations
    }

    [Fact]
    public async Task The_question_goes_to_the_other_gateway_and_so_does_the_request_for_evidence()
    {
        var w = await OpenPairAsync(ScriptResurrectedCoin);
        var rawBefore = w.A.RawCalls;                                     // restore discovery read nothing of the spender
        await w.Service.GetAllUtxosAsync();

        Assert.Equal(0, w.A.SpendCalls);                                  // the lister is never asked to vouch for itself
        Assert.Equal(1, w.B.SpendCalls);                                  // one question
        Assert.Equal(1, w.B.RawCalls);                                    // the spending transaction…
        Assert.Equal(1, w.B.ProofCalls);                                  // …and its proof, from the same side
        Assert.True(w.A.RawCalls - rawBefore <= 1);                       // the lister served the coin's own transaction only
    }

    // ════════════════════════ honest coins still work ════════════════════════

    [Fact]
    public async Task An_unspent_coin_counts_and_is_asked_about_once_until_the_answer_ages()
    {
        var w = await OpenPairAsync((a, _) => a.Coin(F.Receive0, F.TxId(0), 0, TenBlz));

        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());
        var coin = Assert.Single(await w.Service.GetAllUtxosAsync());
        Assert.True(coin.Verified);
        Assert.False(coin.ProvenSpent);
        Assert.Equal(new LiteBalanceBreakdown(TenBlz, 0, 0, TenBlz), await w.Service.GetBalanceBreakdownAsync());
        Assert.Equal(1, w.B.SpendCalls);                                  // three reads, one question

        w.Now += TimeSpan.FromMinutes(9);
        await w.Service.GetAllUtxosAsync();
        await w.Service.WhenVerificationIdleAsync();
        Assert.Equal(1, w.B.SpendCalls);                                  // still relied on

        w.Now += TimeSpan.FromMinutes(2);                                 // 11 minutes: being unspent does not keep
        await w.Service.GetAllUtxosAsync();
        await w.Service.WhenVerificationIdleAsync();
        Assert.Equal(2, w.B.SpendCalls);
        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task A_genuine_new_payment_is_still_announced_once_the_second_gateway_has_been_asked()
    {
        var w = await OpenPairAsync();
        using var watcher = new IncomingPaymentWatcher(w.Service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await PollAsync(watcher);

        w.A.Coin(F.Receive0, F.TxId(0), 0, TenBlz);
        await PollAsync(watcher);

        var payment = Assert.Single(events);
        Assert.Equal(F.TxId(0), payment.TxId);
        Assert.Equal(TenBlz, payment.AmountSatoshis);
        Assert.Equal(1, w.B.SpendCalls);
    }

    [Fact]
    public async Task Only_the_spent_output_of_a_transaction_is_taken_out()
    {
        var w = await OpenPairAsync((a, b) =>
        {
            a.Coin(F.Receive0, F.TxId(12), 0, 30_000_000);
            a.Coin(F.Receive0, F.TxId(12), 1, 20_000_000);
            b.Spent(F.TxId(12), 1, S.TxId(1));                            // the second output only
        });

        var coins = await w.Service.GetAllUtxosAsync();

        Assert.True(coins.Single(c => c.Utxo.OutputIndex == 0).Verified);
        Assert.True(coins.Single(c => c.Utxo.OutputIndex == 1).ProvenSpent);
        Assert.Equal(30_000_000, await w.Service.GetSpendableAsync());
    }

    // ════════════════════════ being unspent does not keep ════════════════════════

    [Fact]
    public async Task A_coin_spent_after_it_was_counted_drops_out_at_the_next_look_and_pages_are_told()
    {
        var w = await OpenPairAsync((a, _) => a.Coin(F.Receive0, F.TxId(0), 0, TenBlz));
        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());
        var told = 0;
        w.Service.VerificationChanged += () => Interlocked.Increment(ref told);

        w.B.Spent(F.TxId(0), 0, S.TxId(0));                               // spent elsewhere; gateway A keeps listing it
        w.Now += TimeSpan.FromMinutes(11);
        await w.Service.GetAllUtxosAsync();                               // this read starts the look, in the background
        await w.Service.WhenVerificationIdleAsync();

        Assert.Equal(0, await w.Service.GetSpendableAsync());
        Assert.Equal(TenBlz, (await w.Service.GetBalanceBreakdownAsync()).ProvenSpent);
        Assert.True(told >= 1);
    }

    [Fact]
    public async Task A_coin_proven_in_an_earlier_session_is_asked_about_before_it_counts_in_this_one()
    {
        var store = new InMemoryWalletStateStore();
        var first = await OpenPairAsync((a, _) => a.Coin(F.Receive0, F.TxId(0), 0, TenBlz), store);
        Assert.Equal(TenBlz, await first.Service.GetSpendableAsync());   // proven, and remembered in the store
        Assert.Contains(F.TxId(0), await store.GetVerifiedCoinsAsync());

        // Spent from another device since. The gateway goes on listing it to the next session.
        var second = await OpenPairAsync(ScriptResurrectedCoin, store);

        var coin = Assert.Single(await second.Service.GetAllUtxosAsync());   // the very first read
        Assert.False(coin.Verified);
        Assert.True(coin.ProvenSpent);
        Assert.Equal(0, await second.Service.GetSpendableAsync());
    }

    // ════════════════════════ only proof removes a coin ════════════════════════

    [Fact]
    public async Task A_claimed_spend_with_no_evidence_delays_a_coin_and_then_is_set_aside()
    {
        // The SECOND gateway is the dishonest one here: it says the coin was spent and serves
        // neither the transaction nor a proof. A claim that never comes with evidence must not
        // be able to hold a real coin back for good.
        var w = await OpenPairAsync((a, b) =>
        {
            ScriptResurrectedCoin(a, b);
            b.Withheld.Add(S.TxId(0));
        });

        for (var attempt = 1; attempt < w.Service.Spends!.MaxUnprovenClaims; attempt++)
        {
            var waiting = Assert.Single(await w.Service.GetAllUtxosAsync());
            Assert.False(waiting.Verified);                              // held while the claim might still be shown
            Assert.False(waiting.ProvenSpent);                           // and never called spent on a claim alone
            Assert.Equal(TenBlz, (await w.Service.GetBalanceBreakdownAsync()).Unverified);
            w.Now += TimeSpan.FromSeconds(61);
        }

        var coin = Assert.Single(await w.Service.GetAllUtxosAsync());
        Assert.True(coin.Verified);                                      // the claim is worth what an unproven claim is worth
        Assert.False(coin.ProvenSpent);
        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task A_claim_that_names_a_real_transaction_which_does_not_spend_the_coin_removes_nothing()
    {
        // S.Tx(0) is genuinely mined — and spends a DIFFERENT coin.
        var w = await OpenPairAsync((a, b) =>
        {
            a.Coin(F.Receive0, F.TxId(1), 0, 900_000_000);
            b.Spent(F.TxId(1), 0, S.TxId(0));
        });

        for (var attempt = 1; attempt <= w.Service.Spends!.MaxUnprovenClaims; attempt++)
        {
            Assert.False(Assert.Single(await w.Service.GetAllUtxosAsync()).ProvenSpent);
            w.Now += TimeSpan.FromSeconds(61);
        }
        Assert.Equal(900_000_000, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task A_second_gateway_that_leaves_the_coin_out_of_its_own_listing_removes_nothing()
    {
        // Gateway B lists no coins at all for the address and names no spend: an absence is not evidence.
        var w = await OpenPairAsync((a, _) => a.Coin(F.Receive0, F.TxId(0), 0, TenBlz));

        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());
    }

    // ════════════════════════ nobody to ask ════════════════════════

    [Fact]
    public async Task With_the_second_gateway_down_the_coin_counts_as_before_and_is_asked_about_again_soon()
    {
        var w = await OpenPairAsync((a, b) => { ScriptResurrectedCoin(a, b); b.Down = true; });

        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());       // the stated limit: no second opinion, no change

        w.B.Down = false;                                                // it comes back
        w.Now += TimeSpan.FromSeconds(61);                               // a minute, not ten
        await w.Service.GetAllUtxosAsync();
        await w.Service.WhenVerificationIdleAsync();
        Assert.Equal(0, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task A_second_gateway_that_does_not_serve_the_question_changes_nothing()
    {
        var w = await OpenPairAsync((a, b) => { ScriptResurrectedCoin(a, b); b.ServesSpends = false; });

        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());
        Assert.True(Assert.Single(await w.Service.GetAllUtxosAsync()).Verified);
    }

    [Fact]
    public async Task With_a_single_gateway_there_is_no_one_to_ask_and_no_request_is_made()
    {
        // OpenAsync is the single-gateway wiring (no failover list): the coin has no known lister.
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, TenBlz));

        Assert.NotNull(w.Service.Spends);
        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());
        Assert.True(Assert.Single(await w.Service.GetAllUtxosAsync()).Verified);
    }

    [Fact]
    public async Task The_gateway_that_listed_a_coin_is_never_the_one_asked_whichever_is_preferred_now()
    {
        var w = await OpenPairAsync(ScriptResurrectedCoin);
        w.A.Spent(F.TxId(1), 0, S.TxId(0));                               // (what A would say about a coin B listed)

        // A listed it → B is asked, even if the sticky preference has since moved to B.
        w.A.Down = true;
        await w.Indexer.GetAddressAsync(F.Receive0);                      // fails over: B is now preferred
        w.A.Down = false;
        var aCalls = w.A.SpendCalls;
        var bCalls = w.B.SpendCalls;
        var fromB = await w.Indexer.FindSpendsFromAnotherGatewayAsync([new LiteOutpoint(F.TxId(0), 0, GatewayA)]);
        Assert.Equal(aCalls, w.A.SpendCalls);
        Assert.Equal(bCalls + 1, w.B.SpendCalls);
        Assert.Equal(S.TxId(0), Assert.Single(fromB!).SpenderTxId);

        // B listed it → A is asked.
        var fromA = await w.Indexer.FindSpendsFromAnotherGatewayAsync([new LiteOutpoint(F.TxId(1), 0, GatewayB)]);
        Assert.Equal(aCalls + 1, w.A.SpendCalls);
        Assert.Equal(bCalls + 1, w.B.SpendCalls);
        Assert.Single(fromA!);

        // No known lister → nobody to ask, and nobody is.
        Assert.Null(await w.Indexer.FindSpendsFromAnotherGatewayAsync([new LiteOutpoint(F.TxId(0), 0)]));
        Assert.Equal(aCalls + 1, w.A.SpendCalls);
        Assert.Equal(bCalls + 1, w.B.SpendCalls);
    }

    [Fact]
    public async Task Every_listed_coin_carries_the_gateway_that_listed_it()
    {
        var w = await OpenPairAsync((a, _) => a.Coin(F.Receive0, F.TxId(0), 0, TenBlz));

        Assert.Equal(GatewayA, Assert.Single(await w.Indexer.GetUtxosAsync(F.Receive0)).Source);
        Assert.Equal(GatewayA, Assert.Single(await w.Service.GetAllUtxosAsync()).Utxo.Source);   // kept through the proof
    }

    // ════════════════════════ sending and the old-key screen ════════════════════════

    [Fact]
    public async Task A_send_passes_over_the_spent_coin_and_spends_a_real_one()
    {
        // The spent coin is the largest, so it would be picked first — and the network would refuse the send.
        var w = await OpenPairAsync((a, b) =>
        {
            ScriptResurrectedCoin(a, b);
            a.Coin(F.Receive0, F.TxId(1), 0, 900_000_000);
        });

        var sent = await w.Service.SendAsync(F.Receive1, 100_000_000);

        Assert.True(sent.Success, sent.Error);
        var tx = Transaction.Parse(w.A.BroadcastHex!, BlazecoinNetwork.Instance);
        Assert.Equal(F.TxId(1), Assert.Single(tx.Inputs).PrevOut.Hash.ToString());
        // The coin just spent: what a second gateway said of it is void, so if a gateway lists it
        // again it is asked about from scratch before it can count.
        Assert.Equal(SpendStanding.Unknown, w.Service.Spends!.StandingOf(F.TxId(1), 0));
    }

    [Fact]
    public async Task With_only_a_spent_coin_a_send_fails_before_anything_is_signed_and_says_why()
    {
        var w = await OpenPairAsync(ScriptResurrectedCoin);

        var sent = await w.Service.SendAsync(F.Receive1, 100_000_000);
        var max = await w.Service.SendMaxAsync(F.Receive1);

        Assert.False(sent.Success);
        Assert.Contains("already been spent", sent.Error);
        Assert.False(max.Success);
        Assert.Null(w.A.BroadcastHex);
        Assert.Null(w.B.BroadcastHex);
    }

    [Fact]
    public async Task The_old_key_screen_does_not_offer_a_coin_that_was_already_spent()
    {
        var w = await OpenPairAsync((a, b) =>
        {
            a.Coin(F.Foreign, F.TxId(11), 0, OldKeyRealSats);
            b.Spent(F.TxId(11), 0, S.TxId(2));
        });

        var check = await w.Service.CheckWifAsync(OldWif);
        var sweep = await w.Service.SweepWifAsync(OldWif);

        Assert.True(check.Valid);
        Assert.Equal(0, check.SpendableSatoshis);                        // was 5,000,000,000 "Recoverable now"
        Assert.Equal(OldKeyRealSats, check.UnverifiedSatoshis);
        Assert.Equal(LiteWalletService.SpentElsewhereMessage, check.UnverifiedReason);
        Assert.False(sweep.Success);
        Assert.Null(w.A.BroadcastHex);
    }

    [Fact]
    public async Task The_old_key_screen_still_offers_an_unspent_coin_with_two_gateways()
    {
        var w = await OpenPairAsync((a, _) => a.Coin(F.Foreign, F.TxId(11), 0, OldKeyRealSats));

        var check = await w.Service.CheckWifAsync(OldWif);

        Assert.Equal(OldKeyRealSats, check.SpendableSatoshis);
        Assert.Equal(0, check.UnverifiedSatoshis);
    }

    // ════════════════════════ the proof of a spend ════════════════════════

    private static LiteSpendClaim RealClaim() =>
        new(F.TxId(0), 0, S.TxId(0), S.BlockHeight, S.RawHex(S.TxId(0)), S.ProofHex(S.TxId(0)));

    private static LiteTxVerifier BareVerifier() =>
        new(new IndexerDataService(new HttpClient(new Fleet()) { BaseAddress = GatewayA }, NullLogger<IndexerDataService>.Instance));

    [Fact]
    public async Task A_spend_is_proven_by_the_mined_transaction_that_consumes_the_output()
    {
        Assert.True(await BareVerifier().VerifySpendAsync(RealClaim()));
    }

    [Fact]
    public async Task A_spend_claim_is_refused_unless_every_part_of_it_holds()
    {
        var verifier = BareVerifier();
        var real = RealClaim();

        // No evidence, or half of it.
        Assert.False(await verifier.VerifySpendAsync(real with { SpenderHex = null }));
        Assert.False(await verifier.VerifySpendAsync(real with { SpenderProofHex = null }));
        Assert.False(await verifier.VerifySpendAsync(real with { SpenderHex = "zz" }));
        // A transaction that is not the one named (another real, mined transaction's bytes).
        Assert.False(await verifier.VerifySpendAsync(real with { SpenderHex = S.RawHex(S.TxId(1)) }));
        // The named transaction, but it consumes another output: wrong coin, then wrong index.
        Assert.False(await verifier.VerifySpendAsync(real with { TxId = F.TxId(1) }));
        Assert.False(await verifier.VerifySpendAsync(real with { Vout = 1 }));
        // The right transaction with a proof of a different one (it is in a block — not shown to be this one).
        Assert.False(await verifier.VerifySpendAsync(real with { SpenderProofHex = S.ProofHex(S.TxId(1)) }));
        Assert.False(await verifier.VerifySpendAsync(real with { SpenderProofHex = F.ProofHex(F.TxId(0)) }));
        // A transaction that spends the output and was never mined: the same inputs, a different output.
        var unmined = Transaction.Parse(S.RawHex(S.TxId(0))!, BlazecoinNetwork.Instance);
        unmined.Outputs[0].Value -= Money.Satoshis(1);
        Assert.False(await verifier.VerifySpendAsync(real with
        {
            SpenderTxId = unmined.GetHash().ToString(),
            SpenderHex = unmined.ToHex(),
        }));
        // The forgery that matters: that never-mined transaction's bytes, offered under the id and
        // the proof of a transaction that WAS mined. The bytes must hash to the id the proof is for.
        Assert.False(await verifier.VerifySpendAsync(real with { SpenderHex = unmined.ToHex() }));
        Assert.False(await verifier.VerifySpendAsync(real with
        {
            SpenderTxId = S.TxId(1),
            SpenderHex = unmined.ToHex(),
            SpenderProofHex = S.ProofHex(S.TxId(1)),
        }));
    }
}

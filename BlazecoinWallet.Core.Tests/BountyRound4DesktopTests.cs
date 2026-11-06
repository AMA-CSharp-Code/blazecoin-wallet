using System.Net;
using System.Text;
using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.AutoPayout;
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Quantum;
using BlazecoinWallet.Core.Services.Send;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Bug-bounty round four (2026-11-06), the desktop wallet's two findings.
/// <list type="bullet">
/// <item>Change went back to a LEGACY address on two send paths the 2026-09-28 fix had not reached:
/// a vintage send from the Provenance page (it minted its change address "legacy" outright) and an
/// auto-payout (a plain <c>sendtoaddress</c>, whose change follows the node's default). Spending
/// part of a post-quantum coin that way put the rest back behind an ECDSA key.</item>
/// <item>The solo miner remembered its payout address with no owner, so after a wallet switch a
/// restart paid the wallet the user had just left.</item>
/// </list>
/// </summary>
public class BountyRound4DesktopTests
{
    private const string Bq = "BQGeaQmsowAjL1ZG8q1AfVH4NgBvtJKJSsjnnKEf62BMjekdU82n";
    private const string LegacyA = "BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z";
    private const string LegacyB = "BTt1goArr9xPyiJmuCVqZ3DpTVMKkBT1dL";

    private static string Ok(string resultJson) => "{\"result\":" + resultJson + ",\"error\":null,\"id\":1}";
    private const string PqWallet = """{"descriptors":[{"desc":"pkh([d34db33f/44h/0h/0h]xpub/0/*)#aaaa","active":true,"internal":false},{"desc":"pqkh(pqseed/0/*)#bbbb","active":true,"internal":false}]}""";
    private const string LegacyOnlyWallet = """{"descriptors":[{"desc":"pkh([d34db33f/44h/0h/0h]xpub/0/*)#aaaa","active":true,"internal":false}]}""";

    /// <summary>Answers each request with the next queued body and records every request.</summary>
    private sealed class Script(params (HttpStatusCode, string)[] replies) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _replies = new(replies);
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Bodies.Add(r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct));
            var (status, body) = _replies.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static BlazecoindRpcService Rpc(Script daemon)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(daemon));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns("http://127.0.0.1:55417");
        var ctx = Substitute.For<IWalletContext>();
        ctx.Active.Returns("Savings");
        return new BlazecoindRpcService(config, ctx, factory);
    }

    // ════════════════════════ vintage-send change ════════════════════════

    [Fact]
    public async Task Vintage_change_is_post_quantum_when_the_wallet_can_make_it()
    {
        var daemon = new Script((HttpStatusCode.OK, Ok(PqWallet)), (HttpStatusCode.OK, Ok($"\"{Bq}\"")));

        var change = await Rpc(daemon).GetChangeAddressAsync();

        Assert.Equal(Bq, change);                                                   // was a legacy B… address
        Assert.Equal(2, daemon.Bodies.Count);
        Assert.Contains("\"method\":\"listdescriptors\"", daemon.Bodies[0]);
        Assert.Contains("\"method\":\"getnewaddress\",\"params\":[\"vintage change\",\"pq\"]", daemon.Bodies[1]);   // was […,"legacy"], unasked
    }

    [Fact]
    public async Task Vintage_change_stays_legacy_for_a_wallet_with_no_post_quantum_keys()
    {
        var daemon = new Script((HttpStatusCode.OK, Ok(LegacyOnlyWallet)), (HttpStatusCode.OK, Ok($"\"{LegacyA}\"")));

        var change = await Rpc(daemon).GetChangeAddressAsync();

        Assert.Equal(LegacyA, change);
        Assert.Contains("\"params\":[\"vintage change\",\"legacy\"]", daemon.Bodies[1]);
    }

    [Fact]
    public async Task A_wallet_that_should_give_post_quantum_change_and_cannot_gives_none_not_legacy()
    {
        var daemon = new Script(
            (HttpStatusCode.OK, Ok(PqWallet)),
            (HttpStatusCode.InternalServerError, """{"result":null,"error":{"code":-4,"message":"Error: This wallet has no post-quantum keys"},"id":1}"""));

        var change = await Rpc(daemon).GetChangeAddressAsync();

        Assert.Null(change);                                                        // the page says it could not get a change address
        Assert.Equal(2, daemon.Bodies.Count);                                       // and no quiet third call for a legacy one
    }

    // ════════════════════════ auto-payout change ════════════════════════

    private sealed class FakeMint(bool? can) : IAddressMintRpc
    {
        public Task<PqAddressResult> GetNewPostQuantumAddressAsync(string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetNewLegacyAddressAsync(string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> CanMintPostQuantumAsync(CancellationToken ct = default) => Task.FromResult(can);
    }

    private sealed class Wallets(string? active) : IWalletContext
    {
        public string? Active { get; private set; } = active;
        public event Action? Changed;
        public void SetActive(string? name) { Active = name; Changed?.Invoke(); }
    }

    private static (AutoPayoutService Service, IWalletRpc Rpc, ISendRpc Sender) Payout(bool? canMintPq)
    {
        var rpc = Substitute.For<IWalletRpc>();
        rpc.GetBalancesAsync().Returns(Task.FromResult<WalletBalances?>(new WalletBalances { Mine = new BalanceMine { Trusted = 200m } }));
        rpc.SendFromWalletAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>()).Returns(Task.FromResult<string?>("legacy-path-txid"));
        var sender = Substitute.For<ISendRpc>();
        sender.SendAsync(Arg.Any<SendRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("send-path-txid"));
        var service = new AutoPayoutService(rpc, new FakeKeyValueStore(), new Wallets("Savings"), sender, new SendChangePolicy(new FakeMint(canMintPq)))
        {
            Threshold = 100m, Address = LegacyB, Live = true, Mode = "above", Batch = "single",
        };
        return (service, rpc, sender);
    }

    [Fact]
    public async Task An_auto_payout_keeps_its_change_post_quantum_and_stays_on_the_armed_wallet()
    {
        var (service, rpc, sender) = Payout(canMintPq: true);
        await service.StartAsync();

        await service.TickAsync(fire: true);

        await sender.Received(1).SendAsync(
            Arg.Is<SendRequest>(r => r.Change == BlazecoinAddressKind.PostQuantum && r.ChangeType == "pq"
                                     && r.Wallet == "Savings" && r.Address == LegacyB && !r.SubtractFeeFromAmount),
            Arg.Any<CancellationToken>());
        await rpc.DidNotReceive().SendFromWalletAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>());   // was this: sendtoaddress, legacy change
        await rpc.DidNotReceive().SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>());
        Assert.Contains("send-path-txid", string.Join("\n", service.Log));
    }

    [Theory]
    [InlineData(false)]     // a wallet made before post-quantum keys existed
    [InlineData(null)]      // the wallet could not be asked
    public async Task An_auto_payout_from_a_wallet_without_post_quantum_keys_sends_as_before(bool? canMintPq)
    {
        var (service, rpc, sender) = Payout(canMintPq);
        await service.StartAsync();

        await service.TickAsync(fire: true);

        await rpc.Received(1).SendFromWalletAsync("Savings", LegacyB, Arg.Any<decimal>());       // asking for "pq" there would fail the send
        await sender.DidNotReceive().SendAsync(Arg.Any<SendRequest>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════ the miner's payout address ════════════════════════

    [Fact]
    public void A_remembered_payout_address_is_reused_only_for_the_wallet_it_was_made_for()
    {
        var memory = new MinerPayoutMemory();
        Assert.Null(memory.ForStart(null, "Savings"));
        Assert.Equal(LegacyB, memory.ForStart(LegacyB, "Savings"));                 // nothing remembered: the caller's choice

        memory.Remember(LegacyA, "Savings");
        Assert.Equal(LegacyA, memory.ForStart(LegacyA, "Savings"));                 // same wallet: no new address on every restart
        Assert.Null(memory.ForStart(LegacyA, "Primary"));                           // another wallet: make a fresh one
        Assert.Equal(LegacyB, memory.ForStart(LegacyB, "Primary"));                 // an address the caller really chose stands

        Assert.False(memory.DropIfWalletChanged("Savings"));
        Assert.True(memory.DropIfWalletChanged("Primary"));
        Assert.Null(memory.Address);
        Assert.Null(memory.ForStart(LegacyA, "Primary"));                           // handed back late by a page: still not a choice
        Assert.False(memory.DropIfWalletChanged("Primary"));                        // nothing left to drop
    }

    private static BlockTemplate Template() => new()
    {
        Version = 0x20000000,
        PreviousBlockHash = new string('0', 64),
        Transactions = [],
        CoinbaseValue = 5_000_000_000,
        Target = new string('0', 63) + "1",          // never met: no block is "found" during the test
        Bits = "1d00ffff",
        Height = 100,
        CurTime = 1_794_000_000,
        MinTime = 1_793_999_000,
    };

    [Fact]
    public async Task After_a_wallet_switch_the_miner_pays_the_wallet_now_in_front_of_the_user()
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetBlockTemplateAsync().Returns(_ => Template());
        rpc.GetNewAddressAsync(Arg.Any<string>()).Returns(LegacyA, LegacyB);        // Savings' address, then Primary's
        var wallets = new Wallets("Savings");
        var miner = new MiningService(rpc, wallets: wallets);
        var options = new MiningOptions { ThreadCount = 1, ThrottlePercent = 90 };

        await miner.StartAsync(options);
        Assert.Equal(LegacyA, miner.GetStatus().PayoutAddress);
        await miner.StopAsync();
        Assert.Equal(LegacyA, miner.GetStatus().PayoutAddress);                     // same wallet: kept for the next start

        wallets.SetActive("Primary");
        Assert.Null(miner.GetStatus().PayoutAddress);                               // was still Savings' address

        // The page hands back whatever it last showed; before the fix that was Savings' address.
        await miner.StartAsync(new MiningOptions { ThreadCount = 1, ThrottlePercent = 90, PayoutAddress = LegacyA });
        try { Assert.Equal(LegacyB, miner.GetStatus().PayoutAddress); }             // a fresh one from Primary
        finally { await miner.StopAsync(); }
    }

    [Fact]
    public async Task A_wallet_switch_while_mining_drops_the_address_when_the_miner_stops()
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetBlockTemplateAsync().Returns(_ => Template());
        rpc.GetNewAddressAsync(Arg.Any<string>()).Returns(LegacyA);
        var wallets = new Wallets("Savings");
        var miner = new MiningService(rpc, wallets: wallets);

        await miner.StartAsync(new MiningOptions { ThreadCount = 1, ThrottlePercent = 90 });
        wallets.SetActive("Primary");
        Assert.Equal(LegacyA, miner.GetStatus().PayoutAddress);                     // still mining: still paying where it was started, and saying so

        await miner.StopAsync();
        Assert.Null(miner.GetStatus().PayoutAddress);
    }
}

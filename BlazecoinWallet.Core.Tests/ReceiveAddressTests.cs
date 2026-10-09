using System.Net;
using System.Text;
using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Quantum;
using BlazecoinWallet.Core.Services.Receive;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Post-quantum receive addresses (2026-09-28): the desktop wallet could only mint legacy addresses,
/// so every coin received landed on an ECDSA key. Pins: the BQ… → P2PQH script against the first
/// post-quantum coinbase on MAINNET; the Receive page's rules (post-quantum by default, legacy only
/// for a wallet without PQ keys or by choice, never one kind substituted for the other); the solo
/// miner's payout (BQ… unless the wallet cannot); and the capability probe over listdescriptors.
/// </summary>
public class ReceiveAddressTests
{
    // Mainnet block 4,250,880, the first post-quantum coinbase: its single pqkh output.
    private const string FirstPqCoinbaseAddress = "BQHcY1yedCSGvq8KyXTAY4GW3wwaabje8hu72ERZrWpCYZcu8Qrn";
    private const string FirstPqCoinbaseScriptHex = "207f0fa890ea79c11fc72806aee9afb93ee41a4c63a47c917b74b32f4bd6c5b5f5ba";
    private const string BqSpec = "BQGeaQmsowAjL1ZG8q1AfVH4NgBvtJKJSsjnnKEf62BMjekdU82n";   // pqkh = 0x00…00
    private const string Legacy = "BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z";                       // hash160 = 0x00…00

    // ════════════════════════════════════════════════════════════ protocol: kinds and scripts

    [Fact]
    public void A_BQ_address_pays_to_the_exact_script_mainnet_used_for_the_first_PQ_coinbase()
        => Assert.Equal(FirstPqCoinbaseScriptHex, BitcoinProtocol.BytesToHex(BitcoinProtocol.AddressToP2PQH(FirstPqCoinbaseAddress)));

    [Fact]
    public void The_P2PQH_script_is_push32_pqkh_OP_CHECKPQSIG()
    {
        var s = BitcoinProtocol.AddressToP2PQH(BqSpec);
        Assert.Equal(34, s.Length);
        Assert.Equal(0x20, s[0]);
        Assert.All(s[1..33], b => Assert.Equal(0, b));
        Assert.Equal(BitcoinProtocol.OpCheckPqSig, s[33]);
    }

    [Fact]
    public void AddressToScriptPubKey_picks_the_script_by_kind()
    {
        Assert.Equal(FirstPqCoinbaseScriptHex, BitcoinProtocol.BytesToHex(BitcoinProtocol.AddressToScriptPubKey(FirstPqCoinbaseAddress)));
        Assert.Equal(BitcoinProtocol.BytesToHex(BitcoinProtocol.AddressToP2PKH(Legacy)), BitcoinProtocol.BytesToHex(BitcoinProtocol.AddressToScriptPubKey("  " + Legacy)));
        Assert.Throws<FormatException>(() => BitcoinProtocol.AddressToScriptPubKey("blz1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"));
        Assert.Throws<FormatException>(() => BitcoinProtocol.AddressToScriptPubKey(BqSpec[..^1] + "m"));
    }

    [Fact]
    public void AddressToP2PQH_refuses_a_legacy_address()
        => Assert.Throws<FormatException>(() => BitcoinProtocol.AddressToP2PQH(Legacy));

    [Theory]
    [InlineData(FirstPqCoinbaseAddress, BlazecoinAddressKind.PostQuantum)]
    [InlineData(BqSpec, BlazecoinAddressKind.PostQuantum)]
    [InlineData(Legacy, BlazecoinAddressKind.Legacy)]
    [InlineData("BnKVYKojUkgynKnD43ZHzSfGTw3apinDMM", BlazecoinAddressKind.Legacy)]
    public void KindOf_classifies_real_addresses(string address, BlazecoinAddressKind kind)
        => Assert.Equal(kind, BitcoinProtocol.KindOf(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31y")]    // legacy, bad checksum
    [InlineData("1BoatSLRHtKNngkdXEeobR76b53LETtpyT")]    // a Bitcoin address
    public void KindOf_is_null_for_anything_else(string? address)
        => Assert.Null(BitcoinProtocol.KindOf(address));

    // ════════════════════════════════════════════════════════════ receive rules

    private sealed class FakeMint : IAddressMintRpc
    {
        public bool? Can { get; set; } = true;
        public Exception? CanThrows { get; set; }
        public PqAddressResult Pq { get; set; } = PqAddressResult.Ok(BqSpec);
        public Exception? PqThrows { get; set; }
        public string? LegacyReply { get; set; } = Legacy;
        public Exception? LegacyThrows { get; set; }
        public List<string> PqLabels { get; } = new();
        public List<string> LegacyLabels { get; } = new();

        public Task<PqAddressResult> GetNewPostQuantumAddressAsync(string label, CancellationToken ct = default)
        { PqLabels.Add(label); if (PqThrows is not null) throw PqThrows; return Task.FromResult(Pq); }
        public Task<string?> GetNewLegacyAddressAsync(string label, CancellationToken ct = default)
        { LegacyLabels.Add(label); if (LegacyThrows is not null) throw LegacyThrows; return Task.FromResult(LegacyReply); }
        public Task<bool?> CanMintPostQuantumAsync(CancellationToken ct = default)
        { if (CanThrows is not null) throw CanThrows; return Task.FromResult(Can); }
    }

    [Theory]
    [InlineData(true, BlazecoinAddressKind.PostQuantum)]
    [InlineData(false, BlazecoinAddressKind.Legacy)]
    [InlineData(null, BlazecoinAddressKind.PostQuantum)]   // unknown: offer PQ, the mint will explain
    public async Task The_default_is_post_quantum_unless_the_wallet_has_no_PQ_chain(bool? can, BlazecoinAddressKind expected)
        => Assert.Equal(expected, await new ReceiveAddressService(new FakeMint { Can = can }).DefaultKindAsync());

    [Fact]
    public async Task A_failing_probe_still_defaults_to_post_quantum()
        => Assert.Equal(BlazecoinAddressKind.PostQuantum, await new ReceiveAddressService(new FakeMint { CanThrows = new TimeoutException() }).DefaultKindAsync());

    [Fact]
    public async Task A_post_quantum_request_returns_a_BQ_address_with_the_trimmed_label()
    {
        var mint = new FakeMint();

        var r = await new ReceiveAddressService(mint).NewAsync("  savings  ", BlazecoinAddressKind.PostQuantum);

        Assert.True(r.Succeeded);
        Assert.Equal(BqSpec, r.Address);
        Assert.Equal(BlazecoinAddressKind.PostQuantum, r.Kind);
        Assert.Equal(new[] { "savings" }, mint.PqLabels);
        Assert.Empty(mint.LegacyLabels);
    }

    [Theory]
    [InlineData(PqAddressOutcome.WalletHasNoPqKeys, true)]
    [InlineData(PqAddressOutcome.LegacyWallet, true)]
    [InlineData(PqAddressOutcome.DaemonTooOld, true)]
    [InlineData(PqAddressOutcome.Failed, false)]
    public async Task A_post_quantum_request_that_cannot_be_met_never_falls_back_to_legacy(PqAddressOutcome outcome, bool cannot)
    {
        var mint = new FakeMint { Pq = PqAddressResult.Fail(outcome, "boom") };

        var r = await new ReceiveAddressService(mint).NewAsync("x", BlazecoinAddressKind.PostQuantum);

        Assert.False(r.Succeeded);
        Assert.Null(r.Address);
        Assert.Equal(cannot, r.WalletCannotMintPostQuantum);
        Assert.False(string.IsNullOrWhiteSpace(r.Problem));
        Assert.Empty(mint.LegacyLabels);                  // the substitution that caused the 09-28 trap
    }

    [Fact]
    public async Task A_BQ_request_answered_with_a_legacy_address_is_refused()
    {
        var mint = new FakeMint { Pq = new PqAddressResult(PqAddressOutcome.Ok, Legacy, null) };
        var r = await new ReceiveAddressService(mint).NewAsync("x", BlazecoinAddressKind.PostQuantum);
        Assert.False(r.Succeeded);
        Assert.Contains("not post-quantum", r.Problem!);
    }

    [Fact]
    public async Task A_BQ_mint_that_throws_is_a_problem_not_a_crash()
    {
        var r = await new ReceiveAddressService(new FakeMint { PqThrows = new HttpRequestException("down") }).NewAsync("x", BlazecoinAddressKind.PostQuantum);
        Assert.False(r.Succeeded);
        Assert.Contains("down", r.Problem!);
        Assert.False(r.WalletCannotMintPostQuantum);
    }

    [Fact]
    public async Task A_legacy_request_returns_a_legacy_address_and_never_touches_the_PQ_chain()
    {
        var mint = new FakeMint();

        var r = await new ReceiveAddressService(mint).NewAsync("old sender", BlazecoinAddressKind.Legacy);

        Assert.True(r.Succeeded);
        Assert.Equal(Legacy, r.Address);
        Assert.Equal(new[] { "old sender" }, mint.LegacyLabels);
        Assert.Empty(mint.PqLabels);
    }

    [Theory]
    [InlineData(BqSpec)]
    [InlineData(null)]
    [InlineData("junk")]
    public async Task A_legacy_request_answered_with_anything_but_a_legacy_address_is_refused(string? reply)
    {
        var r = await new ReceiveAddressService(new FakeMint { LegacyReply = reply }).NewAsync("x", BlazecoinAddressKind.Legacy);
        Assert.False(r.Succeeded);
        Assert.Contains("not a legacy", r.Problem!);
    }

    [Fact]
    public async Task A_legacy_mint_that_throws_is_a_problem()
    {
        var r = await new ReceiveAddressService(new FakeMint { LegacyThrows = new RpcException(-12, "Keypool ran out") }).NewAsync("x", BlazecoinAddressKind.Legacy);
        Assert.Contains("Keypool ran out", r.Problem!);
    }

    [Fact]
    public void The_sender_notes_say_who_can_pay()
    {
        Assert.Contains("V1.5", ReceiveAddressService.SenderNote(BlazecoinAddressKind.PostQuantum));
        Assert.Contains("2.0.5", ReceiveAddressService.SenderNote(BlazecoinAddressKind.PostQuantum));
        Assert.Contains("Any Blazecoin wallet", ReceiveAddressService.SenderNote(BlazecoinAddressKind.Legacy));
    }

    [Fact]
    public void The_service_needs_a_minter() => Assert.Throws<ArgumentNullException>(() => new ReceiveAddressService(null!));

    // ════════════════════════════════════════════════════════════ solo-mining payout

    [Fact]
    public async Task An_unconfigured_payout_is_a_fresh_BQ_address()
    {
        var rpc = Substitute.For<IMiningRpc>();
        var mint = new FakeMint();

        var c = await new PayoutAddressResolver(rpc, mint).ResolveAsync(null);

        Assert.Equal(BqSpec, c.Address);
        Assert.Equal(BlazecoinAddressKind.PostQuantum, c.Kind);
        Assert.Null(c.Note);
        Assert.Equal(new[] { PayoutAddressResolver.Label }, mint.PqLabels);
        await rpc.DidNotReceive().GetNewAddressAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(PqAddressOutcome.WalletHasNoPqKeys, "created before")]
    [InlineData(PqAddressOutcome.DaemonTooOld, "2.1.0")]
    [InlineData(PqAddressOutcome.Failed, "boom")]
    public async Task A_wallet_that_cannot_mint_BQ_mines_to_legacy_and_says_why(PqAddressOutcome outcome, string why)
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetNewAddressAsync(Arg.Any<string>()).Returns(Legacy);

        var c = await new PayoutAddressResolver(rpc, new FakeMint { Pq = PqAddressResult.Fail(outcome, "boom") }).ResolveAsync("");

        Assert.Equal(Legacy, c.Address);
        Assert.Equal(BlazecoinAddressKind.Legacy, c.Kind);
        Assert.StartsWith("Paying to a legacy address", c.Note);
        Assert.Contains(why, c.Note!);
    }

    [Fact]
    public async Task A_BQ_mint_that_throws_falls_back_to_legacy_so_mining_can_start()
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetNewAddressAsync(Arg.Any<string>()).Returns(Legacy);
        var c = await new PayoutAddressResolver(rpc, new FakeMint { PqThrows = new TimeoutException("slow") }).ResolveAsync(null);
        Assert.Equal(Legacy, c.Address);
        Assert.Contains("slow", c.Note!);
    }

    [Fact]
    public async Task Without_a_minter_the_payout_is_legacy_as_before()
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetNewAddressAsync(Arg.Any<string>()).Returns(Legacy);
        var c = await new PayoutAddressResolver(rpc).ResolveAsync(null);
        Assert.Equal(Legacy, c.Address);
        Assert.Null(c.Note);
    }

    [Fact]
    public async Task A_configured_BQ_payout_is_used_as_given_with_no_RPC()
    {
        var rpc = Substitute.For<IMiningRpc>();
        var mint = new FakeMint();

        var c = await new PayoutAddressResolver(rpc, mint).ResolveAsync(" " + FirstPqCoinbaseAddress + " ");

        Assert.Equal(FirstPqCoinbaseAddress, c.Address);
        Assert.Null(c.Note);
        Assert.Empty(mint.PqLabels);
        await rpc.DidNotReceive().GetNewAddressAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task A_configured_legacy_payout_is_used_but_flagged()
    {
        var c = await new PayoutAddressResolver(Substitute.For<IMiningRpc>(), new FakeMint()).ResolveAsync(Legacy);
        Assert.Equal(BlazecoinAddressKind.Legacy, c.Kind);
        Assert.Contains("legacy", c.Note!);
    }

    [Fact]
    public async Task A_configured_payout_that_is_not_an_address_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new PayoutAddressResolver(Substitute.For<IMiningRpc>()).ResolveAsync("not-an-address"));
        Assert.Contains("Invalid payout address", ex.Message);
    }

    [Theory]
    [InlineData(null, "no address")]
    [InlineData(BqSpec, "not a legacy")]
    public async Task A_bad_legacy_reply_stops_mining_before_it_starts(string? reply, string expected)
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetNewAddressAsync(Arg.Any<string>()).Returns(reply);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new PayoutAddressResolver(rpc).ResolveAsync(null));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task The_miner_accepts_a_BQ_payout_and_gets_as_far_as_the_block_template()
    {
        // A null template is the fail-fast path AFTER the payout script was built, with no worker started.
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetBlockTemplateAsync().Returns((BlockTemplate?)null);
        var svc = new MiningService(rpc, mint: new FakeMint());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(new MiningOptions { ThreadCount = 1 }));

        Assert.Contains("getblocktemplate", ex.Message);
        await rpc.DidNotReceive().GetNewAddressAsync(Arg.Any<string>());
        Assert.False(svc.GetStatus().IsRunning);
    }

    [Fact]
    public async Task The_miner_accepts_a_configured_BQ_payout()
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetBlockTemplateAsync().Returns((BlockTemplate?)null);
        var svc = new MiningService(rpc);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StartAsync(new MiningOptions { PayoutAddress = FirstPqCoinbaseAddress, ThreadCount = 1 }));
        Assert.Contains("getblocktemplate", ex.Message);    // past address validation: BQ accepted
    }

    // ════════════════════════════════════════════════════════════ capability probe (listdescriptors)

    private static BlazecoindRpcService Svc(HttpStatusCode status, string body)
    {
        var handler = new Stub(status, body);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns("http://127.0.0.1:55415");
        var ctx = Substitute.For<IWalletContext>();
        ctx.Active.Returns("Primary");
        return new BlazecoindRpcService(config, ctx, factory);
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly HttpStatusCode _s; private readonly string _b;
        public Stub(HttpStatusCode s, string b) { _s = s; _b = b; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_s) { Content = new StringContent(_b, Encoding.UTF8, "application/json") });
    }

    private static string Descs(params (string desc, bool active, bool @internal)[] ds)
        => "{\"result\":{\"wallet_name\":\"Primary\",\"descriptors\":[" +
           string.Join(",", ds.Select(d => $"{{\"desc\":\"{d.desc}\",\"active\":{(d.active ? "true" : "false")},\"internal\":{(d.@internal ? "true" : "false")}}}")) +
           "]},\"error\":null,\"id\":1}";

    [Fact]
    public async Task A_wallet_with_an_active_receive_pqkh_chain_can_mint_BQ()
        => Assert.True(await Svc(HttpStatusCode.OK, Descs(("pkh([d34db33f/44h/0h/0h]xpub/0/*)#abc", true, false), ("pqkh(mldsa44:1234/*)#def", true, false))).CanMintPostQuantumAsync());

    [Fact]
    public async Task A_wallet_made_before_2_1_0_cannot()
        => Assert.False(await Svc(HttpStatusCode.OK, Descs(("pkh(xpub/0/*)#abc", true, false), ("pkh(xpub/1/*)#abd", true, true))).CanMintPostQuantumAsync());

    [Fact]
    public async Task Only_a_change_or_an_inactive_pqkh_chain_does_not_count()
        => Assert.False(await Svc(HttpStatusCode.OK, Descs(("pqkh(mldsa44:aa/*)#x", true, true), ("pqkh(mldsa44:bb/*)#y", false, false))).CanMintPostQuantumAsync());

    [Fact]
    public async Task A_legacy_wallet_cannot()
        => Assert.False(await Svc(HttpStatusCode.InternalServerError,
            """{"result":null,"error":{"code":-4,"message":"listdescriptors is not available for non-descriptor wallets"},"id":1}""").CanMintPostQuantumAsync());

    [Fact]
    public async Task Any_other_failure_is_unknown()
        => Assert.Null(await Svc(HttpStatusCode.InternalServerError,
            """{"result":null,"error":{"code":-18,"message":"Requested wallet does not exist or is not loaded"},"id":1}""").CanMintPostQuantumAsync());
}

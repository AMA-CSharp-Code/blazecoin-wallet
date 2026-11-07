using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// What a gateway may and may not decide about a send (bug-bounty reports 2026-11-06).
/// <list type="bullet">
/// <item>The receipt txid and the session's sent-set are the wallet's OWN — a relay's txid string
/// is never used.</item>
/// <item>One gateway's rejection is not the network's: a second gateway is asked, and the send
/// goes through when that one takes it.</item>
/// <item>One gateway's "accepted" / "already have it" does not end the matter either: the same
/// transaction is given to a second gateway, and to network nodes directly when there is none.</item>
/// <item>None of this may turn an honest rejection into a reported success.</item>
/// </list>
/// </summary>
public class BroadcastTrustTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static readonly string Fake = new('f', 64);

    private static string RealTxHex(out string txId)
    {
        var tx = Transaction.Create(BlazecoinNetwork.Instance);
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256(1), 0)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000),
            new Script(LiteTxVerifier.ScriptForAddress(LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(0))!)));
        txId = tx.GetHash().ToString();
        return tx.ToHex();
    }

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    private sealed class CountingP2P : IP2PBroadcaster
    {
        public int Attempts;
        public string? LastHex;
        public string? AcceptingNode = "node.test:5513";
        public Task<string?> TryBroadcastAsync(string rawTxHex, IEnumerable<string>? nodeEndpoints = null,
            TimeSpan? perNodeTimeout = null, CancellationToken ct = default)
        {
            Attempts++;
            LastHex = rawTxHex;
            return Task.FromResult(AcceptingNode);
        }
    }

    /// <summary>Several gateways behind the real failover handler; each host answers the
    /// broadcast as scripted (or is down), and every host serves the same coins.</summary>
    private sealed class GatewayFleet : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<(HttpStatusCode, string)>> Broadcast = new();
        public readonly List<string> BroadcastCalls = [];
        public readonly List<string> ReadCalls = [];
        public readonly List<string> BroadcastHexSeen = [];
        public string UtxosJson = "[]";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            if (!Broadcast.TryGetValue(host, out var answer))
                throw new HttpRequestException($"no route to {host}");

            if (path.EndsWith("/api/tx/broadcast"))
            {
                BroadcastCalls.Add(host);
                var body = await request.Content!.ReadAsStringAsync(ct);
                BroadcastHexSeen.Add(JsonDocument.Parse(body).RootElement.GetProperty("hex").GetString()!);
                var (status, json) = answer();
                return Json(status, json);
            }
            ReadCalls.Add(host);
            if (path.EndsWith("/utxos")) return Json(HttpStatusCode.OK, UtxosJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public static Func<(HttpStatusCode, string)> Accepts(string txId) => () => (HttpStatusCode.OK, $$"""{"txId":"{{txId}}"}""");
        public static Func<(HttpStatusCode, string)> Rejects(string reason) => () => (HttpStatusCode.UnprocessableEntity, $$"""{"error":"{{reason}}"}""");
        public static Func<(HttpStatusCode, string)> Status(HttpStatusCode code, string reason = "node down") => () => (code, $$"""{"error":"{{reason}}"}""");
    }

    private static IndexerDataService Service(GatewayFleet fleet, params string[] gateways)
    {
        var failover = new GatewayFailoverHandler(gateways.Length > 0 ? gateways : ["https://a.test/", "https://b.test/"], fleet);
        return new IndexerDataService(
            new HttpClient(failover) { BaseAddress = failover.Primary }, NullLogger<IndexerDataService>.Instance);
    }

    // ── the txid is the wallet's own ──

    [Fact]
    public async Task The_relay_result_carries_the_hash_of_the_posted_transaction_not_the_gateways_string()
    {
        var hex = RealTxHex(out var trueTxId);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Accepts(Fake);
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(Fake);

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.True(result.Success);
        Assert.Equal(trueTxId, result.TxId);
        Assert.NotEqual(Fake, result.TxId);
    }

    [Fact]
    public async Task A_send_reports_and_remembers_its_own_txid_whatever_the_relay_answers()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var data = new StubData { BroadcastTxId = _ => Fake };
        data.Coins[wallet.GetReceiveAddress(0)] = [new LiteChainUtxo(new string('7', 64), 0, 1_000_000_000, 100, 4_200_000, false)];
        var service = new LiteWalletService(new MemoryVault(), data);
        await service.RestoreWalletAsync(TestMnemonic);

        var sent = await service.SendAsync(wallet.GetReceiveAddress(1), 100_000_000);

        var trueTxId = LiteTxId.OfHex(data.CapturedBroadcastHex)!;
        Assert.True(sent.Success, sent.Error);
        Assert.Equal(trueTxId, sent.TxId);
        Assert.Contains(trueTxId, service.SessionSentTxIds);
        Assert.DoesNotContain(Fake, service.SessionSentTxIds);
    }

    // ── a rejection needs two gateways ──

    [Fact]
    public async Task One_gateways_forged_rejection_does_not_stop_a_send_the_next_gateway_accepts()
    {
        var hex = RealTxHex(out var trueTxId);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Rejects("bad-txns-in-belowout");   // invented
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(trueTxId);

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.True(result.Success, result.Error);
        Assert.Equal(trueTxId, result.TxId);
        Assert.Equal(new[] { "a.test", "b.test" }, fleet.BroadcastCalls);
        Assert.All(fleet.BroadcastHexSeen, seen => Assert.Equal(hex, seen));   // the same transaction, intact
    }

    [Fact]
    public async Task A_rejection_two_gateways_agree_on_is_the_networks_and_is_reported_plainly()
    {
        var hex = RealTxHex(out _);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Rejects("bad-txns-inputs-missingorspent");
        fleet.Broadcast["b.test"] = GatewayFleet.Rejects("bad-txns-inputs-missingorspent");

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.False(result.Success);
        Assert.Equal(BroadcastFailureKind.Rejected, result.Failure);
        Assert.Equal("bad-txns-inputs-missingorspent", result.Error);
        Assert.False(result.Uncorroborated);
    }

    [Fact]
    public async Task A_rejection_nobody_else_could_confirm_is_reported_as_one_gateways_word()
    {
        var hex = RealTxHex(out _);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Rejects("bad-txns-in-belowout");   // b.test is down

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.False(result.Success);
        Assert.Equal(BroadcastFailureKind.Rejected, result.Failure);
        Assert.True(result.Uncorroborated);
        Assert.StartsWith("bad-txns-in-belowout", result.Error);
        Assert.Contains("one gateway only", result.Error);
        Assert.Contains("switch gateway", result.Error);
    }

    // ── an acceptance is delivered twice ──

    [Fact]
    public async Task An_accepted_transaction_is_given_to_a_second_gateway_as_well()
    {
        var hex = RealTxHex(out var trueTxId);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Accepts(trueTxId);
        fleet.Broadcast["b.test"] = GatewayFleet.Rejects("txn-already-in-mempool");   // it already arrived

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.True(result.Success);
        Assert.False(result.Uncorroborated);
        Assert.Equal(new[] { "a.test", "b.test" }, fleet.BroadcastCalls);
    }

    [Fact]
    public async Task A_forged_already_known_answer_cannot_swallow_a_transaction_the_next_gateway_relays()
    {
        // The worse neighbour of the forged rejection: a gateway that claims the network already
        // has the transaction (so the wallet reports success) while relaying nothing.
        var hex = RealTxHex(out var trueTxId);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Rejects("txn-already-known");       // a lie
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(trueTxId);                  // the real delivery

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.True(result.Success);
        Assert.Equal(trueTxId, result.TxId);
        Assert.Contains("b.test", fleet.BroadcastCalls);
        Assert.Contains(hex, fleet.BroadcastHexSeen);
    }

    [Theory]
    [InlineData(true)]    // "accepted"
    [InlineData(false)]   // "already have it"
    public async Task A_success_only_one_gateway_vouches_for_is_flagged_and_also_goes_to_network_nodes(bool accepted)
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var fleet = new GatewayFleet
        {
            UtxosJson = $$"""[{"txId":"{{new string('7', 64)}}","outputIndex":0,"amount":1000000000,"confirmations":100,"blockHeight":4200000,"isCoinbase":false}]""",
        };
        fleet.Broadcast["a.test"] = accepted ? GatewayFleet.Accepts(Fake) : GatewayFleet.Rejects("txn-already-known");
        var p2p = new CountingP2P();
        var data = Service(fleet, "https://a.test/");          // a single gateway: nobody to ask
        var service = new LiteWalletService(new MemoryVault(), data, p2pFallback: p2p, p2pNodes: ["node.test:5513"]);
        await service.RestoreWalletAsync(TestMnemonic);

        var sent = await service.SendAsync(wallet.GetReceiveAddress(1), 100_000_000);

        Assert.True(sent.Success, sent.Error);
        Assert.Equal(1, p2p.Attempts);                                     // handed to nodes directly as well
        Assert.Equal(fleet.BroadcastHexSeen.Single(), p2p.LastHex);        // the very same transaction
        Assert.Equal(LiteTxId.OfHex(p2p.LastHex), sent.TxId);
    }

    [Fact]
    public async Task A_success_two_gateways_back_needs_no_extra_push()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var fleet = new GatewayFleet
        {
            UtxosJson = $$"""[{"txId":"{{new string('7', 64)}}","outputIndex":0,"amount":1000000000,"confirmations":100,"blockHeight":4200000,"isCoinbase":false}]""",
        };
        fleet.Broadcast["a.test"] = GatewayFleet.Accepts(Fake);
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(Fake);
        var p2p = new CountingP2P();
        var service = new LiteWalletService(new MemoryVault(), Service(fleet), p2pFallback: p2p, p2pNodes: ["node.test:5513"]);
        await service.RestoreWalletAsync(TestMnemonic);

        var sent = await service.SendAsync(wallet.GetReceiveAddress(1), 100_000_000);

        Assert.True(sent.Success, sent.Error);
        Assert.Equal(0, p2p.Attempts);
    }

    // ── never a false success ──

    [Fact]
    public async Task A_rejection_is_never_turned_into_a_success_by_the_peer_to_peer_push()
    {
        // The reporter's suggested fix (push over P2P whenever a gateway rejects) would report an
        // honestly rejected transaction as sent: the push is fire-and-forget. A rejection — even
        // one nobody could confirm — stays a failure, and nothing is pushed.
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var fleet = new GatewayFleet
        {
            UtxosJson = $$"""[{"txId":"{{new string('7', 64)}}","outputIndex":0,"amount":1000000000,"confirmations":100,"blockHeight":4200000,"isCoinbase":false}]""",
        };
        fleet.Broadcast["a.test"] = GatewayFleet.Rejects("bad-txns-inputs-missingorspent");
        var p2p = new CountingP2P();
        var service = new LiteWalletService(new MemoryVault(), Service(fleet, "https://a.test/"), p2pFallback: p2p, p2pNodes: ["node.test:5513"]);
        await service.RestoreWalletAsync(TestMnemonic);

        var sent = await service.SendAsync(wallet.GetReceiveAddress(1), 100_000_000);

        Assert.False(sent.Success);
        Assert.StartsWith("bad-txns-inputs-missingorspent", sent.Error);
        Assert.Equal(0, p2p.Attempts);
        Assert.Empty(service.SessionSentTxIds);
    }

    [Fact]
    public async Task The_whole_send_survives_a_gateway_that_forges_rejections()
    {
        // The report's scenario end to end: gateway A serves the coin and then "rejects" every
        // broadcast with an invented daemon reason; gateway B is honest.
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var fleet = new GatewayFleet
        {
            UtxosJson = $$"""[{"txId":"{{new string('7', 64)}}","outputIndex":0,"amount":1000000000,"confirmations":100,"blockHeight":4200000,"isCoinbase":false}]""",
        };
        fleet.Broadcast["a.test"] = GatewayFleet.Rejects("bad-txns-in-belowout");
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(Fake);
        var p2p = new CountingP2P();
        var service = new LiteWalletService(new MemoryVault(), Service(fleet), p2pFallback: p2p, p2pNodes: ["node.test:5513"]);
        await service.RestoreWalletAsync(TestMnemonic);

        var sent = await service.SendAsync(wallet.GetReceiveAddress(1), 100_000_000);

        Assert.True(sent.Success, sent.Error);
        Assert.Equal(LiteTxId.OfHex(fleet.BroadcastHexSeen[0]), sent.TxId);
        Assert.Equal(new[] { "a.test", "b.test" }, fleet.BroadcastCalls);
    }

    // ── answers that judge nothing ──

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_answer_that_judged_nothing_moves_on_to_the_next_gateway(HttpStatusCode status)
    {
        var hex = RealTxHex(out var trueTxId);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Status(status);
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(trueTxId);

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.True(result.Success, result.Error);
        Assert.Equal(trueTxId, result.TxId);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task When_no_gateway_judged_the_transaction_the_failure_is_an_outage_not_a_rejection(HttpStatusCode status)
    {
        // A 429 or a 500 used to be classified "the network rejected the transaction", which
        // ended the send and kept the peer-to-peer fallback from running.
        var hex = RealTxHex(out _);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Status(status);
        fleet.Broadcast["b.test"] = GatewayFleet.Status(status);

        var result = await Service(fleet).BroadcastAsync(hex);

        Assert.False(result.Success);
        Assert.Equal(BroadcastFailureKind.Unreachable, result.Failure);
    }

    [Fact]
    public async Task Every_gateway_down_is_still_an_outage()
    {
        var hex = RealTxHex(out _);
        var result = await Service(new GatewayFleet()).BroadcastAsync(hex);

        Assert.False(result.Success);
        Assert.Equal(BroadcastFailureKind.Unreachable, result.Failure);
    }

    // ── routing ──

    [Fact]
    public async Task Asking_for_a_second_opinion_does_not_move_ordinary_traffic_to_the_other_gateway()
    {
        var hex = RealTxHex(out var trueTxId);
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Accepts(trueTxId);
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(trueTxId);
        var service = Service(fleet);

        await service.BroadcastAsync(hex);
        await service.GetUtxosAsync("Bxyz");

        Assert.Equal(new[] { "a.test" }, fleet.ReadCalls);   // reads stay with the preferred gateway
    }

    [Fact]
    public async Task The_failover_handler_skips_excluded_gateways_and_names_who_answered()
    {
        var fleet = new GatewayFleet();
        fleet.Broadcast["a.test"] = GatewayFleet.Accepts(Fake);
        fleet.Broadcast["b.test"] = GatewayFleet.Accepts(Fake);
        var failover = new GatewayFailoverHandler(["https://a.test/", "https://b.test/"], fleet);
        using var client = new HttpClient(failover) { BaseAddress = failover.Primary };

        using var plain = new HttpRequestMessage(HttpMethod.Get, "indexer/api/address/Bx/utxos");
        (await client.SendAsync(plain)).Dispose();
        Assert.True(plain.Options.TryGetValue(GatewayRouting.AnsweredBy, out var who));
        Assert.Equal("a.test", who!.Host);

        using var steered = new HttpRequestMessage(HttpMethod.Get, "indexer/api/address/Bx/utxos");
        steered.Options.Set(GatewayRouting.Exclude, [who]);
        (await client.SendAsync(steered)).Dispose();
        Assert.True(steered.Options.TryGetValue(GatewayRouting.AnsweredBy, out var second));
        Assert.Equal("b.test", second!.Host);

        using var nobody = new HttpRequestMessage(HttpMethod.Get, "indexer/api/address/Bx/utxos");
        nobody.Options.Set(GatewayRouting.Exclude, [new Uri("https://a.test/"), new Uri("https://b.test/")]);
        await Assert.ThrowsAsync<NoOtherGatewayException>(() => client.SendAsync(nobody)); // still an HttpRequestException, now a named one
    }

    /// <summary>Stub chain data + relay: one scripted coin set, captures the broadcast, answers
    /// with a caller-chosen txid.</summary>
    private sealed class StubData : ILiteWalletData
    {
        public readonly Dictionary<string, List<LiteChainUtxo>> Coins = [];
        public Func<string, string> BroadcastTxId = _ => new string('b', 64);
        public string? CapturedBroadcastHex;
        public bool SupportsHistory => false;
        public bool SupportsChainVerification => false;
        public Task<LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default) =>
            Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiteChainUtxo>>(Coins.TryGetValue(address, out var c) ? c : []);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default)
        {
            CapturedBroadcastHex = rawTxHex;
            return Task.FromResult(LiteBroadcastResult.Ok(BroadcastTxId(rawTxHex)));
        }
    }
}

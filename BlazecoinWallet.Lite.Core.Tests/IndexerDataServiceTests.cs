using System.Net;
using System.Text;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Direct coverage of IndexerDataService's gateway JSON mapping — the read paths a lying or
/// flaky gateway can feed. The verifier and pipeline tests use fakes; here the REAL service
/// parses scripted gateway responses (address/utxos/history/raw-tx/proof/broadcast), and the
/// never-throw contract is exercised on malformed/error responses.
/// </summary>
public class IndexerDataServiceTests
{
    private sealed class ScriptedGateway : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode, string)> ByPathSuffix = new();
        public bool Dead;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Dead) throw new HttpRequestException("connection refused");
            var path = request.RequestUri!.AbsolutePath;
            foreach (var (suffix, resp) in ByPathSuffix)
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(resp.Item1)
                    { Content = new StringContent(resp.Item2, Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static IndexerDataService Build(ScriptedGateway gw)
    {
        var http = new HttpClient(gw) { BaseAddress = new Uri("https://gw.test/") };
        return new IndexerDataService(http, NullLogger<IndexerDataService>.Instance);
    }

    [Fact]
    public void Indexer_mode_supports_chain_verification()
        => Assert.True(Build(new ScriptedGateway()).SupportsChainVerification);

    [Fact]
    public async Task Utxos_map_including_confirmations_and_coinbase_flag()
    {
        var gw = new ScriptedGateway();
        gw.ByPathSuffix["/utxos"] = (HttpStatusCode.OK,
            """[{"txId":"aa","outputIndex":1,"amount":500,"confirmations":42,"blockHeight":900,"isCoinbase":true}]""");

        var utxos = await Build(gw).GetUtxosAsync("Bxyz");
        var u = Assert.Single(utxos);
        Assert.Equal("aa", u.TxId);
        Assert.Equal(1, u.OutputIndex);
        Assert.Equal(500, u.Amount);
        Assert.Equal(42, u.Confirmations);
        Assert.True(u.IsCoinbase);
    }

    [Fact]
    public async Task Raw_tx_and_proof_unwrap_their_json_envelopes()
    {
        var gw = new ScriptedGateway();
        gw.ByPathSuffix["/raw"] = (HttpStatusCode.OK, """{"txId":"aa","hex":"0100beef"}""");
        gw.ByPathSuffix["/proof"] = (HttpStatusCode.OK, """{"txId":"aa","proof":"deadbeef"}""");

        var svc = Build(gw);
        Assert.Equal("0100beef", await svc.GetRawTransactionHexAsync("aa"));
        Assert.Equal("deadbeef", await svc.GetTxOutProofAsync("aa"));
    }

    [Fact]
    public async Task Reads_degrade_to_null_or_empty_never_throw()
    {
        // Never-throw contract: a dead gateway must not crash the UI — reads return
        // empty/null so the pages simply show nothing.
        var svc = Build(new ScriptedGateway { Dead = true });
        Assert.Null(await svc.GetAddressAsync("Bxyz"));
        Assert.Empty(await svc.GetUtxosAsync("Bxyz"));
        Assert.Empty(await svc.GetHistoryAsync("Bxyz"));
        Assert.Null(await svc.GetRawTransactionHexAsync("aa"));
        Assert.Null(await svc.GetTxOutProofAsync("aa"));
    }

    [Fact]
    public async Task Broadcast_maps_success_rejection_and_unreachable()
    {
        var gw = new ScriptedGateway();

        gw.ByPathSuffix["/broadcast"] = (HttpStatusCode.OK, """{"txId":"feedface"}""");
        var ok = await Build(gw).BroadcastAsync("00ff");
        Assert.True(ok.Success);
        Assert.Equal("feedface", ok.TxId);

        gw.ByPathSuffix["/broadcast"] = (HttpStatusCode.UnprocessableEntity, """{"error":"txn-mempool-conflict"}""");
        var rejected = await Build(gw).BroadcastAsync("00ff");
        Assert.False(rejected.Success);
        Assert.Equal(BroadcastFailureKind.Rejected, rejected.Failure);
        Assert.Equal("txn-mempool-conflict", rejected.Error);

        gw.ByPathSuffix["/broadcast"] = (HttpStatusCode.ServiceUnavailable, """{"error":"node down"}""");
        var down = await Build(gw).BroadcastAsync("00ff");
        Assert.False(down.Success);
        Assert.Equal(BroadcastFailureKind.Unreachable, down.Failure);

        gw.ByPathSuffix["/broadcast"] = (HttpStatusCode.UnprocessableEntity, """{"error":"txn-already-in-mempool"}""");
        var dup = await Build(gw).BroadcastAsync("00ff");
        Assert.Equal(BroadcastFailureKind.Duplicate, dup.Failure);
    }
}

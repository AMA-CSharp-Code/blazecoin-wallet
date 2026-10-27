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
        /// <summary>Thrown instead of answering — e.g. the budget handler's timeout shape.</summary>
        public Exception? Fault;
        /// <summary>Each request's path and the budget it declared (null = none).</summary>
        public readonly List<(string Path, RequestBudget? Budget)> Seen = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!.AbsolutePath,
                request.Options.TryGetValue(RequestTimeoutHandler.Budget, out var b) ? b : null));
            if (Dead) throw new HttpRequestException("connection refused");
            if (Fault != null) throw Fault;
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


    // ── the provenance trace (2026-10-27: its own time budget, a timeout named as one) ──

    private const string EmptyAncestryJson =
        """{"address":"B","totalSatoshis":0,"outputCount":0,"transactionsRead":0,"truncated":false,"fifo":{"attributedSatoshis":0,"years":[],"months":[],"specials":[]},"haircut":{"attributedSatoshis":0,"years":[],"months":[],"specials":[]}}""";

    [Fact]
    public async Task The_ancestry_call_declares_the_trace_budget_and_ordinary_reads_do_not()
    {
        var gw = new ScriptedGateway();
        gw.ByPathSuffix["/utxos"] = (HttpStatusCode.OK, "[]");
        gw.ByPathSuffix["/ancestry"] = (HttpStatusCode.OK, EmptyAncestryJson);
        var svc = Build(gw);

        await svc.GetUtxosAsync("Baddr");
        var trace = await svc.GetAncestryAsync("Baddr");

        Assert.Null(trace.Error);
        Assert.Collection(gw.Seen,
            u => { Assert.EndsWith("/utxos", u.Path); Assert.Null(u.Budget); },
            a => { Assert.EndsWith("/ancestry", a.Path); Assert.Equal(RequestBudget.Trace, a.Budget); });
    }

    [Theory]
    [InlineData("\"truncated\":true,\"truncatedBy\":\"time\"", true, "time")]
    [InlineData("\"truncated\":true,\"truncatedBy\":\"reads\"", true, "reads")]
    [InlineData("\"truncated\":true", true, null)]           // an older gateway: no reason field
    [InlineData("\"truncated\":false", false, null)]
    public async Task Ancestry_maps_the_budget_flags_from_the_gateway(string flags, bool truncated, string? by)
    {
        var gw = new ScriptedGateway();
        gw.ByPathSuffix["/ancestry"] = (HttpStatusCode.OK, EmptyAncestryJson.Replace("\"truncated\":false", flags));
        var svc = Build(gw);

        var trace = await svc.GetAncestryAsync("Baddr");

        Assert.Null(trace.Error);
        Assert.Equal(truncated, trace.Report!.Truncated);
        Assert.Equal(by, trace.Report.TruncatedBy);
    }

    [Fact]
    public async Task An_ancestry_timeout_is_named_as_one_and_says_a_retry_resumes()
    {
        // The budget handler's shape (and HttpClient.Timeout's): TaskCanceled over Timeout.
        var gw = new ScriptedGateway { Fault = new TaskCanceledException("budget", new TimeoutException()) };
        var svc = Build(gw);

        var trace = await svc.GetAncestryAsync("Baddr");

        Assert.Null(trace.Report);
        Assert.Contains("did not finish the trace in time", trace.Error);
        Assert.Contains("carries on from where it stopped", trace.Error);
        Assert.DoesNotContain("connection", trace.Error);   // 2026-10-26's misdiagnosis, never again
    }

    [Fact]
    public async Task A_dead_gateway_is_still_reported_as_a_connection_problem()
    {
        var svc = Build(new ScriptedGateway { Dead = true });

        var trace = await svc.GetAncestryAsync("Baddr");

        Assert.Null(trace.Report);
        Assert.Contains("check your connection", trace.Error);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_a_failure_result_not_a_throw()
    {
        var gw = new ScriptedGateway { Fault = new OperationCanceledException() };
        var svc = Build(gw);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        var trace = await svc.GetAncestryAsync("Baddr", caller.Token);

        Assert.Null(trace.Report);
        Assert.NotNull(trace.Error);
        Assert.DoesNotContain("did not finish", trace.Error);   // not a budget verdict — the user stopped it
    }
}

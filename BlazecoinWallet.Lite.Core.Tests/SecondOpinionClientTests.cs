using System.Net;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Audit 2026-11-09: the ONE client every second opinion goes through, and the one place the
/// broadcast verdict is combined. Five callers used to hand-roll the first with five meanings
/// for "nobody else" and "nobody answered"; these pin the single meaning of each outcome.
/// </summary>
public class SecondOpinionClientTests
{
    private static readonly Uri A = new("https://a.test/");
    private static readonly Uri B = new("https://b.test/");

    /// <summary>The wire: a host that is not scripted is unreachable.</summary>
    private sealed class Wire : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Hosts = new();
        public readonly List<string> Calls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(request.RequestUri!.Host);
            return Hosts.TryGetValue(request.RequestUri.Host, out var answer)
                ? Task.FromResult(answer())
                : throw new HttpRequestException("no route");
        }
    }

    private static SecondOpinionClient Client(HttpMessageHandler transport, Uri baseAddress) =>
        new(new HttpClient(transport) { BaseAddress = baseAddress }, NullLogger.Instance);

    private static SecondOpinionClient Failover(Wire wire, params Uri[] gateways)
    {
        var failover = new GatewayFailoverHandler(gateways.Select(g => g.ToString()), wire);
        return Client(failover, failover.Primary);
    }

    private static HttpRequestMessage Question() => new(HttpMethod.Get, "indexer/api/x");

    [Fact]
    public async Task Another_server_answering_is_an_answer_whatever_its_status()
    {
        var wire = new Wire { Hosts = { ["a.test"] = () => new(HttpStatusCode.OK), ["b.test"] = () => new(HttpStatusCode.NotFound) } };
        var client = Failover(wire, A, B);

        using var opinion = await client.SendAsync(Question(), [A], "the question");

        Assert.Equal(SecondGateway.Answered, opinion.Status);
        Assert.Equal(B, opinion.AnsweredBy);
        Assert.Equal(HttpStatusCode.NotFound, opinion.Response!.StatusCode);   // an error is still its answer
        Assert.False(opinion.IsSuccess);
        Assert.Equal(["b.test"], wire.Calls);
    }

    [Fact]
    public async Task Nobody_named_to_exclude_means_nobody_else_and_nothing_is_sent()
    {
        var wire = new Wire { Hosts = { ["a.test"] = () => new(HttpStatusCode.OK) } };
        var client = Failover(wire, A, B);

        Assert.Equal(SecondGateway.None, (await client.SendAsync(Question(), null, "the question")).Status);
        Assert.Equal(SecondGateway.None, (await client.SendAsync(Question(), [], "the question")).Status);
        Assert.Empty(wire.Calls);
    }

    [Fact]
    public async Task Every_configured_gateway_excluded_means_nobody_else()
    {
        var wire = new Wire { Hosts = { ["a.test"] = () => new(HttpStatusCode.OK) } };

        var opinion = await Failover(wire, A).SendAsync(Question(), [A], "the question");

        Assert.Equal(SecondGateway.None, opinion.Status);
        Assert.Null(opinion.Response);
        Assert.Empty(wire.Calls);
    }

    [Fact]
    public async Task Someone_else_configured_who_does_not_answer_is_unavailable_not_absent()
    {
        var wire = new Wire { Hosts = { ["a.test"] = () => new(HttpStatusCode.OK) } };   // b.test is down

        var opinion = await Failover(wire, A, B).SendAsync(Question(), [A], "the question");

        Assert.Equal(SecondGateway.Unavailable, opinion.Status);
        Assert.Null(opinion.Response);
        Assert.Equal(["b.test"], wire.Calls);
    }

    [Fact]
    public async Task A_transport_that_names_nobody_gives_no_second_opinion()
    {
        // No failover handler: the exclusion is ignored and nobody says who answered.
        var wire = new Wire { Hosts = { ["a.test"] = () => new(HttpStatusCode.OK) } };

        var opinion = await Client(wire, A).SendAsync(Question(), [A], "the question");

        Assert.Equal(SecondGateway.None, opinion.Status);
        Assert.Null(opinion.Response);
    }

    /// <summary>Ignores the exclusion and names the same server's other name as the one that answered.</summary>
    private sealed class OtherNameOfTheSameServer(Wire wire) : DelegatingHandler(wire)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var res = await base.SendAsync(request, ct);
            request.Options.Set(GatewayRouting.AnsweredBy, new Uri("https://51-210-47-141.sslip.io/"));
            return res;
        }
    }

    [Fact]
    public async Task An_excluded_server_answering_under_its_other_name_gives_no_second_opinion()
    {
        var strasbourg = new Uri("https://gw.blazecoin.co.uk/");
        var wire = new Wire { Hosts = { ["gw.blazecoin.co.uk"] = () => new(HttpStatusCode.OK) } };

        var opinion = await Client(new OtherNameOfTheSameServer(wire), strasbourg).SendAsync(Question(), [strasbourg], "the question");

        Assert.Equal(SecondGateway.None, opinion.Status);
    }

    [Fact]
    public async Task The_callers_cancellation_is_thrown_never_reported_as_an_outcome()
    {
        var wire = new Wire { Hosts = { ["b.test"] = () => new(HttpStatusCode.OK) } };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Failover(wire, A, B).SendAsync(Question(), [A], "the question", cancelled.Token));
    }

    /// <summary>Nothing but the one client sets the exclusion or compares servers: the data
    /// service asks through it, so a sixth question cannot grow a sixth meaning.</summary>
    [Fact]
    public void The_gateway_data_service_asks_every_second_opinion_through_the_one_client()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "BlazecoinWallet.Lite.Core"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var data = Path.Combine(dir!.FullName, "BlazecoinWallet.Lite.Core", "Data");
        var service = File.ReadAllText(Path.Combine(data, "IndexerDataService.cs"));

        Assert.DoesNotContain("GatewayRouting.Exclude", service);
        Assert.DoesNotContain("GatewaySite.", service);
        Assert.DoesNotContain("NoOtherGatewayException", service);
        Assert.Contains("_secondOpinion.SendAsync", service);
        Assert.Contains("GatewayRouting.Exclude", File.ReadAllText(Path.Combine(data, "SecondOpinionClient.cs")));
        // …and the broadcast rule and its wording are not in the HTTP class either.
        Assert.DoesNotContain("reported by one gateway only", service);
        Assert.Contains("BroadcastVerdict.Combine", service);
    }

    // ── the broadcast verdict ─────────────────────────────────────────────────────────────

    private const string Own = "aa";
    private static RelayAttempt Accepted(Uri by, string? txId = null) => RelayAttempt.FromReply(HttpStatusCode.OK, txId, null, by);
    private static RelayAttempt Rejected(Uri by, string reason = "bad-txns") => RelayAttempt.FromReply(HttpStatusCode.UnprocessableEntity, null, reason, by);
    private static RelayAttempt Duplicate(Uri by) => RelayAttempt.FromReply(HttpStatusCode.UnprocessableEntity, null, "txn-already-in-mempool", by);
    private static RelayAttempt Busy(Uri by, HttpStatusCode status = HttpStatusCode.ServiceUnavailable, string? reason = null) => RelayAttempt.FromReply(status, null, reason, by);

    [Fact]
    public void One_reply_is_an_acceptance_a_judgement_or_nothing()
    {
        Assert.Equal(RelayVerdict.Accepted, Accepted(A, "cafe").Verdict);
        Assert.Equal("cafe", Accepted(A, "cafe").RelayTxId);
        Assert.Equal(RelayVerdict.Rejected, Rejected(A).Verdict);
        Assert.Equal(RelayVerdict.Rejected, RelayAttempt.FromReply(HttpStatusCode.BadRequest, null, null, A).Verdict);
        Assert.Equal("The network rejected the transaction (400).", RelayAttempt.FromReply(HttpStatusCode.BadRequest, null, null, A).Reason);
        Assert.Equal(RelayVerdict.Duplicate, Duplicate(A).Verdict);
        // A 429 or a 5xx judged nothing — and only a 503's reason is repeated.
        Assert.Equal(RelayVerdict.NotJudged, Busy(A, HttpStatusCode.TooManyRequests, "slow down").Verdict);
        Assert.Null(Busy(A, HttpStatusCode.TooManyRequests, "slow down").Reason);
        Assert.Equal("node restarting", Busy(A, HttpStatusCode.ServiceUnavailable, "node restarting").Reason);
        Assert.Equal(RelayVerdict.NotJudged, Busy(A, HttpStatusCode.InternalServerError).Verdict);
    }

    [Fact]
    public void An_acceptance_stands_on_one_gateway_and_is_backed_by_a_second()
    {
        Assert.Equal(LiteBroadcastResult.Ok(Own), BroadcastVerdict.Combine(Accepted(A), Accepted(B), Own));
        Assert.Equal(LiteBroadcastResult.Ok(Own), BroadcastVerdict.Combine(Accepted(A), Duplicate(B), Own));
        Assert.Equal(LiteBroadcastResult.Ok(Own, uncorroborated: true), BroadcastVerdict.Combine(Accepted(A), RelayAttempt.None, Own));
        Assert.Equal(LiteBroadcastResult.Ok(Own, uncorroborated: true), BroadcastVerdict.Combine(Accepted(A), Rejected(B), Own));
        Assert.Equal(LiteBroadcastResult.Ok(Own, uncorroborated: true), BroadcastVerdict.Combine(Rejected(A), Accepted(B), Own));
        // The id is the wallet's own, whatever a relay called it.
        Assert.Equal(Own, BroadcastVerdict.Combine(Accepted(A, "beef"), Accepted(B, "f00d"), Own).TxId);
        Assert.True(BroadcastVerdict.NamesAnotherTransaction(Accepted(A, "beef"), Own));
        Assert.False(BroadcastVerdict.NamesAnotherTransaction(Accepted(A, "AA"), Own));
        Assert.Equal(BroadcastVerdict.AcceptedWithoutTxId, BroadcastVerdict.Combine(Accepted(A), RelayAttempt.None, null).Error);
    }

    [Fact]
    public void A_rejection_is_the_networks_only_when_two_gateways_agree()
    {
        var both = BroadcastVerdict.Combine(Rejected(A), Rejected(B), Own);
        Assert.Equal(LiteBroadcastResult.Fail("bad-txns", BroadcastFailureKind.Rejected), both);

        var one = BroadcastVerdict.Combine(Rejected(A), RelayAttempt.None, Own);
        Assert.Equal(BroadcastFailureKind.Rejected, one.Failure);
        Assert.True(one.Uncorroborated);
        Assert.Equal("bad-txns — reported by one gateway only; no second gateway could confirm it. " +
                     "If this looks wrong, switch gateway in Settings and try again.", one.Error);
        Assert.Equal(one, BroadcastVerdict.Combine(Busy(A), Rejected(B), Own));
    }

    [Fact]
    public void A_duplicate_means_the_network_has_it_and_silence_means_an_outage()
    {
        Assert.Equal(LiteBroadcastResult.Fail("txn-already-in-mempool", BroadcastFailureKind.Duplicate),
            BroadcastVerdict.Combine(Duplicate(A), Duplicate(B), Own));
        Assert.True(BroadcastVerdict.Combine(Duplicate(A), RelayAttempt.None, Own).Uncorroborated);

        Assert.Equal(LiteBroadcastResult.Fail("Could not reach the Blazecoin service — check your connection.", BroadcastFailureKind.Unreachable),
            BroadcastVerdict.Combine(RelayAttempt.None, RelayAttempt.None, Own));
        Assert.Equal(LiteBroadcastResult.Fail("The network node is temporarily unreachable — try again shortly.", BroadcastFailureKind.Unreachable),
            BroadcastVerdict.Combine(Busy(A), Busy(B), Own));
        Assert.Equal("node restarting", BroadcastVerdict.Combine(Busy(A, reason: "node restarting"), RelayAttempt.None, Own).Error);
    }
}

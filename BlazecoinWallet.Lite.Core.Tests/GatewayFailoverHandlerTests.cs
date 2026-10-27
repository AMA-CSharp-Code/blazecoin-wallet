using System.Net;
using System.Text;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The client-side gateway failover contract (Electrum model): dead gateways are skipped,
/// the last good one is remembered (sticky), request bodies survive the retry intact, an
/// HTTP error response is an ANSWER (never a failover trigger), and when every gateway is
/// down the delivery failure propagates.
/// </summary>
public class GatewayFailoverHandlerTests
{
    /// <summary>Scripted inner transport: per-host behavior + call/body capture.</summary>
    private sealed class ScriptedTransport : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Hosts = new();
        public readonly List<string> Calls = [];
        public string? LastBodySeen;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.Host);
            if (request.Content != null)
                LastBodySeen = await request.Content.ReadAsStringAsync(ct);
            return Hosts.TryGetValue(request.RequestUri.Host, out var f)
                ? f()
                : throw new HttpRequestException($"no route to {request.RequestUri.Host}");
        }
    }

    private static HttpClient Client(ScriptedTransport transport, params string[] gateways)
    {
        var failover = new GatewayFailoverHandler(gateways, transport);
        return new HttpClient(failover) { BaseAddress = failover.Primary };
    }

    [Fact]
    public async Task Dead_primary_fails_over_and_the_survivor_becomes_sticky()
    {
        var transport = new ScriptedTransport();
        transport.Hosts["b.test"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("from-b") };
        var client = Client(transport, "https://a.test/", "https://b.test/");

        var first = await client.GetStringAsync("indexer/api/x");
        Assert.Equal("from-b", first);
        Assert.Equal(new[] { "a.test", "b.test" }, transport.Calls);

        transport.Calls.Clear();
        await client.GetStringAsync("indexer/api/y");
        Assert.Equal(new[] { "b.test" }, transport.Calls); // sticky — no re-probe of the dead one
    }

    [Fact]
    public async Task Post_body_survives_the_failover_retry()
    {
        var transport = new ScriptedTransport();
        transport.Hosts["b.test"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{}") };
        var client = Client(transport, "https://a.test/", "https://b.test/");

        var res = await client.PostAsync("api/tx/broadcast",
            new StringContent("{\"hex\":\"cafe\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("{\"hex\":\"cafe\"}", transport.LastBodySeen);
    }

    [Fact]
    public async Task An_http_error_is_an_answer_not_an_outage()
    {
        var transport = new ScriptedTransport();
        transport.Hosts["a.test"] = () => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        transport.Hosts["b.test"] = () => new HttpResponseMessage(HttpStatusCode.OK);
        var client = Client(transport, "https://a.test/", "https://b.test/");

        var res = await client.GetAsync("api/tx/broadcast");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode); // a's verdict stands
        Assert.Equal(new[] { "a.test" }, transport.Calls);                // b never consulted
    }

    [Theory]
    [InlineData("http://gateway.example.com/")]       // cleartext off-box — MITM risk
    [InlineData("ftp://gateway.example.com/")]         // wrong scheme entirely
    public void A_non_https_off_box_gateway_is_rejected(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            new GatewayFailoverHandler([url], new HttpClientHandler()));
    }

    [Theory]
    [InlineData("https://gateway.example.com/")]       // https anywhere
    [InlineData("http://localhost:7002/")]             // loopback http (dev / emulator)
    [InlineData("http://127.0.0.1:7002/")]
    public void Https_or_loopback_gateways_are_accepted(string url)
    {
        var ex = Record.Exception(() => new GatewayFailoverHandler([url], new HttpClientHandler()));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Every_gateway_down_propagates_the_delivery_failure()
    {
        var transport = new ScriptedTransport();
        var client = Client(transport, "https://a.test/", "https://b.test/");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("indexer/api/x"));
        Assert.Equal(new[] { "a.test", "b.test" }, transport.Calls); // both were tried
    }


    // ── per-attempt budgets (RequestTimeoutHandler sits INSIDE the failover, 2026-10-27) ──

    /// <summary>A gateway that accepts the connection and never answers, honouring cancellation.</summary>
    private sealed class StallingTransport : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public readonly List<RequestBudget?> BudgetsSeen = [];
        public string? StallHost;
        public TimeSpan Stall = TimeSpan.FromSeconds(10);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.Host);
            BudgetsSeen.Add(request.Options.TryGetValue(RequestTimeoutHandler.Budget, out var b) ? b : null);
            if (request.RequestUri.Host == StallHost) await Task.Delay(Stall, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("from-" + request.RequestUri.Host) };
        }
    }

    private static HttpClient BudgetedClient(HttpMessageHandler transport, TimeSpan defaultBudget, TimeSpan traceBudget, params string[] gateways)
    {
        // The composition root's shape: failover OUTSIDE, budgets INSIDE (per attempt), client ceiling off.
        var failover = new GatewayFailoverHandler(gateways, new RequestTimeoutHandler(defaultBudget, traceBudget, transport));
        return new HttpClient(failover) { BaseAddress = failover.Primary, Timeout = Timeout.InfiniteTimeSpan };
    }

    [Fact]
    public async Task A_gateway_that_accepts_but_never_answers_is_failed_over_after_its_budget()
    {
        var transport = new StallingTransport { StallHost = "a.test" };
        using var client = BudgetedClient(transport, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5),
            "https://a.test/", "https://b.test/");

        var started = DateTime.UtcNow;
        var answer = await client.GetStringAsync("indexer/api/x");

        Assert.Equal("from-b.test", answer);
        Assert.Equal(new[] { "a.test", "b.test" }, transport.Calls);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "the stall was cut at the budget, not waited out");
    }

    [Fact]
    public async Task The_requests_budget_rides_along_to_the_retry()
    {
        var transport = new StallingTransport { StallHost = "a.test" };
        using var client = BudgetedClient(transport, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200),
            "https://a.test/", "https://b.test/");

        using var request = new HttpRequestMessage(HttpMethod.Get, "indexer/api/address/B/ancestry");
        request.Options.Set(RequestTimeoutHandler.Budget, RequestBudget.Trace);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Both attempts were timed under the TRACE budget — the option survived the re-send.
        Assert.Equal(new RequestBudget?[] { RequestBudget.Trace, RequestBudget.Trace }, transport.BudgetsSeen);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_a_failover_trigger()
    {
        var transport = new StallingTransport { StallHost = "a.test" };
        using var client = BudgetedClient(transport, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
            "https://a.test/", "https://b.test/");
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("indexer/api/x", caller.Token));

        Assert.Equal(new[] { "a.test" }, transport.Calls);   // the user said stop — nobody else is asked
    }

    [Fact]
    public async Task Every_gateway_stalling_propagates_the_timeout_after_trying_each()
    {
        var transport = new EverythingStalls();
        using var client = BudgetedClient(transport, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50),
            "https://a.test/", "https://b.test/");

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("indexer/api/x"));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(new[] { "a.test", "b.test" }, transport.Calls);   // both were given their budget
    }

    private sealed class EverythingStalls : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.Host);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

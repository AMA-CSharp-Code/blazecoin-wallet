using System.Net;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The per-request time budgets (2026-10-27). One HttpClient ceiling for every gateway call cut
/// every mobile provenance trace off at 30 s while the gateway was still walking (status 499 in
/// its log); now the ordinary budget applies by default and the trace budget to a request that
/// declares <see cref="RequestBudget.Trace"/>. A spent budget must look exactly like the old
/// HttpClient timeout (TaskCanceledException over TimeoutException) so every caller's catch block
/// keeps working, and the caller's own cancellation must pass through untouched.
/// </summary>
public class RequestTimeoutHandlerTests
{
    /// <summary>Answers after a delay, honouring cancellation like a real socket read.</summary>
    private sealed class SlowTransport : HttpMessageHandler
    {
        public TimeSpan Delay = TimeSpan.Zero;
        public CancellationToken LastToken;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastToken = ct;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private static HttpClient Client(SlowTransport transport, TimeSpan defaultBudget, TimeSpan traceBudget) =>
        new(new RequestTimeoutHandler(defaultBudget, traceBudget, transport))
        {
            BaseAddress = new Uri("https://gw.test/"),
            Timeout = Timeout.InfiniteTimeSpan,   // as the composition root sets it: the handler owns the budgets
        };

    private static HttpRequestMessage Trace(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Options.Set(RequestTimeoutHandler.Budget, RequestBudget.Trace);
        return request;
    }

    [Fact]
    public async Task A_fast_request_passes_under_either_budget()
    {
        var transport = new SlowTransport();
        using var client = Client(transport, Short, Long);

        Assert.Equal("ok", await client.GetStringAsync("indexer/api/x"));
        using var traced = await client.SendAsync(Trace("indexer/api/y"));
        Assert.Equal(HttpStatusCode.OK, traced.StatusCode);
    }

    [Fact]
    public async Task The_default_budget_cuts_a_slow_request_the_way_HttpClient_Timeout_did()
    {
        var transport = new SlowTransport { Delay = TimeSpan.FromSeconds(5) };
        using var client = Client(transport, Short, Long);

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("indexer/api/x"));

        // The shape callers already catch: TaskCanceledException over a TimeoutException.
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("budget", ex.Message);
        // …and it fired at the budget, not after the transport's 5 s.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        // The transport was told to stop (its token was cancelled), so no work leaks on.
        Assert.True(transport.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_request_marked_as_a_trace_outlives_the_default_budget()
    {
        // 400 ms would die under the 100 ms default; under the trace budget it is answered.
        var transport = new SlowTransport { Delay = TimeSpan.FromMilliseconds(400) };
        using var client = Client(transport, Short, Long);

        using var response = await client.SendAsync(Trace("indexer/api/address/B/ancestry"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_trace_budget_is_a_ceiling_too()
    {
        var transport = new SlowTransport { Delay = TimeSpan.FromSeconds(5) };
        using var client = Client(transport, Short, TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.SendAsync(Trace("indexer/api/address/B/ancestry")));

        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task The_callers_own_cancellation_passes_through_as_cancellation_not_as_a_timeout()
    {
        var transport = new SlowTransport { Delay = TimeSpan.FromSeconds(5) };
        using var client = Client(transport, Long, Long);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("indexer/api/x", caller.Token));

        // Not re-labelled as a budget: no TimeoutException, and the page's "user navigated
        // away" paths (which test ct.IsCancellationRequested) still see their own token.
        Assert.False(ex.InnerException is TimeoutException);
        Assert.True(caller.IsCancellationRequested);
    }

    [Fact]
    public void BudgetFor_resolves_the_declared_budget_and_defaults_the_rest()
    {
        using var handler = new RequestTimeoutHandler(Short, Long, new SlowTransport());

        using var plain = new HttpRequestMessage(HttpMethod.Get, "https://gw.test/x");
        Assert.Equal(Short, handler.BudgetFor(plain));

        using var traced = Trace("https://gw.test/y");
        Assert.Equal(Long, handler.BudgetFor(traced));

        using var explicitDefault = new HttpRequestMessage(HttpMethod.Get, "https://gw.test/z");
        explicitDefault.Options.Set(RequestTimeoutHandler.Budget, RequestBudget.Default);
        Assert.Equal(Short, handler.BudgetFor(explicitDefault));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -5)]
    public void Budgets_must_be_positive(int defaultSeconds, int traceSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestTimeoutHandler(
            TimeSpan.FromSeconds(defaultSeconds), TimeSpan.FromSeconds(traceSeconds), new SlowTransport()));
    }

    [Fact]
    public void The_shipped_defaults_give_the_trace_minutes_and_reads_the_old_thirty_seconds()
    {
        // The 30 s ordinary ceiling is unchanged; the trace gets a budget sized for a gateway
        // slice (~20 s) plus queueing (15 s) plus a not-yet-updated gateway's longer walk.
        var options = new LiteWalletOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), options.HttpTimeout);
        Assert.True(options.TraceTimeout >= TimeSpan.FromMinutes(2), "a trace needs minutes, not seconds");
    }
}

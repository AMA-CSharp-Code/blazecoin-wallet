namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Per-request time budgets for the gateway client. <see cref="HttpClient.Timeout"/> is ONE
/// ceiling for every call, and the lite wallet has two very different kinds of gateway call:
/// the ordinary reads (an address, its coins, a proof — fractions of a second) and the
/// provenance trace, where the server walks a coin's entire mint ancestry behind a
/// one-at-a-time gate (up to 100,000 transaction reads — minutes, not seconds, for coins
/// that descend through the pool's consolidations). With the single 30 s ceiling every
/// mobile trace since 2026-10-26 was cut off by the CLIENT at 30 s (the gateway log shows
/// status 499 at 30.5 s, five attempts in a row) and the page blamed the connection.
///
/// The client's own timeout is therefore switched off and this handler applies the budget:
/// <see cref="DefaultTimeout"/> for every request, <see cref="TraceTimeout"/> for a request
/// marked with <see cref="Budget"/> = <see cref="RequestBudget.Trace"/>. A budget that runs out
/// surfaces exactly as <see cref="HttpClient.Timeout"/> did — a <see cref="TaskCanceledException"/>
/// wrapping a <see cref="TimeoutException"/> — so every caller's catch block keeps working.
/// </summary>
public sealed class RequestTimeoutHandler : DelegatingHandler
{
    /// <summary>Set on a request to pick its budget; absent = <see cref="RequestBudget.Default"/>.</summary>
    public static readonly HttpRequestOptionsKey<RequestBudget> Budget = new("blz.request-budget");

    public TimeSpan DefaultTimeout { get; }
    public TimeSpan TraceTimeout { get; }

    public RequestTimeoutHandler(TimeSpan defaultTimeout, TimeSpan traceTimeout, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        if (defaultTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
        if (traceTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(traceTimeout));
        DefaultTimeout = defaultTimeout;
        TraceTimeout = traceTimeout;
    }

    /// <summary>The budget a request will get — the service's declaration, resolved.</summary>
    public TimeSpan BudgetFor(HttpRequestMessage request) =>
        request.Options.TryGetValue(Budget, out var b) && b == RequestBudget.Trace ? TraceTimeout : DefaultTimeout;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(BudgetFor(request));
        try
        {
            return await base.SendAsync(request, budget.Token);
        }
        catch (OperationCanceledException ex) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The budget, not the caller — same shape HttpClient.Timeout throws, so the
            // existing "could not reach the service" paths catch it unchanged.
            throw new TaskCanceledException(
                $"The request was not answered within its {BudgetFor(request).TotalSeconds:0} s budget.",
                new TimeoutException(), ex.CancellationToken);
        }
    }
}

/// <summary>Which time budget a gateway request runs under (see <see cref="RequestTimeoutHandler"/>).</summary>
public enum RequestBudget
{
    /// <summary>An ordinary read or relay: answered in well under a second by a healthy gateway.</summary>
    Default = 0,
    /// <summary>A provenance trace: the server's ancestry walk can legitimately take minutes.</summary>
    Trace = 1,
}

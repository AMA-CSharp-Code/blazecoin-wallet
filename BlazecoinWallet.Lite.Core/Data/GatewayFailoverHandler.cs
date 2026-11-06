namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Client-side gateway failover — the Electrum model: the wallet ships an ordered LIST of
/// gateway URLs and quietly fails over when one can't be reached, remembering the last
/// good one. Failover triggers ONLY on delivery failure (connect error / timeout), never
/// on an HTTP response — a 4xx/5xx is an ANSWER from a live gateway, not an outage.
/// Request bodies are buffered once so a retry can re-send them intact, and the request's
/// options (its <see cref="RequestTimeoutHandler"/> budget) ride along, because the budget
/// handler sits INSIDE this one and times each attempt on its own — a timeout is a
/// per-gateway verdict here, not the caller's cancellation (2026-10-27).
/// <para>A caller that needs a SECOND opinion (bug-bounty reports 2026-11-06: one gateway's
/// word on a broadcast must not be final) reads which gateway answered from
/// <see cref="GatewayRouting.AnsweredBy"/> and repeats the request with that gateway in
/// <see cref="GatewayRouting.Exclude"/>. Such a request never moves the sticky preference.</para>
/// </summary>
public sealed class GatewayFailoverHandler : DelegatingHandler
{
    private readonly Uri[] _gateways;
    private int _preferred; // index of the last gateway that answered

    public GatewayFailoverHandler(IEnumerable<string> gatewayUrls, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        // Normalisation + the https/loopback security rule live in LiteGatewayUrls, shared
        // with the settings screen so both enforce the same boundary.
        _gateways = LiteGatewayUrls.NormalizeList(gatewayUrls).ToArray();
    }

    /// <summary>The first (primary) gateway — used as the HttpClient BaseAddress.</summary>
    public Uri Primary => _gateways[0];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Buffer the body once so every attempt can carry it (HttpContent is single-read).
        byte[]? body = null;
        System.Net.Http.Headers.HttpContentHeaders? bodyHeaders = null;
        if (request.Content != null)
        {
            body = await request.Content.ReadAsByteArrayAsync(ct);
            bodyHeaders = request.Content.Headers;
        }

        var pathAndQuery = request.RequestUri!.PathAndQuery;
        Exception? lastFailure = null;
        var start = Volatile.Read(ref _preferred);
        request.Options.TryGetValue(GatewayRouting.Exclude, out var excluded);
        var secondOpinion = excluded is { Count: > 0 };

        for (var i = 0; i < _gateways.Length; i++)
        {
            var idx = (start + i) % _gateways.Length;
            if (secondOpinion && excluded!.Contains(_gateways[idx])) continue;
            using var attempt = new HttpRequestMessage(request.Method, new Uri(_gateways[idx], pathAndQuery));
            foreach (var h in request.Headers)
                attempt.Headers.TryAddWithoutValidation(h.Key, h.Value);
            foreach (var (key, value) in request.Options)
                ((IDictionary<string, object?>)attempt.Options)[key] = value;
            if (body != null)
            {
                var content = new ByteArrayContent(body);
                foreach (var h in bodyHeaders!)
                    content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                attempt.Content = content;
            }

            try
            {
                var response = await base.SendAsync(attempt, ct);
                // Sticky: keep using what works — but a second-opinion request was steered away
                // from the preferred gateway on purpose and must not re-point everything else.
                if (!secondOpinion) Volatile.Write(ref _preferred, idx);
                request.Options.Set(GatewayRouting.AnsweredBy, _gateways[idx]);
                return response;
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ex; // unreachable — try the next gateway
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastFailure = ex; // timeout (not caller cancellation) — try the next gateway
            }
        }

        throw lastFailure ?? new HttpRequestException("No other gateway is configured.");
    }
}

/// <summary>
/// Per-request routing notes between a caller and <see cref="GatewayFailoverHandler"/>, carried
/// in <see cref="HttpRequestMessage.Options"/> so no caller needs a reference to the handler
/// (and a plain HttpClient — tests, a single custom gateway — simply ignores them).
/// </summary>
public static class GatewayRouting
{
    /// <summary>Set by the handler on the caller's request: the gateway whose answer came back.</summary>
    public static readonly HttpRequestOptionsKey<Uri> AnsweredBy = new("blz.gateway.answered-by");

    /// <summary>Set by the caller: gateways this request must NOT be sent to (ask someone else).</summary>
    public static readonly HttpRequestOptionsKey<IReadOnlyCollection<Uri>> Exclude = new("blz.gateway.exclude");
}

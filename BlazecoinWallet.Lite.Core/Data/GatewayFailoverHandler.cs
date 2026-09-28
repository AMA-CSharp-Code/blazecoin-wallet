namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Client-side gateway failover — the Electrum model: the wallet ships an ordered LIST of
/// gateway URLs and quietly fails over when one can't be reached, remembering the last
/// good one. Failover triggers ONLY on delivery failure (connect error / timeout), never
/// on an HTTP response — a 4xx/5xx is an ANSWER from a live gateway, not an outage.
/// Request bodies are buffered once so a retry can re-send them intact.
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

        for (var i = 0; i < _gateways.Length; i++)
        {
            var idx = (start + i) % _gateways.Length;
            using var attempt = new HttpRequestMessage(request.Method, new Uri(_gateways[idx], pathAndQuery));
            foreach (var h in request.Headers)
                attempt.Headers.TryAddWithoutValidation(h.Key, h.Value);
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
                Volatile.Write(ref _preferred, idx); // sticky: keep using what works
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

        throw lastFailure!;
    }
}

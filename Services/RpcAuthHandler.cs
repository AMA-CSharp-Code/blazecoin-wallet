using System.Net.Http.Headers;

namespace BlazecoinWallet.Maui.Services;

/// <summary>Applies the daemon RPC HTTP Basic auth per request, from
/// <see cref="RpcCredentials"/> (explicit creds or the datadir cookie). Per-request
/// (not baked once into the HttpClient) so a daemon restart that rotates the cookie
/// is picked up automatically.</summary>
public sealed class RpcAuthHandler(RpcCredentials creds) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var auth = creds.GetBasicAuth();
        if (auth is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        return base.SendAsync(request, cancellationToken);
    }
}

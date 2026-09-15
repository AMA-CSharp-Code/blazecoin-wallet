namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Validation for personal-node RPC connections, shared by <see cref="PersonalNodeDataService"/>
/// and the settings screen: the RPC call carries the node's Basic-auth credentials, so the URL
/// must be https or loopback — never cleartext to a LAN/remote host (audit M2).
/// </summary>
public static class LitePersonalNode
{
    /// <summary>Trims + ensures a trailing slash + enforces https/loopback. Throws on violation.</summary>
    public static Uri NormalizeRpcUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Node RPC URL is empty.", nameof(url));

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed.EndsWith('/') ? trimmed : trimmed + "/", UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new ArgumentException(
                $"Node RPC '{url}' must use https (http is allowed only for loopback) — " +
                "credentials must not cross the network in cleartext.", nameof(url));
        return uri;
    }
}

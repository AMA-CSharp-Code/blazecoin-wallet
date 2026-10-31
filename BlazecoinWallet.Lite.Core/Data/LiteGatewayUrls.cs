namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Gateway-URL normalisation + the security rule shared by the failover handler and the
/// settings screen: wallet traffic MUST be https (a cleartext gateway would let a network
/// attacker tamper with UTXO/broadcast data); only loopback is allowed over http (dev host /
/// emulator against a local gateway).
/// </summary>
public static class LiteGatewayUrls
{
    /// <summary>Trims, ensures a trailing slash, and enforces the https/loopback rule.
    /// Throws <see cref="ArgumentException"/> for anything invalid.</summary>
    public static Uri Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Gateway URL is empty.", nameof(url));

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed.EndsWith('/') ? trimmed : trimmed + "/", UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));

        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return uri;
        throw new ArgumentException($"Gateway '{url}' must use https (http is allowed only for loopback).", nameof(url));
    }

    /// <summary>Normalises + validates a whole list (must be non-empty).</summary>
    public static IReadOnlyList<Uri> NormalizeList(IEnumerable<string> urls)
    {
        var list = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(Normalize).ToArray();
        if (list.Length == 0)
            throw new ArgumentException("At least one gateway URL is required.", nameof(urls));
        return list;
    }
}

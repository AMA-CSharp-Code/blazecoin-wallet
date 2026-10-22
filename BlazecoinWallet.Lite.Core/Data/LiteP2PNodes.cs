namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Validation for P2P fallback node endpoints ("host:port") — the peers the wallet pushes a
/// signed tx to when every gateway is down. Shared by the settings screen so a bad endpoint
/// can't be saved. These are unauthenticated peer connections (no credentials), so the only
/// rule is a well-formed host + port.
/// </summary>
public static class LiteP2PNodes
{
    /// <summary>Trims + validates a "host:port" endpoint (host non-empty, port 1–65535). Throws on violation.</summary>
    public static string Normalize(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Node endpoint is empty.", nameof(endpoint));

        var trimmed = endpoint.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1)
            throw new ArgumentException($"'{endpoint}' must be host:port (e.g. 85.15.179.171:55414).", nameof(endpoint));

        var host = trimmed[..colon];
        var portText = trimmed[(colon + 1)..];
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new ArgumentException($"'{endpoint}' has an invalid port (must be 1–65535).", nameof(endpoint));
        if (host.Contains(' '))
            throw new ArgumentException($"'{endpoint}' has an invalid host.", nameof(endpoint));

        return $"{host}:{port}";
    }

    /// <summary>Normalises + validates a whole list (must be non-empty).</summary>
    public static IReadOnlyList<string> NormalizeList(IEnumerable<string> endpoints)
    {
        var list = endpoints.Where(e => !string.IsNullOrWhiteSpace(e)).Select(Normalize).ToArray();
        if (list.Length == 0)
            throw new ArgumentException("At least one node endpoint is required.", nameof(endpoints));
        return list;
    }
}

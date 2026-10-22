namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Block-explorer deep links. The base URL points at an explorer's permalink root (the MVC
/// app's <c>/tx/{id}</c> + <c>/address/{addr}</c> routes); the wallet appends the path. Empty
/// means "no explorer configured" — links simply don't render. https-only (loopback allowed
/// for dev), the same transport rule as the gateway.
/// </summary>
public static class LiteExplorer
{
    /// <summary>
    /// The default explorer for a fresh profile (2026-09-05): the production site's permalink root —
    /// <c>/tx/{id}</c>, <c>/address/{addr}</c>, <c>/block/{id}</c> — public from the 2026-09-07 cutover.
    /// A stored empty string still means "no explorer" (the user cleared it); the default applies only
    /// when nothing was ever saved.
    /// </summary>
    public const string DefaultBaseUrl = "https://blazecoin.co.uk/";

    /// <summary>Normalises the base URL. Empty/whitespace → "" (disabled). Throws on an insecure URL.</summary>
    public static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed.EndsWith('/') ? trimmed : trimmed + "/", UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{url}' is not a valid URL.", nameof(url));
        if (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            return uri.AbsoluteUri;
        throw new ArgumentException($"Explorer URL '{url}' must use https (http is allowed only for loopback).", nameof(url));
    }

    /// <summary>Link to a transaction, or null when no explorer is configured.</summary>
    public static string? TxUrl(string baseUrl, string txId) =>
        string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}tx/{Uri.EscapeDataString(txId)}";

    /// <summary>Link to an address, or null when no explorer is configured.</summary>
    public static string? AddressUrl(string baseUrl, string address) =>
        string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}address/{Uri.EscapeDataString(address)}";
}

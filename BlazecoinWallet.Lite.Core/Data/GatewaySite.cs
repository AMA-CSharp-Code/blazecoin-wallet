namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Which SERVER a gateway URL reaches (audit 2026-11-09). The shipped list names each of the
/// project's two gateways twice — a vanity domain and an sslip.io fallback that keeps working if
/// the domain's DNS breaks — so "a gateway other than the one that answered" used to be satisfied
/// by the same server under its other name, and one server's word counted as two.
/// A second opinion is asked of another SITE: every name of the site that answered is excluded.
/// <para>A URL that is not one of the names below is its own site (host and port), so a
/// user-configured gateway is compared with nothing but itself. A new name for a shipped server
/// MUST be added here in the release that ships it.</para>
/// </summary>
public static class GatewaySite
{
    private const string Strasbourg = "blazecoin/strasbourg";
    private const string Beauharnois = "blazecoin/beauharnois";

    private static readonly Dictionary<string, string> KnownHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gw.blazecoin.co.uk"] = Strasbourg,
        ["51-210-47-141.sslip.io"] = Strasbourg,
        ["gw2.blazecoin.co.uk"] = Beauharnois,
        ["54-39-23-245.sslip.io"] = Beauharnois,
    };

    /// <summary>The site a gateway URL belongs to: the server's name for a shipped gateway
    /// (whatever the port), else the URL's own host and port.</summary>
    public static string Of(Uri gateway) =>
        KnownHosts.TryGetValue(gateway.Host, out var site) ? site : gateway.Authority.ToLowerInvariant();

    /// <summary>True when <paramref name="gateway"/> is one of the names the wallet ships for its own servers.</summary>
    public static bool IsShippedName(Uri gateway) => KnownHosts.ContainsKey(gateway.Host);

    /// <summary>True when both URLs reach the same server. An unknown gateway (null) is the same as nothing.</summary>
    public static bool Same(Uri? a, Uri? b) =>
        a != null && b != null && string.Equals(Of(a), Of(b), StringComparison.Ordinal);

    /// <summary>True when <paramref name="gateway"/> reaches the same server as any of <paramref name="others"/>.</summary>
    public static bool SameAsAny(Uri? gateway, IEnumerable<Uri>? others) =>
        gateway != null && others != null && others.Any(o => Same(gateway, o));
}

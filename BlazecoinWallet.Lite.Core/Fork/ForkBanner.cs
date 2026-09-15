namespace BlazecoinWallet.Lite.Fork;

/// <summary>
/// The pure §7.1 decision: which of the three banner states (or none) a head shows, given the
/// gateway's announcement, the build's own version and supported forks, and the chain tip —
/// plus the exact wording every head renders, so the text is pinned once, here, not per page.
/// Block counts are the authority — days are derived from the 30-second target and rounded
/// down, never read from the calendar (Phoenix lesson).
/// </summary>
public static class ForkBanner
{
    /// <summary>Two weeks of blocks at the 30-second target — the Notice/Warning boundary.</summary>
    public const long WarningWindowBlocks = 40_320;

    /// <summary>Blocks per day at the 30-second target.</summary>
    public const long BlocksPerDay = 2_880;

    /// <summary>Where every banner state sends the user for the new build.</summary>
    public const string DownloadUrl = "https://blazecoin.co.uk/Wallet";

    /// <summary>The download link as shown in the banner text (no scheme).</summary>
    public const string DownloadHost = "blazecoin.co.uk/Wallet";

    /// <summary>
    /// Evaluates the banner state.
    /// <list type="bullet">
    /// <item><see cref="ForkBannerState.None"/> — no status, or <c>forkHeight</c>/<c>forkName</c>
    /// unset, or this build carries the fork's rules AND is not below <c>minClientVersion</c>.</item>
    /// <item><see cref="ForkBannerState.Stopped"/> — <c>tip ≥ forkHeight</c> and the build lacks the rules.</item>
    /// <item><see cref="ForkBannerState.Warning"/> — <c>minClientVersion</c> newer than this build (at any
    /// distance), or <c>forkHeight − tip ≤ 40,320</c>.</item>
    /// <item><see cref="ForkBannerState.Notice"/> — otherwise (more than two weeks of blocks away).</item>
    /// </list>
    /// </summary>
    public static ForkBannerState Evaluate(ForkStatus? status, string clientVersion,
        IReadOnlySet<string> supportedForks, long tip)
    {
        if (status is null || !status.IsAnnounced) return ForkBannerState.None;
        var forkHeight = status.ForkHeight!.Value;
        var forkName = status.ForkName!.Trim();

        var hasRules = supportedForks.Any(f => string.Equals(f, forkName, StringComparison.OrdinalIgnoreCase));
        var needsNewer = NeedsNewerClient(status.MinClientVersion, clientVersion);

        if (tip >= forkHeight)
            return hasRules ? ForkBannerState.None : ForkBannerState.Stopped;

        // Already on a build that follows the fork and meets the floor — nothing to say.
        if (hasRules && !needsNewer) return ForkBannerState.None;

        if (needsNewer) return ForkBannerState.Warning;
        return forkHeight - tip <= WarningWindowBlocks ? ForkBannerState.Warning : ForkBannerState.Notice;
    }

    /// <summary>Whole days of blocks between the tip and the fork height (rounded down; 0 at or past it).</summary>
    public static long DaysUntil(long forkHeight, long tip) =>
        forkHeight <= tip ? 0 : (forkHeight - tip) / BlocksPerDay;

    /// <summary>
    /// True when <paramref name="minClientVersion"/> parses as a version strictly newer than
    /// <paramref name="clientVersion"/>. Either side unparsable → false (a garbled announcement
    /// must not shout at every client; the block-height branch still works).
    /// Missing components are treated as zero, so <c>2.0.5</c> == <c>2.0.5.0</c>.
    /// </summary>
    public static bool NeedsNewerClient(string? minClientVersion, string clientVersion)
    {
        if (!TryParse(minClientVersion, out var min) || !TryParse(clientVersion, out var mine)) return false;
        return min > mine;
    }

    // ── Wording (§7.1, verbatim) ───────────────────────────────────────────────────────────

    /// <summary>"about D days" / "less than a day" — the parenthetical in the Notice/Warning text.</summary>
    public static string DaysPhrase(long forkHeight, long tip)
    {
        var days = DaysUntil(forkHeight, tip);
        return days switch
        {
            0 => "less than a day",
            1 => "about 1 day",
            _ => $"about {days:N0} days",
        };
    }

    /// <summary>The build to fetch, as named in the text: <c>vY</c> when announced, else a plain phrase.</summary>
    public static string TargetVersionPhrase(string? minClientVersion) =>
        string.IsNullOrWhiteSpace(minClientVersion) ? "the latest version" : $"v{minClientVersion.Trim()}";

    /// <summary>The Notice / Warning headline: "Update required before block N".</summary>
    public static string UpdateHeadline(long forkHeight) => $"Update required before block {forkHeight:N0}";

    /// <summary>
    /// The Notice / Warning body (same text, only the colour and dismissibility differ):
    /// "(about D days). Your wallet (vX) will stop following the chain at that block. Get vY from
    /// blazecoin.co.uk/Wallet." The host is returned separately so a head can render it as a link.
    /// </summary>
    public static string UpdateBody(ForkStatus status, string clientVersion, long tip) =>
        $"({DaysPhrase(status.ForkHeight!.Value, tip)}). Your wallet (v{clientVersion}) will stop following " +
        $"the chain at that block. Get {TargetVersionPhrase(status.MinClientVersion)} from";

    /// <summary>The Stopped headline: "This wallet stopped at block N."</summary>
    public static string StoppedHeadline(long forkHeight) => $"This wallet stopped at block {forkHeight:N0}.";

    /// <summary>
    /// The Stopped body: "Balance last verified at block M. Funds are safe; install vY and restore
    /// from your recovery phrase." (link rendered by the head after it).
    /// </summary>
    public static string StoppedBody(ForkStatus status, long lastVerifiedHeight) =>
        $"Balance last verified at block {lastVerifiedHeight:N0}. Funds are safe; install " +
        $"{TargetVersionPhrase(status.MinClientVersion)} and restore from your recovery phrase.";

    /// <summary>
    /// The honest "last verified" height for the Stopped text: nothing at or past the fork
    /// height is verified by a build without the rules, so the figure is capped at
    /// <c>forkHeight − 1</c>; a head with a PoW-verified tip below that passes it in.
    /// </summary>
    public static long LastVerifiedHeight(long forkHeight, long? verifiedTip)
    {
        var cap = Math.Max(forkHeight - 1, 0);
        return verifiedTip is > 0 ? Math.Min(verifiedTip.Value, cap) : cap;
    }

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOfAny(['+', '-']);
        if (plus > 0) s = s[..plus];
        if (!System.Version.TryParse(s.Contains('.') ? s : s + ".0", out var parsed)) return false;
        version = new Version(
            Math.Max(parsed.Major, 0), Math.Max(parsed.Minor, 0),
            Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }
}

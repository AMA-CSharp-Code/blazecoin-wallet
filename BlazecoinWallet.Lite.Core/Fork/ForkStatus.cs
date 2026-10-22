namespace BlazecoinWallet.Lite.Fork;

/// <summary>
/// The gateway's fork announcement (PQ_SIGNATURES §7.1) as last read from <c>/api/status</c>.
/// All three announcement fields are <c>null</c> until the operator sets <c>Fork:*</c> in
/// gateway configuration — months before the fork height, per the notice period.
/// </summary>
/// <param name="ForkName">The fork codename (e.g. <c>pqsig</c>); compared case-insensitively
/// against <see cref="LiteClientInfo.SupportedForks"/>.</param>
/// <param name="ForkHeight">The activation height <c>H_Q</c>.</param>
/// <param name="MinClientVersion">The first lite build carrying the fork rules.</param>
/// <param name="Tip">The gateway's daemon height at the time of the read — block counts are the
/// authority for "how long is left", never the calendar (Phoenix lesson).</param>
/// <param name="FetchedAtUtc">When this status was read.</param>
public sealed record ForkStatus(
    string? ForkName,
    long? ForkHeight,
    string? MinClientVersion,
    long Tip,
    DateTime FetchedAtUtc)
{
    /// <summary>True when the operator has announced a fork (name + height both set).</summary>
    public bool IsAnnounced => ForkHeight.HasValue && !string.IsNullOrWhiteSpace(ForkName);
}

/// <summary>The one banner's three states (§7.1) plus "nothing to show".</summary>
public enum ForkBannerState
{
    /// <summary>No announcement, or this build already carries the fork rules.</summary>
    None,
    /// <summary>More than two weeks of blocks to go — dismissible per session.</summary>
    Notice,
    /// <summary>Within two weeks, or a newer client is required — amber, not dismissible; Send still works.</summary>
    Warning,
    /// <summary>The fork height has passed and this build lacks the rules — red, Send blocked.</summary>
    Stopped,
}

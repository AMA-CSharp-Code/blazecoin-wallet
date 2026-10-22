namespace BlazecoinWallet.Core.Services.AutoPayout;

/// <summary>Persisted auto-payout configuration (localStorage key
/// <c>blz_autopayout</c>). Schema is unchanged from when this lived in
/// Send.razor so existing saved settings keep loading.
///
/// NB: <see cref="Enabled"/> is written but deliberately NOT restored on load —
/// auto-payout always starts STOPPED so a money-mover can never resume sending
/// on its own after a restart/crash.</summary>
public sealed class AutoPayoutSettings
{
    public bool Enabled { get; set; }
    public decimal? Threshold { get; set; }
    public string Address { get; set; } = "";
    public string Label { get; set; } = "";
    public string Mode { get; set; } = "above";     // "above" | "reserve"
    public decimal? Reserve { get; set; }
    public string Batch { get; set; } = "single";    // "single" | "split"
    public decimal? BatchSize { get; set; }
    public int CooldownMin { get; set; }
    public decimal? DailyCap { get; set; }
}

/// <summary>Durable runtime accounting (localStorage key
/// <c>blz_autopayout_state</c>) — the cooldown timer and daily-cap tally — kept
/// separate from settings so a restart can't reset the cap and over-pay.</summary>
public sealed class AutoPayoutState
{
    public DateTime LastPayoutUtc { get; set; }
    public decimal SentToday { get; set; }
    public DateTime SentTodayDate { get; set; }
}

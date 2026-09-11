namespace BlazecoinWallet.Core.Services.Settings;

/// <summary>Typed accessors for the user-preference values kept in localStorage,
/// so pages stop repeating magic key strings and ad-hoc parsing (SOLID audit #4).
///
/// Validated getters return <c>null</c> when the stored value is missing or
/// invalid, so each page keeps its own field default and any domain-specific
/// clamping (e.g. mining threads clamped to the machine's core count). The
/// always-resolvable settings (explorer URL, confirm-send) return a concrete
/// value. Backed by <see cref="Storage.IKeyValueStore"/>.</summary>
public interface ISettingsService
{
    /// <summary>Saved dashboard skin id (legacy "pheonix" migrated to "phoenix"),
    /// or null if none saved. The page still validates it against its skin list.</summary>
    Task<string?> GetDashboardSkinAsync();
    Task SetDashboardSkinAsync(string id);

    /// <summary>Dashboard auto-refresh seconds (>= 0), or null.</summary>
    Task<int?> GetRefreshSecsAsync();
    Task SetRefreshSecsAsync(int secs);

    /// <summary>Confirmations before "Confirmed" (> 0), or null.</summary>
    Task<int?> GetConfThresholdAsync();
    Task SetConfThresholdAsync(int n);

    /// <summary>Amount display unit ("BLZ" or "mBLZ"), or null.</summary>
    Task<string?> GetAmountUnitAsync();
    Task SetAmountUnitAsync(string unit);

    /// <summary>Amount display decimals (> 0), or null.</summary>
    Task<int?> GetAmountDecimalsAsync();
    Task SetAmountDecimalsAsync(int d);

    /// <summary>Block-explorer URL template, or "" if unset.</summary>
    Task<string> GetExplorerUrlAsync();
    Task SetExplorerUrlAsync(string url);

    /// <summary>Whether Send shows a review/confirm step (default true).</summary>
    Task<bool> GetConfirmSendAsync();
    Task SetConfirmSendAsync(bool on);

    /// <summary>Auto-lock seconds (> 0), or null.</summary>
    Task<int?> GetAutoLockSecsAsync();
    Task SetAutoLockSecsAsync(int secs);

    /// <summary>Saved mining thread count (>= 1), or null. Page clamps to cores.</summary>
    Task<int?> GetMiningThreadsAsync();
    Task SetMiningThreadsAsync(int n);

    /// <summary>Saved mining throttle percent (0..90), or null.</summary>
    Task<int?> GetMiningThrottleAsync();
    Task SetMiningThrottleAsync(int n);

    /// <summary>Remove every preference key (the Preferences "reset to defaults").
    /// Pages still set their own field defaults afterwards.</summary>
    Task ResetAsync();
}

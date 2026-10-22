using BlazecoinWallet.Core.Services.Storage;

namespace BlazecoinWallet.Core.Services.Settings;

/// <summary>Default <see cref="ISettingsService"/> — the single home for the
/// preference keys and their parse/validation rules, over a key/value store.
/// Validation matches what the pages did inline so behaviour is unchanged.</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly IKeyValueStore _store;

    public SettingsService(IKeyValueStore store) => _store = store;

    // Single source of truth for the preference keys.
    private const string DashboardSkin = "dashboard_skin";
    private const string ResolvedRandom = "resolved_random";
    private const string RefreshSecs = "dashboard_refresh_secs";
    private const string ConfThreshold = "conf_threshold";
    private const string MiningThreads = "mining_threads";
    private const string MiningThrottle = "mining_throttle";
    private const string AmountUnit = "amount_unit";
    private const string AmountDecimals = "amount_decimals";
    private const string ExplorerUrl = "explorer_url";
    /// <summary>Fresh-profile explorer (2026-09-05): the production site's permalink root. A saved "" disables links.</summary>
    public const string DefaultExplorerUrl = "https://blazecoin.co.uk";
    private const string ConfirmSend = "confirm_send";
    private const string AutoLockSecs = "auto_lock_secs";

    public async Task<string?> GetDashboardSkinAsync()
    {
        var v = await _store.GetAsync(DashboardSkin);
        if (v == "pheonix") v = "phoenix"; // migrate old typo'd id
        return string.IsNullOrEmpty(v) ? null : v;
    }
    public Task SetDashboardSkinAsync(string id) => _store.SetAsync(DashboardSkin, id);

    public async Task<int?> GetRefreshSecsAsync()
        => int.TryParse(await _store.GetAsync(RefreshSecs), out var n) && n >= 0 ? n : null;
    public Task SetRefreshSecsAsync(int secs) => _store.SetAsync(RefreshSecs, secs.ToString());

    public async Task<int?> GetConfThresholdAsync()
        => int.TryParse(await _store.GetAsync(ConfThreshold), out var n) && n > 0 ? n : null;
    public Task SetConfThresholdAsync(int n) => _store.SetAsync(ConfThreshold, n.ToString());

    public async Task<string?> GetAmountUnitAsync()
    {
        var v = await _store.GetAsync(AmountUnit);
        return v is "BLZ" or "mBLZ" ? v : null;
    }
    public Task SetAmountUnitAsync(string unit) => _store.SetAsync(AmountUnit, unit);

    public async Task<int?> GetAmountDecimalsAsync()
        => int.TryParse(await _store.GetAsync(AmountDecimals), out var n) && n > 0 ? n : null;
    public Task SetAmountDecimalsAsync(int d) => _store.SetAsync(AmountDecimals, d.ToString());

    public async Task<string> GetExplorerUrlAsync() => await _store.GetAsync(ExplorerUrl) ?? DefaultExplorerUrl;
    public Task SetExplorerUrlAsync(string url) => _store.SetAsync(ExplorerUrl, url?.Trim() ?? "");

    public async Task<bool> GetConfirmSendAsync() => await _store.GetAsync(ConfirmSend) != "false";
    public Task SetConfirmSendAsync(bool on) => _store.SetAsync(ConfirmSend, on ? "true" : "false");

    public async Task<int?> GetAutoLockSecsAsync()
        => int.TryParse(await _store.GetAsync(AutoLockSecs), out var n) && n > 0 ? n : null;
    public Task SetAutoLockSecsAsync(int secs) => _store.SetAsync(AutoLockSecs, secs.ToString());

    public async Task<int?> GetMiningThreadsAsync()
        => int.TryParse(await _store.GetAsync(MiningThreads), out var n) && n >= 1 ? n : null;
    public Task SetMiningThreadsAsync(int n) => _store.SetAsync(MiningThreads, n.ToString());

    public async Task<int?> GetMiningThrottleAsync()
        => int.TryParse(await _store.GetAsync(MiningThrottle), out var n) && n is >= 0 and <= 90 ? n : null;
    public Task SetMiningThrottleAsync(int n) => _store.SetAsync(MiningThrottle, n.ToString());

    public async Task ResetAsync()
    {
        // Same key set the Preferences reset always cleared (resolved_random is a
        // skin cache; removing it here matches the prior behaviour).
        foreach (var k in new[]
        {
            DashboardSkin, ResolvedRandom, RefreshSecs, ConfThreshold, MiningThreads,
            MiningThrottle, AmountUnit, AmountDecimals, ExplorerUrl, ConfirmSend, AutoLockSecs,
        })
            await _store.RemoveAsync(k);
    }
}

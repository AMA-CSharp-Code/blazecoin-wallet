using System.Text.Json;
using BlazecoinWallet.Core.Services.Storage;

namespace BlazecoinWallet.Core.Services.AutoPayout;

/// <summary>Auto-payout engine. Logic is a faithful extraction of what used to
/// live in Send.razor's @code block — see <see cref="IAutoPayoutService"/> for
/// the contract and threading model.</summary>
public sealed class AutoPayoutService : IAutoPayoutService
{
    private readonly IWalletRpc _rpc;
    private readonly IKeyValueStore _store;

    public AutoPayoutService(IWalletRpc rpc, IKeyValueStore store)
    {
        _rpc = rpc;
        _store = store;
    }

    private const string SettingsKey = "blz_autopayout";
    private const string StateKey = "blz_autopayout_state";
    private const string InflightKey = "blz_autopayout_inflight";

    // ----- Settings -----
    public decimal? Threshold { get; set; }
    public string Address { get; set; } = "";
    public string Label { get; set; } = "";
    public string Mode { get; set; } = "above";
    public decimal? Reserve { get; set; }
    public string Batch { get; set; } = "single";
    public decimal? BatchSize { get; set; } = 30000m;
    public int CooldownMin { get; set; } = 10;
    public decimal? DailyCap { get; set; }
    public bool Live { get; set; }

    // ----- Runtime -----
    public bool Enabled { get; private set; }
    public decimal Spendable { get; private set; }
    public string Reason { get; private set; } = "";
    private readonly List<string> _log = new();
    public IReadOnlyList<string> Log => _log;
    public bool PayoutInFlight { get; private set; }

    public bool ManualSendInProgress { get; set; }
    public Func<string, string, Task>? SavePayee { get; set; }

    public event Action? Changed;
    private void Notify() => Changed?.Invoke();

    // Durable accounting (cooldown timer + daily-cap tally).
    private DateTime _lastPayoutUtc = DateTime.MinValue;
    private decimal _sentToday;
    private DateTime _sentTodayDate = DateTime.MinValue;

    // 0 = idle, 1 = running — atomic guard against concurrent ticks.
    private int _running;

    // SegWit is consensus-disabled on this chain (SegwitHeight = INT_MAX), so coins
    // sent to a bech32 (blz1...) address are unspendable and permanently lost. Block them.
    private static bool IsUnsafeSegwitAddress(string? addr) =>
        (addr ?? "").Trim().StartsWith("blz1", StringComparison.OrdinalIgnoreCase);

    public async Task StartAsync()
    {
        Enabled = true;
        await SaveSettingsAsync();
        await TickAsync(fire: false);
    }

    public async Task StopAsync()
    {
        Enabled = false;
        Reason = "";
        await SaveSettingsAsync();
    }

    public async Task RefreshSpendableAsync()
    {
        try { Spendable = (await _rpc.GetBalancesAsync())?.Mine?.Trusted ?? 0m; } catch { }
    }

    // Custom up/down arrows step the number fields by a sensible per-field increment.
    public async Task StepAsync(string field, int dir)
    {
        switch (field)
        {
            case "threshold": Threshold = Bump(Threshold, dir, 10m);   break;
            case "reserve":   Reserve   = Bump(Reserve,   dir, 100m);  break;
            case "batch":     BatchSize = Bump(BatchSize, dir, 10m);   break;
            case "cap":       DailyCap  = Bump(DailyCap,  dir, 1000m); break;
            case "cooldown":  CooldownMin = Math.Max(0, CooldownMin + dir); break;
        }
        await SaveSettingsAsync();
    }

    private static decimal? Bump(decimal? v, int dir, decimal step)
    {
        var n = (v ?? 0m) + dir * step;
        return n < 0m ? 0m : n;
    }

    private void AddLog(string msg)
    {
        _log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {msg}");
        if (_log.Count > 40) _log.RemoveRange(40, _log.Count - 40);
    }

    // ----- Persistence -----
    public async Task SaveSettingsAsync()
    {
        var s = new AutoPayoutSettings
        {
            Enabled = Enabled, Threshold = Threshold, Address = Address, Label = Label,
            Mode = Mode, Reserve = Reserve, Batch = Batch,
            BatchSize = BatchSize, CooldownMin = CooldownMin, DailyCap = DailyCap,
        };
        await _store.SetAsync(SettingsKey, JsonSerializer.Serialize(s));
    }

    public async Task LoadAsync()
    {
        await LoadSettingsAsync();
        await LoadStateAsync();
        await CheckInflightOnLoadAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var json = await _store.GetAsync(SettingsKey);
        if (string.IsNullOrEmpty(json)) return;
        AutoPayoutSettings? s;
        try { s = JsonSerializer.Deserialize<AutoPayoutSettings>(json); }
        catch { return; }
        if (s is null) return;
        // NB: Enabled is deliberately NOT restored — always starts STOPPED so a
        // money-mover can never resume sending on its own after a restart/crash.
        Threshold = s.Threshold; Address = s.Address ?? ""; Label = s.Label ?? "";
        Mode = s.Mode ?? "above"; Reserve = s.Reserve; Batch = s.Batch ?? "single";
        BatchSize = s.BatchSize ?? 30000m; CooldownMin = s.CooldownMin; DailyCap = s.DailyCap;
    }

    private async Task SaveStateAsync()
    {
        var st = new AutoPayoutState { LastPayoutUtc = _lastPayoutUtc, SentToday = _sentToday, SentTodayDate = _sentTodayDate };
        await _store.SetAsync(StateKey, JsonSerializer.Serialize(st));
    }

    private async Task LoadStateAsync()
    {
        var json = await _store.GetAsync(StateKey);
        if (string.IsNullOrEmpty(json)) return;
        AutoPayoutState? st;
        try { st = JsonSerializer.Deserialize<AutoPayoutState>(json); }
        catch { return; }
        if (st is null) return;
        _lastPayoutUtc = st.LastPayoutUtc; _sentToday = st.SentToday; _sentTodayDate = st.SentTodayDate;
    }

    // Crash recovery: a marker is written before a live batch loop and cleared after.
    // If it's still set on next launch, the app died mid-payout — warn, don't re-fire.
    private async Task SetInflightAsync(string? info)
    {
        if (info is null) await _store.RemoveAsync(InflightKey);
        else await _store.SetAsync(InflightKey, info);
    }

    private async Task CheckInflightOnLoadAsync()
    {
        var info = await _store.GetAsync(InflightKey);
        if (!string.IsNullOrEmpty(info))
        {
            AddLog($"⚠ A previous LIVE payout may have been interrupted ({info}). Check your Transactions before re-arming.");
            await SetInflightAsync(null);
        }
    }

    // ----- The engine -----
    public async Task TickAsync(bool fire)
    {
        if (!Enabled) return;
        // Atomic guard: only one tick body runs at a time, whatever the thread.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            var spendable = (await _rpc.GetBalancesAsync())?.Mine?.Trusted ?? 0m;
            Spendable = spendable;

            if (Threshold is not decimal thr || thr < 0) { Reason = "Set a threshold (0 or more)."; Notify(); return; }
            if (string.IsNullOrWhiteSpace(Address)) { Reason = "Set a destination address."; Notify(); return; }
            if (IsUnsafeSegwitAddress(Address)) { Reason = "Paused: destination is a blz1/SegWit address (unsafe)."; Notify(); return; }
            if (spendable <= thr) { Reason = $"Waiting: spendable {spendable:N4} not above threshold {thr:N4}."; Notify(); return; }
            var sinceLast = (DateTime.UtcNow - _lastPayoutUtc).TotalMinutes;
            if (fire && sinceLast < CooldownMin) { Reason = $"Cooldown: {(CooldownMin - sinceLast):N1} min left."; Notify(); return; }

            if (_sentTodayDate.Date != DateTime.UtcNow.Date) { _sentTodayDate = DateTime.UtcNow; _sentToday = 0m; }

            decimal amt = Mode == "reserve" ? spendable - (Reserve ?? 0m) : spendable - thr;
            if (amt <= 0m) { Reason = "Nothing above the threshold/reserve to send."; Notify(); return; }
            if (amt > spendable) amt = spendable;

            if (DailyCap is decimal cap && cap > 0m)
            {
                var room = cap - _sentToday;
                if (room <= 0m) { Reason = $"Daily cap {cap:N4} BLZ reached for today."; Notify(); return; }
                if (amt > room) amt = room;
            }

            // Split into batches if requested.
            var batches = new List<decimal>();
            if (Batch == "split" && BatchSize is decimal bs && bs > 0m)
            {
                var rem = amt;
                while (rem > 0m) { var b = Math.Min(bs, rem); batches.Add(b); rem -= b; }
            }
            else batches.Add(amt);

            // Status-only refresh (Start / page load): show what WOULD happen, but never
            // fire — that keeps the pump the sole payout source, so no double-trigger.
            if (!fire)
            {
                Reason = $"Ready — would send {amt:N4} BLZ in {batches.Count} tx on the next check.";
                Notify();
                return;
            }

            // Mutual exclusion: never fire a live payout while a manual Send is mid-flight
            // (they'd contend for the same UTXOs). Defer to the next check.
            if (Live && ManualSendInProgress) { Reason = "Waiting: a manual send is in progress."; Notify(); return; }

            var lbl = string.IsNullOrWhiteSpace(Label) ? "" : $" ({Label.Trim()})";

            if (!Live)
            {
                AddLog($"DRY RUN: would send {amt:N8} BLZ to {Address.Trim()}{lbl} as {batches.Count} tx [{string.Join(", ", batches.Select(b => b.ToString("N2")))}].");
                Reason = $"Dry-run fired ({batches.Count} tx) — auto-payout stopped. Press Start to arm again.";
                _lastPayoutUtc = DateTime.UtcNow;
                _sentToday += amt;
            }
            else // LIVE — real broadcasts: per-batch commit, stop on the first failure.
            {
                PayoutInFlight = true;
                await SetInflightAsync($"{amt:N4} BLZ to {Address.Trim()} at {DateTime.Now:HH:mm:ss}");
                int done = 0; decimal sentAmt = 0m;
                foreach (var b in batches)
                {
                    try
                    {
                        var txid = await _rpc.SendToAddressAsync(Address.Trim(), b);
                        if (string.IsNullOrEmpty(txid)) { AddLog($"FAILED tx {done + 1}/{batches.Count} ({b:N4} BLZ): no txid returned — stopping."); break; }
                        done++; sentAmt += b;
                        _sentToday += b; _lastPayoutUtc = DateTime.UtcNow;
                        await SaveStateAsync();   // durability: commit accounting after each confirmed tx
                        AddLog($"SENT {b:N8} BLZ -> {txid}  ({done}/{batches.Count})");
                        Notify();
                    }
                    catch (Exception ex) { AddLog($"FAILED tx {done + 1}/{batches.Count} ({b:N4} BLZ): {ex.Message} — stopping."); break; }
                }
                await SetInflightAsync(null);
                PayoutInFlight = false;
                if (done > 0 && !string.IsNullOrWhiteSpace(Label) && SavePayee != null) await SavePayee(Address.Trim(), Label.Trim());
                Reason = done == batches.Count
                    ? $"Payout complete — sent {sentAmt:N4} BLZ in {done} tx. Auto-payout stopped."
                    : $"Payout PARTIAL — sent {sentAmt:N4} BLZ in {done}/{batches.Count} tx, then stopped. Check the log.";
            }

            await SaveStateAsync();   // persist final cooldown/daily-cap state (durability)

            // One-shot: disarm after a payout so it can never repeatedly fire. The stopped
            // state is persisted, so a restart won't re-fire either. Re-arm with Start.
            Enabled = false;
            await SaveSettingsAsync();
            Notify();
        }
        catch (Exception ex) { AddLog($"Error: {ex.Message}"); Reason = $"Error: {ex.Message}"; Notify(); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}

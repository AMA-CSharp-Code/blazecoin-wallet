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
    private readonly IWalletBinding _wallets;
    private readonly Send.ISendRpc _sender;
    private readonly Send.SendChangePolicy _changePolicy;

    /// <summary>All five collaborators are REQUIRED (audit 2026-11-09). Three of them used to be
    /// optional, and leaving one out switched a safeguard off without a word: no
    /// <paramref name="wallets"/> and a payout went to whatever wallet the RPC layer addressed; no
    /// <paramref name="sender"/> or <paramref name="changePolicy"/> and it went out as a plain
    /// <c>sendtoaddress</c> with change on the node's default — legacy.</summary>
    /// <param name="wallets">How the engine gets the wallet it is armed on: it remembers WHICH
    /// wallet that was, stops if the active wallet is no longer that one, and sends from exactly
    /// that wallet (bug-bounty report 2026-11-06) — and it does not arm at all on a wallet the app
    /// switched to and the user has not acknowledged (audit 2026-11-09).</param>
    /// <param name="sender">Every payout goes out through the send path that names its wallet and
    /// can choose where change returns, so a wallet with post-quantum keys keeps its change behind
    /// them (bug-bounty report 2026-11-06, fourth round: an auto-payout from a BQ… coin put the
    /// change on an ECDSA address).</param>
    public AutoPayoutService(IWalletRpc rpc, IKeyValueStore store, IWalletBinding wallets,
        Send.ISendRpc sender, Send.SendChangePolicy changePolicy)
    {
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _changePolicy = changePolicy ?? throw new ArgumentNullException(nameof(changePolicy));
    }

    /// <summary>The wallet the engine was armed on ("" = the node's default wallet endpoint);
    /// null when it is not armed.</summary>
    public string? ArmedWallet { get; private set; }

    /// <summary>
    /// True when the engine was armed on one wallet and the app is now on another
    /// (<paramref name="activeNow"/>). A balance read can re-point the app at a different wallet
    /// when the selected one is no longer loaded on the node; an armed payout must never follow
    /// it there — the threshold, the figures and the user's decision were all about the wallet
    /// it was armed on. The comparison is the binding's, the same one the Send page and the
    /// Console use (audit 2026-11-09).
    /// </summary>
    private bool WalletChangedSinceArmed(out string? activeNow)
    {
        activeNow = null;
        if (ArmedWallet is not string armed) return false;
        var still = _wallets.IsStillActive(armed, out var now);
        activeNow = now;
        return !still;
    }

    private static string WalletLabel(string? wallet) => string.IsNullOrEmpty(wallet) ? "(default)" : $"\"{wallet}\"";

    private async Task StopForWalletChangeAsync(string during, string? activeNow)
    {
        var message = $"Auto-payout STOPPED {during}: it was armed on wallet {WalletLabel(ArmedWallet)} and the active wallet " +
                      $"is now {WalletLabel(activeNow)}. Press Start to arm it on the wallet you mean.";
        AddLog("⚠ " + message);
        Reason = message;
        Enabled = false;
        ArmedWallet = null;
        await SaveSettingsAsync();
        Notify();
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
    /// <summary>True from a press of Start that was refused until the next Start or Stop.</summary>
    private bool _startRefused;
    public bool ShowReason => (Enabled || _startRefused) && !string.IsNullOrEmpty(Reason);

    /// <summary>A press of Start that must not arm the engine: nothing is armed, and the reason is
    /// shown under the button (<see cref="ShowReason"/>) and kept in the log.</summary>
    private void RefuseStart(string why)
    {
        ArmedWallet = null;
        Enabled = false;
        _startRefused = true;
        Reason = why;
        AddLog("⚠ Auto-payout NOT started: " + why);
        Notify();
    }
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

    public async Task StartAsync()
    {
        // What the user is looking at when they press Start — unless the app has just moved to
        // that wallet by itself and the user has not acknowledged it: then nothing is armed, and
        // the refusal (it names both wallets) is shown under the button and logged (audit 2026-11-09).
        _startRefused = false;
        // An amount field holds text that is not an amount: the engine would run on the value the
        // setting had before, which is not what the page shows. This was only a disabled button
        // until the audit of 2026-11-09.
        if (HasUnreadableAmount)
        {
            RefuseStart("An amount could not be read, so that setting was left as it was. Correct the marked field, then press Start. " + TypedAmount.InvalidMessage);
            return;
        }
        try { ArmedWallet = _wallets.BindActiveForWrite(); }
        catch (WalletSwitchPendingException ex) { RefuseStart(ex.Message); return; }
        Enabled = true;
        await SaveSettingsAsync();
        await TickAsync(fire: false);
    }

    public async Task StopAsync()
    {
        Enabled = false;
        ArmedWallet = null;
        _startRefused = false;
        Reason = "";
        await SaveSettingsAsync();
    }

    public async Task RefreshSpendableAsync()
    {
        try { Spendable = (await _rpc.GetBalancesAsync())?.Mine?.Trusted ?? 0m; } catch { }
    }

    // Custom up/down arrows step the number fields by a sensible per-field increment.
    public async Task StepAsync(AutoPayoutField field, int dir)
    {
        _unreadable.Remove(field);   // the arrows set a real number
        switch (field)
        {
            case AutoPayoutField.Threshold: Threshold = Bump(Threshold, dir, 10m);   break;
            case AutoPayoutField.Reserve:   Reserve   = Bump(Reserve,   dir, 100m);  break;
            case AutoPayoutField.BatchSize: BatchSize = Bump(BatchSize, dir, 10m);   break;
            case AutoPayoutField.DailyCap:  DailyCap  = Bump(DailyCap,  dir, 1000m); break;
            case AutoPayoutField.Cooldown:  CooldownMin = Math.Max(0, CooldownMin + dir); break;
        }
        await SaveSettingsAsync();
    }

    // ----- The four amounts as typed -----
    // Text that is not an amount, by field. The setting itself keeps its previous value.
    private readonly Dictionary<AutoPayoutField, string> _unreadable = new();

    public bool HasUnreadableAmount => _unreadable.Count > 0;
    public bool IsUnreadable(AutoPayoutField field) => _unreadable.ContainsKey(field);

    public string AmountText(AutoPayoutField field)
    {
        if (_unreadable.TryGetValue(field, out var asTyped)) return asTyped;
        var shown = new TypedAmount();
        shown.SetValue(Amount(field));
        return shown.Text;
    }

    public void SetAmountText(AutoPayoutField field, string? text)
    {
        _ = Amount(field);           // an amount field, or it throws
        var typed = new TypedAmount();
        typed.SetText(text);
        if (typed.IsInvalid) { _unreadable[field] = typed.Text; return; }   // the setting is left as it was
        _unreadable.Remove(field);
        switch (field)
        {
            case AutoPayoutField.Threshold: Threshold = typed.Value; break;
            case AutoPayoutField.Reserve:   Reserve   = typed.Value; break;
            case AutoPayoutField.BatchSize: BatchSize = typed.Value; break;
            case AutoPayoutField.DailyCap:  DailyCap  = typed.Value; break;
        }
    }

    private decimal? Amount(AutoPayoutField field) => field switch
    {
        AutoPayoutField.Threshold => Threshold,
        AutoPayoutField.Reserve => Reserve,
        AutoPayoutField.BatchSize => BatchSize,
        AutoPayoutField.DailyCap => DailyCap,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not an amount field."),
    };

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
            // Checked AFTER the read on purpose: the read itself is what re-points the app at
            // another wallet when the armed one is gone, and this balance would then be that
            // other wallet's.
            if (WalletChangedSinceArmed(out var movedTo)) { await StopForWalletChangeAsync("before sending anything", movedTo); return; }
            Spendable = spendable;

            if (Threshold is not decimal thr || thr < 0) { Reason = "Set a threshold (0 or more)."; Notify(); return; }
            if (string.IsNullOrWhiteSpace(Address)) { Reason = "Set a destination address."; Notify(); return; }
            // Only a legacy B… or a post-quantum BQ… address is ever paid (PayableAddress — one rule
            // for every send; this was a check of one prefix, blz1, until the audit of 2026-11-09).
            if (PayableAddress.Problem(Address, "destination") is { } notPayable) { Reason = "Paused: " + notPayable; Notify(); return; }
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
                // A rehearsal sends nothing, so it uses up nothing: neither the day's cap nor the
                // cooldown. It used to charge both and persist them (bug-bounty report 2026-11-06,
                // seventh round), so the real payout that followed a dry run was refused — "Daily
                // cap reached for today" — until midnight UTC, with not one transaction attempted.
            }
            else // LIVE — real broadcasts: per-batch commit, stop on the first failure.
            {
                PayoutInFlight = true;
                await SetInflightAsync($"{amt:N4} BLZ to {Address.Trim()} at {DateTime.Now:HH:mm:ss}");
                int done = 0; decimal sentAmt = 0m;
                var walletChanged = false;
                string? changedTo = null;
                // Where change goes, decided once for the whole payout: post-quantum when this wallet can.
                var changeKind = (await _changePolicy.DecideAsync()).Change;
                foreach (var b in batches)
                {
                    try
                    {
                        // Each batch comes out of the wallet the engine was armed on, by name —
                        // or not at all.
                        if (WalletChangedSinceArmed(out changedTo)) { walletChanged = true; break; }
                        if (ArmedWallet is not string armed) { AddLog($"FAILED tx {done + 1}/{batches.Count} ({b:N4} BLZ): the engine is not armed on a wallet — stopping."); break; }
                        // One path for every payout (audit 2026-11-09): the send that names its
                        // wallet. A wallet without post-quantum keys gets no change type (asking
                        // for "pq" there would fail the send) — but still by name.
                        var txid = await _sender.SendAsync(new Send.SendRequest(Address.Trim(), b, false, changeKind) { Wallet = armed });
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
                if (walletChanged)
                {
                    await SaveStateAsync();
                    await StopForWalletChangeAsync($"after {done} of {batches.Count} tx", changedTo);
                    return;
                }
            }

            await SaveStateAsync();   // persist final cooldown/daily-cap state (durability)

            // One-shot: disarm after a payout so it can never repeatedly fire. The stopped
            // state is persisted, so a restart won't re-fire either. Re-arm with Start.
            Enabled = false;
            ArmedWallet = null;
            await SaveSettingsAsync();
            Notify();
        }
        catch (Exception ex) { AddLog($"Error: {ex.Message}"); Reason = $"Error: {ex.Message}"; Notify(); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}

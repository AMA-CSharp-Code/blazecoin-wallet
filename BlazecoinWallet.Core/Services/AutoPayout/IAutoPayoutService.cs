namespace BlazecoinWallet.Core.Services.AutoPayout;

/// <summary>The auto-payout engine, extracted from Send.razor so the
/// money-moving logic (threshold/cooldown/daily-cap evaluation, batching,
/// dry-run vs live execution, durable accounting and crash recovery) lives in a
/// testable service rather than the UI.
///
/// Lifecycle: the Send page drives a 20-second pump that calls
/// <see cref="TickAsync"/> on the UI thread, and subscribes to <see cref="Changed"/>
/// to re-render. The service holds no timer of its own, so it runs only while
/// the page keeps pumping it — preserving the original "runs only while this
/// page is open" behaviour.
///
/// Settings properties are two-way bound by the UI. Runtime properties are
/// read-only to callers and mutated only inside the engine.</summary>
public interface IAutoPayoutService
{
    // ----- Settings (two-way bound in the UI; persisted via SaveSettingsAsync) -----
    decimal? Threshold { get; set; }
    string Address { get; set; }
    string Label { get; set; }
    string Mode { get; set; }          // "above" | "reserve"
    decimal? Reserve { get; set; }
    string Batch { get; set; }         // "single" | "split"
    decimal? BatchSize { get; set; }
    int CooldownMin { get; set; }
    decimal? DailyCap { get; set; }

    /// <summary>Live (real broadcasts) vs dry-run. NEVER restored on load —
    /// always starts dry-run. Set only via an explicit user confirmation.</summary>
    bool Live { get; set; }

    // ----- Runtime state (read by the UI) -----
    /// <summary>True while armed/watching. Always starts false on load.</summary>
    bool Enabled { get; }
    decimal Spendable { get; }
    string Reason { get; }
    IReadOnlyList<string> Log { get; }
    /// <summary>True while a LIVE batch loop is broadcasting — the manual Send
    /// form checks this for mutual exclusion over the same UTXOs.</summary>
    bool PayoutInFlight { get; }

    // ----- Coordination with the manual Send form -----
    /// <summary>Set by the page while a manual send is mid-flight; the engine
    /// defers a live payout while it's true (mutual exclusion).</summary>
    bool ManualSendInProgress { get; set; }

    /// <summary>Optional hook the page wires to its address-book save, so a
    /// successful live payout records the payee (label, address) the same way a
    /// manual send does — without coupling the engine to the address-book store.</summary>
    Func<string, string, Task>? SavePayee { get; set; }

    /// <summary>Raised when runtime state changes mid-tick so the page can
    /// re-render (log lines, reason, spendable, enabled flag).</summary>
    event Action? Changed;

    /// <summary>Restore settings + durable accounting and surface a warning if a
    /// previous live payout was interrupted. Does NOT restore Enabled/Live.</summary>
    Task LoadAsync();
    Task SaveSettingsAsync();

    /// <summary>Arm the watcher (and do an immediate status-only refresh).</summary>
    Task StartAsync();
    /// <summary>Disarm the watcher.</summary>
    Task StopAsync();

    /// <summary>Bump a numeric setting from the UI spinner arrows and save.
    /// field ∈ {threshold, reserve, batch, cap, cooldown}.</summary>
    Task StepAsync(string field, int dir);

    /// <summary>Read the current spendable balance into <see cref="Spendable"/>.</summary>
    Task RefreshSpendableAsync();

    /// <summary>One evaluation. <paramref name="fire"/> = false is status-only
    /// (Start / page load): it shows what WOULD happen but never sends, keeping
    /// the page's 20s pump the sole payout source.</summary>
    Task TickAsync(bool fire);
}

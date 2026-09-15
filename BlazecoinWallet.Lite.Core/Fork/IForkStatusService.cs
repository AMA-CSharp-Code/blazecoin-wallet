namespace BlazecoinWallet.Lite.Fork;

/// <summary>
/// The head's view of the gateway's fork announcement (§7.1): the last status read, the banner
/// state it evaluates to for THIS build, and the per-session Notice dismissal. One instance per
/// head, polled while the app is open (30 s, the chain's block spacing) and on demand.
/// </summary>
public interface IForkStatusService
{
    /// <summary>The last successfully read status; null until the first read succeeds.</summary>
    ForkStatus? Current { get; }

    /// <summary>The banner state for this build against <see cref="Current"/>.</summary>
    ForkBannerState State { get; }

    /// <summary>Raised (on a worker thread) whenever <see cref="Current"/> changes.</summary>
    event Action? Changed;

    /// <summary>Starts the 30-second poll loop (idempotent). Runs only while the app is open.</summary>
    void Start();

    /// <summary>Stops the poll loop.</summary>
    void Stop();

    /// <summary>Reads the gateway status now. Failures are swallowed — the previous status stands.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>True once the user has dismissed the Notice this session (returns on the next launch).</summary>
    bool NoticeDismissed { get; }

    /// <summary>Dismisses the Notice for this session. Warning/Stopped are never dismissible.</summary>
    void DismissNotice();
}

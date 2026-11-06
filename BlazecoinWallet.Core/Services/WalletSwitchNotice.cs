namespace BlazecoinWallet.Core.Services;

/// <summary>The app re-pointed itself at a different wallet without being asked.</summary>
/// <param name="From">The wallet that was active and is no longer loaded on the node (null when none was chosen).</param>
/// <param name="To">The wallet now active.</param>
public sealed record WalletSwitch(string? From, string To, DateTime AtUtc);

/// <summary>
/// Tells the user when the active wallet was changed FOR them. When the selected wallet is no
/// longer loaded on the node (it was unloaded, or the daemon restarted without it), a balance or
/// history read re-points the app at a wallet that is loaded so the window does not dead-end —
/// but until 2026-11-06 the only trace of that was a log line, and every figure on screen
/// quietly became another wallet's (bug-bounty report 2026-11-06). The layout shows this until
/// the user dismisses it.
/// </summary>
public interface IWalletSwitchNotice
{
    /// <summary>The switch the user has not acknowledged yet, or null.</summary>
    WalletSwitch? Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes (possibly on a worker thread).</summary>
    event Action? Changed;

    void Record(string? from, string to);

    void Dismiss();
}

public sealed class WalletSwitchNotice : IWalletSwitchNotice
{
    private readonly Func<DateTime> _clock;
    private WalletSwitch? _current;

    public WalletSwitchNotice() : this(null) { }
    public WalletSwitchNotice(Func<DateTime>? clock) => _clock = clock ?? (() => DateTime.UtcNow);

    public WalletSwitch? Current => _current;

    public event Action? Changed;

    public void Record(string? from, string to)
    {
        // Keep the ORIGINAL wallet if several switches pile up unacknowledged: what the user
        // needs to know is which wallet they were on when they last looked.
        var original = _current?.From ?? (string.IsNullOrEmpty(from) ? null : from);
        _current = new WalletSwitch(original, to, _clock());
        Raise();
    }

    public void Dismiss()
    {
        if (_current == null) return;
        _current = null;
        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch { /* a broken subscriber must not break an RPC call */ }
    }
}

/// <summary>
/// The wallet a call was meant for is not loaded on the node, and the call was one that must
/// not be redirected to another wallet (anything that moves coins, signs, hands out an
/// address, or changes a wallet's passphrase). Carries RPC code -18 like the daemon's own
/// error, with a message that says what did NOT happen.
/// </summary>
public sealed class WalletNotLoadedException : RpcException
{
    /// <summary>The wallet the call was for ("" = the node's default wallet endpoint).</summary>
    public string Wallet { get; }

    public WalletNotLoadedException(string? wallet)
        : base(-18, string.IsNullOrEmpty(wallet)
            ? "No wallet is loaded on the node, so nothing was done. Load or choose a wallet and try again."
            : $"The wallet \"{wallet}\" is not loaded on the node, so nothing was done. Check which wallet is active and try again.")
    {
        Wallet = wallet ?? string.Empty;
    }
}

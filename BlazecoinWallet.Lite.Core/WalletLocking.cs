namespace BlazecoinWallet.Lite;

/// <summary>
/// The head-agnostic "lock the wallet now" verb the shared UI calls (header 🔒 button,
/// idle timer). What locking MEANS is per head: on Android the keys live in the Keystore and
/// a re-lock is the PIN gate (<see cref="AppLockSession"/>); on the web head the keys live in
/// the browser's password vault, so a real lock DROPS them from memory and forces the
/// password ceremony again. Heads register their own; the default below covers every head
/// that only has the PIN gate.
/// </summary>
public interface IWalletLocker
{
    /// <summary>False when locking would achieve nothing visible (no PIN set, no re-lockable
    /// vault) — the UI hides the button rather than offer a no-op.</summary>
    bool CanLock { get; }

    /// <summary>Lock now. Must be safe to call repeatedly and while already locked.</summary>
    Task LockAsync();
}

/// <summary>Default: the PIN gate is the only lock (Android/iOS/dev host). Locking with no
/// PIN set does nothing, so <see cref="CanLock"/> follows <see cref="IPinLock.IsSet"/>.</summary>
public sealed class PinGateWalletLocker(IPinLock pin, AppLockSession session) : IWalletLocker
{
    public bool CanLock => pin.IsSet;

    public Task LockAsync()
    {
        if (pin.IsSet) session.Lock();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Idle auto-lock policy. Persisted per head; heads that have no idle concept (Android
/// re-locks on backgrounding instead) keep the in-memory default with
/// <see cref="Supported"/> = false, which hides the Settings card.
/// </summary>
public interface IAutoLockSettings
{
    /// <summary>Whether this head implements idle auto-lock at all (drives the Settings card).</summary>
    bool Supported { get; }

    /// <summary>Minutes of inactivity before the wallet locks; 0 = never.</summary>
    int IdleMinutes { get; }

    void SetIdleMinutes(int minutes);
}

/// <summary>Non-persistent, unsupported default (the TryAdd fallback in AddLiteWallet).</summary>
public sealed class InMemoryAutoLockSettings : IAutoLockSettings
{
    public bool Supported => false;
    public int IdleMinutes { get; private set; }
    public void SetIdleMinutes(int minutes) => IdleMinutes = Math.Max(0, minutes);
}

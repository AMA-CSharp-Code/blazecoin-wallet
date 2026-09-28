using System.Security.Cryptography;

namespace BlazecoinWallet.Lite;

/// <summary>
/// A local app-lock PIN (an extra gate on top of the OS lock screen). NOT the key
/// protection — the seed lives in the platform keystore regardless — so this is a
/// convenience/shoulder-surf guard. The PIN is stored only as a PBKDF2 salt+hash, never
/// reversible. Persisted per platform.
/// </summary>
public interface IPinLock
{
    bool IsSet { get; }
    void Set(string pin);
    bool Verify(string pin);
    void Clear();
}

/// <summary>Salted PBKDF2(SHA256) hashing shared by every <see cref="IPinLock"/> — a short
/// PIN is weak on its own, so a per-PIN random salt + iterations blunt offline brute force.</summary>
public static class PinHasher
{
    private const int Iterations = 100_000;

    public static (string Salt, string Hash) Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string pin, string saltB64, string hashB64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}

/// <summary>Non-persistent default (dev/test); real heads register a persistent one.</summary>
public sealed class InMemoryPinLock : IPinLock
{
    private string? _salt, _hash;
    public bool IsSet => _hash != null;
    public void Set(string pin) => (_salt, _hash) = PinHasher.Hash(pin);
    public bool Verify(string pin) => _hash != null && PinHasher.Verify(pin, _salt!, _hash);
    public void Clear() => (_salt, _hash) = (null, null);
}

/// <summary>Per-session unlock state for the launch lock — a singleton the layout reads to
/// decide whether to show the lock overlay. Reset each app start (a fresh singleton).</summary>
public sealed class AppLockSession
{
    // Absorbs the flow's own trailing lifecycle events: Android can deliver the covered
    // activity's OnStop AFTER the file-chooser flow has already completed and closed its
    // suppression window (seen live in the 2026-07-27 emulator smoke — the decoded address
    // appeared, then the late OnStop re-locked the wallet an instant later). Real
    // backgrounding is user-driven and happens well past this window.
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(3);
    private readonly long _graceMs;

    public AppLockSession() : this(DefaultGrace) { }
    public AppLockSession(TimeSpan externalOperationGrace) => _graceMs = (long)externalOperationGrace.TotalMilliseconds;

    /// <summary>Raised when the lock state flips — the layout subscribes and re-renders, so a
    /// background/resume re-lock shows the lock screen even though the WebView's component
    /// tree survived.</summary>
    public event Action? StateChanged;

    public bool IsUnlocked { get; private set; }

    public void Unlock() { IsUnlocked = true; StateChanged?.Invoke(); }

    /// <summary>Re-lock — the app head calls this when the app is backgrounded so a PIN is
    /// required again on return (not just at cold launch).</summary>
    public void Lock() { IsUnlocked = false; StateChanged?.Invoke(); }

    private int _externalOperations;
    private long _lastExternalOperationEndTicks = long.MinValue / 2;

    /// <summary>
    /// Marks an app-initiated flow that intentionally leaves the activity — the QR-scan
    /// file chooser today, a permission dialog tomorrow. The OS "stop" such a flow causes
    /// is part of the flow, not the user leaving the wallet, and re-locking there swaps in
    /// the lock screen and tears down the requesting page mid-flow (2026-07-27 emulator
    /// smoke: picking a QR image bounced through the PIN gate and the decoded address was
    /// lost). Dispose closes the window; nesting is ref-counted.
    /// </summary>
    public IDisposable BeginExternalOperation()
    {
        Interlocked.Increment(ref _externalOperations);
        return new ExternalOperation(this);
    }

    /// <summary>Background re-lock entry point for app heads: locks unless an app-initiated
    /// external flow is what pushed the app behind another activity — either still active,
    /// or ended within the grace window (late OnStop delivery, see the field note).</summary>
    public void LockForBackgrounding()
    {
        if (Volatile.Read(ref _externalOperations) > 0) return;
        if (Environment.TickCount64 - Volatile.Read(ref _lastExternalOperationEndTicks) < _graceMs) return;
        Lock();
    }

    private sealed class ExternalOperation(AppLockSession session) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref session._lastExternalOperationEndTicks, Environment.TickCount64);
                Interlocked.Decrement(ref session._externalOperations);
            }
        }
    }
}

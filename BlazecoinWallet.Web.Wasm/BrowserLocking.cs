using System.Runtime.Versioning;
using BlazecoinWallet.Lite;
using Microsoft.JSInterop;

namespace BlazecoinWallet.Web.Wasm;

/// <summary>
/// Idle auto-lock policy for the web head, persisted in localStorage. Default 15 minutes;
/// 0 = never. Supported = true so the shared Settings page shows the card (Android hides
/// it — that head re-locks on backgrounding instead).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserAutoLockSettings : IAutoLockSettings
{
    private const string Key = "auto_lock_minutes";
    public const int DefaultMinutes = 15;

    public bool Supported => true;
    public int IdleMinutes => int.TryParse(BrowserKv.GetOrNull(Key), out var m) ? Math.Max(0, m) : DefaultMinutes;
    public void SetIdleMinutes(int minutes) => BrowserKv.Set(Key, Math.Max(0, minutes).ToString());
}

/// <summary>
/// What "lock" means in a browser. When the wallet is protected by the password vault, a lock
/// DROPS the keys from memory (vault + wallet service) so the VaultGate demands the password
/// again — the only lock that means anything on a platform with no hardware keystore. A
/// session-only wallet (user declined a password) has nothing to fall back to except the PIN
/// gate, so there the lock is the PIN gate if one is set, else nothing (CanLock = false and the
/// button stays hidden rather than offer a no-op).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserVaultLocker(BrowserEncryptedSeedVault vault, LiteWalletService wallet,
    IPinLock pin, AppLockSession session) : IWalletLocker
{
    /// <summary>Synchronous blob probe — the vault's own key, checked without the JS module
    /// because the layout evaluates CanLock on every render.</summary>
    private static bool HasBlob => BrowserKv.RawContains(BrowserEncryptedSeedVault.StoreKey);

    public bool CanLock => HasBlob || pin.IsSet;

    public Task LockAsync()
    {
        if (HasBlob)
        {
            wallet.Lock();      // forget the HD keys
            vault.Relock();     // forget the mnemonic; the gate re-renders the password screen
        }
        else if (pin.IsSet)
        {
            session.Lock();     // PIN gate only — a session-only wallet must not be thrown away
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// The idle timer: polls js/idle-lock.js every 30 s and calls the locker once the wallet has
/// sat untouched (or hidden) past <see cref="IAutoLockSettings.IdleMinutes"/>. Only fires while
/// something is actually unlocked, so a locked vault never "re-locks" in a loop. Started once
/// by the VaultGate (the root component of the web head); never throws out of its loop.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserIdleLock(IJSRuntime js, IAutoLockSettings settings, IWalletLocker locker,
    LiteWalletService wallet, BrowserEncryptedSeedVault vault) : IAsyncDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private IJSObjectReference? _module;
    private CancellationTokenSource? _cts;

    /// <summary>Raised after an idle lock fires (the gate re-evaluates on the vault event
    /// already; this is for a layout that wants to say "locked for inactivity").</summary>
    public event Action? IdleLocked;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/idle-lock.js");
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(); }
                catch { /* a failed poll must never kill the loop */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* module import failed — no idle lock this session; nothing else depends on it */ }
    }

    private async Task TickAsync()
    {
        var minutes = settings.IdleMinutes;
        if (minutes <= 0 || !locker.CanLock) return;
        if (!wallet.IsUnlocked && !vault.HasSeedInMemory) return;   // already locked — nothing to do
        var idleMs = await _module!.InvokeAsync<double>("idleMs");
        if (idleMs < minutes * 60_000d) return;
        await locker.LockAsync();
        IdleLocked?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (_module != null)
        {
            try { await _module.DisposeAsync(); } catch { }
        }
    }
}

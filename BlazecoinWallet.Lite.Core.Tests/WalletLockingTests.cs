using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Core.Tests;

/// <summary>
/// The head-agnostic lock verb (2026-08-23). The default locker is the PIN gate: it can only
/// lock when a PIN exists, and locking is exactly <see cref="AppLockSession.Lock"/>. The
/// web head's vault locker is exercised by the WASM head itself (browser-only types).
/// </summary>
public class WalletLockingTests
{
    [Fact]
    public void Pin_gate_locker_cannot_lock_without_a_pin_and_does_nothing()
    {
        var pin = new InMemoryPinLock();
        var session = new AppLockSession();
        session.Unlock();
        var locker = new PinGateWalletLocker(pin, session);

        Assert.False(locker.CanLock);
        locker.LockAsync().GetAwaiter().GetResult();
        Assert.True(session.IsUnlocked);   // no PIN ⇒ a lock would be a no-op screen; stays unlocked
    }

    [Fact]
    public async Task Pin_gate_locker_locks_the_session_when_a_pin_is_set()
    {
        var pin = new InMemoryPinLock();
        pin.Set("1234");
        var session = new AppLockSession();
        session.Unlock();
        var locker = new PinGateWalletLocker(pin, session);
        var flips = 0;
        session.StateChanged += () => flips++;

        Assert.True(locker.CanLock);
        await locker.LockAsync();

        Assert.False(session.IsUnlocked);
        Assert.Equal(1, flips);
        await locker.LockAsync();          // idempotent
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void In_memory_auto_lock_settings_are_unsupported_and_clamp_at_zero()
    {
        var s = new InMemoryAutoLockSettings();
        Assert.False(s.Supported);
        Assert.Equal(0, s.IdleMinutes);
        s.SetIdleMinutes(15);
        Assert.Equal(15, s.IdleMinutes);
        s.SetIdleMinutes(-5);
        Assert.Equal(0, s.IdleMinutes);
    }

    [Fact]
    public async Task Lock_drops_the_keys_and_the_next_unlock_reads_the_vault_again()
    {
        // A vault that counts reads: Lock() must force UnlockAsync back through it.
        var vault = new CountingVault();
        var reader = new NullReader();
        var svc = new LiteWalletService(vault, reader, reader);

        Assert.True(await svc.UnlockAsync());
        Assert.True(svc.IsUnlocked);
        Assert.Equal(1, vault.Loads);

        svc.Lock();
        Assert.False(svc.IsUnlocked);

        Assert.True(await svc.UnlockAsync());
        Assert.Equal(2, vault.Loads);
    }

    private sealed class CountingVault : ISeedVault
    {
        // The standard test mnemonic (BIP39 vector) — any valid 12 words will do.
        private const string Words = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        public int Loads;
        public Task<bool> HasWalletAsync() => Task.FromResult(true);
        public Task<string?> LoadMnemonicAsync() { Loads++; return Task.FromResult<string?>(Words); }
        public Task SaveMnemonicAsync(string mnemonic) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }

    private sealed class NullReader : IChainReader, ITxRelay
    {
        public bool SupportsHistory => false;
        public bool SupportsChainVerification => false;
        public Task<BlazecoinWallet.Lite.Data.LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default)
            => Task.FromResult<BlazecoinWallet.Lite.Data.LiteAddressSummary?>(null);
        public Task<IReadOnlyList<BlazecoinWallet.Lite.Data.LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BlazecoinWallet.Lite.Data.LiteChainUtxo>>([]);
        public Task<IReadOnlyList<BlazecoinWallet.Lite.Data.LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BlazecoinWallet.Lite.Data.LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<BlazecoinWallet.Lite.Data.LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default)
            => Task.FromResult(new BlazecoinWallet.Lite.Data.LiteBroadcastResult(false, null, "unused", BlazecoinWallet.Lite.Data.BroadcastFailureKind.Unreachable));
    }
}

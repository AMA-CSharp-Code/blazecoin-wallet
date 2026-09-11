namespace BlazecoinWallet.Lite;

/// <summary>
/// Non-secret wallet state that must survive restarts — today just how many receive
/// addresses the user has revealed (rotation position). Heads supply a persistent
/// implementation (Android Preferences); the in-memory default suits tests and the dev
/// host. NEVER key material — that's <see cref="ISeedVault"/>'s job. Losing this store is
/// harmless: a gap-limit rescan rediscovers every used address from the chain.
/// </summary>
public interface IWalletStateStore
{
    /// <summary>How many external (receive) addresses are revealed; 0 = never stored.</summary>
    Task<int> GetRevealedAddressCountAsync();
    Task SetRevealedAddressCountAsync(int count);

    /// <summary>How many INTERNAL-chain change addresses have been used (BIP44 change chain);
    /// 0 = none yet. Persisting this stops a change address from being reused; losing it is
    /// still safe — a restore gap-scans the change chain to recover the position. Default
    /// no-op so a store that predates the change chain keeps compiling.</summary>
    Task<int> GetChangeAddressCountAsync() => Task.FromResult(0);
    Task SetChangeAddressCountAsync(int count) => Task.CompletedTask;

    /// <summary>The furthest header-chain checkpoint verified by proof-of-work ("height:hash:bits");
    /// null = never synced, so the wallet re-anchors from the shipped checkpoint. Losing it is
    /// safe (re-sync from the anchor) and it never overrides the shipped anchor if it's behind.
    /// Default no-op so a store that predates header sync keeps compiling.</summary>
    Task<string?> GetVerifiedCheckpointAsync() => Task.FromResult<string?>(null);
    Task SetVerifiedCheckpointAsync(string value) => Task.CompletedTask;

    /// <summary>Whether the recovery-phrase backup has been PROVEN — the quiz passed, or the
    /// wallet was restored from a typed phrase (possession is proof). False drives the Home
    /// warning chip and the verify-backup flow. Default TRUE so a store that predates the
    /// flag keeps today's chip-less behaviour (this gates a cosmetic warning, never money).</summary>
    Task<bool> GetBackupVerifiedAsync() => Task.FromResult(true);
    Task SetBackupVerifiedAsync(bool verified) => Task.CompletedTask;

    /// <summary>Wipes stored state (wallet reset).</summary>
    Task ClearAsync();
}

/// <summary>Default volatile store — fine for tests and the throwaway dev host.</summary>
public sealed class InMemoryWalletStateStore : IWalletStateStore
{
    private int _revealed;
    private int _change;
    private string? _checkpoint;
    private bool _backupVerified = true;   // no wallet yet = nothing to warn about
    public Task<int> GetRevealedAddressCountAsync() => Task.FromResult(_revealed);
    public Task SetRevealedAddressCountAsync(int count) { _revealed = count; return Task.CompletedTask; }
    public Task<int> GetChangeAddressCountAsync() => Task.FromResult(_change);
    public Task SetChangeAddressCountAsync(int count) { _change = count; return Task.CompletedTask; }
    public Task<string?> GetVerifiedCheckpointAsync() => Task.FromResult(_checkpoint);
    public Task SetVerifiedCheckpointAsync(string value) { _checkpoint = value; return Task.CompletedTask; }
    public Task<bool> GetBackupVerifiedAsync() => Task.FromResult(_backupVerified);
    public Task SetBackupVerifiedAsync(bool verified) { _backupVerified = verified; return Task.CompletedTask; }
    public Task ClearAsync() { _revealed = 0; _change = 0; _checkpoint = null; _backupVerified = true; return Task.CompletedTask; }
}

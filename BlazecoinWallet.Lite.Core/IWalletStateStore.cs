namespace BlazecoinWallet.Lite;

/// <summary>
/// Non-secret wallet state that must survive restarts: the rotation positions (how many
/// receive, change and post-quantum addresses are in use), the header-sync checkpoint and
/// anchors, the coins already proven, and whether the recovery-phrase backup has been
/// proven. Heads supply a persistent implementation (Android/iOS Preferences, the browser's
/// storage); the in-memory one suits tests. NEVER key material — that's
/// <see cref="ISeedVault"/>'s job.
/// <para>Losing this store never loses money, and every value is written so that its ABSENCE
/// is the safe reading: positions are rediscovered from the chain, sync and proofs are redone,
/// and a missing backup flag means "not verified" — the wallet asks again rather than
/// vouching for a backup it has no record of (bug-bounty report 2026-11-06).</para>
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

    /// <summary>Header anchors the wallet has verified ABOVE the shipped table
    /// ("height:hash,height:hash…", one per 1,000 blocks — about 190 bytes a day). Lets a merkle
    /// proof's block be linked to a nearby trusted hash after a restart. Losing it is safe (a
    /// proof then links to the verified tip instead, and the anchors are re-learned). Default
    /// no-op so a store that predates anchors keeps compiling.</summary>
    Task<string?> GetHeaderAnchorsAsync() => Task.FromResult<string?>(null);
    Task SetHeaderAnchorsAsync(string value) => Task.CompletedTask;

    /// <summary>Whether the recovery-phrase backup has been PROVEN — the quiz passed, or the
    /// wallet was restored from a typed phrase (possession is proof). False drives the Home
    /// warning chip and the verify-backup flow.
    /// <para>NO RECORD MEANS NOT VERIFIED, here and in every implementation. The default used to
    /// be true ("a store that predates the flag keeps chip-less behaviour"), which made a wallet
    /// whose flag had gone missing — state cleared while the vault survived, a write that never
    /// landed — read as proven: no warning chip, and the verify page saying "the written copy has
    /// been proven" with no quiz ever run (bug-bounty report 2026-11-06). Every released build
    /// writes the flag when a wallet is created (false) or restored (true), so the only wallets
    /// this changes are ones with no record, and all it costs them is being asked to verify.</para></summary>
    Task<bool> GetBackupVerifiedAsync() => Task.FromResult(false);
    Task SetBackupVerifiedAsync(bool verified) => Task.CompletedTask;

    /// <summary>How many POST-QUANTUM (BQ…) receive addresses have been revealed (PQ_SIGNATURES.md
    /// §6 chain, index 0..count-1); 0 = none. Losing it is safe — a restore gap-scans the PQ
    /// chain once the fork is active. Default no-op so a store that predates the PQ chain
    /// keeps compiling (it then forgets reveals across restarts, which only costs a rescan).</summary>
    Task<int> GetPqAddressCountAsync() => Task.FromResult(0);
    Task SetPqAddressCountAsync(int count) => Task.CompletedTask;

    /// <summary>The coins this wallet has PROVEN on the chain ("txid:vout:address:sats;…"), so a
    /// restart does not re-verify every coin before the balance can be shown (bug-bounty report
    /// 2026-11-06: only proven coins count). Non-secret; losing it is safe — the coins are simply
    /// verified again, and a spend re-verifies its inputs regardless. Default no-op so a store
    /// that predates it keeps compiling.</summary>
    Task<string?> GetVerifiedCoinsAsync() => Task.FromResult<string?>(null);
    Task SetVerifiedCoinsAsync(string value) => Task.CompletedTask;

    /// <summary>Wipes stored state (wallet reset).</summary>
    Task ClearAsync();
}

/// <summary>Default volatile store — fine for tests and the throwaway dev host.</summary>
public sealed class InMemoryWalletStateStore : IWalletStateStore
{
    private int _revealed;
    private int _change;
    private int _pq;
    private string? _checkpoint;
    private string? _anchors;
    private string? _verifiedCoins;
    private bool _backupVerified;          // no record = not verified, like every persistent store
    public Task<int> GetRevealedAddressCountAsync() => Task.FromResult(_revealed);
    public Task SetRevealedAddressCountAsync(int count) { _revealed = count; return Task.CompletedTask; }
    public Task<int> GetChangeAddressCountAsync() => Task.FromResult(_change);
    public Task SetChangeAddressCountAsync(int count) { _change = count; return Task.CompletedTask; }
    public Task<string?> GetVerifiedCheckpointAsync() => Task.FromResult(_checkpoint);
    public Task SetVerifiedCheckpointAsync(string value) { _checkpoint = value; return Task.CompletedTask; }
    public Task<string?> GetHeaderAnchorsAsync() => Task.FromResult(_anchors);
    public Task SetHeaderAnchorsAsync(string value) { _anchors = value; return Task.CompletedTask; }
    public Task<bool> GetBackupVerifiedAsync() => Task.FromResult(_backupVerified);
    public Task SetBackupVerifiedAsync(bool verified) { _backupVerified = verified; return Task.CompletedTask; }
    public Task<int> GetPqAddressCountAsync() => Task.FromResult(_pq);
    public Task SetPqAddressCountAsync(int count) { _pq = count; return Task.CompletedTask; }
    public Task<string?> GetVerifiedCoinsAsync() => Task.FromResult(_verifiedCoins);
    public Task SetVerifiedCoinsAsync(string value) { _verifiedCoins = value; return Task.CompletedTask; }
    public Task ClearAsync() { _revealed = 0; _change = 0; _pq = 0; _checkpoint = null; _anchors = null; _verifiedCoins = null; _backupVerified = false; return Task.CompletedTask; }
}

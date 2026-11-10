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
    /// still safe — a restore gap-scans the change chain to recover the position.</summary>
    Task<int> GetChangeAddressCountAsync();
    Task SetChangeAddressCountAsync(int count);

    /// <summary>The furthest header-chain checkpoint verified by proof-of-work ("height:hash:bits");
    /// null = never synced, so the wallet re-anchors from the shipped checkpoint. Losing it is
    /// safe (re-sync from the anchor) and it never overrides the shipped anchor if it's behind.
    /// Written and read under the wallet's seal (<see cref="SealedValue"/>, audit 2026-11-09): a
    /// value here that the wallet did not seal is ignored, like a missing one.</summary>
    Task<string?> GetVerifiedCheckpointAsync();
    Task SetVerifiedCheckpointAsync(string value);

    /// <summary>Header anchors the wallet has verified ABOVE the shipped table
    /// ("height:hash,height:hash…", one per 1,000 blocks — about 190 bytes a day). Lets a merkle
    /// proof's block be linked to a nearby trusted hash after a restart. Losing it is safe (a
    /// proof then links to the verified tip instead, and the anchors are re-learned). Sealed like
    /// the checkpoint.</summary>
    Task<string?> GetHeaderAnchorsAsync();
    Task SetHeaderAnchorsAsync(string value);

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
    Task<bool> GetBackupVerifiedAsync();
    Task SetBackupVerifiedAsync(bool verified);

    /// <summary>How many POST-QUANTUM (BQ…) receive addresses have been revealed (PQ_SIGNATURES.md
    /// §6 chain, index 0..count-1); 0 = none. Losing it is safe — a restore gap-scans the PQ
    /// chain once the fork is active.</summary>
    Task<int> GetPqAddressCountAsync();
    Task SetPqAddressCountAsync(int count);

    /// <summary>The coins this wallet has PROVEN on the chain ("txid:vout:address:sats;…"), so a
    /// restart does not re-verify every coin before the balance can be shown (bug-bounty report
    /// 2026-11-06: only proven coins count). Non-secret; losing it is safe — the coins are simply
    /// verified again, and a spend re-verifies its inputs regardless.</summary>
    Task<string?> GetVerifiedCoinsAsync();
    Task SetVerifiedCoinsAsync(string value);

    /// <summary>Wipes stored state (wallet reset).</summary>
    Task ClearAsync();
}

/// <summary>
/// What a store answers when it has NO RECORD of a value — the one place every implementation
/// takes it from (audit 2026-11-09). The interface used to carry these as default members, so a
/// store could compile without a member and silently DISCARD its writes; and each head store
/// spelled the "no record" readings out for itself. Every value's absence is the safe reading.
/// </summary>
public static class WalletStateDefaults
{
    /// <summary>No count stored: none revealed or used — the position is rediscovered from the chain.</summary>
    public const int AddressCount = 0;

    /// <summary>No record of the backup flag: NOT verified (bug-bounty report 2026-11-06).</summary>
    public const bool BackupVerified = false;

    /// <summary>A stored count as text (a store that keeps strings); anything unreadable is no record.</summary>
    public static int Count(string? stored) => int.TryParse(stored, out var n) ? n : AddressCount;

    /// <summary>The stored backup flag as text; anything unreadable is no record.</summary>
    public static bool Verified(string? stored) => bool.TryParse(stored, out var v) ? v : BackupVerified;
}

/// <summary>Default volatile store — fine for tests and the throwaway dev host.</summary>
public sealed class InMemoryWalletStateStore : IWalletStateStore
{
    private int _revealed = WalletStateDefaults.AddressCount;
    private int _change = WalletStateDefaults.AddressCount;
    private int _pq = WalletStateDefaults.AddressCount;
    private string? _checkpoint;
    private string? _anchors;
    private string? _verifiedCoins;
    private bool _backupVerified = WalletStateDefaults.BackupVerified;   // no record = not verified, like every persistent store
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

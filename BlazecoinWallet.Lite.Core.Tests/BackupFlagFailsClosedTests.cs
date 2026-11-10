using System.Text.RegularExpressions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The backup-verified flag fails CLOSED (bug-bounty report 2026-11-06): a wallet with no record
/// of the flag is "not verified". It used to read as verified in every store, so a wallet whose
/// state had gone missing lost its warning chip and was told its phrase "has been proven".
/// </summary>
public class BackupFlagFailsClosedTests
{
    private sealed class Vault : ISeedVault
    {
        public string? Words = MinedBlockFixture.Mnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Words != null);
        public Task SaveMnemonicAsync(string m) { Words = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Words);
        public Task ClearAsync() { Words = null; return Task.CompletedTask; }
    }

    private sealed class NoChain : Data.ILiteWalletData
    {
        public bool SupportsHistory => false;
        public bool SupportsChainVerification => false;
        public Task<Data.LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<Data.LiteAddressSummary?>(null);
        public Task<IReadOnlyList<Data.LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Data.LiteChainUtxo>>([]);
        public Task<IReadOnlyList<Data.LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Data.LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Data.LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default) => Task.FromResult(Data.LiteBroadcastResult.Fail("unused", Data.BroadcastFailureKind.Unreachable));
    }

    [Fact]
    public async Task No_record_of_the_flag_means_not_verified()
    {
        Assert.False(await new InMemoryWalletStateStore().GetBackupVerifiedAsync());
        // The one reading every store takes it from (audit 2026-11-09).
        Assert.False(WalletStateDefaults.BackupVerified);
        Assert.False(WalletStateDefaults.Verified(null));
        Assert.False(WalletStateDefaults.Verified("not a flag"));
        Assert.True(WalletStateDefaults.Verified("True"));
        Assert.Equal(0, WalletStateDefaults.AddressCount);
        Assert.Equal(0, WalletStateDefaults.Count(null));
        Assert.Equal(7, WalletStateDefaults.Count("7"));
    }

    /// <summary>A store used to be able to leave a member out and get a default that silently
    /// DISCARDED its writes (and a fork-status stub got the gateway's claimed height as its
    /// "verified" tip). Nothing on either interface has a default any more (audit 2026-11-09).</summary>
    [Theory]
    [InlineData(typeof(IWalletStateStore))]
    [InlineData(typeof(Fork.IForkStatusService))]
    public void No_member_of_the_store_or_the_fork_status_has_a_default_implementation(Type contract)
    {
        var members = contract.GetMethods().Where(m => !m.IsSpecialName || m.Name.StartsWith("get_") || m.Name.StartsWith("set_"));
        Assert.All(members, m => Assert.True(m.IsAbstract, $"{contract.Name}.{m.Name} has a default implementation"));
    }

    [Fact]
    public async Task A_wallet_whose_state_was_lost_is_asked_to_verify_again()
    {
        // The vault survived; the state store is new (cleared, evicted, never written).
        var service = new LiteWalletService(new Vault(), new NoChain(), stateStore: new InMemoryWalletStateStore());
        Assert.True(await service.UnlockAsync());

        Assert.False(await service.IsBackupVerifiedAsync());        // was true: no chip, "has been proven"

        await service.MarkBackupVerifiedAsync();                    // the quiz, passed
        Assert.True(await service.IsBackupVerifiedAsync());
    }

    [Fact]
    public async Task Clearing_the_store_forgets_the_proof_too()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetBackupVerifiedAsync(true);
        await store.ClearAsync();
        Assert.False(await store.GetBackupVerifiedAsync());
    }

    /// <summary>The four persistent stores live in head projects this suite cannot load (MAUI
    /// Preferences, the browser, the dev host's files), so their one-line readers are pinned at
    /// the source: none may fall back to "verified" when the value is absent. The three shipped
    /// heads take the reading from <see cref="WalletStateDefaults"/>; the dev host spells its own.</summary>
    [Theory]
    [InlineData("BlazecoinWallet.App.Droid/PreferencesWalletStateStore.cs", @"Get\(BackupVerifiedKey,\s*WalletStateDefaults\.BackupVerified\)")]
    [InlineData("BlazecoinWallet.App.iOS/PreferencesWalletStateStore.cs", @"Get\(BackupVerifiedKey,\s*WalletStateDefaults\.BackupVerified\)")]
    [InlineData("BlazecoinWallet.Web.Wasm/BrowserStores.cs", @"WalletStateDefaults\.Verified\(BrowserKv\.GetOrNull\(""backup_verified""\)\)")]
    [InlineData("BlazecoinWallet.Web.DevHost/DevFileWalletStateStore.cs", @"File\.Exists\(BackupVerifiedPath\) &&")]
    public void Every_heads_store_reads_an_absent_flag_as_not_verified(string relativePath, string failClosedPattern)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "BlazecoinWallet.Lite.Core"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName, relativePath));

        Assert.Matches(failClosedPattern, source);
        Assert.DoesNotMatch(@"Get\(BackupVerifiedKey,\s*true\)", source);
        Assert.DoesNotMatch(@"!bool\.TryParse\(BrowserKv\.GetOrNull\(""backup_verified""\)", source);
        Assert.DoesNotMatch(@"!File\.Exists\(BackupVerifiedPath\)\s*\|\|", source);
    }
}

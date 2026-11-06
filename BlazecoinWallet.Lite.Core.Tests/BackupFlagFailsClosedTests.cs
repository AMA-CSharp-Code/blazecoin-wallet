using System.Text.RegularExpressions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The backup-verified flag fails CLOSED (bug-bounty report 2026-11-06): a wallet with no record
/// of the flag is "not verified". It used to read as verified in every store, so a wallet whose
/// state had gone missing lost its warning chip and was told its phrase "has been proven".
/// </summary>
public class BackupFlagFailsClosedTests
{
    /// <summary>A store written before the flag existed: it implements only what the interface forces.</summary>
    private sealed class OldStore : IWalletStateStore
    {
        public Task<int> GetRevealedAddressCountAsync() => Task.FromResult(1);
        public Task SetRevealedAddressCountAsync(int count) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }

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
        Assert.False(await ((IWalletStateStore)new OldStore()).GetBackupVerifiedAsync());   // the interface default
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
    /// the source: none may fall back to "verified" when the value is absent.</summary>
    [Theory]
    [InlineData("BlazecoinWallet.App.Droid/PreferencesWalletStateStore.cs", @"Get\(BackupVerifiedKey,\s*false\)")]
    [InlineData("BlazecoinWallet.App.iOS/PreferencesWalletStateStore.cs", @"Get\(BackupVerifiedKey,\s*false\)")]
    [InlineData("BlazecoinWallet.Web.Wasm/BrowserStores.cs", @"bool\.TryParse\(BrowserKv\.GetOrNull\(""backup_verified""\), out var v\) && v")]
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

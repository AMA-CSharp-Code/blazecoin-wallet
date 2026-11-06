using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// "Forget this wallet" forgets everything the device holds about it (bug-bounty report
/// 2026-11-06, fourth round). The web wallet's Forget cleared the seed vault alone: the app-lock
/// PIN stayed — so the lock screen came up over the setup page, with no way past it for someone
/// who had forgotten the PIN as well — and so did the saved state, which the next wallet inherited.
/// </summary>
public class ForgetWalletTests
{
    private sealed class Vault : ISeedVault
    {
        public string? Words = MinedBlockFixture.Mnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Words != null);
        public Task SaveMnemonicAsync(string m) { Words = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Words);
        public Task ClearAsync() { Words = null; return Task.CompletedTask; }
    }

    private sealed class NoChain : ILiteWalletData
    {
        public bool SupportsHistory => false;
        public bool SupportsChainVerification => false;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default) => Task.FromResult(LiteBroadcastResult.Fail("unused", BroadcastFailureKind.Unreachable));
    }

    [Fact]
    public async Task Forgetting_a_wallet_clears_its_seed_its_saved_state_and_its_pin()
    {
        var vault = new Vault();
        var store = new InMemoryWalletStateStore();
        var pin = new InMemoryPinLock();
        var service = new LiteWalletService(vault, new NoChain(), stateStore: store);
        Assert.True(await service.UnlockAsync());
        await service.MarkBackupVerifiedAsync();
        await store.SetRevealedAddressCountAsync(7);
        await store.SetVerifiedCoinsAsync("something:0:proven:1:0:0");
        pin.Set("2468");

        await service.ForgetDeviceWalletAsync(pin);

        Assert.False(await vault.HasWalletAsync());
        Assert.False(service.IsUnlocked);
        Assert.False(pin.IsSet);                                    // was still set: the lock screen covered /setup
        Assert.Equal(0, await store.GetRevealedAddressCountAsync());   // was 7: the next wallet's starting point
        Assert.Null(await store.GetVerifiedCoinsAsync());
        Assert.False(await store.GetBackupVerifiedAsync());
    }

    [Fact]
    public async Task Forgetting_works_with_no_pin_lock_at_all()
    {
        var vault = new Vault();
        var service = new LiteWalletService(vault, new NoChain());
        await service.UnlockAsync();

        await service.ForgetDeviceWalletAsync(null);

        Assert.False(await vault.HasWalletAsync());
    }

    /// <summary>The web head's Forget button lives in a project this suite cannot load; its handler
    /// is pinned at the source to go through the one method above.</summary>
    [Fact]
    public void The_web_wallets_forget_button_uses_it()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "BlazecoinWallet.Web.Wasm"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "BlazecoinWallet.Web.Wasm", "VaultGate.razor"));

        Assert.Matches(@"private async Task ForgetAsync\(\)\s*\{[^}]*Wallet\.ForgetDeviceWalletAsync\(Pin\)", source);
    }
}

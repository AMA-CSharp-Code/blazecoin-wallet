using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using NBitcoin;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>Message signing / verification — proof of address control, fully client-side.</summary>
public class LiteMessageTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored = TestMnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    private sealed class NullData : ILiteWalletData
    {
        public bool SupportsHistory => false;
        public bool SupportsChainVerification => false;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string t, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string t, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteBroadcastResult> BroadcastAsync(string h, CancellationToken ct = default) => Task.FromResult(LiteBroadcastResult.Ok("x"));
    }

    private static async Task<LiteWalletService> UnlockedAsync()
    {
        var s = new LiteWalletService(new MemoryVault(), new NullData());
        await s.UnlockAsync();
        return s;
    }

    [Fact]
    public async Task Signs_with_own_address_and_verifies()
    {
        var s = await UnlockedAsync();
        var (address, signature) = s.SignMessage("I control this address — Blazecoin 2014");

        Assert.Equal(s.Address, address);
        Assert.True(BlazecoinMessage.Verify(address, "I control this address — Blazecoin 2014", signature));
    }

    [Fact]
    public async Task Verification_fails_on_tamper()
    {
        var s = await UnlockedAsync();
        var (address, signature) = s.SignMessage("original message");

        Assert.False(BlazecoinMessage.Verify(address, "TAMPERED message", signature));       // wrong message
        var other = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(9);
        Assert.False(BlazecoinMessage.Verify(other, "original message", signature));          // wrong address
        Assert.False(BlazecoinMessage.Verify(address, "original message", "not-a-signature")); // garbage sig
    }

    [Fact]
    public async Task Can_sign_with_a_specific_revealed_address()
    {
        var s = await UnlockedAsync();
        // reveal a second address (Fakeless: NullData reports unused, but reveal allows up to gap)
        await s.RevealNextAddressAsync();
        var second = s.Addresses[0]; // sign with the older slot explicitly

        var (address, signature) = s.SignMessage("hello", withAddress: second);
        Assert.Equal(second, address);
        Assert.True(BlazecoinMessage.Verify(second, "hello", signature));
    }

    [Fact]
    public async Task Signing_with_a_foreign_address_throws()
    {
        var s = await UnlockedAsync();
        var foreign = new Key().GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString();
        Assert.Throws<InvalidOperationException>(() => s.SignMessage("x", withAddress: foreign));
    }
}

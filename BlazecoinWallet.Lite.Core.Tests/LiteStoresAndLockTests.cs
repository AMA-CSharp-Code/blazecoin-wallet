using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>The 2026-07-25 convenience stores + app-lock + BIP39 passphrase.</summary>
public class LiteStoresAndLockTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    // ── Labels ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Label_store_sets_gets_and_clears()
    {
        var s = new InMemoryLabelStore();
        s.Set("txabc", "rent");
        Assert.Equal("rent", s.Get("txabc"));
        Assert.Null(s.Get("unknown"));
        s.Set("txabc", "  ");            // blank clears
        Assert.Null(s.Get("txabc"));
    }

    // ── Address book ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Address_book_upserts_by_address_and_sorts_by_name()
    {
        var ab = new InMemoryAddressBook();
        ab.Save("Zoe", "B1");
        ab.Save("Alice", "B2");
        ab.Save("Alice (updated)", "B2"); // same address → update, not duplicate
        Assert.Equal(2, ab.Entries.Count);
        Assert.Equal("Alice (updated)", ab.Entries[0].Name); // sorted, updated
        ab.Remove("B1");
        Assert.Single(ab.Entries);
    }

    // ── Watch list ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void Watch_list_adds_and_removes_by_address()
    {
        var w = new InMemoryWatchList();
        w.Add("Cold storage", "Bcold");
        w.Add("Cold storage v2", "Bcold"); // upsert
        Assert.Single(w.Entries);
        Assert.Equal("Cold storage v2", w.Entries[0].Label);
        w.Remove("Bcold");
        Assert.Empty(w.Entries);
    }

    // ── Watch-only xpub ──────────────────────────────────────────────────────────────────
    [Fact]
    public void Watch_xpub_derives_the_same_receive_addresses_as_the_wallet()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var xpub = wallet.GetAccountXpub();

        Assert.True(WatchXpub.IsXpub(xpub));
        Assert.False(WatchXpub.IsXpub("not-an-xpub"));

        var derived = WatchXpub.ExternalAddresses(xpub, 5).ToList();
        for (var i = 0; i < 5; i++)
            Assert.Equal(wallet.GetReceiveAddress(i), derived[i]); // watch-only sees the real addresses
    }

    // ── PIN lock ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Pin_lock_verifies_the_right_pin_only()
    {
        var pin = new InMemoryPinLock();
        Assert.False(pin.IsSet);
        pin.Set("2468");
        Assert.True(pin.IsSet);
        Assert.True(pin.Verify("2468"));
        Assert.False(pin.Verify("0000"));
        pin.Clear();
        Assert.False(pin.IsSet);
        Assert.False(pin.Verify("2468"));
    }

    [Fact]
    public void Pin_hash_is_salted_so_equal_pins_differ_on_disk()
    {
        var (s1, h1) = PinHasher.Hash("1234");
        var (s2, h2) = PinHasher.Hash("1234");
        Assert.NotEqual(s1, s2);          // random salt
        Assert.NotEqual(h1, h2);          // → different hash for the same PIN
        Assert.True(PinHasher.Verify("1234", s1, h1));
        Assert.False(PinHasher.Verify("1235", s1, h1));
    }

    // ── BIP39 passphrase ─────────────────────────────────────────────────────────────────
    private sealed class PassphraseVault : ISeedVault
    {
        public string? Mnemonic;
        public string Passphrase = string.Empty;
        public Task<bool> HasWalletAsync() => Task.FromResult(Mnemonic != null);
        public Task SaveMnemonicAsync(string m) { Mnemonic = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Mnemonic);
        public Task ClearAsync() { Mnemonic = null; Passphrase = string.Empty; return Task.CompletedTask; }
        public Task SavePassphraseAsync(string? p) { Passphrase = p ?? string.Empty; return Task.CompletedTask; }
        public Task<string> LoadPassphraseAsync() => Task.FromResult(Passphrase);
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

    private sealed class NoPassphraseVault : ISeedVault
    {
        public string? Mnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Mnemonic != null);
        public Task SaveMnemonicAsync(string m) { Mnemonic = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Mnemonic);
        public Task ClearAsync() { Mnemonic = null; return Task.CompletedTask; }
        // Deliberately does NOT override the passphrase methods — uses the interface defaults.
    }

    [Fact]
    public async Task A_vault_without_passphrase_support_fails_loudly_on_a_real_passphrase()
    {
        // Audit round-3 F1: the default must throw for a NON-empty passphrase (silently
        // discarding it would make funds invisible at next unlock), but stay a no-op for none.
        var svc = new LiteWalletService(new NoPassphraseVault(), new NullData());
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.RestoreWalletAsync(TestMnemonic, "a real passphrase"));

        // No passphrase → the default no-op is fine.
        await new LiteWalletService(new NoPassphraseVault(), new NullData()).RestoreWalletAsync(TestMnemonic);
    }

    [Fact]
    public async Task Passphrase_changes_the_wallet_and_persists_across_unlock()
    {
        var plainVault = new PassphraseVault();
        var plain = new LiteWalletService(plainVault, new NullData());
        await plain.RestoreWalletAsync(TestMnemonic);                 // no passphrase
        var plainAddr = plain.Address;

        var passVault = new PassphraseVault();
        var withPass = new LiteWalletService(passVault, new NullData());
        await withPass.RestoreWalletAsync(TestMnemonic, "correct horse");
        var passAddr = withPass.Address;

        Assert.NotEqual(plainAddr, passAddr);                         // same seed, different wallet
        Assert.Equal("correct horse", passVault.Passphrase);          // persisted

        // A fresh service over the same vault unlocks to the SAME passphrase-derived address.
        var reopened = new LiteWalletService(passVault, new NullData());
        Assert.True(await reopened.UnlockAsync());
        Assert.Equal(passAddr, reopened.Address);
    }
}

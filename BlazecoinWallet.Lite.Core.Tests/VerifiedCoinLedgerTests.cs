namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Audit 2026-11-09: the remembered proven coins have one home (<see cref="VerifiedCoinLedger"/>)
/// — the stored line, the set, its seal and the facts cache used to live in the wallet service.
/// The format on disk did not change: these pin it with literals.
/// </summary>
public class VerifiedCoinLedgerTests
{
    private static readonly string TxA = new('a', 64);
    private static readonly string TxB = new('b', 64);
    private static readonly LiteHdWallet Wallet = LiteHdWallet.Restore(MinedBlockFixture.Mnemonic);
    private static byte[] Key => Wallet.DeriveStateKey("verified-coins/v1");

    [Fact]
    public void A_remembered_coin_is_one_line_of_six_fields()
    {
        var coin = new RememberedCoin(TxA, 3, "BAddress", 1_000_000_000, IsCoinbase: true, Height: 4_300_000);

        Assert.Equal($"{TxA}:3:BAddress:1000000000:1:4300000", coin.Serialize());
        Assert.Equal(coin, RememberedCoin.TryParse(coin.Serialize()));
        Assert.Equal(new RememberedCoin(TxB, 0, "BOther", 5, false, 0), RememberedCoin.TryParse($"{TxB}:0:BOther:5:0:0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaa:0:BAddress:5:0:0")]                                                                  // not a txid
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:0:BAddress:5")]           // the four fields 1.3.3 wrote
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:-1:BAddress:5:0:0")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:0:BAddress:0:0:0")]      // no value
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:0:BAddress:5:2:0")]      // coinbase is 0 or 1
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:0:BAddress:5:0:-4")]
    public void A_line_of_any_other_shape_is_not_a_remembered_coin(string line) =>
        Assert.Null(RememberedCoin.TryParse(line));

    [Fact]
    public async Task The_stored_set_is_the_lines_joined_by_semicolons_in_txid_order_under_the_wallets_seal()
    {
        var store = new InMemoryWalletStateStore();
        var ledger = new VerifiedCoinLedger(store);
        ledger.Note(TxB, 1, "BOther", new CoinFacts(IsCoinbase: false, Height: 0));
        ledger.Note(TxA, 0, "BAddress", new CoinFacts(IsCoinbase: true, Height: 4_300_000));

        await ledger.SaveAsync(Wallet, [(TxB, 1, "BOther", 7), (TxA, 0, "BAddress", 9), (TxA, 5, "BUnknown", 11)]);   // the last has no facts: not proven here

        var stored = await store.GetVerifiedCoinsAsync();
        Assert.True(SealedValue.TryOpen(Key, stored, out var text));
        Assert.Equal($"{TxA}:0:BAddress:9:1:4300000;{TxB}:1:BOther:7:0:0", text);
        Assert.Equal(stored, RememberedCoinsSeal.Seal(Key, text));        // the seal the 2026-11-09 release wrote
    }

    [Fact]
    public async Task What_this_wallet_sealed_is_read_back_once_and_nothing_else_is()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync(SealedValue.Seal(Key, $"{TxA}:0:BAddress:9:1:4300000;junk;{TxB}:1:BOther:7:0:0"));

        var ledger = new VerifiedCoinLedger(store);
        Assert.Empty(await ledger.LoadAsync(null));                       // locked: nothing to open a seal with — and not "read"
        var coins = await ledger.LoadAsync(Wallet);
        Assert.Equal([new RememberedCoin(TxA, 0, "BAddress", 9, true, 4_300_000), new RememberedCoin(TxB, 1, "BOther", 7, false, 0)], coins);
        Assert.True(ledger.TryGetFacts(TxA, 0, "BAddress", out var facts));
        Assert.Equal(new CoinFacts(true, 4_300_000), facts);
        Assert.Empty(await ledger.LoadAsync(Wallet));                     // once per unlock of the store

        // A bare line, or another wallet's seal, is ignored.
        await store.SetVerifiedCoinsAsync($"{TxA}:0:BAddress:9:1:4300000");
        Assert.Empty(await new VerifiedCoinLedger(store).LoadAsync(Wallet));
        var other = LiteHdWallet.Restore("legal winner thank year wave sausage worth useful legal winner thank yellow");
        await store.SetVerifiedCoinsAsync(SealedValue.Seal(other.DeriveStateKey("verified-coins/v1"), $"{TxA}:0:BAddress:9:1:4300000"));
        Assert.Empty(await new VerifiedCoinLedger(store).LoadAsync(Wallet));
    }

    [Fact]
    public async Task An_empty_view_a_locked_wallet_or_no_change_writes_nothing()
    {
        var store = new InMemoryWalletStateStore();
        var ledger = new VerifiedCoinLedger(store);
        ledger.Note(TxA, 0, "BAddress", new CoinFacts(false, 0));

        await ledger.SaveAsync(Wallet, []);                               // a failed read must not forget the proofs
        Assert.Null(await store.GetVerifiedCoinsAsync());
        await ledger.SaveAsync(null, [(TxA, 0, "BAddress", 9)]);          // locked: nothing to seal with
        Assert.Null(await store.GetVerifiedCoinsAsync());

        await ledger.SaveAsync(Wallet, [(TxA, 0, "BAddress", 9)]);
        var written = await store.GetVerifiedCoinsAsync();
        Assert.NotNull(written);
        await store.SetVerifiedCoinsAsync("overwritten by someone else");
        await ledger.SaveAsync(Wallet, [(TxA, 0, "BAddress", 9)]);        // unchanged: not written again
        Assert.Equal("overwritten by someone else", await store.GetVerifiedCoinsAsync());

        ledger.Clear();                                                   // a reset forgets what was known
        Assert.False(ledger.TryGetFacts(TxA, 0, "BAddress", out _));
        Assert.Equal(2000, VerifiedCoinLedger.MaxPersisted);
    }
}

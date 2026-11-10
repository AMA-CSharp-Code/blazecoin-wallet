using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Bug-bounty round thirteen (2026-11-09): the set of coins the wallet remembers as proven is
/// kept in ordinary app storage (browser localStorage, Android preferences), and an entry there
/// was adopted on its FORMAT alone. Anything able to write that storage could add a well-formed
/// line for a transaction that exists nowhere; if a gateway then listed the same coin, it showed
/// as spendable balance with the chain never asked. (A send still re-verified every input, so
/// nothing could move.) The remembered set now carries a seal keyed from the wallet's own seed:
/// what the wallet did not write itself is not adopted — those coins are simply proven again.
/// </summary>
public partial class ReadPathVerificationTests
{
    private static byte[] StateKey(string mnemonic = F.Mnemonic) =>
        LiteHdWallet.Restore(mnemonic).DeriveStateKey(RememberedCoinsSeal.Purpose);

    /// <summary>The remembered set exactly as this wallet writes it.</summary>
    private static string Sealed(string entries) => RememberedCoinsSeal.Seal(StateKey(), entries);

    private const string OtherMnemonic =
        "legal winner thank year wave sausage worth useful legal winner thank yellow";

    [Fact]
    public async Task A_hand_written_remembered_coin_is_not_adopted()
    {
        // The report: a well-formed entry for a transaction that exists nowhere, written straight
        // into the store, and a gateway that lists the same coin but can prove nothing about it.
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync($"{FakeTxId}:0:{F.Receive0}:{FakeSats}:0:0");

        var w = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 25), store);

        Assert.Equal(0, await w.Service.GetSpendableAsync());          // was: FakeSats, the chain never asked
        Assert.True(w.Gateway.RawCalls > 0);                           // the coin was put to the chain
        Assert.False(w.Service.Checks!.TryGetVerified($"c:{FakeTxId}:0:{F.Receive0}", out _));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]   // a made-up seal
    [InlineData("zz")]                                                                    // not even hex
    [InlineData("")]
    public async Task A_made_up_seal_does_not_help(string seal)
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync($"{FakeTxId}:0:{F.Receive0}:{FakeSats}:0:0;m:{seal}");

        var w = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 25), store);

        Assert.Equal(0, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task A_set_sealed_by_another_wallet_is_not_adopted()
    {
        // A genuine seal, but made with a different wallet's seed: copying another device's
        // store over this one must not carry its coins in.
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync(
            RememberedCoinsSeal.Seal(StateKey(OtherMnemonic), $"{FakeTxId}:0:{F.Receive0}:{FakeSats}:0:0"));

        var w = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 25), store);

        Assert.Equal(0, await w.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task Changing_one_figure_in_a_sealed_set_voids_all_of_it()
    {
        var store = new InMemoryWalletStateStore();
        var first = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);
        Assert.Equal(1_000_000_000, await first.Service.GetSpendableAsync());
        var genuine = await store.GetVerifiedCoinsAsync();

        // The proven 10 BLZ is edited to 9,000 BLZ; the seal is left as it was.
        await store.SetVerifiedCoinsAsync(genuine!.Replace(":1000000000:", ":900000000000:"));
        Assert.NotEqual(genuine, await store.GetVerifiedCoinsAsync());

        var second = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 900_000_000_000);
            gw.Withheld.Add(F.TxId(0));                                 // and nothing can be proven this time
        }, store);

        Assert.Equal(0, await second.Service.GetSpendableAsync());     // not 9,000 BLZ, and not adopted at all
    }

    [Fact]
    public async Task An_extra_line_appended_to_a_sealed_set_voids_it_too()
    {
        var store = new InMemoryWalletStateStore();
        var first = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);
        await first.Service.GetSpendableAsync();
        var genuine = await store.GetVerifiedCoinsAsync();
        var seal = genuine![genuine.LastIndexOf(";m:", StringComparison.Ordinal)..];
        var body = genuine[..genuine.LastIndexOf(";m:", StringComparison.Ordinal)];

        await store.SetVerifiedCoinsAsync($"{body};{FakeTxId}:0:{F.Receive0}:{FakeSats}:0:0{seal}");

        var second = await OpenAsync(gw => gw.Coin(F.Receive0, FakeTxId, 0, FakeSats, confs: 25), store);

        Assert.Equal(0, await second.Service.GetSpendableAsync());
    }

    [Fact]
    public async Task What_the_wallet_wrote_itself_is_still_adopted_without_asking_again()
    {
        var store = new InMemoryWalletStateStore();
        var first = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);
        Assert.Equal(1_000_000_000, await first.Service.GetSpendableAsync());

        Assert.Equal(Sealed($"{F.TxId(0)}:0:{F.Receive0}:1000000000:0:0"), await store.GetVerifiedCoinsAsync());

        var second = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000);
            gw.Withheld.Add(F.TxId(0));
        }, store);

        Assert.Equal(1_000_000_000, await second.Service.GetSpendableAsync());
        Assert.Equal(0, second.Gateway.RawCalls);                      // remembered: the chain is not asked twice
    }

    [Fact]
    public async Task A_set_written_before_the_seal_existed_is_proven_again_once_and_sealed()
    {
        // What 1.3.4 – 1.3.6 wrote: genuine, complete, unsealed. It costs one re-verification.
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync($"{F.TxId(0)}:0:{F.Receive0}:1000000000:0:0");

        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, 1_000_000_000), store);

        Assert.Equal(1_000_000_000, await w.Service.GetSpendableAsync());
        Assert.True(w.Gateway.RawCalls > 0);                           // proven afresh, not taken on the old line's word
        Assert.Equal(Sealed($"{F.TxId(0)}:0:{F.Receive0}:1000000000:0:0"), await store.GetVerifiedCoinsAsync());
    }

    [Fact]
    public void The_seal_opens_only_with_the_key_that_made_it_and_only_over_the_same_text()
    {
        var key = StateKey();
        var text = RememberedCoinsSeal.Seal(key, "a:0:b:1:0:0;c:1:d:2:1:9");

        Assert.True(RememberedCoinsSeal.TryOpen(key, text, out var entries));
        Assert.Equal("a:0:b:1:0:0;c:1:d:2:1:9", entries);

        Assert.False(RememberedCoinsSeal.TryOpen(StateKey(OtherMnemonic), text, out _));
        Assert.False(RememberedCoinsSeal.TryOpen(key, text.Replace("b:1", "b:7"), out _));
        Assert.False(RememberedCoinsSeal.TryOpen(key, "a:0:b:1:0:0;c:1:d:2:1:9", out _));   // no seal at all
        Assert.False(RememberedCoinsSeal.TryOpen(key, null, out _));
        Assert.False(RememberedCoinsSeal.TryOpen(key, "", out _));
    }

    [Fact]
    public void The_state_key_is_the_wallets_own_and_differs_by_purpose()
    {
        var wallet = LiteHdWallet.Restore(F.Mnemonic);

        Assert.Equal(32, wallet.DeriveStateKey("x").Length);
        Assert.Equal(wallet.DeriveStateKey("x"), LiteHdWallet.Restore(F.Mnemonic).DeriveStateKey("x"));   // survives a restore
        Assert.NotEqual(wallet.DeriveStateKey("x"), wallet.DeriveStateKey("y"));
        Assert.NotEqual(wallet.DeriveStateKey("x"), LiteHdWallet.Restore(OtherMnemonic).DeriveStateKey("x"));
        Assert.NotEqual(wallet.DeriveStateKey("x"), LiteHdWallet.Restore(F.Mnemonic, "passphrase").DeriveStateKey("x"));
    }
}

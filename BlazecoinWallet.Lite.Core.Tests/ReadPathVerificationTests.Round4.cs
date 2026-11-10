using BlazecoinWallet.Lite.Data;
using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Bug-bounty round four (2026-11-06): what a proven coin IS comes from its proof too. The 1.3.3
/// read path proved that a coin exists, pays the wallet and is mined, and adopted its real value —
/// but whether it is a coinbase (30 confirmations before it can be spent) was still the gateway's
/// word. A gateway answering "isCoinbase: true" for an ordinary coin locked it as "maturing" for as
/// long as that gateway was used. The funding transaction itself says whether it is a coinbase,
/// and its txid commits to that.
/// </summary>
public partial class ReadPathVerificationTests
{
    private const long TenBlz = 1_000_000_000;

    [Fact]
    public async Task A_gateway_cannot_lock_an_ordinary_coin_by_calling_it_a_coinbase()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, TenBlz, confs: 5, coinbase: true));   // the report's row

        var balance = await w.Service.GetBalanceBreakdownAsync();
        var sent = await w.Service.SendAsync(F.Receive1, 100_000_000);

        Assert.Equal(TenBlz, balance.Spendable);            // was 0…
        Assert.Equal(0, balance.ImmatureCoinbase);          // …with the whole coin "maturing"
        Assert.True(sent.Success, sent.Error);              // was "Insufficient spendable balance: 0 BLZ available"
    }

    [Fact]
    public async Task The_old_key_screen_is_not_fooled_by_the_flag_either()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Foreign, F.TxId(11), 0, OldKeyRealSats, confs: 5, coinbase: true));

        var check = await w.Service.CheckWifAsync(OldWif);

        Assert.Equal(OldKeyRealSats, check.SpendableSatoshis);
        Assert.Equal(0, check.ImmatureSatoshis);
    }

    [Fact]
    public async Task A_coins_kind_is_remembered_with_its_proof()
    {
        var store = new InMemoryWalletStateStore();
        var first = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, TenBlz), store);
        Assert.Equal(TenBlz, await first.Service.GetSpendableAsync());
        Assert.Equal(Sealed($"{F.TxId(0)}:0:{F.Receive0}:{TenBlz}:0:0"), await store.GetVerifiedCoinsAsync());   // …:coinbase:height, then the seal

        // A new session through a gateway that now calls the coin a coinbase and cannot prove anything.
        var second = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, TenBlz, confs: 5, coinbase: true);
            gw.Withheld.Add(F.TxId(0));
        }, store);

        var balance = await second.Service.GetBalanceBreakdownAsync();
        Assert.Equal(TenBlz, balance.Spendable);            // the remembered proof says: an ordinary coin
        Assert.Equal(0, balance.ImmatureCoinbase);
    }

    [Fact]
    public async Task A_coin_proven_to_be_a_coinbase_stays_one_whatever_the_gateway_says()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync(Sealed($"{F.TxId(0)}:0:{F.Receive0}:{TenBlz}:1:0"));             // proven earlier: a coinbase

        var w = await OpenAsync(gw => gw.Coin(F.Receive0, F.TxId(0), 0, TenBlz, confs: 5, coinbase: false), store);

        var balance = await w.Service.GetBalanceBreakdownAsync();
        Assert.Equal(0, balance.Spendable);                 // 5 confirmations: a coinbase is not spendable yet
        Assert.Equal(TenBlz, balance.ImmatureCoinbase);
    }

    [Fact]
    public async Task An_entry_from_before_the_kind_was_recorded_is_proven_again_not_adopted()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCoinsAsync(Sealed($"{F.TxId(0)}:0:{F.Receive0}:{TenBlz}"));                 // the form 1.3.3 wrote (sealed here, so it is the FORM that is refused)

        var w = await OpenAsync(gw =>
        {
            gw.Coin(F.Receive0, F.TxId(0), 0, TenBlz);
            gw.Withheld.Add(F.TxId(0));
        }, store);
        Assert.Equal(0, await w.Service.GetSpendableAsync());                   // not taken on the old entry's word

        w.Gateway.Withheld.Clear();
        w.Now += TimeSpan.FromMinutes(11);                                      // past any back-off
        Assert.Equal(TenBlz, await w.Service.GetSpendableAsync());              // proven afresh…
        Assert.Equal(Sealed($"{F.TxId(0)}:0:{F.Receive0}:{TenBlz}:0:0"), await store.GetVerifiedCoinsAsync());     // …and written back in full
    }

    [Fact]
    public async Task The_verifier_reads_the_coins_kind_from_its_own_transaction()
    {
        var w = await OpenAsync();
        var verifier = new LiteTxVerifier(new IndexerDataService(
            new HttpClient(w.Gateway) { BaseAddress = new Uri("https://gw.test/") },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IndexerDataService>.Instance));

        var v = await verifier.VerifyAsync(F.TxId(0), 0, TenBlz, requireInclusionProof: true, expectedAddress: F.Receive0);

        Assert.True(v.Verified, v.Error);
        Assert.False(v.IsCoinbase);                         // an ordinary payment
        Assert.Null(v.VerifiedHeight);                      // no header feed here (the web head's shape)
    }
}

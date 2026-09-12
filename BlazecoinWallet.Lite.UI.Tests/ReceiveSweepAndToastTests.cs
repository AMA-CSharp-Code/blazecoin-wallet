using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI.Pages;
using BlazecoinWallet.Lite.UI.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The 2026-07-25 feature set at the page layer: Receive's fresh-address rotation, the
/// legacy-WIF sweep page's check→sweep flow, and the layout's incoming-payment toast.
/// </summary>
public class ReceiveSweepAndToastTests : LiteUiTestBase
{
    private static string Slot(int i) => LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(i);

    [Fact]
    public void Receive_shows_the_current_address_and_rotates_on_fresh_address()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var cut = RenderComponent<Receive>();

        cut.WaitForAssertion(() => Assert.Contains(Slot(0), cut.Markup));

        // FakeReader reports every address as used (TxCount 1), so rotation is allowed.
        cut.FindAll("button").Single(b => b.TextContent.Contains("Fresh address")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(Slot(1), cut.Markup);                       // new current address
            Assert.Contains("Previous addresses (1)", cut.Markup);      // old one still listed
        });
    }

    [Fact]
    public void Sweep_checks_a_wif_then_sweeps_and_shows_the_txid()
    {
        var relay = new FakeRelay();
        Wire(new LoadedVault(), new FakeReader(), relay);
        var cut = RenderComponent<Sweep>();

        var oldKey = new NBitcoin.Key();
        var wif = oldKey.GetWif(BlazecoinNetwork.Instance).ToString();
        var oldAddress = oldKey.GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString();

        cut.Find("input").Change(wif);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Check Key")).Click();

        // FakeReader serves 0.1 BLZ for any address — the preview shows the old address + amount.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(oldAddress, cut.Markup);
            Assert.Contains("0.1", cut.Markup);
        });

        cut.FindAll("button").Single(b => b.TextContent.Contains("Sweep It All In")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Recovered!", cut.Markup);
            Assert.Contains("cafebabe", cut.Markup); // the relay's txid
        });
        Assert.Equal(1, relay.Calls);
    }

    [Fact]
    public void Sweep_rejects_an_invalid_key_with_a_friendly_error()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var cut = RenderComponent<Sweep>();

        cut.Find("input").Change("not-a-key");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Check Key")).Click();

        cut.WaitForAssertion(() => Assert.Contains("private key", cut.Markup, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Layout_toasts_an_incoming_payment_from_the_watcher()
    {
        var reader = new FakeReader { SpendableSats = 0 }; // start empty
        var wallet = Wire(new LoadedVault(), reader, new FakeRelay());
        await wallet.UnlockAsync();
        var watcher = new IncomingPaymentWatcher(wallet);
        Services.AddSingleton(watcher);

        var cut = RenderComponent<LiteLayout>();
        try
        {
            await watcher.PollOnceAsync();      // baseline: nothing
            reader.SpendableSats = 2_500_000;   // 0.025 BLZ arrives
            await watcher.PollOnceAsync();

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Received", cut.Markup);
                Assert.Contains("0.025", cut.Markup);
            });
        }
        finally { watcher.Stop(); }
    }
}

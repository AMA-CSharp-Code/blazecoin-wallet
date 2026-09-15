using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI.Pages;
using BlazecoinWallet.Lite.UI.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>The 2026-07-25 feature pages: message tools, address book, watch, tx detail,
/// backup quiz, and the app-lock gate.</summary>
public class NewFeaturePageTests : LiteUiTestBase
{
    [Fact]
    public void Tools_signs_a_message_and_verifies_it()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var cut = RenderComponent<Tools>();

        cut.WaitForState(() => cut.FindAll("textarea").Count > 0);
        cut.FindAll("textarea")[0].Change("prove it"); // the sign message box
        cut.FindAll("button").Single(b => b.TextContent.Contains("Sign")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Signature", cut.Markup));
        // The rendered signature verifies against the shown address.
        var sig = cut.FindAll(".lite-txid").Last().TextContent.Trim();
        var address = cut.FindAll(".lite-txid").First().TextContent.Trim();
        Assert.True(BlazecoinMessage.Verify(address, "prove it", sig));
    }

    [Fact]
    public void Address_book_saves_and_lists_a_payee()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var book = new InMemoryAddressBook();
        Services.AddSingleton<IAddressBook>(book); // override Wire's default (last wins)
        var cut = RenderComponent<AddressBook>();

        var addr = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(3);
        cut.FindAll("input")[0].Change("Exchange");
        cut.FindAll("input")[1].Change(addr);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save")).Click();

        Assert.Single(book.Entries);
        Assert.Equal("Exchange", book.Entries[0].Name);
        cut.WaitForAssertion(() => Assert.Contains("Exchange", cut.Markup));
    }

    [Fact]
    public void Address_book_rejects_an_invalid_address()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var book = new InMemoryAddressBook();
        Services.AddSingleton<IAddressBook>(book);
        var cut = RenderComponent<AddressBook>();

        cut.FindAll("input")[0].Change("Bad");
        cut.FindAll("input")[1].Change("not-an-address");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save")).Click();

        Assert.Empty(book.Entries);
        Assert.Contains("not a valid", cut.Markup);
    }

    [Fact]
    public void App_lock_hides_the_wallet_until_the_pin_is_entered()
    {
        var wallet = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        Services.AddSingleton(new IncomingPaymentWatcher(wallet)); // LiteLayout hosts it
        var pin = new InMemoryPinLock();
        pin.Set("2468");
        Services.AddSingleton<IPinLock>(pin); // override Wire's default (last wins)

        var cut = RenderComponent<LiteLayout>(ps => ps.Add(l => l.Body, "<div id=\"secret\">balance</div>"));

        Assert.Contains("Wallet Locked", cut.Markup);
        Assert.DoesNotContain("id=\"secret\"", cut.Markup);   // body hidden while locked

        cut.Find("input[type=password]").Change("2468");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Unlock")).Click();

        cut.WaitForAssertion(() => Assert.Contains("id=\"secret\"", cut.Markup)); // body now shown

        // Resume re-lock: backgrounding calls AppLockSession.Lock() → the layout re-renders
        // and the lock screen returns (a fresh PIN is required on return, not just cold launch).
        Services.GetRequiredService<AppLockSession>().Lock();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Wallet Locked", cut.Markup);
            Assert.DoesNotContain("id=\"secret\"", cut.Markup);
        });
    }

    [Fact]
    public void Watch_page_lists_a_watched_address_balance()
    {
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 4_200_000 }, new FakeRelay());
        var watch = new InMemoryWatchList();
        Services.AddSingleton<IWatchList>(watch); // override Wire's default (last wins)
        var cut = RenderComponent<Watch>();

        var addr = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);
        cut.FindAll("input")[0].Change("Cold");
        cut.FindAll("input")[1].Change(addr);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Watch")).Click();

        Assert.Single(watch.Entries);
        cut.WaitForAssertion(() => Assert.Contains("Cold", cut.Markup));
    }
}

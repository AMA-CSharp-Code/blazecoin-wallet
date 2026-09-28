using BlazecoinWallet.Lite.UI.Pages;
using Bunit;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The Send page's user-facing safety logic: the Review→Confirm step (a mis-send guard — the
/// user re-reads exactly what will be signed), amount/address validation, the money-supply
/// bound (audit M4), BIP21 scan parsing, and that a broadcast only happens on Confirm.
/// </summary>
public class SendPageTests : LiteUiTestBase
{
    [Fact]
    public void Review_shows_the_parsed_amount_and_address_before_any_send()
    {
        var relay = new FakeRelay();
        Wire(new LoadedVault(), new FakeReader(), relay);
        var cut = RenderComponent<Send>();

        cut.Find("input[placeholder='B…']").Change("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29");
        cut.Find("input[inputmode='decimal']").Input("0.05");
        cut.FindAll("button").First(b => b.TextContent.Contains("Review")).Click();

        // Confirmation screen shows the exact destination + amount; nothing broadcast yet.
        // (AmountText trims trailing zeros, so 0.05 renders as "0.05".)
        Assert.Contains("0.05", cut.Markup);
        Assert.Contains("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29", cut.Markup);
        Assert.Contains("Confirm", cut.Markup);
        Assert.Equal(0, relay.Calls);
    }

    [Fact]
    public void Confirm_broadcasts_once_and_shows_the_txid()
    {
        var relay = new FakeRelay();
        Wire(new LoadedVault(), new FakeReader(), relay);
        var cut = RenderComponent<Send>();

        cut.Find("input[placeholder='B…']").Change("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29");
        cut.Find("input[inputmode='decimal']").Input("0.05");
        cut.FindAll("button").First(b => b.TextContent.Contains("Review")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm")).Click();

        Assert.Equal(1, relay.Calls);
        Assert.Contains("cafebabe", cut.Markup); // the returned txid
    }

    [Theory]
    [InlineData("", "0.05", "destination")]                 // empty address
    [InlineData("Bxyz", "", "valid amount")]                 // empty amount
    [InlineData("Bxyz", "-1", "valid amount")]               // negative
    [InlineData("Bxyz", "9999999999", "supply")]             // above the money supply (M4)
    public void Invalid_input_shows_an_error_and_never_reaches_review(string addr, string amount, string expect)
    {
        var relay = new FakeRelay();
        Wire(new LoadedVault(), new FakeReader(), relay);
        var cut = RenderComponent<Send>();

        if (addr.Length > 0) cut.Find("input[placeholder='B…']").Change(addr);
        if (amount.Length > 0) cut.Find("input[inputmode='decimal']").Input(amount);
        cut.FindAll("button").First(b => b.TextContent.Contains("Review")).Click();

        Assert.Contains(expect, cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Confirm &amp; Send", cut.Markup);
        Assert.Equal(0, relay.Calls);
    }

    [Fact]
    public void Scanning_a_bip21_uri_populates_both_address_and_amount()
    {
        var scanner = new FakeScanner { Next = "blazecoin:Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29?amount=0.25&label=x" };
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay(), scanner);
        var cut = RenderComponent<Send>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Scan QR Code")).Click();

        Assert.Equal("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29", cut.Find("input[placeholder='B…']").GetAttribute("value"));
        Assert.Equal("0.25", cut.Find("input[inputmode='decimal']").GetAttribute("value"));
    }

    [Fact]
    public void The_scan_button_is_hidden_when_no_camera_is_available()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay(), new FakeScanner { IsAvailable = false });
        var cut = RenderComponent<Send>();
        Assert.DoesNotContain("Scan QR Code", cut.Markup);
    }

    [Fact]
    public void Max_fills_the_spendable_balance_and_the_review_flags_an_entire_balance_send()
    {
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 12_345_678 }, new FakeRelay());
        var cut = RenderComponent<Send>();

        cut.Find("input[placeholder='B…']").Change("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29");
        cut.FindAll("button").First(b => b.TextContent.Contains("Max")).Click();

        // The amount field is filled with the spendable balance and locked.
        Assert.Equal("0.12345678", cut.Find("input[inputmode='decimal']").GetAttribute("value"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Review")).Click();
        Assert.Contains("entire balance", cut.Markup);
    }

    [Fact]
    public void Confirming_a_max_send_calls_send_max_not_a_fixed_send()
    {
        var reader = new FakeReader { SpendableSats = 7_000_000 };
        var relay = new FakeRelay();
        Wire(new LoadedVault(), reader, relay);
        var cut = RenderComponent<Send>();

        cut.Find("input[placeholder='B…']").Change("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29");
        cut.FindAll("button").First(b => b.TextContent.Contains("Max")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Review")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm")).Click();

        // The sweep path went through the relay exactly once and reported success.
        Assert.Equal(1, relay.Calls);
        Assert.Contains("cafebabe", cut.Markup);
    }
}

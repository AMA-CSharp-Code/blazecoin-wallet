using BlazecoinWallet.Lite.UI.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// Audit 2026-11-09: the receipt for a broadcast — green, or amber with the "not confirmed yet"
/// note — is ONE component. Send, Sweep and the Home move card used to write it out each.
/// </summary>
public class SendReceiptTests : LiteUiTestBase
{
    private bool _wired;

    private IRenderedComponent<SendReceipt> Render(bool unconfirmed, string? txId = "cafe")
    {
        if (!_wired) { Services.AddSingleton<IExplorerSettings>(Explorer); _wired = true; }
        return RenderComponent<SendReceipt>(p => p
            .Add(r => r.Unconfirmed, unconfirmed)
            .Add(r => r.TxId, txId)
            .Add(r => r.ConfirmedLead, "Sent! The network accepted transaction")
            .Add(r => r.UnconfirmedLead, "Sent, but not confirmed yet — transaction")
            .AddChildContent("<i>tail</i>"));
    }

    [Fact]
    public void A_confirmed_receipt_is_green_names_the_transaction_and_carries_no_note()
    {
        var cut = Render(unconfirmed: false);

        Assert.Single(cut.FindAll(".lite-success"));
        Assert.Empty(cut.FindAll(".lite-unconfirmed"));
        Assert.Contains("Sent! The network accepted transaction", cut.Markup);
        Assert.Contains("<strong>cafe</strong>", cut.Markup);
        Assert.Empty(cut.FindAll(".lite-unconfirmed-note"));
        Assert.Contains("<i>tail</i>", cut.Markup);
    }

    [Fact]
    public void An_unconfirmed_receipt_is_amber_and_carries_the_one_note()
    {
        var cut = Render(unconfirmed: true);

        Assert.Single(cut.FindAll(".lite-unconfirmed"));
        Assert.Empty(cut.FindAll(".lite-success"));
        Assert.Contains("not confirmed yet", cut.Markup);
        Assert.Equal(LiteSendResult.UnconfirmedNote, cut.Find(".lite-unconfirmed-note").TextContent);
    }

    [Fact]
    public void The_transaction_links_to_the_explorer_when_one_is_configured_and_can_be_left_out()
    {
        Explorer.Save("https://explorer.test/");
        var linked = Render(unconfirmed: false);
        Assert.Contains("https://explorer.test/", linked.Find("a.lite-link").GetAttribute("href"));

        var none = Render(unconfirmed: true, txId: null);
        Assert.Empty(none.FindAll("strong"));
    }

    [Fact]
    public void Every_page_that_shows_a_receipt_uses_the_one_component()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "BlazecoinWallet.Lite.UI", "Pages"))) dir = dir.Parent;
        var pages = Path.Combine(dir!.FullName, "BlazecoinWallet.Lite.UI", "Pages");
        foreach (var page in new[] { "Send.razor", "Sweep.razor", "Home.razor" })
        {
            var source = File.ReadAllText(Path.Combine(pages, page));
            Assert.Contains("<SendReceipt", source);
            Assert.DoesNotContain("lite-unconfirmed-note", source);
            Assert.DoesNotContain("UnconfirmedNote", source);
        }
    }
}

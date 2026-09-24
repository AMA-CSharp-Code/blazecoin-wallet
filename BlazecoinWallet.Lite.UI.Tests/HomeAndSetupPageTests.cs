using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>Home (balance + the history-capability branch, audit F2) and Setup (create flow).</summary>
public class HomeAndSetupPageTests : LiteUiTestBase
{
    [Fact]
    public void Home_shows_the_spendable_balance()
    {
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 12_345_678 }, new FakeRelay());
        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => Assert.Contains("0.12345678", cut.Markup));
    }

    [Fact]
    public void Home_history_rows_navigate_to_the_transaction_detail_view()
    {
        // History rows are now tappable → /tx/{id} (the explorer link moved to the detail page).
        var reader = new FakeReader { History = [new BlazecoinWallet.Lite.Data.LiteHistoryEntry("deadbeef01", 100, DateTime.UtcNow, 5_000_000, "received")] };
        Wire(new LoadedVault(), reader, new FakeRelay());
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".lite-history-row")));
        cut.Find(".lite-history-row").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/tx/deadbeef01", nav.Uri);
    }

    [Fact]
    public void Home_names_an_in_flight_send_instead_of_blaming_maturity()
    {
        // The exact live confusion (2026-08-03): after a send broadcasts, the gateway
        // hides the spent outpoints from the utxo list while the confirmed summary still
        // counts them — the old card blamed coinbase maturity ("freshly mined coins wait
        // 30 blocks") for a gap that was really coins leaving.
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 0, SummaryBalance = 10_000_000 }, new FakeRelay());
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("leaving in an unconfirmed send", cut.Markup);
            Assert.DoesNotContain("wait 30 blocks", cut.Markup);
        });
    }

    [Fact]
    public void Home_shows_the_provenance_chip_only_when_there_are_coins_to_trace()
    {
        // Funded wallet → the gold chip is the discoverable front door to /provenance.
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 12_345_678 }, new FakeRelay());
        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".lite-prov-chip")));

        cut.Find(".lite-prov-chip").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/provenance", nav.Uri);
    }

    [Fact]
    public void Home_hides_the_provenance_chip_on_an_empty_wallet()
    {
        // No confirmed coins → no lineage to trace; the chip would be a dead end.
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 0 }, new FakeRelay());
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.Contains("Balance", cut.Markup));
        Assert.Empty(cut.FindAll(".lite-prov-chip"));
    }

    [Fact]
    public void Home_says_history_is_unsupported_on_a_personal_node_not_no_activity()
    {
        // Audit F2: a bare personal node has no address index — the UI must be honest about
        // "unsupported here" rather than implying "no activity".
        Wire(new LoadedVault(), new FakeReader { HistorySupported = false }, new FakeRelay());
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("doesn't index transaction history", cut.Markup);
            Assert.DoesNotContain("Nothing yet", cut.Markup);
        });
    }

    [Fact]
    public void Setup_create_shows_the_twelve_word_mnemonic_for_backup()
    {
        // Empty vault → Setup stays put (no redirect), Create reveals the phrase once.
        var vault = new LoadedVault { Words = null };
        Wire(vault, new FakeReader(), new FakeRelay());
        var cut = RenderComponent<Setup>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Create")).Click();

        cut.WaitForAssertion(() =>
        {
            // 12 numbered word cells rendered.
            Assert.Equal(12, cut.FindAll(".lite-word").Count);
            Assert.NotNull(vault.Words); // stored
        });
    }

    [Fact]
    public void Quiz_skip_warns_and_only_proceeds_on_explicit_accept()
    {
        var vault = new LoadedVault { Words = null };
        Wire(vault, new FakeReader(), new FakeRelay());
        var nav = Services.GetRequiredService<Bunit.TestDoubles.FakeNavigationManager>();
        var cut = RenderComponent<Setup>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Create")).Click();
        cut.WaitForAssertion(() => Assert.Equal(12, cut.FindAll(".lite-word").Count));
        cut.Find("input[type='checkbox']").Change(true);
        cut.FindAll("button").First(b => b.TextContent.Contains("Continue")).Click();

        cut.FindAll("button").First(b => b.TextContent.Contains("Skip for now")).Click();
        Assert.Contains("gone forever", cut.Markup);          // the warning modal is up

        cut.FindAll("button").First(b => b.TextContent.Contains("Go back")).Click();
        Assert.DoesNotContain("gone forever", cut.Markup);    // dismissed — still on the quiz

        cut.FindAll("button").First(b => b.TextContent.Contains("Skip for now")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("accept the risk")).Click();
        Assert.Equal(nav.BaseUri, nav.Uri);                   // wallet opened
    }
}

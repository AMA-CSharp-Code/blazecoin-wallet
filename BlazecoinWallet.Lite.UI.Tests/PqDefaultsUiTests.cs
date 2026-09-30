using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.UI.Pages;
using Bunit;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// Post-quantum by default on the lite pages (2026-09-28): Receive opens on BQ… with the sender note,
/// Home offers "Move everything to post-quantum" while legacy coins remain (two taps, one send), and
/// the legacy-key sweep shows — and pays — a BQ… destination. Before the fork none of it appears.
/// FakeReader's coin implies tip 109, so activation height 100 = "the fork is active".
/// </summary>
public class PqDefaultsUiTests : LiteUiTestBase
{
    private static readonly LiteHdWallet W = LiteHdWallet.Restore(TestMnemonic);

    // ════════════════════════════════════════════════════════════ Receive

    [Fact]
    public void Receive_opens_on_post_quantum_with_the_sender_note_and_legacy_one_tap_away()
    {
        var svc = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        svc.PqActivationHeight = 100;

        var cut = RenderComponent<Receive>();

        cut.WaitForAssertion(() => Assert.Contains(W.GetPqAddress(0), cut.Markup));
        Assert.Contains("V1.5 and older wallets cannot pay a BQ", cut.Markup);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Legacy (B")).Click();
        cut.WaitForAssertion(() => Assert.Contains(W.GetReceiveAddress(0), cut.Markup));
        Assert.Contains("any Blazecoin wallet can pay it", cut.Markup);
    }

    [Fact]
    public void Receive_before_the_fork_stays_on_legacy()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());   // chain height 4,250,000 > tip 109

        var cut = RenderComponent<Receive>();

        cut.WaitForAssertion(() => Assert.Contains(W.GetReceiveAddress(0), cut.Markup));
        Assert.DoesNotContain(W.GetPqAddress(0), cut.Markup);
        Assert.DoesNotContain("V1.5 and older", cut.Markup);
    }

    // ════════════════════════════════════════════════════════════ Home: move everything to post-quantum

    [Fact]
    public void Home_offers_the_move_while_legacy_coins_remain_and_moves_them_in_one_send()
    {
        var relay = new FakeRelay();
        var svc = Wire(new LoadedVault(), new FakeReader(), relay);
        svc.PqActivationHeight = 100;

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.Contains("Move everything to post-quantum", cut.Markup));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Move everything to post-quantum")).Click();
        cut.WaitForAssertion(() => Assert.Contains("in one transaction", cut.Markup));
        Assert.Equal(0, relay.Calls);                                  // the first tap only explains

        cut.FindAll("button").Single(b => b.TextContent.Contains("Move it now")).Click();

        cut.WaitForAssertion(() => Assert.Contains("to your post-quantum address", cut.Markup));
        Assert.Equal(1, relay.Calls);
    }

    [Fact]
    public void Home_before_the_fork_offers_no_move()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());

        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.Contains("Balance", cut.Markup));
        Assert.DoesNotContain("Move everything to post-quantum", cut.Markup);
    }

    [Fact]
    public void Home_shows_a_refused_move_and_does_not_retry()
    {
        var relay = new FakeRelay { Result = LiteBroadcastResult.Fail("txn-mempool-conflict", BroadcastFailureKind.Rejected) };
        var svc = Wire(new LoadedVault(), new FakeReader(), relay);
        svc.PqActivationHeight = 100;

        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => Assert.Contains("Move everything to post-quantum", cut.Markup));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Move everything to post-quantum")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Move it now")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Not moved:", cut.Markup));
        Assert.Contains("txn-mempool-conflict", cut.Markup);
        Assert.Contains("Refresh before trying again", cut.Markup);
        Assert.Equal(1, relay.Calls);
    }

    // ════════════════════════════════════════════════════════════ Sweep

    [Fact]
    public void Sweep_shows_the_post_quantum_destination_after_the_fork()
    {
        var svc = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        svc.PqActivationHeight = 100;

        var cut = RenderComponent<Sweep>();
        using var oldKey = new NBitcoin.Key();
        cut.Find("input").Change(oldKey.GetWif(BlazecoinNetwork.Instance).ToString());
        cut.FindAll("button").Single(b => b.TextContent.Contains("Check Key")).Click();

        cut.WaitForAssertion(() => Assert.Contains("(post-quantum address)", cut.Markup));
        Assert.Contains(W.GetPqAddress(0), cut.Markup);
    }
}

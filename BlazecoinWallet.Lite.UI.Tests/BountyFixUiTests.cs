using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.Fork;
using BlazecoinWallet.Lite.UI.Pages;
using BlazecoinWallet.Lite.UI.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// What the pages show after the 2026-11-06 bug-bounty fixes: a typed or scanned "1,5" is one
/// and a half; a coin or received row the wallet has not proven on the chain is labelled as
/// such and kept out of the balance; an unsigned fork announcement says where it came from.
/// </summary>
public class BountyFixUiTests : LiteUiTestBase
{
    private const string Dest = "Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29";

    // ── amounts ──

    [Theory]
    [InlineData("1,5", "1.5")]
    [InlineData("0,5", "0.5")]
    [InlineData("1.5", "1.5")]
    public void A_typed_comma_is_the_decimal_mark_on_the_review_screen(string typed, string shown)
    {
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 10_000_000_000 }, new FakeRelay());
        var cut = RenderComponent<Send>();

        cut.Find("input[placeholder='B…']").Change(Dest);
        cut.Find("input[inputmode='decimal']").Input(typed);
        cut.FindAll("button").First(b => b.TextContent.Contains("Review")).Click();

        // The figure the user confirms — it used to read 15 for "1,5".
        Assert.Equal(shown, cut.Find(".lite-balance").TextContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
    }

    [Theory]
    [InlineData("1,234.5")]
    [InlineData("1 000")]
    public void An_amount_with_a_thousands_separator_never_reaches_review(string typed)
    {
        Wire(new LoadedVault(), new FakeReader { SpendableSats = 10_000_000_000 }, new FakeRelay());
        var cut = RenderComponent<Send>();

        cut.Find("input[placeholder='B…']").Change(Dest);
        cut.Find("input[inputmode='decimal']").Input(typed);
        cut.FindAll("button").First(b => b.TextContent.Contains("Review")).Click();

        Assert.Contains("valid amount", cut.Markup);
        Assert.DoesNotContain("Confirm & Send", cut.Markup);
    }

    [Fact]
    public void A_scanned_request_with_a_comma_amount_fills_the_address_and_leaves_the_amount_out()
    {
        var scanner = new FakeScanner { Next = $"blazecoin:{Dest}?amount=1,5" };
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay(), scanner);
        var cut = RenderComponent<Send>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Scan QR Code")).Click();

        Assert.Equal(Dest, cut.Find("input[placeholder='B…']").GetAttribute("value"));
        var amount = cut.Find("input[inputmode='decimal']").GetAttribute("value");
        Assert.True(string.IsNullOrEmpty(amount), $"amount was pre-filled with '{amount}'");   // was "15"
        Assert.Contains("amount isn't valid", cut.Markup);
    }

    // ── unproven coins and rows ──

    /// <summary>A wallet in gateway mode (a verifier is wired) over a source that can produce no
    /// proof for anything it lists — every confirmed coin and received row stays a claim.</summary>
    private LiteWalletService WireUnprovable(FakeReader reader)
    {
        Wire(new LoadedVault(), reader, new FakeRelay());
        var service = new LiteWalletService(new LoadedVault(), reader, new FakeRelay(), verifier: new LiteTxVerifier(reader));
        service.Checks!.Pace = TimeSpan.Zero;
        Services.AddSingleton(service);   // after Wire — the last registration wins
        return service;
    }

    [Fact]
    public void Home_keeps_an_unproven_coin_out_of_the_balance_and_says_so()
    {
        WireUnprovable(new FakeReader { SpendableSats = 77_700_000_000 });
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("0", cut.Find(".lite-balance").TextContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
            Assert.Contains("777", cut.Find(".lite-unverified").TextContent);
            Assert.Contains("not verified against the chain yet", cut.Find(".lite-unverified").TextContent);
        });
        // Not explained away as an in-flight send, and no provenance chip for coins that are not proven.
        Assert.DoesNotContain("leaving in an unconfirmed send", cut.Markup);
        Assert.DoesNotContain("Coin Provenance", cut.Markup);
    }

    [Fact]
    public void Home_marks_a_received_row_that_is_not_proven()
    {
        var reader = new FakeReader
        {
            SpendableSats = 0,
            History =
            [
                new LiteHistoryEntry(new string('e', 64), 4_300_000, DateTime.UtcNow, 77_700_000_000, "received"),
                new LiteHistoryEntry(new string('a', 64), 4_299_000, DateTime.UtcNow, -5_000_000, "sent"),
                new LiteHistoryEntry(new string('b', 64), 0, DateTime.UtcNow, 3_000_000, "received"),
            ],
        };
        WireUnprovable(reader);
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".lite-history-row");
            Assert.Equal(3, rows.Count);
            Assert.Contains("not verified on the chain", rows.Single(r => r.TextContent.Contains("777")).TextContent);
            Assert.Contains("unconfirmed", rows.Single(r => r.TextContent.Contains("0.03")).TextContent);
            var sent = rows.Single(r => r.TextContent.Contains("0.05"));
            Assert.DoesNotContain("verified", sent.TextContent);
            Assert.DoesNotContain("unconfirmed", sent.TextContent);
        });
    }

    [Fact]
    public void Home_shows_no_verification_labels_for_a_source_that_is_authoritative()
    {
        var reader = new FakeReader
        {
            SpendableSats = 12_345_678,
            History = [new LiteHistoryEntry(new string('e', 64), 100, DateTime.UtcNow, 5_000_000, "received")],
        };
        Wire(new LoadedVault(), reader, new FakeRelay());   // no verifier: the personal-node shape
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.Contains("0.12345678", cut.Markup));
        Assert.Empty(cut.FindAll(".lite-unverified"));
        Assert.DoesNotContain("not verified", cut.Markup);
        Assert.DoesNotContain("verifying", cut.Markup);
    }

    // ── the fork banner ──

    private sealed class StubFork : IForkStatusService
    {
        public ForkStatus? Current { get; set; }
        public ForkBannerState State { get; set; }
        public long EffectiveTip { get; set; }
        public event Action? Changed { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool NoticeDismissed => false;
        public void DismissNotice() { }
    }

    private void WireBanner(ForkStatus status, ForkBannerState state, long effectiveTip)
    {
        var wallet = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        Services.AddSingleton(new IncomingPaymentWatcher(wallet));
        Services.AddSingleton<IForkStatusService>(new StubFork { Current = status, State = state, EffectiveTip = effectiveTip });
    }

    [Fact]
    public void An_unsigned_announcement_says_it_is_the_gateways_word()
    {
        WireBanner(new ForkStatus("nextfork", 5_000_000, "2.1.0", 4_990_000, DateTime.UtcNow), ForkBannerState.Warning, 4_990_000);
        var cut = RenderComponent<LiteLayout>();

        var bar = cut.Find(".lite-forkbar-warning");
        Assert.Contains(ForkBanner.UnverifiedNote, bar.TextContent);
        Assert.Contains("not signed by the Blazecoin project", bar.QuerySelector(".lite-forkbar-unverified")!.TextContent);
        Assert.Equal("https://blazecoin.co.uk/Wallet", bar.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void A_signed_announcement_carries_no_such_note()
    {
        WireBanner(new ForkStatus("nextfork", 5_000_000, "2.1.0", 4_990_000, DateTime.UtcNow, Authenticated: true),
            ForkBannerState.Warning, 4_990_000);
        var cut = RenderComponent<LiteLayout>();

        Assert.Null(cut.Find(".lite-forkbar-warning").QuerySelector(".lite-forkbar-unverified"));
    }

    [Fact]
    public void The_days_left_are_counted_from_the_wallets_own_tip_not_the_gateways_claim()
    {
        // The gateway claims a tip one block short of the fork; the wallet's verified tip is 17 days out.
        WireBanner(new ForkStatus("nextfork", 5_000_000, "2.1.0", 4_999_999, DateTime.UtcNow), ForkBannerState.Notice, 5_000_000 - 50_000);
        var cut = RenderComponent<LiteLayout>();

        Assert.Contains("(about 17 days).", cut.Find(".lite-forkbar-notice").TextContent);
    }

    [Fact]
    public void The_banner_prints_a_version_only_when_it_is_a_version()
    {
        // Even if a status with free text reached the layout, the wording refuses it.
        WireBanner(new ForkStatus("nextfork", 5_000_000, "9.9 from wallet-update.example (not", 4_990_000, DateTime.UtcNow),
            ForkBannerState.Warning, 4_990_000);
        var cut = RenderComponent<LiteLayout>();

        var text = cut.Find(".lite-forkbar-warning").TextContent;
        Assert.Contains("Get the latest version from", text);
        Assert.DoesNotContain("wallet-update.example", text);
    }
}

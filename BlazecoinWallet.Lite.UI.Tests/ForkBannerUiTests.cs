using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Fork;
using BlazecoinWallet.Lite.UI.Pages;
using BlazecoinWallet.Lite.UI.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// PQ_SIGNATURES §7.1 rendering: the layout shows ONE banner in three states from the fork
/// status service (nothing when None), the Notice dismisses for the session, the Warning
/// doubles as "Reload to update" on the web head, the Stopped state gates Send, and the
/// Settings page quotes the same version and the announcement.
/// </summary>
public class ForkBannerUiTests : LiteUiTestBase
{
    private const long H = 5_000_000;

    private sealed class StubFork : IForkStatusService
    {
        public ForkStatus? Current { get; set; }
        public ForkBannerState State { get; set; }
        public event Action? Changed;
        public int Starts;
        public void Start() => Starts++;
        public void Stop() { }
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool NoticeDismissed { get; private set; }
        public void DismissNotice() { NoticeDismissed = true; Changed?.Invoke(); }
        public void Raise() => Changed?.Invoke();
    }

    private static ForkStatus Announced(long tip, string? min = "2.1.0") =>
        new("pqsig", H, min, tip, DateTime.UtcNow);

    private StubFork WireLayout(ForkBannerState state, long tip, string? min = "2.1.0")
    {
        var wallet = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        Services.AddSingleton(new IncomingPaymentWatcher(wallet));
        var fork = new StubFork
        {
            State = state,
            Current = state == ForkBannerState.None ? null : Announced(tip, min),
        };
        Services.AddSingleton<IForkStatusService>(fork); // after Wire — last registration wins
        return fork;
    }

    private static string N(long v) => v.ToString("N0");

    [Fact]
    public void No_announcement_renders_no_banner_and_starts_the_poll()
    {
        var fork = WireLayout(ForkBannerState.None, 0);
        var cut = RenderComponent<LiteLayout>();
        Assert.DoesNotContain("lite-forkbar", cut.Markup);
        Assert.DoesNotContain("Update required", cut.Markup);
        Assert.Equal(1, fork.Starts);
    }

    [Fact]
    public void Notice_renders_the_spec_text_with_the_link_and_a_dismiss_button()
    {
        WireLayout(ForkBannerState.Notice, H - 50_000); // 17 days
        var cut = RenderComponent<LiteLayout>();
        var bar = cut.Find(".lite-forkbar-notice");

        Assert.Equal("status", bar.GetAttribute("role"));
        Assert.Contains($"Update required before block {N(H)}", bar.TextContent);
        Assert.Contains("(about 17 days).", bar.TextContent);
        Assert.Contains($"Your wallet (v{LiteClientInfo.Version}) will stop following the chain at that block.", bar.TextContent);
        Assert.Contains("Get v2.1.0 from", bar.TextContent);
        Assert.Contains("blazecoin.co.uk/Wallet", bar.TextContent);
        Assert.Equal("https://blazecoin.co.uk/Wallet", bar.QuerySelector("a")!.GetAttribute("href"));
        Assert.NotNull(bar.QuerySelector("button.lite-forkbar-close"));
        // Notice is the quiet one — gold, not amber, not red.
        Assert.Contains("--blz-gold", bar.GetAttribute("style"));
    }

    [Fact]
    public void Notice_dismiss_hides_it_for_the_session()
    {
        var fork = WireLayout(ForkBannerState.Notice, H - 50_000);
        var cut = RenderComponent<LiteLayout>();
        cut.Find("button.lite-forkbar-close").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("lite-forkbar", cut.Markup));
        Assert.True(fork.NoticeDismissed);
    }

    [Fact]
    public void Warning_is_amber_not_dismissible_and_says_reload_on_the_web_head()
    {
        var was = LiteClientInfo.IsWebHead;
        try
        {
            LiteClientInfo.IsWebHead = true;
            WireLayout(ForkBannerState.Warning, H - 40_320); // exactly two weeks
            var cut = RenderComponent<LiteLayout>();
            var bar = cut.Find(".lite-forkbar-warning");

            Assert.Equal("alert", bar.GetAttribute("role"));
            Assert.Contains("#ffc107", bar.GetAttribute("style"));
            Assert.Null(bar.QuerySelector("button.lite-forkbar-close"));
            Assert.Contains($"Update required before block {N(H)}", bar.TextContent);
            Assert.Contains("(about 14 days).", bar.TextContent);
            Assert.Contains("Reload to update.", bar.TextContent); // the bundle is behind the floor
        }
        finally { LiteClientInfo.IsWebHead = was; }
    }

    [Fact]
    public void Warning_on_a_native_head_or_at_the_floor_has_no_reload_hint()
    {
        var was = LiteClientInfo.IsWebHead;
        try
        {
            // Native head: a reload changes nothing, so the hint is suppressed.
            LiteClientInfo.IsWebHead = false;
            WireLayout(ForkBannerState.Warning, H - 10);
            var cut = RenderComponent<LiteLayout>();
            Assert.Contains("Update required", cut.Markup);
            Assert.DoesNotContain("Reload to update", cut.Markup);
        }
        finally { LiteClientInfo.IsWebHead = was; }
    }

    [Fact]
    public void Warning_within_two_weeks_on_the_web_head_without_a_newer_floor_has_no_reload_hint()
    {
        var was = LiteClientInfo.IsWebHead;
        try
        {
            // Web head but minClientVersion == this build: the served bundle IS current, the
            // warning is purely the block clock — reloading would change nothing.
            LiteClientInfo.IsWebHead = true;
            WireLayout(ForkBannerState.Warning, H - 10, min: LiteClientInfo.Version);
            var cut = RenderComponent<LiteLayout>();
            Assert.Contains("Update required", cut.Markup);
            Assert.DoesNotContain("Reload to update", cut.Markup);
        }
        finally { LiteClientInfo.IsWebHead = was; }
    }

    [Fact]
    public void Stopped_is_red_and_reports_the_last_verified_block()
    {
        WireLayout(ForkBannerState.Stopped, H + 3);
        var cut = RenderComponent<LiteLayout>();
        var bar = cut.Find(".lite-forkbar-stopped");

        Assert.Equal("alert", bar.GetAttribute("role"));
        Assert.Contains("--blz-danger", bar.GetAttribute("style"));
        Assert.Null(bar.QuerySelector("button.lite-forkbar-close"));
        Assert.Contains($"This wallet stopped at block {N(H)}.", bar.TextContent);
        // No header sync in the test wiring → the honest cap, one below the fork height.
        Assert.Contains($"Balance last verified at block {N(H - 1)}.", bar.TextContent);
        Assert.Contains("Funds are safe; install v2.1.0 and restore from your recovery phrase.", bar.TextContent);
        Assert.Equal("https://blazecoin.co.uk/Wallet", bar.QuerySelector("a")!.GetAttribute("href"));
        Assert.DoesNotContain("Reload to update", bar.TextContent);
    }

    [Fact]
    public void The_layout_re_renders_when_the_poll_changes_the_state()
    {
        var fork = WireLayout(ForkBannerState.None, 0);
        var cut = RenderComponent<LiteLayout>();
        Assert.DoesNotContain("lite-forkbar", cut.Markup);

        fork.Current = Announced(H - 10);
        fork.State = ForkBannerState.Warning;
        fork.Raise();
        cut.WaitForAssertion(() => Assert.Contains("lite-forkbar-warning", cut.Markup));

        fork.Current = Announced(H);
        fork.State = ForkBannerState.Stopped;
        fork.Raise();
        cut.WaitForAssertion(() => Assert.Contains("lite-forkbar-stopped", cut.Markup));
        Assert.DoesNotContain("lite-forkbar-warning", cut.Markup); // ONE banner
    }

    // ── Send gate ──

    [Fact]
    public void Send_blocks_review_and_confirm_in_the_Stopped_state()
    {
        var relay = new FakeRelay();
        Wire(new LoadedVault(), new FakeReader(), relay);
        Services.AddSingleton<IForkStatusService>(new StubFork { State = ForkBannerState.Stopped, Current = Announced(H + 1) });
        var cut = RenderComponent<Send>();

        var stopped = cut.Find(".lite-send-stopped");
        Assert.Contains($"This wallet stopped at block {N(H)}.", stopped.TextContent);
        Assert.Contains("install v2.1.0 and restore from your recovery phrase", stopped.TextContent);

        var review = cut.FindAll("button").First(b => b.TextContent.Contains("Review"));
        Assert.True(review.HasAttribute("disabled"));

        // Even a click that gets through (script, stale DOM) never reaches the confirm screen.
        cut.Find("input[placeholder='B…']").Change("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29");
        cut.Find("input[inputmode='decimal']").Input("0.05");
        review.Click();
        Assert.DoesNotContain("Please confirm", cut.Markup); // no review screen
        Assert.Equal(0, relay.Calls);
    }

    [Fact]
    public void Send_works_normally_in_Warning()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        Services.AddSingleton<IForkStatusService>(new StubFork { State = ForkBannerState.Warning, Current = Announced(H - 10) });
        var cut = RenderComponent<Send>();

        Assert.DoesNotContain("lite-send-stopped", cut.Markup);
        var review = cut.FindAll("button").First(b => b.TextContent.Contains("Review"));
        Assert.False(review.HasAttribute("disabled"));
        cut.Find("input[placeholder='B…']").Change("Bf3VWe6aNDbXkQcqju3kSGCYBVymTtin29");
        cut.Find("input[inputmode='decimal']").Input("0.05");
        review.Click();
        Assert.Contains("Please confirm", cut.Markup); // the review screen
    }

    [Fact]
    public void Send_locks_when_the_poll_flips_to_Stopped_while_the_page_is_open()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var fork = new StubFork { State = ForkBannerState.Warning, Current = Announced(H - 1) };
        Services.AddSingleton<IForkStatusService>(fork);
        var cut = RenderComponent<Send>();
        Assert.DoesNotContain("lite-send-stopped", cut.Markup);

        fork.Current = Announced(H);
        fork.State = ForkBannerState.Stopped;
        fork.Raise();
        cut.WaitForAssertion(() => Assert.Contains("lite-send-stopped", cut.Markup));
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Contains("Review")).HasAttribute("disabled"));
    }

    // ── Settings ──

    private void WireSettingsExtras()
    {
        Services.AddSingleton<IGatewaySettings>(new InMemoryGatewaySettings(["https://gw.test/"]));
        Services.AddSingleton<IPersonalNodeSettings>(new InMemoryPersonalNodeSettings());
        Services.AddSingleton<IP2PNodeSettings>(new InMemoryP2PNodeSettings(["85.15.179.171:55414"]));
    }

    [Fact]
    public void Settings_quotes_the_shared_client_version_and_hides_fork_status_when_unannounced()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        WireSettingsExtras();
        var cut = RenderComponent<Settings>();

        Assert.Contains($"· {LiteClientInfo.Version}", cut.Markup);
        Assert.Contains("2.0.5", LiteClientInfo.FromAssembly(typeof(Settings).Assembly)); // the UI <Version>
        Assert.DoesNotContain("Fork Status", cut.Markup);
    }

    [Fact]
    public void Settings_shows_the_three_announcement_fields_when_set()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        WireSettingsExtras();
        Services.AddSingleton<IForkStatusService>(new StubFork { State = ForkBannerState.Notice, Current = Announced(H - 100_000) });
        var cut = RenderComponent<Settings>();

        var card = cut.Find(".lite-fork-status");
        Assert.Contains("Fork Status", card.TextContent);
        Assert.Contains("pqsig", card.TextContent);
        Assert.Contains(N(H), card.TextContent);
        Assert.Contains("v2.1.0", card.TextContent);
        Assert.Contains("phoenix413", card.TextContent); // what THIS build follows
    }
}

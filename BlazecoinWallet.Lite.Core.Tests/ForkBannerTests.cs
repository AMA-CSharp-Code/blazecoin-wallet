using System.Reflection;
using BlazecoinWallet.Lite.Fork;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// PQ_SIGNATURES §7.1 test pins: the three banner states at the EXACT block boundaries
/// (forkHeight − tip = 40,321 / 40,320 / 0), minClientVersion equal / newer, null fields render
/// nothing, days derived from blocks and rounded down — and the wording, pinned once here so
/// every head quotes the same sentence.
/// </summary>
public class ForkBannerTests
{
    private const long H = 5_000_000;
    private const string Build = "2.0.5";

    private static readonly IReadOnlySet<string> Phoenix =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "phoenix413" };
    private static readonly IReadOnlySet<string> PhoenixAndPq =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "phoenix413", "pqsig" };

    private static ForkStatus At(long tip, string? name = "pqsig", long? height = H, string? min = Build)
        => new(name, height, min, tip, DateTime.UnixEpoch);

    // ── The block-distance boundaries (min == build, so only the height branch speaks) ──

    [Theory]
    [InlineData(40_321, ForkBannerState.Notice)]   // more than two weeks → Notice
    [InlineData(40_320, ForkBannerState.Warning)]  // exactly two weeks → Warning
    [InlineData(1, ForkBannerState.Warning)]
    [InlineData(0, ForkBannerState.Stopped)]       // tip == forkHeight, no rules → Stopped
    [InlineData(-1, ForkBannerState.Stopped)]      // past it stays Stopped
    public void Block_distance_boundaries_for_a_build_without_the_fork_rules(long distance, ForkBannerState expected)
    {
        var tip = H - distance;
        Assert.Equal(expected, ForkBanner.Evaluate(At(tip), Build, Phoenix, tip));
    }

    [Theory]
    [InlineData(40_321)]
    [InlineData(40_320)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_build_carrying_the_fork_rules_shows_nothing_at_any_distance(long distance)
    {
        var tip = H - distance;
        Assert.Equal(ForkBannerState.None, ForkBanner.Evaluate(At(tip), Build, PhoenixAndPq, tip));
    }

    [Fact]
    public void Fork_name_is_compared_case_insensitively()
    {
        Assert.Equal(ForkBannerState.None, ForkBanner.Evaluate(At(H, name: "PQSIG"), Build, PhoenixAndPq, H));
        Assert.Equal(ForkBannerState.Stopped, ForkBanner.Evaluate(At(H, name: "PQSIG"), Build, Phoenix, H));
    }

    // ── minClientVersion ──

    [Fact]
    public void Equal_min_client_version_does_not_warn_far_out()
        => Assert.Equal(ForkBannerState.Notice, ForkBanner.Evaluate(At(H - 100_000, min: Build), Build, Phoenix, H - 100_000));

    [Fact]
    public void Null_min_client_version_still_renders_the_height_states()
    {
        Assert.Equal(ForkBannerState.Notice, ForkBanner.Evaluate(At(H - 100_000, min: null), Build, Phoenix, H - 100_000));
        Assert.Equal(ForkBannerState.Warning, ForkBanner.Evaluate(At(H - 10, min: null), Build, Phoenix, H - 10));
        Assert.Equal(ForkBannerState.Stopped, ForkBanner.Evaluate(At(H, min: null), Build, Phoenix, H));
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    [InlineData(40_321)]
    public void Newer_min_client_version_warns_at_any_distance(long distance)
    {
        var tip = H - distance;
        Assert.Equal(ForkBannerState.Warning, ForkBanner.Evaluate(At(tip, min: "2.1.0"), Build, Phoenix, tip));
        // Even a build that carries the rules is told when it is below the floor.
        Assert.Equal(ForkBannerState.Warning, ForkBanner.Evaluate(At(tip, min: "2.1.0"), Build, PhoenixAndPq, tip));
    }

    [Fact]
    public void Newer_min_client_version_past_the_height_is_still_Stopped_not_Warning()
        => Assert.Equal(ForkBannerState.Stopped, ForkBanner.Evaluate(At(H, min: "2.1.0"), Build, Phoenix, H));

    [Theory]
    [InlineData("2.1.0", "2.0.5", true)]
    [InlineData("v2.1.0", "2.0.5", true)]      // leading v tolerated
    [InlineData("2.0.5.1", "2.0.5", true)]     // a fourth component counts
    [InlineData("2.0.5", "2.0.5.0", false)]    // missing components are zero
    [InlineData("2.0.5", "2.0.5", false)]
    [InlineData("2.0.4", "2.0.5", false)]
    [InlineData("2.1.0", "2.1.0+abc123", false)] // commit suffix stripped → equal
    [InlineData("soon", "2.0.5", false)]       // garbled announcement never shouts
    [InlineData("2.1.0", "dev", false)]        // unparsable build never shouts either
    [InlineData(null, "2.0.5", false)]
    [InlineData("", "2.0.5", false)]
    public void Version_floor_comparison(string? min, string build, bool expected)
        => Assert.Equal(expected, ForkBanner.NeedsNewerClient(min, build));

    // ── Null fields ⇒ nothing ──

    [Fact]
    public void Null_status_or_unset_announcement_renders_nothing()
    {
        Assert.Equal(ForkBannerState.None, ForkBanner.Evaluate(null, Build, Phoenix, 1));
        Assert.Equal(ForkBannerState.None, ForkBanner.Evaluate(At(H, name: null), Build, Phoenix, H));
        Assert.Equal(ForkBannerState.None, ForkBanner.Evaluate(At(H, name: "  "), Build, Phoenix, H));
        Assert.Equal(ForkBannerState.None, ForkBanner.Evaluate(At(H, height: null), Build, Phoenix, H));
        // All three null — the shipped-first configuration — is exactly this.
        Assert.Equal(ForkBannerState.None, ForkBanner.Evaluate(At(H, name: null, height: null, min: null), Build, Phoenix, H));
        Assert.False(At(H, name: null, height: null, min: null).IsAnnounced);
        Assert.True(At(H).IsAnnounced);
    }

    // ── Days: blocks ÷ 2,880, rounded down ──

    [Theory]
    [InlineData(40_320, 14)]
    [InlineData(40_321, 14)]
    [InlineData(43_199, 14)]
    [InlineData(43_200, 15)]
    [InlineData(2_880, 1)]
    [InlineData(2_879, 0)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void Days_are_blocks_over_2880_rounded_down(long distance, long expectedDays)
        => Assert.Equal(expectedDays, ForkBanner.DaysUntil(H, H - distance));

    [Fact]
    public void Days_phrase()
    {
        Assert.Equal("less than a day", ForkBanner.DaysPhrase(H, H - 2_879));
        Assert.Equal("about 1 day", ForkBanner.DaysPhrase(H, H - 2_880));
        Assert.Equal("about 14 days", ForkBanner.DaysPhrase(H, H - 40_320));
    }

    // ── Wording (§7.1 verbatim) ──

    [Fact]
    public void Update_text_matches_the_spec()
    {
        var n = H.ToString("N0");
        Assert.Equal($"Update required before block {n}", ForkBanner.UpdateHeadline(H));
        Assert.Equal(
            "(about 14 days). Your wallet (v2.0.5) will stop following the chain at that block. Get v2.1.0 from",
            ForkBanner.UpdateBody(At(H - 40_320, min: "2.1.0"), Build, H - 40_320));
        Assert.EndsWith("Get the latest version from", ForkBanner.UpdateBody(At(H - 40_320, min: null), Build, H - 40_320));
        Assert.Equal("blazecoin.co.uk/Wallet", ForkBanner.DownloadHost);
        Assert.Equal("https://blazecoin.co.uk/Wallet", ForkBanner.DownloadUrl);
    }

    [Fact]
    public void Stopped_text_matches_the_spec()
    {
        var n = H.ToString("N0");
        var m = (H - 1).ToString("N0");
        Assert.Equal($"This wallet stopped at block {n}.", ForkBanner.StoppedHeadline(H));
        Assert.Equal(
            $"Balance last verified at block {m}. Funds are safe; install v2.1.0 and restore from your recovery phrase.",
            ForkBanner.StoppedBody(At(H + 3, min: "2.1.0"), H - 1));
    }

    [Fact]
    public void Last_verified_height_is_capped_below_the_fork_height()
    {
        Assert.Equal(H - 1, ForkBanner.LastVerifiedHeight(H, null));      // no header sync → the cap
        Assert.Equal(H - 10, ForkBanner.LastVerifiedHeight(H, H - 10));   // a verified tip below it stands
        Assert.Equal(H - 1, ForkBanner.LastVerifiedHeight(H, H + 5));     // nothing past H is verified
        Assert.Equal(H - 1, ForkBanner.LastVerifiedHeight(H, 0));         // "unknown" tip → the cap
        Assert.Equal(0, ForkBanner.LastVerifiedHeight(0, null));
    }

    // ── This build ──

    [Fact]
    public void This_build_carries_phoenix413_and_the_pq_fork()
    {
        Assert.True(LiteClientInfo.SupportedForks.Contains("phoenix413"));
        Assert.True(LiteClientInfo.SupportedForks.Contains("PHOENIX413")); // case-insensitive set
        // 2.0.5 ships the lite P2PQH rules (PqInputVerifier, BQ receive/send), so it claims pqsig;
        // the activation constant is the fork height chosen 2026-09-15.
        Assert.True(LiteClientInfo.SupportedForks.Contains("pqsig"));
        Assert.Equal(4_250_000L, BlazecoinChain.PqSigActivationHeight);
    }

    [Fact]
    public void Version_is_never_blank_and_the_head_can_set_it()
    {
        Assert.False(string.IsNullOrWhiteSpace(LiteClientInfo.Version));
        LiteClientInfo.SetVersion("  "); // ignored
        Assert.False(string.IsNullOrWhiteSpace(LiteClientInfo.Version));
        LiteClientInfo.SetVersion(" 2.0.5 ");
        Assert.Equal("2.0.5", LiteClientInfo.Version);
    }

    [Fact]
    public void Informational_version_is_read_without_a_commit_suffix()
    {
        // Lite.Core itself: whatever the SDK stamped, the accessor yields a parsable x.y.z.
        var v = LiteClientInfo.FromAssembly(typeof(LiteClientInfo).Assembly);
        Assert.DoesNotContain("+", v);
        Assert.True(Version.TryParse(v, out _), v);
        // And any assembly at all yields something non-blank.
        Assert.False(string.IsNullOrWhiteSpace(LiteClientInfo.FromAssembly(Assembly.GetExecutingAssembly())));
    }
}

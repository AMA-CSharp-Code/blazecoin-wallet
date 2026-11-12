using System.Net;
using System.Text;
using BlazecoinWallet.Lite.Fork;
using NBitcoin;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// A gateway's fork announcement is an unauthenticated claim from a party the wallet does not
/// trust (bug-bounty report 2026-11-06). These pin what it can and cannot do:
/// it cannot stop sends unless the Blazecoin project signed it; it cannot put its own text in
/// the wallet's alert; it cannot invent a fork at a block this build already follows the chain
/// past; and on a head that verifies headers, its claimed chain height decides nothing.
/// </summary>
public class ForkAnnouncementTests
{
    private const long Checkpoint = 4_373_000;          // at or below HeaderChainSync.MainnetCheckpoint
    private static readonly Key ProjectKey = new();
    private static readonly IReadOnlyList<string> Pinned = [ProjectKey.PubKey.ToHex()];

    private sealed class ScriptedGateway : HttpMessageHandler
    {
        public string Body = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }

    private static string Status(string? name, long? height, string? minVersion, long tip, string? signature = null)
    {
        static string Q(string? s) => System.Text.Json.JsonSerializer.Serialize(s);
        return "{\"daemonReachable\":true,\"daemonHeight\":" + tip + ",\"indexedHeight\":" + tip +
               ",\"forkName\":" + Q(name) + ",\"forkHeight\":" + (height?.ToString() ?? "null") +
               ",\"minClientVersion\":" + Q(minVersion) + ",\"forkSignature\":" + Q(signature) + "}";
    }

    private static async Task<GatewayForkStatusService> ReadAsync(string body, long? verifiedTip = null,
        IReadOnlyList<string>? keys = null)
    {
        var svc = new GatewayForkStatusService(
            new HttpClient(new ScriptedGateway { Body = body }) { BaseAddress = new Uri("https://gw.test/") },
            verifiedTip: () => verifiedTip, announcementKeys: keys);
        await svc.RefreshAsync();
        return svc;
    }

    // ── the report's scenario ──

    [Fact]
    public async Task A_fabricated_fork_status_cannot_stop_sends()
    {
        // The exact response from the report: an unknown fork, a claimed tip past its height.
        var svc = await ReadAsync(Status("roguefork2099", 4_500_000, "9.9.9", tip: 5_000_000));

        Assert.NotEqual(ForkBannerState.Stopped, svc.State);     // was Stopped: Review + Confirm disabled
        Assert.Equal(ForkBannerState.Warning, svc.State);        // still told, as the gateway's word
        Assert.False(svc.Current!.Authenticated);
    }

    [Fact]
    public async Task A_fabricated_fork_status_cannot_stop_sends_even_with_keys_pinned()
    {
        var svc = await ReadAsync(Status("roguefork2099", 4_500_000, "9.9.9", tip: 5_000_000, signature: "3006020101020101"),
            keys: Pinned);

        Assert.NotEqual(ForkBannerState.Stopped, svc.State);
        Assert.False(svc.Current!.Authenticated);
    }

    [Theory]
    [InlineData("9.9 from wallet-update.example (not")]      // an instruction written by the gateway
    [InlineData("2.0.0 — enter your recovery phrase at wallet-update.example")]
    [InlineData("<b>9.9.9</b>")]
    [InlineData("9.9.9\nline two")]
    [InlineData("1.2.3.4.5")]
    [InlineData("999999.1")]
    public async Task The_banner_never_carries_text_the_gateway_wrote(string minClientVersion)
    {
        var svc = await ReadAsync(Status("roguefork2099", 4_500_000, minClientVersion, tip: 5_000_000));

        var status = svc.Current!;
        Assert.Null(status.MinClientVersion);                                   // dropped, not displayed
        Assert.Equal("the latest version", ForkBanner.TargetVersionPhrase(status.MinClientVersion));
        var everything = ForkBanner.UpdateBody(status, "1.3.3", svc.EffectiveTip) + " " +
                         ForkBanner.StoppedBody(status, 1) + " " + ForkBanner.UpdateHeadline(status.ForkHeight!.Value);
        Assert.DoesNotContain("example", everything);
        Assert.DoesNotContain("recovery phrase at", everything);
        // Even handed the raw text directly, the wording refuses it.
        Assert.Equal("the latest version", ForkBanner.TargetVersionPhrase(minClientVersion));
    }

    [Theory]
    [InlineData("pqsig v2 — see wallet-update.example")]
    [InlineData("a b")]
    [InlineData("<script>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]                  // 33 characters
    public async Task A_fork_name_that_is_not_a_plain_token_announces_nothing(string name)
    {
        var svc = await ReadAsync(Status(name, 4_500_000, "9.9.9", tip: 5_000_000));

        Assert.False(svc.Current!.IsAnnounced);
        Assert.Equal(ForkBannerState.None, svc.State);
    }

    // ── a fork cannot be announced in the build's own past ──

    [Theory]
    [InlineData(1)]
    [InlineData(4_000_000)]
    [InlineData(Checkpoint)]
    public async Task A_fork_this_build_does_not_follow_cannot_sit_at_or_below_its_shipped_checkpoint(long height)
    {
        Assert.True(Checkpoint <= ForkAnnouncement.ShippedCheckpointHeight);
        // Judged at the wallet's own verified tip, which is past such a height by definition —
        // the reason judging at the verified tip is not enough by itself.
        var svc = await ReadAsync(Status("roguefork2099", height, "9.9.9", tip: 5_000_000),
            verifiedTip: ForkAnnouncement.ShippedCheckpointHeight + 10);

        Assert.False(svc.Current!.IsAnnounced);
        Assert.Equal(ForkBannerState.None, svc.State);
    }

    [Fact]
    public async Task A_fork_this_build_follows_may_be_announced_anywhere_and_shows_nothing()
    {
        var svc = await ReadAsync(Status("pqsig", 4_250_000, null, tip: 4_400_000));

        Assert.True(svc.Current!.IsAnnounced);
        Assert.Equal(ForkBannerState.None, svc.State);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(null)]
    public void A_missing_or_non_positive_height_is_never_plausible(long? height)
        => Assert.False(ForkAnnouncement.IsPlausibleHeight(height, "pqsig", LiteClientInfo.SupportedForks, Checkpoint));

    // ── the claimed tip decides nothing where the wallet has its own ──

    [Fact]
    public async Task A_claimed_tip_cannot_carry_a_wallet_with_a_verified_tip_past_the_fork_height()
    {
        var sig = ForkAnnouncement.Sign(ProjectKey, "nextfork", 4_500_000, "9.9.9");
        // A genuine, signed announcement replayed early with an inflated tip.
        var svc = await ReadAsync(Status("nextfork", 4_500_000, "9.9.9", tip: 5_000_000, signature: sig),
            verifiedTip: 4_400_000, keys: Pinned);

        Assert.True(svc.Current!.Authenticated);
        Assert.Equal(4_400_000, svc.EffectiveTip);
        Assert.Equal(ForkBannerState.Warning, svc.State);        // not there yet by the wallet's own count
        Assert.Equal(5_000_000, svc.Current.Tip);                // the claim is kept, as a claim
    }

    [Fact]
    public async Task Without_a_verified_tip_the_gateways_height_is_used()
    {
        var svc = await ReadAsync(Status("nextfork", 4_500_000, null, tip: 4_450_000));
        Assert.Equal(4_450_000, svc.EffectiveTip);
    }

    // ── a signed announcement still does what §7.1 designed ──

    [Fact]
    public async Task A_signed_announcement_past_its_height_stops_a_build_without_the_rules()
    {
        var sig = ForkAnnouncement.Sign(ProjectKey, "nextfork", 4_500_000, "9.9.9");

        var web = await ReadAsync(Status("nextfork", 4_500_000, "9.9.9", tip: 4_500_001, signature: sig), keys: Pinned);
        Assert.True(web.Current!.Authenticated);
        Assert.Equal(ForkBannerState.Stopped, web.State);

        var android = await ReadAsync(Status("nextfork", 4_500_000, "9.9.9", tip: 4_500_001, signature: sig),
            verifiedTip: 4_500_000, keys: Pinned);
        Assert.Equal(ForkBannerState.Stopped, android.State);
    }

    [Fact]
    public async Task A_signature_covers_the_name_the_height_and_the_version()
    {
        var sig = ForkAnnouncement.Sign(ProjectKey, "nextfork", 4_500_000, "2.1.0");

        async Task<bool> Authentic(string name, long height, string? version) =>
            (await ReadAsync(Status(name, height, version, tip: 4_400_000, signature: sig), keys: Pinned)).Current!.Authenticated;

        Assert.True(await Authentic("nextfork", 4_500_000, "2.1.0"));
        Assert.True(await Authentic("NextFork", 4_500_000, "v2.1.0"));    // canonical form: case and a leading v
        Assert.False(await Authentic("otherfork", 4_500_000, "2.1.0"));
        Assert.False(await Authentic("nextfork", 4_400_001, "2.1.0"));    // moved earlier → not what was signed
        Assert.False(await Authentic("nextfork", 4_500_000, "9.9.9"));
        Assert.False(await Authentic("nextfork", 4_500_000, null));
    }

    [Fact]
    public void A_signature_is_refused_from_an_unknown_key_or_when_malformed()
    {
        var good = ForkAnnouncement.Sign(ProjectKey, "nextfork", 4_500_000, "2.1.0");
        var stranger = ForkAnnouncement.Sign(new Key(), "nextfork", 4_500_000, "2.1.0");

        Assert.True(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", good, Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", stranger, Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", good, []));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", null, Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", "", Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", "not hex", Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", "deadbeef", Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", new string('a', 400), Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0", good, ["not a key"]));
        Assert.False(ForkAnnouncement.IsSigned(null, 4_500_000, "2.1.0", good, Pinned));
        Assert.False(ForkAnnouncement.IsSigned("nextfork", null, "2.1.0", good, Pinned));
    }

    [Fact]
    public void Signing_refuses_values_the_wallet_would_drop()
    {
        Assert.Throws<ArgumentException>(() => ForkAnnouncement.Sign(ProjectKey, "two words", 4_500_000, null));
        Assert.Throws<ArgumentException>(() => ForkAnnouncement.Sign(ProjectKey, "nextfork", 4_500_000, "soon"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ForkAnnouncement.Sign(ProjectKey, "nextfork", 0, null));
    }

    // ── the pure decision ──

    [Fact]
    public void Past_the_height_without_the_rules_an_unsigned_announcement_warns_and_a_signed_one_stops()
    {
        var forks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "phoenix413" };
        var unsigned = new ForkStatus("pqsig", 5_000_000, "2.1.0", 5_000_001, DateTime.UnixEpoch);
        var signed = unsigned with { Authenticated = true };

        Assert.Equal(ForkBannerState.Warning, ForkBanner.Evaluate(unsigned, "2.0.5", forks, 5_000_001));
        Assert.Equal(ForkBannerState.Stopped, ForkBanner.Evaluate(signed, "2.0.5", forks, 5_000_001));
    }

    // ── field shapes ──

    [Theory]
    [InlineData("2.1.0", "2.1.0")]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData(" 2 ", "2")]
    [InlineData("1.3.3.1", "1.3.3.1")]
    [InlineData("soon", null)]
    [InlineData("2.1.0-beta", null)]
    [InlineData("2,1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Version_is_digits_and_dots_or_nothing(string? text, string? expected)
        => Assert.Equal(expected, ForkAnnouncement.CleanVersion(text));

    [Theory]
    [InlineData("pqsig", "pqsig")]
    [InlineData(" phoenix413 ", "phoenix413")]
    [InlineData("next-fork_2", "next-fork_2")]
    [InlineData("two words", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Name_is_a_plain_token_or_nothing(string? text, string? expected)
        => Assert.Equal(expected, ForkAnnouncement.CleanName(text));

    // ── the keys this build ships ──

    [Fact]
    public void This_build_pins_two_distinct_announcement_keys()
    {
        // A primary and a spare (2026-11-12). Each must be a real compressed secp256k1 point:
        // IsSigned swallows a malformed key, so a typo here would silently pin nothing.
        Assert.Equal(2, ForkAnnouncement.PinnedKeys.Count);
        Assert.Equal(2, ForkAnnouncement.PinnedKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var hex in ForkAnnouncement.PinnedKeys)
        {
            Assert.Equal(66, hex.Length);
            Assert.Equal(hex, hex.ToLowerInvariant());
            var key = new PubKey(hex);
            Assert.True(key.IsCompressed);
            Assert.Equal(hex, key.ToHex());
        }
    }

    [Fact]
    public async Task With_the_shipped_keys_a_stranger_s_signature_still_cannot_stop_sends()
    {
        // No key list is passed: the service uses the keys pinned in the build. A well-formed
        // signature by any other key is worth exactly as much as no signature.
        var sig = ForkAnnouncement.Sign(new Key(), "nextfork", ForkAnnouncement.ShippedCheckpointHeight + 100_000, "9.9.9");
        var svc = await ReadAsync(Status("nextfork", ForkAnnouncement.ShippedCheckpointHeight + 100_000, "9.9.9",
            tip: ForkAnnouncement.ShippedCheckpointHeight + 200_000, signature: sig));

        Assert.Equal(ForkBannerState.Warning, svc.State);
        Assert.False(svc.Current!.Authenticated);
    }

    [Fact]
    public void Either_of_two_pinned_keys_authenticates_an_announcement()
    {
        // The spare must work on its own: that is the whole reason it is pinned.
        var first = new Key();
        var spare = new Key();
        IReadOnlyList<string> both = [first.PubKey.ToHex(), spare.PubKey.ToHex()];

        Assert.True(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0",
            ForkAnnouncement.Sign(first, "nextfork", 4_500_000, "2.1.0"), both));
        Assert.True(ForkAnnouncement.IsSigned("nextfork", 4_500_000, "2.1.0",
            ForkAnnouncement.Sign(spare, "nextfork", 4_500_000, "2.1.0"), both));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Bug-bounty round sixteen (2026-11-18), the lite wallet's two findings.
/// <list type="bullet">
/// <item>Personal-node mode reported every address with a transaction count of 0, and restore
/// discovery reads that count: a phrase restored against your own node found nothing past the
/// first receive address.</item>
/// <item>The guessing limit on the app-lock PIN was a flat 30-second wait held only in memory:
/// no growth, and a restart of the app started the count again.</item>
/// </list>
/// </summary>
public class BountyRound16LiteTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>A daemon whose chainstate holds one coin on each of the given addresses.</summary>
    private sealed class FundedAddressesRpc(params string[] funded) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            var result = "null";
            if (body.GetProperty("method").GetString() == "scantxoutset")
            {
                var descriptor = body.GetProperty("params")[1][0].GetString()!;      // addr(<address>)
                var index = Array.FindIndex(funded, a => descriptor == $"addr({a})");
                var unspents = index < 0 ? "" :
                    $"{{\"txid\":\"{new string((char)('a' + index), 64)}\",\"vout\":0,\"amount\":0.50000000,\"height\":90}}";
                result = $"{{\"success\":true,\"height\":100,\"unspents\":[{unspents}]}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"result\":{result},\"error\":null}}", Encoding.UTF8, "application/json") };
        }
    }

    private static PersonalNodeDataService Node(params string[] funded) => new(
        new HttpClient(new FundedAddressesRpc(funded)),
        new PersonalNodeOptions("http://127.0.0.1:55413/", RpcUser: "u", RpcPassword: "p"),
        NullLogger<PersonalNodeDataService>.Instance);

    // ════════════════════════ personal-node restore ════════════════════════

    [Fact]
    public async Task A_funded_address_reports_activity_on_a_personal_node()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var address = wallet.GetReceiveAddress(3);

        var summary = await Node(address).GetAddressAsync(address);

        Assert.NotNull(summary);
        Assert.Equal(50_000_000, summary!.Balance);
        Assert.True(summary.TxCount > 0);       // was 0: "never used" to restore discovery
    }

    [Fact]
    public async Task A_restore_on_a_personal_node_finds_coins_past_the_first_address()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        // Coins on receive slots 0 and 3 and on change slot 1, as a wallet in ordinary use has.
        var node = Node(wallet.GetReceiveAddress(0), wallet.GetReceiveAddress(3), wallet.GetChangeAddress(1));
        var rotation = new AddressRotation(node, new InMemoryWalletStateStore());

        await rotation.DiscoverFromChainAsync(wallet);

        Assert.Equal(4, rotation.RevealedCount);    // was 1: slot 3's coin invisible and unspendable
        Assert.Equal(2, rotation.ChangeCount);      // was 0: the change coin too
    }

    // ════════════════════════ guessing limit ════════════════════════

    private static void MissUntilTripped(AttemptLimiter limiter)
    {
        for (var i = 1; i < AttemptLimiter.MaxMisses; i++) Assert.False(limiter.Miss());
        Assert.True(limiter.Miss());
    }

    [Fact]
    public void Each_wait_earned_in_a_row_is_longer_than_the_last_up_to_an_hour()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new AttemptLimiter { Clock = () => now };

        var waits = new List<TimeSpan>();
        for (var trip = 0; trip < 7; trip++)
        {
            MissUntilTripped(limiter);
            var wait = limiter.Wait!.Value;
            waits.Add(wait);
            now += wait;                         // serve it exactly
            Assert.Null(limiter.Wait);
        }

        Assert.Equal(
            new[] { 30, 60, 300, 900, 3600, 3600, 3600 }.Select(s => TimeSpan.FromSeconds(s)),
            waits);                              // was 30 s every time
    }

    [Fact]
    public void Trying_every_four_digit_pin_takes_months_not_hours()
    {
        // 10,000 PINs at five per wait = 2,000 waits. Flat 30 s: 16 h 40 min. Growing: 83 days.
        var total = Enumerable.Range(1, 10_000 / AttemptLimiter.MaxMisses)
            .Aggregate(TimeSpan.Zero, (sum, trip) => sum + AttemptLimiter.LockoutFor(trip));

        Assert.True(total > TimeSpan.FromDays(80), total.ToString());
    }

    [Fact]
    public void The_count_and_the_wait_survive_a_restart_where_the_head_stores_them()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var stored = new InMemoryAttemptLimiterState();            // stands for the head's durable store

        var before = new AttemptLimiter(stored) { Clock = () => now };
        for (var i = 0; i < 4; i++) before.Miss();                 // four misses, then the app is closed

        var after = new AttemptLimiter(stored) { Clock = () => now };
        Assert.True(after.Miss());                                 // the fifth trips: the four were remembered
        Assert.Equal(AttemptLimiter.Lockout, after.Wait);

        var again = new AttemptLimiter(stored) { Clock = () => now };
        Assert.NotNull(again.Wait);                                // and a restart does not end the wait

        now += AttemptLimiter.Lockout;
        MissUntilTripped(again);
        Assert.Equal(TimeSpan.FromMinutes(1), new AttemptLimiter(stored) { Clock = () => now }.Wait);   // nor the growth
    }

    [Fact]
    public void A_right_answer_starts_the_count_and_the_growth_over()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new AttemptLimiter { Clock = () => now };
        MissUntilTripped(limiter); now += limiter.Wait!.Value;
        MissUntilTripped(limiter); now += limiter.Wait!.Value;     // the second wait was a minute

        limiter.Reset();

        MissUntilTripped(limiter);
        Assert.Equal(AttemptLimiter.Lockout, limiter.Wait);
        Assert.Equal(AttemptLimiter.TrippedMessage, limiter.TripMessage);
    }

    [Fact]
    public void A_clock_set_back_cannot_stretch_a_wait_past_the_longest_one()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new AttemptLimiter { Clock = () => now };
        MissUntilTripped(limiter);

        now -= TimeSpan.FromDays(30);

        Assert.Equal(TimeSpan.FromHours(1), limiter.Wait);
    }

    [Fact]
    public void Later_trips_say_how_long_they_last()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new AttemptLimiter { Clock = () => now };
        var said = new List<string>();
        for (var trip = 0; trip < 5; trip++)
        {
            MissUntilTripped(limiter);
            said.Add(limiter.TripMessage);
            now += limiter.Wait!.Value;
        }

        Assert.Equal(new[]
        {
            "Too many attempts — locked for 30 seconds.",
            "Too many attempts — locked for 1 minute.",
            "Too many attempts — locked for 5 minutes.",
            "Too many attempts — locked for 15 minutes.",
            "Too many attempts — locked for 1 hour.",
        }, said);
        Assert.Equal("Too many attempts — wait 31s.", AttemptLimiter.WaitMessage(TimeSpan.FromSeconds(30)));
        Assert.Equal("Too many attempts — wait 5 min.", AttemptLimiter.WaitMessage(TimeSpan.FromMinutes(5)));
    }
}

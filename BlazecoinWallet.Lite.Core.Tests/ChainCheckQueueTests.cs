using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// <see cref="ChainCheckQueue"/> — the one budget every read-path chain check runs under
/// (bug-bounty report 2026-11-06): each claim is checked once, a few inside the read and the
/// rest in the background; a failure backs off and is retried; a spend can force a check now.
/// </summary>
public class ChainCheckQueueTests
{
    private static ChainCheckQueue.Item Ok(string key, long value, Action? onRun = null, long priority = 0) =>
        new(key, _ => { onRun?.Invoke(); return Task.FromResult(new ChainCheckQueue.Outcome(true, value, null)); }, priority);

    private static ChainCheckQueue.Item Fails(string key, Action? onRun = null) =>
        new(key, _ => { onRun?.Invoke(); return Task.FromResult(new ChainCheckQueue.Outcome(false, 0, "no proof")); });

    private static ChainCheckQueue Queue(Func<DateTime>? clock = null)
    {
        var q = new ChainCheckQueue { Pace = TimeSpan.Zero };
        if (clock != null) q.Clock = clock;
        return q;
    }

    [Fact]
    public async Task A_claim_is_pending_until_checked_then_verified_with_the_chains_value()
    {
        using var q = Queue();
        Assert.Equal(ChainCheckState.Pending, q.StateOf("a"));

        await q.EnsureAsync([Ok("a", 42)], inlineLimit: 5, TimeSpan.FromSeconds(5));

        Assert.Equal(ChainCheckState.Verified, q.StateOf("a"));
        Assert.True(q.TryGetVerified("a", out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task A_verified_claim_is_never_checked_again()
    {
        using var q = Queue();
        var runs = 0;
        for (var i = 0; i < 3; i++)
            await q.EnsureAsync([Ok("a", 1, () => runs++)], 5, TimeSpan.FromSeconds(5));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_failed_claim_is_unverified_and_retried_only_after_its_back_off()
    {
        var now = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        using var q = Queue(() => now);
        var runs = 0;
        var item = Fails("a", () => runs++);

        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));
        Assert.Equal(ChainCheckState.Unverified, q.StateOf("a"));
        Assert.Equal("no proof", q.LastError("a"));

        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));
        Assert.Equal(1, runs);                               // inside 10 s

        now += TimeSpan.FromSeconds(10);
        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));
        Assert.Equal(2, runs);

        now += TimeSpan.FromSeconds(19);
        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));
        Assert.Equal(2, runs);                               // the second back-off is 20 s
        now += TimeSpan.FromSeconds(1);
        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));
        Assert.Equal(3, runs);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(6, 320)]
    [InlineData(7, 600)]       // capped at ten minutes
    [InlineData(50, 600)]
    public void Back_off_doubles_from_ten_seconds_to_ten_minutes(int attempts, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), ChainCheckQueue.BackOff(attempts));

    [Fact]
    public async Task A_failed_claim_that_later_checks_out_becomes_verified()
    {
        var now = DateTime.UnixEpoch;
        using var q = Queue(() => now);
        var available = false;
        var item = new ChainCheckQueue.Item("a",
            _ => Task.FromResult(new ChainCheckQueue.Outcome(available, 7, available ? null : "not yet")));

        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));
        available = true;
        now += TimeSpan.FromSeconds(10);
        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));

        Assert.Equal(ChainCheckState.Verified, q.StateOf("a"));
        Assert.Null(q.LastError("a"));
    }

    [Fact]
    public async Task Checking_now_ignores_the_back_off_and_returns_the_outcome()
    {
        var now = DateTime.UnixEpoch;
        using var q = Queue(() => now);
        var available = false;
        var item = new ChainCheckQueue.Item("a",
            _ => Task.FromResult(new ChainCheckQueue.Outcome(available, 9, available ? null : "not yet")));
        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));     // fails; backing off
        available = true;

        var outcome = await q.CheckNowAsync(item);

        Assert.True(outcome.Ok);
        Assert.Equal(9, outcome.Value);
        Assert.Equal(ChainCheckState.Verified, q.StateOf("a"));
    }

    [Fact]
    public async Task Only_so_many_checks_run_inside_the_read_and_the_rest_follow_in_the_background()
    {
        using var q = Queue();
        var changed = 0;
        q.Changed += () => Interlocked.Increment(ref changed);
        var items = Enumerable.Range(0, 10).Select(i => Ok($"k{i}", i, priority: i)).ToList();

        await q.EnsureAsync(items, inlineLimit: 3, TimeSpan.FromSeconds(5));

        // The three highest priorities were done before the read returned.
        Assert.Equal(ChainCheckState.Verified, q.StateOf("k9"));
        Assert.Equal(ChainCheckState.Verified, q.StateOf("k8"));
        Assert.Equal(ChainCheckState.Verified, q.StateOf("k7"));

        await q.WhenIdleAsync();
        Assert.All(items, i => Assert.Equal(ChainCheckState.Verified, q.StateOf(i.Key)));
        Assert.True(changed >= 1);
    }

    [Fact]
    public async Task A_slow_check_does_not_hold_the_read_past_its_budget_and_still_lands()
    {
        using var q = Queue();
        var release = new TaskCompletionSource();
        var slow = new ChainCheckQueue.Item("slow", async _ =>
        {
            await release.Task;
            return new ChainCheckQueue.Outcome(true, 5, null);
        });

        var started = DateTime.UtcNow;
        await q.EnsureAsync([slow], 5, TimeSpan.FromMilliseconds(100));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(ChainCheckState.Pending, q.StateOf("slow"));

        release.SetResult();
        await q.WhenIdleAsync();
        Assert.Equal(ChainCheckState.Verified, q.StateOf("slow"));
    }

    [Fact]
    public async Task One_claim_asked_for_twice_at_once_is_checked_once()
    {
        using var q = Queue();
        var runs = 0;
        var release = new TaskCompletionSource();
        var item = new ChainCheckQueue.Item("a", async _ =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return new ChainCheckQueue.Outcome(true, 1, null);
        });

        var first = q.CheckNowAsync(item);
        var second = q.CheckNowAsync(item);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_check_that_throws_is_a_failure_not_a_crash()
    {
        using var q = Queue();
        var item = new ChainCheckQueue.Item("a", _ => throw new InvalidOperationException("boom"));

        await q.EnsureAsync([item], 5, TimeSpan.FromSeconds(5));

        Assert.Equal(ChainCheckState.Unverified, q.StateOf("a"));
        Assert.Equal("boom", q.LastError("a"));
    }

    [Fact]
    public async Task Clearing_forgets_results_and_drops_queued_work()
    {
        using var q = Queue();
        await q.EnsureAsync([Ok("a", 1), Fails("b")], 5, TimeSpan.FromSeconds(5));

        q.Clear();

        Assert.Equal(ChainCheckState.Pending, q.StateOf("a"));
        Assert.Equal(ChainCheckState.Pending, q.StateOf("b"));
        Assert.False(q.TryGetVerified("a", out _));
        await q.WhenIdleAsync();
    }

    [Fact]
    public async Task A_seeded_result_counts_without_a_check()
    {
        using var q = Queue();
        q.Seed("a", 11);
        var runs = 0;

        await q.EnsureAsync([Ok("a", 99, () => runs++)], 5, TimeSpan.FromSeconds(5));

        Assert.Equal(0, runs);
        Assert.True(q.TryGetVerified("a", out var value));
        Assert.Equal(11, value);
        Assert.Single(q.Verified("a"));
    }
}

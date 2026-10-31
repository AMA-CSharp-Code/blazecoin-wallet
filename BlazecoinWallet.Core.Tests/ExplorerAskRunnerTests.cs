using BlazecoinWallet.Core.Services.Provenance.Explorer;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Pacing an Explorer Lineage ask (2026-10-31): the Indexer allows 120 public reads a minute
/// per client and answers 429 to the rest. Pins: a gap of at least MinInterval between
/// requests (and none after the last), the address cap with the largest holdings first, a 429
/// waited out for the explorer's Retry-After (or the default) and the SAME address asked
/// again, the give-up after MaxRetries in a row, null answers skipped, progress reported with
/// the wait, and cancellation.
/// </summary>
public class ExplorerAskRunnerTests
{
    private static readonly ExplorerAskOptions Fast = new(TimeSpan.FromMilliseconds(600), 1_000, TimeSpan.FromSeconds(60), MaxRetries: 3);

    /// <summary>Records every wait instead of sleeping.</summary>
    private sealed class Waits
    {
        public readonly List<TimeSpan> Recorded = new();
        public Task Delay(TimeSpan t, CancellationToken ct) { Recorded.Add(t); return Task.CompletedTask; }
    }

    private static Task<string?> Echo(string a, CancellationToken _) => Task.FromResult<string?>(a + "!");

    [Fact]
    public async Task Requests_are_spaced_by_the_interval_with_no_gap_after_the_last()
    {
        var waits = new Waits();
        var r = await ExplorerAskRunner.RunAsync(new[] { "a", "b", "c" }, Echo, Fast, delay: waits.Delay);

        Assert.Equal(new[] { "a!", "b!", "c!" }, r.Answers);
        Assert.Equal(3, r.Asked);
        Assert.Equal(0, r.LeftOut);
        Assert.Equal(2, waits.Recorded.Count);                                    // between a–b and b–c only
        Assert.All(waits.Recorded, w => Assert.InRange(w, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public async Task The_cap_asks_the_first_addresses_only_and_counts_the_rest()
    {
        var asked = new List<string>();
        var r = await ExplorerAskRunner.RunAsync(new[] { "a", "b", "c", "d" },
            (a, _) => { asked.Add(a); return Task.FromResult<string?>(a); },
            Fast with { MaxAddresses = 2 }, delay: new Waits().Delay);

        Assert.Equal(new[] { "a", "b" }, asked);
        Assert.Equal(2, r.Asked);
        Assert.Equal(2, r.LeftOut);
    }

    [Fact]
    public void Addresses_are_ordered_largest_holding_first_so_the_cap_drops_dust()
    {
        var order = ExplorerAskRunner.AddressesByHolding(new (string?, long)[]
        {
            ("dust", 5), ("big", 1_000_000), ("mid", 400), ("mid", 300), (null, 999), ("", 999), ("big", 1),
        });
        Assert.Equal(new[] { "big", "mid", "dust" }, order);
    }

    [Fact]
    public async Task A_429_is_waited_out_for_the_explorers_time_and_the_same_address_asked_again()
    {
        var waits = new Waits();
        var calls = new List<string>();
        var progress = new List<ExplorerAskProgress>();
        var failedOnce = false;

        var r = await ExplorerAskRunner.RunAsync(new[] { "a", "b" }, (a, _) =>
        {
            calls.Add(a);
            if (a == "b" && !failedOnce)
            {
                failedOnce = true;
                throw new ExplorerUnavailableException("limited", 429) { RetryAfter = TimeSpan.FromSeconds(17) };
            }
            return Task.FromResult<string?>(a);
        }, Fast, p => { progress.Add(p); return Task.CompletedTask; }, waits.Delay);

        Assert.Equal(new[] { "a", "b", "b" }, calls);
        Assert.Equal(new[] { "a", "b" }, r.Answers);
        Assert.Contains(TimeSpan.FromSeconds(17), waits.Recorded);
        var waiting = Assert.Single(progress, p => p.Waiting is not null);
        Assert.Equal((1, 2, TimeSpan.FromSeconds(17)), (waiting.Done, waiting.Total, waiting.Waiting!.Value));
        Assert.Equal((2, 2), (progress[^1].Done, progress[^1].Total));
    }

    [Fact]
    public async Task A_429_without_a_time_waits_the_default()
    {
        var waits = new Waits();
        var n = 0;
        await ExplorerAskRunner.RunAsync(new[] { "a" }, (_, _) =>
            ++n == 1 ? throw new ExplorerUnavailableException("limited", 429) : Task.FromResult<string?>("ok"),
            Fast, delay: waits.Delay);
        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, waits.Recorded);
    }

    [Fact]
    public async Task Too_many_429s_in_a_row_give_up_with_the_explorers_message()
    {
        var waits = new Waits();
        var ex = await Assert.ThrowsAsync<ExplorerUnavailableException>(() =>
            ExplorerAskRunner.RunAsync<string>(new[] { "a" },
                (_, _) => throw new ExplorerUnavailableException("limited", 429),
                Fast with { MaxRetries = 2 }, delay: waits.Delay));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(2, waits.Recorded.Count);                                    // two waits, the third 429 is thrown
    }

    [Fact]
    public async Task Other_failures_are_not_retried()
    {
        var waits = new Waits();
        await Assert.ThrowsAsync<ExplorerUnavailableException>(() =>
            ExplorerAskRunner.RunAsync<string>(new[] { "a", "b" },
                (_, _) => throw new ExplorerUnavailableException("down", 503), Fast, delay: waits.Delay));
        Assert.Empty(waits.Recorded);
    }

    [Fact]
    public async Task Unknown_addresses_are_skipped_but_still_count_as_asked()
    {
        var r = await ExplorerAskRunner.RunAsync(new[] { "a", "b", "c" },
            (a, _) => Task.FromResult<string?>(a == "b" ? null : a), Fast, delay: new Waits().Delay);
        Assert.Equal(new[] { "a", "c" }, r.Answers);
        Assert.Equal(3, r.Asked);
    }

    [Fact]
    public async Task Cancellation_stops_the_ask()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExplorerAskRunner.RunAsync(new[] { "a", "b", "c" }, (a, _) =>
            {
                if (++calls == 2) cts.Cancel();
                return Task.FromResult<string?>(a);
            }, Fast, delay: new Waits().Delay, ct: cts.Token));
        Assert.Equal(2, calls);
    }
}

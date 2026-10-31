using System.Diagnostics;

namespace BlazecoinWallet.Core.Services.Provenance.Explorer;

/// <summary>
/// How an ask of the explorer is paced (2026-10-31). The Indexer allows 120 public reads a
/// minute per client address and answers 429 to the 121st with no queue, so a wallet that
/// asks for hundreds of addresses as fast as the wire allows loses the whole ask on request
/// 121. <see cref="MinInterval"/> keeps a well-behaved ask under the limit (600 ms start to
/// start = 100/min); <see cref="MaxAddresses"/> bounds one ask to about ten minutes; a 429
/// that still arrives (three windows on one computer, or someone else's limit) is waited
/// out — <c>Retry-After</c> when the explorer sends it, <see cref="DefaultRetryWait"/>
/// otherwise — and the ask resumes from the address it stopped at.
/// </summary>
public sealed record ExplorerAskOptions(TimeSpan MinInterval, int MaxAddresses, TimeSpan DefaultRetryWait, int MaxRetries)
{
    public static readonly ExplorerAskOptions Default = new(TimeSpan.FromMilliseconds(600), 1_000, TimeSpan.FromSeconds(60), 3);
}

/// <summary>Where an ask has got to; <see cref="Waiting"/> is set while it sits out a 429.</summary>
public sealed record ExplorerAskProgress(int Done, int Total, TimeSpan? Waiting);

/// <summary>The answers that came back, how many addresses were asked, and how many the cap left out.</summary>
public sealed record ExplorerAskResult<T>(IReadOnlyList<T> Answers, int Asked, int LeftOut);

/// <summary>Runs one GET per address against the explorer at a pace it accepts.</summary>
public static class ExplorerAskRunner
{
    /// <summary>
    /// The distinct addresses that hold coins, largest holding first — so when the cap bites
    /// it is the dust that is left out, never the large holdings.
    /// </summary>
    public static IReadOnlyList<string> AddressesByHolding(IEnumerable<(string? Address, long Satoshis)> unspent) =>
        unspent.Where(u => !string.IsNullOrWhiteSpace(u.Address))
            .GroupBy(u => u.Address!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(u => u.Satoshis))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .ToList();

    /// <summary>
    /// Asks <paramref name="ask"/> for the first <see cref="ExplorerAskOptions.MaxAddresses"/>
    /// of <paramref name="addresses"/> in order, at least <see cref="ExplorerAskOptions.MinInterval"/>
    /// apart. A null answer (the explorer has never seen the address) is skipped. A 429 is
    /// waited out and the same address asked again, up to <see cref="ExplorerAskOptions.MaxRetries"/>
    /// times in a row; the one after that is thrown to the caller like any other failure.
    /// <paramref name="delay"/> is how the runner waits (tests pass a recorder; the page passes nothing = <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).
    /// </summary>
    public static async Task<ExplorerAskResult<T>> RunAsync<T>(
        IReadOnlyList<string> addresses,
        Func<string, CancellationToken, Task<T?>> ask,
        ExplorerAskOptions options,
        Func<ExplorerAskProgress, Task>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default) where T : class
    {
        delay ??= Task.Delay;
        var asked = Math.Min(addresses.Count, options.MaxAddresses);
        var answers = new List<T>(asked);
        var retries = 0;
        var i = 0;
        while (i < asked)
        {
            ct.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            T? answer;
            try
            {
                answer = await ask(addresses[i], ct);
            }
            catch (ExplorerUnavailableException ex) when (ex.StatusCode == 429 && retries < options.MaxRetries)
            {
                retries++;
                var wait = ex.RetryAfter ?? options.DefaultRetryWait;
                if (progress is not null) await progress(new ExplorerAskProgress(i, asked, wait));
                await delay(wait, ct);
                continue;   // the same address again
            }

            retries = 0;
            if (answer is not null) answers.Add(answer);
            i++;
            if (progress is not null) await progress(new ExplorerAskProgress(i, asked, null));

            if (i < asked)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed < options.MinInterval) await delay(options.MinInterval - elapsed, ct);
            }
        }
        return new ExplorerAskResult<T>(answers, asked, addresses.Count - asked);
    }
}

using System.Collections.Concurrent;

namespace BlazecoinWallet.Lite;

/// <summary>Where one thing the gateway claimed stands against the chain.</summary>
public enum ChainCheckState
{
    /// <summary>Not yet looked at, or being looked at now.</summary>
    Pending,
    /// <summary>Proven: the claim matches what the chain commits to.</summary>
    Verified,
    /// <summary>Looked at and NOT proven — the proof was missing, did not fit, or contradicted
    /// the claim. Retried later; never counted meanwhile.</summary>
    Unverified,
}

/// <summary>
/// Runs the wallet's read-path chain checks (bug-bounty report 2026-11-06: balance, the payment
/// toast and history trusted gateway rows as served; verification ran only when spending).
/// One queue for every kind of check so they share ONE budget:
/// <list type="bullet">
/// <item><b>Each claim is checked once</b> and the result kept; a check in flight is shared.</item>
/// <item><b>A few checks run inline</b>, inside the read that needs them, within a time budget —
/// an ordinary wallet's coins are proven before its balance is first shown.</item>
/// <item><b>The rest run in the background at a fixed pace</b>, so a wallet with hundreds of
/// coins never spends the gateway's per-client read allowance (each check is two requests)
/// and starves its own balance reads.</item>
/// <item><b>A failed check backs off</b> (10 s doubling to 10 min) and is tried again — a proof
/// that was not available yet, or a gateway that was lying a minute ago, recovers on its own;
/// a fabricated claim simply never verifies.</item>
/// </list>
/// Nothing here decides what a result MEANS; callers count only <see cref="ChainCheckState.Verified"/>.
/// </summary>
internal sealed class ChainCheckQueue : IDisposable
{
    /// <summary>One check: a stable key for the claim, and how to test it. The check returns the
    /// value the chain commits to (the caller compares or adopts it).</summary>
    public sealed record Item(string Key, Func<CancellationToken, Task<Outcome>> Check, long Priority = 0);

    /// <param name="Ok">The claim was proven.</param>
    /// <param name="Value">What the chain says (e.g. the output's true value in satoshis).</param>
    public readonly record struct Outcome(bool Ok, long Value, string? Error);

    private sealed record Failure(int Attempts, DateTime NextAttemptUtc, string? Error);

    private readonly ConcurrentDictionary<string, long> _verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Failure> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<Outcome>> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Item> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _workerGate = new();
    private CancellationTokenSource _lifetime = new();
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;   // guarded by _workerGate, so "queue empty" and "worker gone" are one decision

    /// <summary>The time source for back-off (tests move it forward).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The gap between background checks. A check is two requests on the web wallet (raw
    /// transaction + proof) and about four where headers are verified too (linking the block to
    /// an anchor), so one check every two seconds stays at or under half of a gateway's default
    /// per-client read allowance (240 a minute) and leaves the rest for ordinary reads.</summary>
    public TimeSpan Pace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many checks may run at once inside a read.</summary>
    public int InlineConcurrency { get; set; } = 3;

    /// <summary>Raised (on a worker thread) when background checks have changed some results.</summary>
    public event Action? Changed;

    public bool TryGetVerified(string key, out long value) => _verified.TryGetValue(key, out value);

    public ChainCheckState StateOf(string key) =>
        _verified.ContainsKey(key) ? ChainCheckState.Verified
        : _failed.ContainsKey(key) ? ChainCheckState.Unverified
        : ChainCheckState.Pending;

    /// <summary>Why the last check of <paramref name="key"/> failed, when it did.</summary>
    public string? LastError(string key) => _failed.TryGetValue(key, out var f) ? f.Error : null;

    /// <summary>Adopts a result proven in an earlier session (persisted by the caller).</summary>
    public void Seed(string key, long value) => _verified[key] = value;

    /// <summary>Every proven result whose key starts with <paramref name="prefix"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, long>> Verified(string prefix) =>
        _verified.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Makes sure every item is being dealt with: up to <paramref name="inlineLimit"/> of the
    /// ones due (highest priority first) are checked now, waiting at most
    /// <paramref name="inlineBudget"/>; the rest join the paced background queue. Returns when
    /// the inline part is done or the budget is spent — whichever is first.
    /// </summary>
    public async Task EnsureAsync(IReadOnlyCollection<Item> items, int inlineLimit, TimeSpan inlineBudget, CancellationToken ct = default)
    {
        var now = Clock();
        var due = items
            .Where(i => !_verified.ContainsKey(i.Key))
            .Where(i => !_failed.TryGetValue(i.Key, out var f) || f.NextAttemptUtc <= now)
            .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(i => i.Priority)
            .ToList();
        if (due.Count == 0) return;

        var inline = due.Where(i => !_waiting.ContainsKey(i.Key)).Take(Math.Max(0, inlineLimit)).ToList();
        foreach (var later in due.Except(inline))
            _waiting.TryAdd(later.Key, later);
        if (!_waiting.IsEmpty) EnsureWorker();
        if (inline.Count == 0) return;

        // Not disposed: checks that outlive the budget still release it.
        var gate = new SemaphoreSlim(Math.Max(1, InlineConcurrency));
        var lifetime = _lifetime.Token;
        var batch = Task.WhenAll(inline.Select(async item =>
        {
            await gate.WaitAsync(lifetime);
            try { await RunAsync(item, lifetime); }
            finally { gate.Release(); }
        }));

        // The budget bounds how long the READ waits, not the checks: a slow proof finishes in
        // the background and is there for the next read.
        var finished = await Task.WhenAny(batch, Task.Delay(inlineBudget, ct));
        if (finished == batch) await IgnoreAsync(batch);
        else _ = IgnoreAsync(batch);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Checks one claim NOW, whatever its back-off says, and waits for the answer —
    /// for a spend, where the user is waiting on exactly these coins.</summary>
    public async Task<Outcome> CheckNowAsync(Item item, CancellationToken ct = default)
    {
        if (_verified.TryGetValue(item.Key, out var known)) return new Outcome(true, known, null);
        _waiting.TryRemove(item.Key, out _);
        var run = RunAsync(item, _lifetime.Token);
        var finished = await Task.WhenAny(run, Task.Delay(Timeout.Infinite, ct));
        if (finished != run) ct.ThrowIfCancellationRequested();
        return await run;
    }

    /// <summary>Completes when no check is running or waiting (for tests, and for a caller that
    /// wants everything settled).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task worker;
            lock (_workerGate) worker = _worker;
            await IgnoreAsync(worker);
            var running = _running.Values.ToArray();
            if (running.Length > 0) { await IgnoreAsync(Task.WhenAll(running)); continue; }
            lock (_workerGate)
                if (!_workerRunning && _waiting.IsEmpty && _running.IsEmpty) return;
            await Task.Yield();
        }
    }

    /// <summary>Forgets everything (wallet reset / lock): results, failures, and queued work.</summary>
    public void Clear()
    {
        var old = Interlocked.Exchange(ref _lifetime, new CancellationTokenSource());
        try { old.Cancel(); } catch { /* disposed already */ }
        old.Dispose();
        _waiting.Clear();
        _verified.Clear();
        _failed.Clear();
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { /* disposed already */ }
        _lifetime.Dispose();
    }

    private Task<Outcome> RunAsync(Item item, CancellationToken lifetime) =>
        _running.GetOrAdd(item.Key, _ => ExecuteAsync(item, lifetime));

    private async Task<Outcome> ExecuteAsync(Item item, CancellationToken lifetime)
    {
        await Task.Yield();   // never run a check on the caller's stack inside GetOrAdd
        Outcome outcome;
        try
        {
            outcome = await item.Check(lifetime);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            _running.TryRemove(item.Key, out _);
            return new Outcome(false, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            outcome = new Outcome(false, 0, ex.Message);
        }

        if (!lifetime.IsCancellationRequested)
        {
            if (outcome.Ok)
            {
                _verified[item.Key] = outcome.Value;
                _failed.TryRemove(item.Key, out _);
            }
            else
            {
                var attempts = (_failed.TryGetValue(item.Key, out var prior) ? prior.Attempts : 0) + 1;
                _failed[item.Key] = new Failure(attempts, Clock() + BackOff(attempts), outcome.Error);
            }
        }
        _running.TryRemove(item.Key, out _);
        return outcome;
    }

    /// <summary>10 s, 20 s, 40 s … capped at 10 minutes.</summary>
    internal static TimeSpan BackOff(int attempts)
    {
        var seconds = 10d * Math.Pow(2, Math.Min(Math.Max(attempts, 1) - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, 600));
    }

    private void EnsureWorker()
    {
        lock (_workerGate)
        {
            if (_workerRunning) return;
            _workerRunning = true;
            var lifetime = _lifetime.Token;
            _worker = Task.Run(() => DrainAsync(lifetime));
        }
    }

    private async Task DrainAsync(CancellationToken lifetime)
    {
        var changed = false;
        var stoodDown = false;
        try
        {
            while (true)
            {
                Item? next;
                lock (_workerGate)
                {
                    next = lifetime.IsCancellationRequested
                        ? null
                        : _waiting.Values.OrderByDescending(i => i.Priority).FirstOrDefault();
                    // Standing down is decided under the same lock EnsureWorker takes, so an item
                    // queued a moment later always finds no worker and starts one.
                    if (next == null) { _workerRunning = false; stoodDown = true; break; }
                    _waiting.TryRemove(next.Key, out _);
                }
                if (_verified.ContainsKey(next.Key)) continue;

                await RunAsync(next, lifetime);
                changed = true;
                if (Pace > TimeSpan.Zero) await Task.Delay(Pace, lifetime);
            }
        }
        catch (OperationCanceledException) { /* Clear() / Dispose() */ }
        catch { /* nothing may kill the loop silently mid-flight; the next read restarts it */ }
        finally
        {
            if (!stoodDown)
                lock (_workerGate) _workerRunning = false;
            if (changed && !lifetime.IsCancellationRequested)
            {
                try { Changed?.Invoke(); }
                catch { /* a broken subscriber must not stop future checks */ }
            }
        }
    }

    private static async Task IgnoreAsync(Task task)
    {
        try { await task; }
        catch { /* each check records its own failure */ }
    }
}

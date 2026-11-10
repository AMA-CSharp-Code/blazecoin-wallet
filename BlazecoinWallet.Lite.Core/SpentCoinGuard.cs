using System.Collections.Concurrent;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite;

/// <summary>Where a coin the gateway lists stands on the one question its own proof cannot
/// answer: has it been spent since?</summary>
internal enum SpendStanding
{
    /// <summary>Not looked at yet, or a second gateway says it was spent and the wallet has not
    /// been able to check that yet. Not counted meanwhile.</summary>
    Unknown,
    /// <summary>Nobody has shown a spend: a second gateway was asked and named none, or there was
    /// nobody else to ask. Counted.</summary>
    Clear,
    /// <summary>PROVEN spent: a second gateway served the mined transaction that spent it.</summary>
    Spent,
}

/// <summary>
/// Keeps coins that have already been spent out of the balance (bug-bounty report 2026-11-06,
/// sixth round). The read path proves each coin was CREATED — its transaction hashes to its id,
/// pays this wallet, and is mined — and that proof stays true for ever, including after the coin
/// is spent. Whether it is still unspent was the listing gateway's word alone, so a gateway that
/// kept a spent coin in its answer had it counted as spendable and announced as a payment, with
/// every proof genuine.
/// <para>Nothing short of the whole chain proves a coin is unspent. A spend, though, is a mined
/// transaction, and that can be proven the way a coin is. So each proven coin is put to a
/// gateway OTHER than the one that listed it (<see cref="ISpendCrossCheck"/>), and a spend that
/// gateway can show — checked by <see cref="LiteTxVerifier.JudgeSpendAsync"/>, never believed —
/// takes the coin out. One honest gateway among those the wallet uses is enough.</para>
/// <list type="bullet">
/// <item><b>A coin is looked at before it first counts</b> (inside the read, within a time
/// budget) and again every <see cref="RecheckInterval"/> in the background — being unspent is
/// not a fact that keeps.</item>
/// <item><b>Only proof removes a coin.</b> A second gateway that merely leaves a coin out, or
/// claims a spend it cannot show, removes nothing: after <see cref="MaxUnprovenClaims"/> tries
/// the claim is set aside and the coin counts (and keeps being asked about).</item>
/// <item><b>A claim nobody could judge decides nothing</b> (audit 2026-11-09). Evidence that was
/// not fetched this round, or whose block could not be placed for want of headers, is neither a
/// proof nor one of those tries: the coin stays out of the balance (or keeps the standing it had)
/// and is asked about again, ahead of the coins that have had their turn.</item>
/// <item><b>No second opinion is not a refusal.</b> With one gateway configured, or the others
/// down, the coin counts as before — that case is no worse than it was, and is the stated limit.</item>
/// </list>
/// </summary>
internal sealed class SpentCoinGuard
{
    /// <param name="EvidenceUtc">When evidence of a spend of this coin was last fetched and looked at
    /// (whatever came of it); <see cref="DateTime.MinValue"/> = never.</param>
    private sealed record Entry(SpendStanding Standing, DateTime DueUtc, int UnprovenClaims, DateTime EvidenceUtc = default);

    private readonly ISpendCrossCheck _crossCheck;
    private readonly LiteTxVerifier _verifier;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _watched = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _oneRound = new(1, 1);
    private CancellationTokenSource _lifetime = new();
    private int _refreshing;

    public SpentCoinGuard(ISpendCrossCheck crossCheck, LiteTxVerifier verifier)
    {
        _crossCheck = crossCheck;
        _verifier = verifier;
    }

    /// <summary>The time source (tests move it forward).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>How long a "no spend shown" answer is relied on before the coin is asked about again.</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How soon to ask again when the last round settled nothing: nobody answered, or a
    /// spend was claimed but could not be checked.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many rounds a claimed-but-unproven spend keeps a coin from counting. A real
    /// spend's evidence arrives on a retry; a claim that never comes with evidence is just a claim.</summary>
    public int MaxUnprovenClaims { get; set; } = 4;

    /// <summary>How long a coin this wallet itself spent is watched closely if it turns up again.</summary>
    public TimeSpan WatchOwnSpendFor { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Raised (on a worker thread) when a round the caller did not wait for changed a coin's standing.</summary>
    public event Action? Changed;

    public static string Key(string txId, int vout) => $"{txId}:{vout}";

    public SpendStanding StandingOf(string txId, int vout) =>
        _entries.TryGetValue(Key(txId, vout), out var e) ? e.Standing : SpendStanding.Unknown;

    /// <summary>
    /// Makes sure every coin has been looked at: coins never looked at are checked now, waiting at
    /// most <paramref name="budget"/> (a round that outlives it finishes in the background and
    /// raises <see cref="Changed"/>); coins whose last answer has aged are refreshed in the
    /// background, one refresh at a time.
    /// </summary>
    public async Task EnsureAsync(IReadOnlyCollection<LiteOutpoint> coins, TimeSpan budget, CancellationToken ct = default)
    {
        if (coins.Count == 0) return;
        var now = Clock();
        var fresh = new List<LiteOutpoint>();
        var aged = new List<LiteOutpoint>();
        foreach (var coin in coins)
        {
            if (!_entries.TryGetValue(Key(coin.TxId, coin.Vout), out var e)) fresh.Add(coin);
            else if (e.DueUtc <= now) (e.Standing == SpendStanding.Unknown ? fresh : aged).Add(coin);
        }

        var lifetime = _lifetime.Token;
        if (fresh.Count > 0)
        {
            var waited = new Waited();
            var round = RunAsync(fresh, waited, lifetime);
            var finished = await Task.WhenAny(round, Task.Delay(budget, ct));
            if (finished == round) await IgnoreAsync(round);
            else { waited.GaveUp = true; _ = IgnoreAsync(round); }
            ct.ThrowIfCancellationRequested();
        }

        if (aged.Count > 0 && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try { await IgnoreAsync(RunAsync(aged, new Waited { GaveUp = true }, lifetime)); }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        }
    }

    /// <summary>Looks at one coin NOW, whatever its last answer's age, and waits — for a spend,
    /// where the user is waiting on exactly this coin.</summary>
    public async Task<SpendStanding> CheckNowAsync(LiteOutpoint coin, CancellationToken ct = default)
    {
        await CheckNowAsync([coin], ct);
        return StandingOf(coin.TxId, coin.Vout);
    }

    /// <summary>The same for several coins at once — one round, so one question per request's
    /// worth of coins instead of one each. Read each coin's answer with <see cref="StandingOf"/>.</summary>
    public async Task CheckNowAsync(IReadOnlyList<LiteOutpoint> coins, CancellationToken ct = default)
    {
        if (coins.Count == 0) return;
        var round = RunAsync(coins, new Waited(), _lifetime.Token, force: true);
        var finished = await Task.WhenAny(round, Task.Delay(Timeout.Infinite, ct));
        if (finished != round) ct.ThrowIfCancellationRequested();
        await IgnoreAsync(round);
    }

    /// <summary>This wallet has just spent the coin. What was known about it is dropped, so if a
    /// gateway lists it again it is looked at from scratch before it can count — and for a while
    /// it is asked about every <see cref="RetryInterval"/>, because a spend that was not mined yet
    /// when it reappeared is mined a block or two later.</summary>
    public void NoteOwnSpend(string txId, int vout)
    {
        var key = Key(txId, vout);
        _watched[key] = Clock() + WatchOwnSpendFor;
        _entries.TryRemove(key, out _);
    }

    /// <summary>Forgets everything (wallet lock / reset).</summary>
    public void Clear()
    {
        var old = Interlocked.Exchange(ref _lifetime, new CancellationTokenSource());
        try { old.Cancel(); } catch { /* disposed already */ }
        old.Dispose();
        _entries.Clear();
        _watched.Clear();
    }

    /// <summary>Completes when no round is running (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            await _oneRound.WaitAsync();
            _oneRound.Release();
            if (Volatile.Read(ref _refreshing) == 0 && _oneRound.CurrentCount == 1) return;
            await Task.Yield();
        }
    }

    private sealed class Waited { public volatile bool GaveUp; }

    /// <summary>One round over some coins. Rounds run one at a time, and each begins by dropping
    /// the coins an earlier round has just settled — so two reads asking about the same coins
    /// at once cost one set of requests.</summary>
    private async Task RunAsync(IReadOnlyList<LiteOutpoint> coins, Waited waited, CancellationToken lifetime, bool force = false)
    {
        var changed = false;
        await _oneRound.WaitAsync(lifetime);
        try
        {
            var now = Clock();
            var todo = coins
                .GroupBy(c => Key(c.TxId, c.Vout), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Where(c => force || !_entries.TryGetValue(Key(c.TxId, c.Vout), out var e) || e.DueUtc <= now)
                // One question fetches evidence for only so many claims, in the order asked: the
                // coins whose evidence has waited longest go first, so every claim gets its turn
                // and a coin already proven spent does not keep taking one (audit 2026-11-09).
                .OrderBy(c => _entries.TryGetValue(Key(c.TxId, c.Vout), out var e) ? e.EvidenceUtc : default)
                .ToList();

            // A coin with no known source came through a client with no failover list: there is
            // nobody else to ask about it.
            foreach (var coin in todo.Where(c => c.Source == null))
                changed |= Settle(coin, SpendStanding.Clear, now + RecheckInterval, now);

            foreach (var chunk in todo.Where(c => c.Source != null).Chunk(Math.Max(1, _crossCheck.MaxOutpointsPerCall)))
            {
                var claims = await _crossCheck.FindSpendsFromAnotherGatewayAsync(chunk, lifetime);
                now = Clock();
                if (claims == null)
                {
                    // No second opinion. A coin already judged keeps its standing; one never
                    // judged counts, as it did before there was anybody to ask.
                    foreach (var coin in chunk)
                        changed |= Settle(coin, StandingOrDefault(coin, SpendStanding.Clear), now + RetryInterval, now);
                    continue;
                }

                var asked = chunk.ToDictionary(c => Key(c.TxId, c.Vout), StringComparer.OrdinalIgnoreCase);
                var proven = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // evidence not fetched this round
                var uncheckable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // fetched, but its block could not be placed
                foreach (var claim in claims)
                {
                    var key = Key(claim.TxId, claim.Vout);
                    if (proven.Contains(key) || !asked.TryGetValue(key, out var coin)) continue;
                    // Evidence the cap left for a later round was not looked at: the claim is
                    // neither proven nor failed, and the coin is not "no spend named" either.
                    if (claim.EvidencePending) { waiting.Add(key); continue; }
                    // The headers that place the spender's block come, like the evidence, from a
                    // server other than the one that listed the coin — the party being doubted.
                    switch (await _verifier.JudgeSpendAsync(claim, [coin.Source!], lifetime))
                    {
                        case SpendProof.Proven: proven.Add(key); break;
                        case SpendProof.CannotCheck: uncheckable.Add(key); break;
                        default: failed.Add(key); break;
                    }
                }

                now = Clock();
                foreach (var coin in chunk)
                {
                    var key = Key(coin.TxId, coin.Vout);
                    if (proven.Contains(key))
                        changed |= Settle(coin, SpendStanding.Spent, now + RecheckInterval, now, evidenceSeen: true);
                    else if (failed.Contains(key))
                        changed |= SettleUnprovenClaim(coin, now);
                    else if (uncheckable.Contains(key))
                        changed |= SettleUnjudgedClaim(coin, now, evidenceSeen: true);
                    else if (waiting.Contains(key))
                        changed |= SettleUnjudgedClaim(coin, now, evidenceSeen: false);
                    else
                        changed |= Settle(coin, SpendStanding.Clear, now + RecheckInterval, now);
                }
            }
        }
        finally
        {
            _oneRound.Release();
            if (changed && waited.GaveUp && !lifetime.IsCancellationRequested)
            {
                try { Changed?.Invoke(); }
                catch { /* a broken subscriber must not stop future rounds */ }
            }
        }
    }

    private SpendStanding StandingOrDefault(LiteOutpoint coin, SpendStanding whenNew) =>
        _entries.TryGetValue(Key(coin.TxId, coin.Vout), out var e) && e.Standing != SpendStanding.Unknown ? e.Standing : whenNew;

    /// <summary>A second gateway says the coin was spent and the evidence did not check out (or did
    /// not arrive). A coin already judged keeps its standing. A new one waits — a real spend's
    /// evidence is there on a retry — but only for <see cref="MaxUnprovenClaims"/> rounds: after
    /// that the claim is worth what any unproven claim is worth, and the coin counts.</summary>
    private bool SettleUnprovenClaim(LiteOutpoint coin, DateTime now)
    {
        var key = Key(coin.TxId, coin.Vout);
        _entries.TryGetValue(key, out var prior);
        if (prior != null && prior.Standing != SpendStanding.Unknown)
            return Set(key, prior with { DueUtc = now + RetryInterval, EvidenceUtc = now });

        var claims = (prior?.UnprovenClaims ?? 0) + 1;
        return Set(key, new Entry(claims >= MaxUnprovenClaims ? SpendStanding.Clear : SpendStanding.Unknown, now + RetryInterval, claims, now));
    }

    /// <summary>A second gateway says the coin was spent and its evidence was NOT judged this round
    /// (audit 2026-11-09): it was not fetched (the per-question cap), or it was and its block could
    /// not be placed because the headers were not to be had. Nothing was learned, so nothing is
    /// decided: a coin already judged keeps its standing, a new one stays out of the balance, and —
    /// unlike evidence that was checked and failed — the round does not count towards
    /// <see cref="MaxUnprovenClaims"/>. Asked about again after <see cref="RetryInterval"/>.</summary>
    private bool SettleUnjudgedClaim(LiteOutpoint coin, DateTime now, bool evidenceSeen)
    {
        var key = Key(coin.TxId, coin.Vout);
        _entries.TryGetValue(key, out var prior);
        var seen = evidenceSeen ? now : prior?.EvidenceUtc ?? default;
        return prior != null && prior.Standing != SpendStanding.Unknown
            ? Set(key, prior with { DueUtc = now + RetryInterval, EvidenceUtc = seen })
            : Set(key, new Entry(SpendStanding.Unknown, now + RetryInterval, prior?.UnprovenClaims ?? 0, seen));
    }

    private bool Settle(LiteOutpoint coin, SpendStanding standing, DateTime due, DateTime now, bool evidenceSeen = false)
    {
        var key = Key(coin.TxId, coin.Vout);
        // A coin this wallet spent a little while ago is asked about often, whatever was answered.
        if (_watched.TryGetValue(key, out var until))
        {
            if (until > now) due = now + RetryInterval < due ? now + RetryInterval : due;
            else _watched.TryRemove(key, out _);
        }
        return Set(key, new Entry(standing, due, 0,
            evidenceSeen ? now : _entries.TryGetValue(key, out var prior) ? prior.EvidenceUtc : default));
    }

    private bool Set(string key, Entry entry)
    {
        var before = _entries.TryGetValue(key, out var prior) ? prior.Standing : (SpendStanding?)null;
        _entries[key] = entry;
        return before != entry.Standing;
    }

    private static async Task IgnoreAsync(Task task)
    {
        try { await task; }
        catch { /* a failed round leaves the coins as they were; the next read tries again */ }
    }
}

using System.Globalization;

namespace BlazecoinWallet.Core.Services.Provenance;

/// <summary>Limits that keep a trace bounded on a pathological graph.</summary>
public sealed class AncestryTracerOptions
{
    /// <summary>
    /// How far back a single path may walk before we give up on it. A pathology backstop
    /// only — transactions form a DAG (no cycles), so the walk always terminates and
    /// <see cref="MaxTransactionReads"/> is the real work budget. Raised from 100 on
    /// 2026-08-24: the payout wallet chains change→change on every 15-minute payout
    /// (~96 hops/day since the 2026-08-17 rebuild), so every freshly-paid-out coin sits
    /// hundreds of hops above its coinbases and a 100-hop cap traced it to NOTHING.
    /// </summary>
    public int MaxDepth { get; init; } = 100_000;

    /// <summary>
    /// Total transactions this tracer may read (shared across the whole run). Raised from
    /// 500k on 2026-09-30: the payout wallet's main coin sits on every block the pool ever
    /// mined (a read-only walk passed 200,000 coinbases and was still going), and a cold
    /// wallet consolidated from it inherits that lineage and more. Reads are cheap now that parents
    /// are fetched in batches; the cached transactions cost a few hundred bytes each.
    /// </summary>
    public int MaxTransactionReads { get; init; } = 1_000_000;

    /// <summary>
    /// Cap on FIFO segments held for one output. Heavy mixing fragments a sat-range list;
    /// past this the remainder is folded into untraced runs rather than growing without
    /// bound — early pedigree survives, later positions are honestly unproven. (Payout
    /// coins DO exceed this routinely since the 2026-08-17 wallet rebuild: their lineage
    /// blends 150-coinbase sweep batches over a long change-chain, so expect partial FIFO
    /// attribution there; the haircut model is unaffected by this cap.)
    /// </summary>
    public int MaxSegmentsPerOutput { get; init; } = 4_096;

    /// <summary>Reads between progress reports while inside one output's walk.</summary>
    public int ProgressEveryReads { get; init; } = 200;
}

/// <summary>
/// A contiguous run of satoshis inside one output, in position order — the FIFO
/// ("ordinal") view. <see cref="MintHeight"/> null means the run's origin isn't known
/// (an unreadable ancestor, a cutoff, or coinbase fee-sats), never a guess.
/// A struct: every hop of a change chain caches a list of up to
/// <see cref="AncestryTracerOptions.MaxSegmentsPerOutput"/> of these, and on the payout
/// wallet that list was the largest single share of a trace's memory (2026-09-30).
/// </summary>
public readonly record struct SatSegment(long Offset, long Length, long? MintHeight);

/// <summary>Progress ticks for the UI while a trace runs.</summary>
public sealed record TraceProgress(int OutputsDone, int OutputsTotal, int TransactionsRead);

/// <summary>Where a satoshi was minted.</summary>
public sealed record MintPoint(long Height, DateTime TimeUtc, MintVintage Vintage);

/// <summary>
/// One wallet output's ancestry under BOTH models. <see cref="ByVintage"/> is the
/// value-proportional haircut (fractions — what the website's engine computes, so the
/// two reconcile), folded per vintage bucket: a calendar month ("2026-06") for ordinary
/// blocks, the special's own key ("block-413") otherwise. <see cref="Segments"/> is the
/// FIFO sat-range pedigree, where every satoshi belongs to exactly ONE minting block and
/// its position inside the output is known — which is what makes exact vintage coin
/// control possible. Attribution below <see cref="Satoshis"/> means part of the lineage
/// couldn't be proven — never invented.
/// </summary>
public sealed record OutputProvenance(
    string TxId, int Vout, long Satoshis,
    IReadOnlyDictionary<string, long> ByVintage,
    IReadOnlyList<SatSegment> Segments)
{
    public long Attributed => ByVintage.Values.Sum();
    public bool FullyTraced => Attributed == Satoshis;

    /// <summary>Share of this output descending from one vintage bucket, 0–1 (haircut).</summary>
    public double PurityOf(string bucket) =>
        Satoshis <= 0 ? 0 : (double)ByVintage.GetValueOrDefault(bucket) / Satoshis;

    /// <summary>Satoshis per minting block under FIFO — segments folded by block.</summary>
    public IReadOnlyDictionary<long, long> FifoByHeight()
    {
        var map = new Dictionary<long, long>();
        foreach (var s in Segments)
            if (s.MintHeight is { } h) map[h] = map.GetValueOrDefault(h) + s.Length;
        return map;
    }

    public long FifoAttributed => Segments.Where(s => s.MintHeight.HasValue).Sum(s => s.Length);

    /// <summary>Share of this output minted by one block under FIFO, 0–1.</summary>
    public double FifoPurityOf(long height) =>
        Satoshis <= 0 ? 0 : (double)FifoByHeight().GetValueOrDefault(height) / Satoshis;

    /// <summary>
    /// The contiguous runs of this output that a given block minted — the exact
    /// offsets phase 4's coin control slices on.
    /// </summary>
    public IEnumerable<SatSegment> SegmentsOf(long height) =>
        Segments.Where(s => s.MintHeight == height);
}

/// <summary>One aggregated catalogue row (a year, or a special vintage).</summary>
public sealed record VintageRow(
    string Key, string Label, VintageKind Kind, long Satoshis, int OutputCount,
    long FirstHeight, DateTime FirstTimeUtc, DateTime LastTimeUtc)
{
    /// <summary>"May–Dec 2014", "Nov 2015", or a span when a bucket crosses years.</summary>
    public string MonthsLabel
    {
        get
        {
            var (a, b) = (FirstTimeUtc, LastTimeUtc);
            var ci = CultureInfo.InvariantCulture;
            if (a.Year == b.Year && a.Month == b.Month) return a.ToString("MMM yyyy", ci);
            if (a.Year == b.Year) return $"{a.ToString("MMM", ci)}–{b.ToString("MMM", ci)} {a.Year}";
            return $"{a.ToString("MMM yyyy", ci)}–{b.ToString("MMM yyyy", ci)}";
        }
    }
}

/// <summary>
/// One accounting model's catalogue. <see cref="Months"/> is the same year holdings
/// broken down per calendar month (key "yyyy-MM") — free from the block timestamps,
/// and the granularity a collector actually sends at.
/// </summary>
public sealed record VintageBreakdown(
    IReadOnlyList<VintageRow> Years,
    IReadOnlyList<VintageRow> Specials,
    long AttributedSatoshis,
    IReadOnlyList<VintageRow> Months)
{
    /// <summary>The months belonging to one year row, oldest first.</summary>
    public IEnumerable<VintageRow> MonthsOf(VintageRow year) =>
        Months.Where(m => m.Key.StartsWith(year.Key + "-", StringComparison.Ordinal));
}

/// <summary>
/// The finished trace, under both models. <see cref="Fifo"/> is the pedigree view
/// (every satoshi belongs to exactly one block); <see cref="Haircut"/> is the
/// proportional view the website pays claims on. They WILL show different per-vintage
/// numbers — always label which is on screen. <see cref="ReadBudgetReached"/> means the
/// walk stopped reading at <see cref="AncestryTracerOptions.MaxTransactionReads"/>, so
/// some lineage is missing rather than absent.
/// </summary>
public sealed record ProvenanceReport(
    IReadOnlyList<OutputProvenance> Outputs,
    IReadOnlyDictionary<long, MintPoint> Origins,
    VintageBreakdown Fifo,
    VintageBreakdown Haircut,
    long TotalSatoshis,
    int TransactionsRead,
    bool ReadBudgetReached = false)
{
    /// <summary>True when every satoshi traced back to a minting block under FIFO.</summary>
    public bool FullyTraced => Fifo.AttributedSatoshis == TotalSatoshis;
}

/// <summary>
/// Traces the mint ancestry of the wallet's own coins, entirely against the local
/// daemon (backward walk — the website's engine walks FORWARD only because it must
/// serve any address; a wallet needs just its own outputs).
///
/// Accounting is the value-proportional "haircut", identical to the site's engine so
/// the two reconcile: a spend blends its inputs' ancestry into its outputs in
/// proportion to value, with FLOOR division — so mixing dilutes ancestry and can never
/// create it. Whatever can't be read (a parent outside a node with no tx index, or a
/// depth/budget cutoff) simply contributes NOTHING: the result under-claims, never
/// invents. Coinbase outputs terminate a path — their whole value is minted by that
/// block, which is why a wallet full of untouched coinbases traces instantly and
/// exactly.
///
/// The haircut is kept PER VINTAGE BUCKET (month or special), not per block. Once a
/// block's sats enter a long change chain they never leave it (floor division only
/// drops shares below one satoshi), so a per-height vector on the payout wallet grew to
/// every block the pool ever mined — 100,000+ entries — cached at each of ~4,300 hops:
/// tens of gigabytes, and the wallet sat "stuck" in garbage collection (2026-09-30).
/// A bucketed vector has ~150 keys whatever the chain's history. The site's engine
/// buckets by year at the coinbase, so this is the closer match; the two agree to
/// within floor-rounding of a few satoshis per hop. Block counts per bucket come from
/// the coinbases actually reached. The FIFO model stays per block — its segment lists
/// are already capped per output.
///
/// Everything is memoised per output and per transaction, so shared ancestry is walked
/// once, and a transaction's parents are fetched in ONE batch before its inputs are
/// walked — a 600-input sweep is one request, not six hundred. Reuse one instance for a
/// session; construct a new one to drop the caches. Not thread-safe: run one trace at a
/// time (the RPC layer may parallelise a batch internally; the walk itself is serial).
/// </summary>
public sealed class AncestryTracer
{
    private readonly IAncestryRpc _rpc;
    private readonly AncestryTracerOptions _options;

    private readonly Dictionary<string, RawTransaction?> _txCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BlockRef?> _blockCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string TxId, int Vout), IReadOnlyDictionary<string, long>?> _outputCache = new();
    private readonly Dictionary<(string TxId, int Vout), IReadOnlyList<SatSegment>?> _segmentCache = new();
    private readonly Dictionary<long, MintPoint> _origins = new();
    private readonly Dictionary<string, BucketStats> _buckets = new(StringComparer.Ordinal);
    // One string instance per txid. A fetched transaction arrives with its own copy of its
    // id and a copy of every parent's; the child's input already holds the same 64 chars.
    private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private int _reads;
    private int _readsAtLastReport;

    // Per-run progress plumbing (set by TraceOutputsAsync, read by the fetch path).
    private IProgress<TraceProgress>? _progress;
    private int _outputsDone, _outputsTotal;

    /// <summary>Every coinbase reached in one bucket: the block count and span the catalogue shows.</summary>
    private sealed class BucketStats
    {
        public required MintVintage Vintage;
        public readonly HashSet<long> Heights = new();
        public long FirstHeight = long.MaxValue;
        public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;
    }

    public AncestryTracer(IAncestryRpc rpc, AncestryTracerOptions? options = null)
    {
        _rpc = rpc;
        _options = options ?? new AncestryTracerOptions();
    }

    /// <summary>
    /// The haircut bucket a minting block folds into: its calendar month for an ordinary
    /// block, the special vintage's own key otherwise.
    /// </summary>
    public static string BucketKey(MintPoint origin) =>
        origin.Vintage.Kind == VintageKind.Year
            ? origin.TimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : origin.Vintage.Key;

    /// <summary>Trace every unspent output the wallet currently holds.</summary>
    public async Task<ProvenanceReport> TraceUnspentAsync(
        IProgress<TraceProgress>? progress = null, CancellationToken ct = default)
    {
        var utxos = await _rpc.ListUnspentAsync(ct);
        return await TraceOutputsAsync(utxos, progress, ct);
    }

    /// <summary>Trace a specific set of outputs (used by tests and, later, coin control).</summary>
    public async Task<ProvenanceReport> TraceOutputsAsync(
        IReadOnlyList<UnspentOutput> outputs,
        IProgress<TraceProgress>? progress = null,
        CancellationToken ct = default)
    {
        _progress = progress;
        _outputsDone = 0;
        _outputsTotal = outputs.Count;

        var traced = new List<OutputProvenance>(outputs.Count);
        for (var i = 0; i < outputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var u = outputs[i];
            var vector = await ResolveOutputAsync(u.TxId, u.Vout, 0, ct)
                         ?? new Dictionary<string, long>();
            // Second model over the SAME cached transactions — no extra RPC.
            var segments = await ResolveSegmentsAsync(u.TxId, u.Vout, 0, ct)
                           ?? Untraced(u.Satoshis);
            traced.Add(new OutputProvenance(u.TxId, u.Vout, u.Satoshis, vector, segments));
            _outputsDone = i + 1;
            ReportProgress(force: true);
        }
        return BuildReport(traced);
    }

    private void ReportProgress(bool force = false)
    {
        if (_progress is null) return;
        if (!force && _reads - _readsAtLastReport < _options.ProgressEveryReads) return;
        _readsAtLastReport = _reads;
        _progress.Report(new TraceProgress(_outputsDone, _outputsTotal, _reads));
    }

    /// <summary>
    /// The satoshis of one output, split by vintage bucket. Null means "couldn't be
    /// determined" (unreadable transaction, or a depth/budget cutoff) — callers treat
    /// that as no ancestry, which dilutes rather than fabricates.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, long>?> ResolveOutputAsync(
        string txid, int vout, int depth, CancellationToken ct)
    {
        if (_outputCache.TryGetValue((txid, vout), out var cached)) return cached;
        if (depth > _options.MaxDepth || _reads >= _options.MaxTransactionReads) return null;

        // Warm caches make the awaits below complete synchronously, which turns this
        // recursion into plain native-stack recursion; on a deep payout change-chain
        // that can overflow the 1 MB thread stack. A real yield every 256 levels
        // unwinds the stack through the scheduler. (2026-08-24)
        if (depth != 0 && depth % 256 == 0) await Task.Yield();

        var tx = await GetTransactionAsync(txid, ct);
        var output = tx?.Outputs.FirstOrDefault(o => o.N == vout);
        if (tx is null || output is null)
        {
            _outputCache[(txid, vout)] = null;
            return null;
        }

        IReadOnlyDictionary<string, long>? vector;
        if (tx.IsCoinbase)
        {
            // Path terminates: this block minted the whole output.
            var origin = await ResolveOriginAsync(tx, ct);
            vector = origin is null
                ? null
                : new SingleVintage(BucketKey(origin), output.Satoshis);
        }
        else
        {
            vector = await BlendInputsAsync(tx, output, depth, ct);
        }

        _outputCache[(txid, vout)] = vector;
        return vector;
    }

    /// <summary>The haircut: combine the inputs' ancestry, then take this output's share.</summary>
    private async Task<IReadOnlyDictionary<string, long>> BlendInputsAsync(
        RawTransaction tx, RawTxOutput output, int depth, CancellationToken ct)
    {
        var combined = new Dictionary<string, long>(StringComparer.Ordinal);
        long knownInputValue = 0;

        // Every parent in one round trip before the walk touches any of them.
        await PrefetchParentsAsync(tx, ct);

        foreach (var input in tx.Inputs)
        {
            if (input.TxId is null) continue;   // coinbase marker on a mixed vin list
            ct.ThrowIfCancellationRequested();

            // Cached by the prefetch (or the recursion below), so this is a lookup.
            var parent = await GetTransactionAsync(input.TxId, ct);
            var parentOut = parent?.Outputs.FirstOrDefault(o => o.N == input.Vout);
            if (parentOut is not null) knownInputValue += parentOut.Satoshis;

            var parentVector = await ResolveOutputAsync(input.TxId, input.Vout, depth + 1, ct);
            if (parentVector is null) continue; // unreadable input: value without ancestry
            foreach (var (bucket, sats) in parentVector)
                combined[bucket] = combined.GetValueOrDefault(bucket) + sats;
        }

        // Total input value is the true denominator. When every input resolved we have it
        // exactly (fee included). When one didn't, fall back to the sum of outputs, which
        // is a floor on the input total — never smaller, so a share can't exceed what the
        // inputs actually carried.
        var sumOutputs = tx.Outputs.Sum(o => o.Satoshis);
        var denominator = Math.Max(sumOutputs, knownInputValue);
        var vector = new Dictionary<string, long>(StringComparer.Ordinal);
        if (denominator <= 0) return vector;

        foreach (var (bucket, sats) in combined)
        {
            // Int128: sats × value overflows long (both reach 2e17 on this chain).
            var share = (long)((Int128)sats * output.Satoshis / denominator);
            if (share > 0) vector[bucket] = share;
        }
        return vector;
    }

    // ── FIFO sat-ranges (the pedigree model) ──────────────────────────────────
    //
    // Ordinal-style bookkeeping: a coinbase mints a contiguous run of satoshis; a spend
    // lays its inputs' runs end to end IN INPUT ORDER and the outputs take from that
    // stream IN OUTPUT ORDER, so the fee (inputs − outputs) is exactly the TAIL that no
    // output claims. FIFO is a convention rather than physics, but it's the established
    // one, and unlike the haircut it gives every satoshi ONE block and a known position
    // inside its output — which is what allows sending an exact vintage later.
    //
    // Coinbase simplification (verified on this chain 2026-08-01): the whole coinbase
    // output is treated as minted by its block. Strict ordinal theory would carry the
    // fee-sats' older lineage through, but Blazecoin coinbases are EXACTLY the subsidy
    // (413.00000000 at 413, 51.62500000 at 4,160,000 — free relay, so no fees ride
    // along), and the website's engine makes the same assumption, so the two agree.

    private static IReadOnlyList<SatSegment> Untraced(long length) =>
        length <= 0 ? Array.Empty<SatSegment>() : new[] { new SatSegment(0, length, null) };

    /// <summary>
    /// The FIFO sat-ranges of one output. Null means the position stream couldn't be
    /// built (a parent's VALUE was unreadable, so every later offset would be wrong, or
    /// a cutoff) — callers fall back to a single untraced run.
    /// </summary>
    private async Task<IReadOnlyList<SatSegment>?> ResolveSegmentsAsync(
        string txid, int vout, int depth, CancellationToken ct)
    {
        if (_segmentCache.TryGetValue((txid, vout), out var cached)) return cached;
        if (depth > _options.MaxDepth || _reads >= _options.MaxTransactionReads) return null;

        // Same stack-unwind yield as ResolveOutputAsync — see the comment there.
        if (depth != 0 && depth % 256 == 0) await Task.Yield();

        var tx = await GetTransactionAsync(txid, ct);
        var output = tx?.Outputs.FirstOrDefault(o => o.N == vout);
        if (tx is null || output is null)
        {
            _segmentCache[(txid, vout)] = null;
            return null;
        }

        IReadOnlyList<SatSegment>? segments;
        if (tx.IsCoinbase)
        {
            var origin = await ResolveOriginAsync(tx, ct);
            segments = origin is null
                ? null
                : new[] { new SatSegment(0, output.Satoshis, origin.Height) };
        }
        else
        {
            segments = await SliceFromInputStreamAsync(tx, vout, depth, ct);
        }

        _segmentCache[(txid, vout)] = segments;
        return segments;
    }

    /// <summary>
    /// Builds the transaction's input stream and cuts this output's slice out of it.
    /// An input whose ancestry is unknown but whose VALUE is known still occupies its
    /// place as an untraced run — alignment survives. An input whose value can't be read
    /// breaks every offset after it, so the whole transaction goes untraced.
    /// Past <see cref="AncestryTracerOptions.MaxSegmentsPerOutput"/> the remainder is
    /// FOLDED into untraced runs (positions stay exact, early pedigree survives) — the
    /// pre-2026-08-24 code returned null here instead, which nulled the ENTIRE coin for
    /// heavily-blended payout ancestry and contradicted the option's own contract.
    /// </summary>
    private async Task<IReadOnlyList<SatSegment>?> SliceFromInputStreamAsync(
        RawTransaction tx, int vout, int depth, CancellationToken ct)
    {
        var stream = new List<SatSegment>();
        long streamLength = 0;

        await PrefetchParentsAsync(tx, ct);

        foreach (var input in tx.Inputs)
        {
            if (input.TxId is null) continue;
            ct.ThrowIfCancellationRequested();

            var parent = await GetTransactionAsync(input.TxId, ct);
            var parentOut = parent?.Outputs.FirstOrDefault(o => o.N == input.Vout);
            if (parentOut is null) return null;   // value unknown → offsets unknowable

            // At the cap, stop expanding parents entirely — each remaining input becomes
            // one untraced run (skipping the recursion also saves its reads). A parent
            // whose own segment list would blow the cap is folded the same way.
            var atCap = stream.Count >= _options.MaxSegmentsPerOutput;
            var parentSegments = atCap
                ? Untraced(parentOut.Satoshis)
                : await ResolveSegmentsAsync(input.TxId, input.Vout, depth + 1, ct)
                  ?? Untraced(parentOut.Satoshis);
            if (!atCap && stream.Count + parentSegments.Count > _options.MaxSegmentsPerOutput)
                parentSegments = Untraced(parentOut.Satoshis);

            foreach (var s in parentSegments)
            {
                var abs = s with { Offset = streamLength + s.Offset };
                // Adjacent untraced runs merge, so folded stretches cost one segment.
                if (abs.MintHeight is null && stream.Count > 0
                    && stream[^1] is { MintHeight: null } tail
                    && tail.Offset + tail.Length == abs.Offset)
                    stream[^1] = tail with { Length = tail.Length + abs.Length };
                else
                    stream.Add(abs);
            }
            streamLength += parentOut.Satoshis;
        }

        // Outputs consume the stream in order; whatever the outputs don't claim is the
        // fee, which is the tail — the miner's, not ours.
        long start = 0;
        foreach (var o in tx.Outputs.OrderBy(o => o.N))
        {
            if (o.N == vout) return CutRange(stream, start, o.Satoshis);
            start += o.Satoshis;
        }
        return null;
    }

    /// <summary>The runs covering [start, start+length) of a stream, re-based to 0.</summary>
    private static IReadOnlyList<SatSegment> CutRange(List<SatSegment> stream, long start, long length)
    {
        var slice = new List<SatSegment>();
        var end = start + length;
        foreach (var s in stream)
        {
            var segStart = s.Offset;
            var segEnd = s.Offset + s.Length;
            if (segEnd <= start || segStart >= end) continue;     // outside the window
            var from = Math.Max(segStart, start);
            var to = Math.Min(segEnd, end);
            slice.Add(new SatSegment(from - start, to - from, s.MintHeight));
        }

        // Anything the stream didn't cover (inputs shorter than the outputs claim — only
        // possible on malformed data) is left explicitly untraced rather than assumed.
        var covered = slice.Sum(s => s.Length);
        if (covered < length) slice.Add(new SatSegment(covered, length - covered, null));
        return slice;
    }

    /// <summary>Height + time of the block a coinbase was minted in, recorded for classification.</summary>
    private async Task<MintPoint?> ResolveOriginAsync(RawTransaction tx, CancellationToken ct)
    {
        var height = tx.BlockHeight;
        var time = tx.BlockTimeUtc;

        if ((height is null || time is null) && tx.BlockHash is not null)
        {
            var block = await GetBlockRefAsync(tx.BlockHash, ct);
            height ??= block?.Height;
            time ??= block?.TimeUtc;
        }
        if (height is null || time is null) return null;

        if (!_origins.TryGetValue(height.Value, out var origin))
        {
            origin = new MintPoint(height.Value, time.Value,
                VintageClassifier.Classify(height.Value, time.Value));
            _origins[height.Value] = origin;

            var key = BucketKey(origin);
            if (!_buckets.TryGetValue(key, out var stats))
                _buckets[key] = stats = new BucketStats { Vintage = origin.Vintage };
            stats.Heights.Add(origin.Height);
            stats.FirstHeight = Math.Min(stats.FirstHeight, origin.Height);
            if (origin.TimeUtc < stats.First) stats.First = origin.TimeUtc;
            if (origin.TimeUtc > stats.Last) stats.Last = origin.TimeUtc;
        }
        return origin;
    }

    /// <summary>
    /// Reads every parent of <paramref name="tx"/> the cache doesn't hold yet in one
    /// batched call, inside the read budget. Fan-out is where the reads are: a sweep
    /// pulls hundreds of coinbases into one transaction, and one round trip for all of
    /// them is what makes a six-figure trace take minutes rather than an hour.
    /// </summary>
    private async Task PrefetchParentsAsync(RawTransaction tx, CancellationToken ct)
    {
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in tx.Inputs)
        {
            if (input.TxId is null || _txCache.ContainsKey(input.TxId) || !seen.Add(input.TxId)) continue;
            if (_reads + wanted.Count >= _options.MaxTransactionReads) break;
            wanted.Add(input.TxId);
        }
        if (wanted.Count == 0) return;

        ct.ThrowIfCancellationRequested();
        var fetched = await _rpc.GetTransactionsAsync(wanted, ct);
        foreach (var id in wanted)
        {
            _reads++;
            _txCache[Intern(id)] = Slim(fetched.GetValueOrDefault(id));
        }
        ReportProgress();
    }

    private async Task<RawTransaction?> GetTransactionAsync(string txid, CancellationToken ct)
    {
        if (_txCache.TryGetValue(txid, out var cached)) return cached;
        if (_reads >= _options.MaxTransactionReads) return null;

        ct.ThrowIfCancellationRequested();
        _reads++;
        var tx = Slim(await _rpc.GetTransactionAsync(txid, ct));
        _txCache[Intern(txid)] = tx;
        ReportProgress();
        return tx;
    }

    private string Intern(string id)
    {
        if (_ids.TryGetValue(id, out var shared)) return shared;
        _ids[id] = id;
        return id;
    }

    /// <summary>
    /// What the walk keeps of a fetched transaction. Ids are shared with the pool, a
    /// block hash whose height and time are already known is dropped, and an empty
    /// input list becomes the shared empty array. Per cached transaction that is a few
    /// hundred bytes, which on a six-figure trace is a few hundred megabytes (2026-09-30).
    /// </summary>
    private RawTransaction? Slim(RawTransaction? tx)
    {
        if (tx is null) return null;
        var inputs = tx.Inputs.Count == 0
            ? Array.Empty<RawTxInput>()
            : tx.Inputs.Select(i => i.TxId is null ? i : new RawTxInput(Intern(i.TxId), i.Vout)).ToArray();
        var hash = tx.BlockHeight is not null && tx.BlockTimeUtc is not null ? null : tx.BlockHash;
        return new RawTransaction(Intern(tx.TxId), tx.IsCoinbase, hash, tx.BlockHeight, tx.BlockTimeUtc, inputs, tx.Outputs);
    }

    private async Task<BlockRef?> GetBlockRefAsync(string hash, CancellationToken ct)
    {
        if (_blockCache.TryGetValue(hash, out var cached)) return cached;
        var block = await _rpc.GetBlockRefAsync(hash, ct);
        _blockCache[hash] = block;
        return block;
    }

    private ProvenanceReport BuildReport(List<OutputProvenance> outputs) =>
        new(outputs,
            _origins.ToDictionary(kv => kv.Key, kv => kv.Value),
            BreakdownFifo(outputs),
            BreakdownHaircut(outputs),
            outputs.Sum(o => o.Satoshis),
            _reads,
            _reads >= _options.MaxTransactionReads);

    /// <summary>A catalogue bucket being accumulated: sats plus the blocks and span behind them.</summary>
    private sealed class RowAcc
    {
        public required MintVintage Vintage;
        public long Sats;
        public HashSet<long> Heights = new();
        public long FirstHeight = long.MaxValue;
        public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;

        public void Span(long height, DateTime time)
        {
            FirstHeight = Math.Min(FirstHeight, height);
            if (time < First) First = time;
            if (time > Last) Last = time;
        }
    }

    /// <summary>FIFO: per-block segments folded into their vintage buckets, blocks counted exactly.</summary>
    private VintageBreakdown BreakdownFifo(List<OutputProvenance> outputs)
    {
        var acc = new Dictionary<string, RowAcc>(StringComparer.Ordinal);
        long attributed = 0;
        foreach (var o in outputs)
        {
            foreach (var (height, sats) in o.FifoByHeight())
            {
                attributed += sats;
                if (!_origins.TryGetValue(height, out var origin)) continue;
                var key = BucketKey(origin);
                if (!acc.TryGetValue(key, out var row))
                    acc[key] = row = new RowAcc { Vintage = origin.Vintage };
                row.Sats += sats;
                row.Heights.Add(height);
                row.Span(height, origin.TimeUtc);
            }
        }
        return ToBreakdown(acc, attributed);
    }

    /// <summary>
    /// Haircut: vectors are already per bucket. The blocks and span behind a bucket are
    /// the coinbases the walk reached in it — every one of them is an ancestor of some
    /// traced output, so the count is the lineage's, not a guess.
    /// </summary>
    private VintageBreakdown BreakdownHaircut(List<OutputProvenance> outputs)
    {
        var acc = new Dictionary<string, RowAcc>(StringComparer.Ordinal);
        long attributed = 0;
        foreach (var o in outputs)
        {
            foreach (var (key, sats) in o.ByVintage)
            {
                attributed += sats;
                if (!_buckets.TryGetValue(key, out var stats)) continue;
                if (!acc.TryGetValue(key, out var row))
                {
                    acc[key] = row = new RowAcc
                    {
                        Vintage = stats.Vintage, Heights = stats.Heights,
                        FirstHeight = stats.FirstHeight, First = stats.First, Last = stats.Last
                    };
                }
                row.Sats += sats;
            }
        }
        return ToBreakdown(acc, attributed);
    }

    /// <summary>Month buckets roll up into their year rows; specials stand alone.</summary>
    private static VintageBreakdown ToBreakdown(Dictionary<string, RowAcc> acc, long attributed)
    {
        var months = new List<VintageRow>();
        var specials = new List<VintageRow>();
        var years = new Dictionary<string, RowAcc>(StringComparer.Ordinal);

        foreach (var (key, row) in acc)
        {
            if (row.Vintage.Kind != VintageKind.Year)
            {
                specials.Add(new VintageRow(key, row.Vintage.Label, row.Vintage.Kind, row.Sats,
                    row.Heights.Count, row.FirstHeight, row.First, row.Last));
                continue;
            }

            // "2026-06" sorts naturally and prefixes with the year's own key ("2026"),
            // which is how the UI pairs a month back to its year.
            months.Add(new VintageRow(key, row.First.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                VintageKind.Year, row.Sats, row.Heights.Count, row.FirstHeight, row.First, row.Last));

            if (!years.TryGetValue(row.Vintage.Key, out var year))
                years[row.Vintage.Key] = year = new RowAcc { Vintage = row.Vintage };
            year.Sats += row.Sats;
            year.Heights.UnionWith(row.Heights);
            year.Span(row.FirstHeight, row.First);
            year.Span(row.FirstHeight, row.Last);
        }

        return new VintageBreakdown(
            years.Select(kv => new VintageRow(kv.Key, kv.Value.Vintage.Label, VintageKind.Year, kv.Value.Sats,
                    kv.Value.Heights.Count, kv.Value.FirstHeight, kv.Value.First, kv.Value.Last))
                .OrderBy(r => r.FirstTimeUtc.Year).ToList(),
            specials.OrderBy(r => r.FirstHeight).ToList(),
            attributed,
            months.OrderBy(r => r.Key, StringComparer.Ordinal).ToList());
    }
}

/// <summary>
/// A one-bucket haircut vector — every coinbase output has one, and a trace holds
/// hundreds of thousands of them, so a full <see cref="Dictionary{TKey,TValue}"/> each
/// (three allocations, ~200 bytes) was a measurable slice of the trace's memory.
/// </summary>
internal sealed class SingleVintage : IReadOnlyDictionary<string, long>
{
    private readonly string _key;
    private readonly long _value;
    public SingleVintage(string key, long value) => (_key, _value) = (key, value);

    public long this[string key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key);
    public IEnumerable<string> Keys { get { yield return _key; } }
    public IEnumerable<long> Values { get { yield return _value; } }
    public int Count => 1;
    public bool ContainsKey(string key) => string.Equals(key, _key, StringComparison.Ordinal);
    public bool TryGetValue(string key, out long value)
    {
        value = ContainsKey(key) ? _value : 0;
        return ContainsKey(key);
    }
    public IEnumerator<KeyValuePair<string, long>> GetEnumerator()
    {
        yield return new KeyValuePair<string, long>(_key, _value);
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

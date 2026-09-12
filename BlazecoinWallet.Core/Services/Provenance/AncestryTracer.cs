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

    /// <summary>Total transactions this tracer may read (shared across the whole run).</summary>
    public int MaxTransactionReads { get; init; } = 500_000;

    /// <summary>
    /// Cap on FIFO segments held for one output. Heavy mixing fragments a sat-range list;
    /// past this the remainder is folded into untraced runs rather than growing without
    /// bound — early pedigree survives, later positions are honestly unproven. (Payout
    /// coins DO exceed this routinely since the 2026-08-17 wallet rebuild: their lineage
    /// blends 150-coinbase sweep batches over a long change-chain, so expect partial FIFO
    /// attribution there; the haircut model is unaffected by this cap.)
    /// </summary>
    public int MaxSegmentsPerOutput { get; init; } = 4_096;
}

/// <summary>
/// A contiguous run of satoshis inside one output, in position order — the FIFO
/// ("ordinal") view. <see cref="MintHeight"/> null means the run's origin isn't known
/// (an unreadable ancestor, a cutoff, or coinbase fee-sats), never a guess.
/// </summary>
public sealed record SatSegment(long Offset, long Length, long? MintHeight);

/// <summary>Progress ticks for the UI while a trace runs.</summary>
public sealed record TraceProgress(int OutputsDone, int OutputsTotal, int TransactionsRead);

/// <summary>Where a satoshi was minted.</summary>
public sealed record MintPoint(long Height, DateTime TimeUtc, MintVintage Vintage);

/// <summary>
/// One wallet output's ancestry under BOTH models. <see cref="ByHeight"/> is the
/// value-proportional haircut (fractions — what the website's engine computes, so the
/// two reconcile). <see cref="Segments"/> is the FIFO sat-range pedigree, where every
/// satoshi belongs to exactly ONE minting block and its position inside the output is
/// known — which is what makes exact vintage coin control possible. Attribution below
/// <see cref="Satoshis"/> means part of the lineage couldn't be proven — never invented.
/// </summary>
public sealed record OutputProvenance(
    string TxId, int Vout, long Satoshis,
    IReadOnlyDictionary<long, long> ByHeight,
    IReadOnlyList<SatSegment> Segments)
{
    public long Attributed => ByHeight.Values.Sum();
    public bool FullyTraced => Attributed == Satoshis;

    /// <summary>Share of this output descending from one minting block, 0–1 (haircut).</summary>
    public double PurityOf(long height) =>
        Satoshis <= 0 ? 0 : (double)ByHeight.GetValueOrDefault(height) / Satoshis;

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
/// numbers — always label which is on screen.
/// </summary>
public sealed record ProvenanceReport(
    IReadOnlyList<OutputProvenance> Outputs,
    IReadOnlyDictionary<long, MintPoint> Origins,
    VintageBreakdown Fifo,
    VintageBreakdown Haircut,
    long TotalSatoshis,
    int TransactionsRead)
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
/// Everything is memoised per output and per transaction, so shared ancestry is walked
/// once. Reuse one instance for a session; construct a new one to drop the caches.
/// </summary>
public sealed class AncestryTracer
{
    private readonly IAncestryRpc _rpc;
    private readonly AncestryTracerOptions _options;

    private readonly Dictionary<string, RawTransaction?> _txCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BlockRef?> _blockCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string TxId, int Vout), IReadOnlyDictionary<long, long>?> _outputCache = new();
    private readonly Dictionary<(string TxId, int Vout), IReadOnlyList<SatSegment>?> _segmentCache = new();
    private readonly Dictionary<long, MintPoint> _origins = new();
    private int _reads;

    public AncestryTracer(IAncestryRpc rpc, AncestryTracerOptions? options = null)
    {
        _rpc = rpc;
        _options = options ?? new AncestryTracerOptions();
    }

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
        var traced = new List<OutputProvenance>(outputs.Count);
        for (var i = 0; i < outputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var u = outputs[i];
            var vector = await ResolveOutputAsync(u.TxId, u.Vout, 0, ct)
                         ?? new Dictionary<long, long>();
            // Second model over the SAME cached transactions — no extra RPC.
            var segments = await ResolveSegmentsAsync(u.TxId, u.Vout, 0, ct)
                           ?? Untraced(u.Satoshis);
            traced.Add(new OutputProvenance(u.TxId, u.Vout, u.Satoshis, vector, segments));
            progress?.Report(new TraceProgress(i + 1, outputs.Count, _reads));
        }
        return BuildReport(traced);
    }

    /// <summary>
    /// The satoshis of one output, split by minting block. Null means "couldn't be
    /// determined" (unreadable transaction, or a depth/budget cutoff) — callers treat
    /// that as no ancestry, which dilutes rather than fabricates.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, long>?> ResolveOutputAsync(
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

        IReadOnlyDictionary<long, long>? vector;
        if (tx.IsCoinbase)
        {
            // Path terminates: this block minted the whole output.
            var origin = await ResolveOriginAsync(tx, ct);
            vector = origin is null
                ? null
                : new Dictionary<long, long> { [origin.Height] = output.Satoshis };
        }
        else
        {
            vector = await BlendInputsAsync(tx, output, depth, ct);
        }

        _outputCache[(txid, vout)] = vector;
        return vector;
    }

    /// <summary>The haircut: combine the inputs' ancestry, then take this output's share.</summary>
    private async Task<IReadOnlyDictionary<long, long>> BlendInputsAsync(
        RawTransaction tx, RawTxOutput output, int depth, CancellationToken ct)
    {
        var combined = new Dictionary<long, long>();
        long knownInputValue = 0;

        foreach (var input in tx.Inputs)
        {
            if (input.TxId is null) continue;   // coinbase marker on a mixed vin list
            ct.ThrowIfCancellationRequested();

            // Cached after the recursion below reads it, so this costs at most one fetch.
            var parent = await GetTransactionAsync(input.TxId, ct);
            var parentOut = parent?.Outputs.FirstOrDefault(o => o.N == input.Vout);
            if (parentOut is not null) knownInputValue += parentOut.Satoshis;

            var parentVector = await ResolveOutputAsync(input.TxId, input.Vout, depth + 1, ct);
            if (parentVector is null) continue; // unreadable input: value without ancestry
            foreach (var (height, sats) in parentVector)
                combined[height] = combined.GetValueOrDefault(height) + sats;
        }

        // Total input value is the true denominator. When every input resolved we have it
        // exactly (fee included). When one didn't, fall back to the sum of outputs, which
        // is a floor on the input total — never smaller, so a share can't exceed what the
        // inputs actually carried.
        var sumOutputs = tx.Outputs.Sum(o => o.Satoshis);
        var denominator = Math.Max(sumOutputs, knownInputValue);
        var vector = new Dictionary<long, long>();
        if (denominator <= 0) return vector;

        foreach (var (height, sats) in combined)
        {
            // Int128: sats × value overflows long (both reach 2e17 on this chain).
            var share = (long)((Int128)sats * output.Satoshis / denominator);
            if (share > 0) vector[height] = share;
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
        }
        return origin;
    }

    private async Task<RawTransaction?> GetTransactionAsync(string txid, CancellationToken ct)
    {
        if (_txCache.TryGetValue(txid, out var cached)) return cached;
        if (_reads >= _options.MaxTransactionReads) return null;

        _reads++;
        var tx = await _rpc.GetTransactionAsync(txid, ct);
        _txCache[txid] = tx;
        return tx;
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
            Breakdown(outputs, o => o.FifoByHeight()),
            Breakdown(outputs, o => o.ByHeight),
            outputs.Sum(o => o.Satoshis),
            _reads);

    /// <summary>Folds one model's per-block satoshis into the year/special catalogue.</summary>
    private VintageBreakdown Breakdown(
        List<OutputProvenance> outputs, Func<OutputProvenance, IReadOnlyDictionary<long, long>> model)
    {
        // sats + distinct-output count per vintage key, plus the block/time span it covers
        var acc = new Dictionary<string, (MintVintage V, long Sats, int Count, long FirstH, DateTime First, DateTime Last)>();
        // the same, keyed per calendar month — ordinary years only (a special IS one block)
        var months = new Dictionary<string, (MintVintage V, long Sats, int Count, long FirstH, DateTime First, DateTime Last)>();
        long attributed = 0;

        static Dictionary<string, (MintVintage V, long Sats, int Count, long FirstH, DateTime First, DateTime Last)>
            Add(Dictionary<string, (MintVintage V, long Sats, int Count, long FirstH, DateTime First, DateTime Last)> into,
                string key, MintVintage vintage, long sats, long height, DateTime time)
        {
            if (into.TryGetValue(key, out var cur))
            {
                into[key] = (cur.V, cur.Sats + sats, cur.Count + 1,
                    Math.Min(cur.FirstH, height),
                    time < cur.First ? time : cur.First,
                    time > cur.Last ? time : cur.Last);
            }
            else
            {
                into[key] = (vintage, sats, 1, height, time, time);
            }
            return into;
        }

        foreach (var o in outputs)
        {
            foreach (var (height, sats) in model(o))
            {
                attributed += sats;
                if (!_origins.TryGetValue(height, out var origin)) continue;

                Add(acc, origin.Vintage.Key, origin.Vintage, sats, height, origin.TimeUtc);

                if (origin.Vintage.Kind == VintageKind.Year)
                {
                    // "2026-06" sorts naturally and prefixes with the year's own key,
                    // which is how the UI pairs a month back to its year.
                    var monthKey = origin.TimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                    var monthLabel = origin.TimeUtc.ToString("MMM yyyy", CultureInfo.InvariantCulture);
                    Add(months, monthKey, origin.Vintage with { Key = monthKey, Label = monthLabel },
                        sats, height, origin.TimeUtc);
                }
            }
        }

        static List<VintageRow> ToRows(Dictionary<string, (MintVintage V, long Sats, int Count, long FirstH, DateTime First, DateTime Last)> src) =>
            src.Select(kv => new VintageRow(
                kv.Key, kv.Value.V.Label, kv.Value.V.Kind, kv.Value.Sats, kv.Value.Count,
                kv.Value.FirstH, kv.Value.First, kv.Value.Last)).ToList();

        var rows = ToRows(acc);

        return new VintageBreakdown(
            rows.Where(r => r.Kind == VintageKind.Year).OrderBy(r => r.FirstTimeUtc.Year).ToList(),
            rows.Where(r => r.Kind != VintageKind.Year).OrderBy(r => r.FirstHeight).ToList(),
            attributed,
            ToRows(months).OrderBy(r => r.Key, StringComparer.Ordinal).ToList());
    }
}

using System.Globalization;

namespace BlazecoinWallet.Core.Services.Provenance;

/// <summary>
/// One pickable vintage: a whole year, a single MONTH within a year, or a single-block
/// special — with every minting height it covers and the satoshis held across them.
/// </summary>
public sealed record SendableVintage(
    string Key, string Label, VintageKind Kind, long FirstHeight,
    IReadOnlySet<long> Heights, long Satoshis, bool IsMonth = false, bool PureOnly = false);

/// <summary>What one UTXO holds of a chosen vintage (FIFO — whole satoshis).</summary>
public sealed record VintageHolding(OutputProvenance Output, long VintageSatoshis, IReadOnlyList<SatSegment> Runs)
{
    /// <summary>True when the WHOLE output was minted by that block — a collector's coin.</summary>
    public bool IsPure => VintageSatoshis == Output.Satoshis;

    /// <summary>Share of the output that is this vintage, 0–1.</summary>
    public double Purity => Output.Satoshis <= 0 ? 0 : (double)VintageSatoshis / Output.Satoshis;
}

public enum OutputRole
{
    /// <summary>Change that must come FIRST so the recipient's output lands on the vintage run.</summary>
    LeadingChange,
    /// <summary>The satoshis actually being sent — the vintage.</summary>
    Vintage,
    /// <summary>Ordinary trailing change.</summary>
    Change,
}

public sealed record PlannedInput(string TxId, int Vout, long Satoshis);
public sealed record PlannedOutput(string Address, long Satoshis, OutputRole Role);

/// <summary>
/// A transaction that sends satoshis of ONE minting block. Outputs are ORDER-CRITICAL:
/// under FIFO they consume the input stream front to back, so the leading change (when
/// present) exists purely to push the recipient's output onto the vintage run, and the
/// fee is whatever the outputs leave at the tail.
/// </summary>
public sealed record VintageSendPlan(
    long TargetHeight,
    IReadOnlySet<long> TargetHeights,
    IReadOnlyList<PlannedInput> Inputs,
    IReadOnlyList<PlannedOutput> Outputs,
    long FeeSatoshis,
    IReadOnlyList<string> Warnings)
{
    public long InputTotal => Inputs.Sum(i => i.Satoshis);
    public long OutputTotal => Outputs.Sum(o => o.Satoshis);
    public long SendAmount => Outputs.Where(o => o.Role == OutputRole.Vintage).Sum(o => o.Satoshis);

    /// <summary>Inputs must equal outputs + fee, or the daemon would reject it.</summary>
    public bool Balances => InputTotal == OutputTotal + FeeSatoshis;
}

/// <summary>A plan, or the reason there isn't one.</summary>
public sealed record VintagePlanResult(VintageSendPlan? Plan, string? Problem)
{
    public static VintagePlanResult Fail(string problem) => new(null, problem);
    public static VintagePlanResult Ok(VintageSendPlan plan) => new(plan, null);
}

/// <summary>
/// Vintage coin control: choosing which satoshis to spend so a send carries a chosen
/// block's lineage.
///
/// Under FIFO this is exact rather than approximate. Two strategies, in order:
///  1. PURE coins — outputs minted 100% by the target block. Spend them whole and the
///     recipient's output is unambiguously that vintage.
///  2. An exact SLICE of one blended output — the target's satoshis sit at a known
///     offset, so a leading change output pushes the recipient's output onto exactly
///     that run.
///
/// Two rules the caller must respect and this planner enforces: outputs are
/// ORDER-CRITICAL (never re-sort them), and the fee is taken from the TAIL, so the
/// vintage must never be the last thing in the stream.
/// </summary>
public static class VintageCoinControl
{
    /// <summary>
    /// Smallest output the network will relay. `minrelaytxfee=0` makes sends FREE on this
    /// chain, but dust is a SEPARATE policy (`dustrelayfee`, left at Core's default), and
    /// it bites regardless of fee. Verified against the live daemon 2026-08-01 with
    /// `testmempoolaccept`: 545 satoshis → "dust", 546 → accepted. Every output of a plan
    /// must clear this — change included — or the whole transaction is rejected.
    /// </summary>
    public const long DustThresholdSatoshis = 546;

    /// <summary>Names any output that the network would reject as dust, or null if all pass.</summary>
    private static string? DustProblem(IEnumerable<PlannedOutput> outputs, long threshold)
    {
        if (threshold <= 0) return null;
        foreach (var o in outputs.Where(o => o.Satoshis < threshold))
        {
            var what = o.Role switch
            {
                OutputRole.Vintage => "The amount being sent",
                OutputRole.LeadingChange => "The leading change output that positions the vintage",
                _ => "The change coming back to you",
            };
            return $"{what} is {o.Satoshis:N0} satoshis, below the network's {threshold:N0}-satoshi dust limit — " +
                   "it would be rejected as dust. Sends are free here, but every output must still be at least " +
                   $"{threshold:N0} satoshis.";
        }
        return null;
    }

    /// <summary>
    /// The sendable vintages, ONE ROW PER VINTAGE — not per block. A year spans
    /// thousands of blocks (a mining wallet holds thousands of 2026 coinbases), so
    /// grouping is what a picker needs; specials collapse to themselves. Years also
    /// get one entry PER MONTH ("2026-06"), because month is the granularity a
    /// collector actually sends at. Ordered oldest first, each year followed by its
    /// own months.
    /// </summary>
    public static IReadOnlyList<SendableVintage> SendableVintages(ProvenanceReport report)
    {
        var groups = new Dictionary<string, (MintVintage V, string Label, long First, HashSet<long> Heights, long Sats, bool IsMonth)>();

        void Add(string key, MintVintage vintage, string label, long height, long sats, bool isMonth)
        {
            if (groups.TryGetValue(key, out var cur))
            {
                cur.Heights.Add(height);
                groups[key] = (cur.V, cur.Label, Math.Min(cur.First, height), cur.Heights, cur.Sats + sats, cur.IsMonth);
            }
            else
            {
                groups[key] = (vintage, label, height, new HashSet<long> { height }, sats, isMonth);
            }
        }

        foreach (var output in report.Outputs)
        {
            foreach (var segment in output.Segments)
            {
                if (segment.MintHeight is not { } height) continue;
                if (!report.Origins.TryGetValue(height, out var origin)) continue;

                Add(origin.Vintage.Key, origin.Vintage, origin.Vintage.Label, height, segment.Length, false);

                if (origin.Vintage.Kind == VintageKind.Year)
                {
                    Add(origin.TimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture), origin.Vintage,
                        origin.TimeUtc.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                        height, segment.Length, true);
                }
            }
        }

        var sendable = new List<SendableVintage>();
        foreach (var g in groups)
        {
            var entry = new SendableVintage(g.Key, g.Value.Label, g.Value.V.Kind,
                g.Value.First, g.Value.Heights, g.Value.Sats, g.Value.IsMonth);
            sendable.Add(entry);

            // A PURE-ONLY twin, offered only when the vintage is a MIX — if every coin
            // of it is already pure the plain entry sends pure coins anyway (the planner
            // always prefers them), and a duplicate row would just bloat the list.
            var pure = PureSatoshis(report, g.Value.Heights);
            if (pure > 0 && pure < g.Value.Sats)
            {
                sendable.Add(entry with
                {
                    Key = g.Key + PureSuffix,
                    Label = g.Value.Label + " — unmixed outputs only",
                    Satoshis = pure,
                    PureOnly = true,
                });
            }
        }

        // Oldest first; a year sorts before its own months, and a pure-only twin
        // immediately after the entry it belongs to.
        return sendable
            .OrderBy(v => v.FirstHeight).ThenBy(v => v.IsMonth).ThenBy(v => v.PureOnly)
            .ToList();
    }

    /// <summary>Marks a picker entry that must spend whole pure coins only.</summary>
    public const string PureSuffix = "#pure";

    /// <summary>Satoshis held in outputs minted ENTIRELY by one of these blocks.</summary>
    public static long PureSatoshis(ProvenanceReport report, IReadOnlySet<long> heights) =>
        Holdings(report, heights).Where(h => h.IsPure).Sum(h => h.VintageSatoshis);

    /// <summary>Every UTXO holding the given vintage, purest and largest first.</summary>
    public static IReadOnlyList<VintageHolding> Holdings(ProvenanceReport report, long height) =>
        Holdings(report, new HashSet<long> { height });

    /// <summary>
    /// Every UTXO holding any of these minting blocks — the whole-vintage form. Runs
    /// that sit next to each other are MERGED, so a year's worth of adjacent blocks
    /// reads as one sendable run rather than thousands of one-block slivers.
    /// </summary>
    public static IReadOnlyList<VintageHolding> Holdings(ProvenanceReport report, IReadOnlySet<long> heights)
    {
        var holdings = new List<VintageHolding>();
        foreach (var o in report.Outputs)
        {
            var runs = MergeAdjacent(o.Segments.Where(s => s.MintHeight is { } h && heights.Contains(h)));
            if (runs.Count == 0) continue;
            holdings.Add(new VintageHolding(o, runs.Sum(r => r.Length), runs));
        }
        return holdings
            .OrderByDescending(h => h.IsPure)
            .ThenByDescending(h => h.Purity)
            .ThenByDescending(h => h.VintageSatoshis)
            .ToList();
    }

    /// <summary>Folds touching runs into one so a contiguous stretch is sendable whole.</summary>
    private static List<SatSegment> MergeAdjacent(IEnumerable<SatSegment> runs)
    {
        var merged = new List<SatSegment>();
        foreach (var run in runs.OrderBy(r => r.Offset))
        {
            var last = merged.Count > 0 ? merged[^1] : null;
            if (last is not null && last.Offset + last.Length == run.Offset)
                merged[^1] = last with { Length = last.Length + run.Length };
            else
                merged.Add(run);
        }
        return merged;
    }

    /// <summary>Total satoshis of a vintage the wallet can actually send.</summary>
    public static long Available(ProvenanceReport report, long height) =>
        Holdings(report, height).Sum(h => h.VintageSatoshis);

    public static long Available(ProvenanceReport report, IReadOnlySet<long> heights) =>
        Holdings(report, heights).Sum(h => h.VintageSatoshis);

    /// <summary>
    /// Builds a send of <paramref name="amountSatoshis"/> carrying block
    /// <paramref name="height"/>'s lineage. <paramref name="changeAddress"/> receives
    /// everything that isn't sent (it comes back re-attributed, which is why merging is
    /// warned about).
    /// </summary>
    public static VintagePlanResult PlanSend(
        ProvenanceReport report,
        long height,
        long amountSatoshis,
        string recipient,
        string changeAddress,
        long feeSatoshis)
        => PlanSend(report, new HashSet<long> { height }, height, amountSatoshis, recipient, changeAddress, feeSatoshis);

    /// <summary>
    /// Whole-vintage form: any of <paramref name="heights"/> satisfies the send (a year
    /// spans many blocks). <paramref name="targetHeight"/> is what the plan records and
    /// verifies against — pass the vintage's first block.
    /// </summary>
    public static VintagePlanResult PlanSend(
        ProvenanceReport report,
        IReadOnlySet<long> heights,
        long targetHeight,
        long amountSatoshis,
        string recipient,
        string changeAddress,
        long feeSatoshis,
        bool pureOnly = false,
        long dustThresholdSatoshis = DustThresholdSatoshis)
    {
        if (amountSatoshis <= 0) return VintagePlanResult.Fail("Amount must be positive.");
        if (feeSatoshis < 0) return VintagePlanResult.Fail("Fee cannot be negative.");
        if (string.IsNullOrWhiteSpace(recipient)) return VintagePlanResult.Fail("A recipient address is required.");
        if (string.IsNullOrWhiteSpace(changeAddress)) return VintagePlanResult.Fail("A change address is required.");

        var height = targetHeight;
        var holdings = Holdings(report, heights);
        if (holdings.Count == 0) return VintagePlanResult.Fail("This wallet holds none of that vintage.");

        var available = holdings.Sum(h => h.VintageSatoshis);
        if (available < amountSatoshis)
            return VintagePlanResult.Fail($"Only {available:N0} satoshis of that vintage are held.");

        // 1. Pure coins first — no slicing needed and nothing else can contaminate them.
        var pure = holdings.Where(h => h.IsPure).ToList();
        if (pure.Sum(h => h.VintageSatoshis) >= amountSatoshis)
            return PlanFromPureCoins(height, heights, pure, amountSatoshis, recipient, changeAddress, feeSatoshis, dustThresholdSatoshis);

        // Pure-only was asked for and the pure coins don't cover it — refuse rather than
        // quietly falling back to slicing a blended output.
        if (pureOnly)
            return VintagePlanResult.Fail(
                $"Pure coins of that vintage total {pure.Sum(h => h.VintageSatoshis):N0} satoshis — " +
                "not enough. Choose a smaller amount, or pick the vintage without the pure-coins restriction.");

        // 2. Otherwise slice a single blended output that holds a big enough run.
        foreach (var h in holdings.Where(x => !x.IsPure))
        {
            var run = h.Runs.FirstOrDefault(r => r.Length >= amountSatoshis);
            if (run is null) continue;
            return PlanFromSlice(height, heights, h, run, amountSatoshis, recipient, changeAddress, feeSatoshis, dustThresholdSatoshis);
        }

        return VintagePlanResult.Fail(
            "That vintage is split across outputs in runs smaller than the amount. " +
            "Send a smaller amount, or consolidate first (which re-blends lineage).");
    }

    /// <summary>
    /// Sends whole coins INTACT — no change output at all, so a block's coinbase leaves
    /// the wallet exactly as it was minted. This is the collector's move: on this chain
    /// relay is free, so with a zero fee the recipient receives the complete original
    /// coinbase, satoshi for satoshi. Any fee comes out of the coin itself, which is why
    /// a non-zero fee is warned about — it breaks the "whole coin" property.
    /// </summary>
    public static VintagePlanResult PlanWholeCoins(
        ProvenanceReport report,
        IReadOnlySet<long> heights,
        long targetHeight,
        int coinCount,
        string recipient,
        long feeSatoshis,
        long? coinValueSatoshis = null,
        long dustThresholdSatoshis = DustThresholdSatoshis)
    {
        if (coinCount <= 0) return VintagePlanResult.Fail("Choose at least one output.");
        if (feeSatoshis < 0) return VintagePlanResult.Fail("Fee cannot be negative.");
        if (string.IsNullOrWhiteSpace(recipient)) return VintagePlanResult.Fail("A recipient address is required.");

        // Only whole PURE coins qualify — sending a blended output intact would deliver
        // someone else's lineage along with it.
        var pure = Holdings(report, heights)
            .Where(h => h.IsPure)
            .OrderBy(h => h.Output.Satoshis)      // smallest first, unless a size is named
            .ToList();

        if (pure.Count == 0)
            return VintagePlanResult.Fail("That vintage holds no whole (pure) outputs — every one of them is mixed with other lineage.");

        // A named coin size is how a collector says "the 51.625 coinbase, not the dust" —
        // without it the smallest coin wins, which surprises people holding both.
        if (coinValueSatoshis is { } wanted)
        {
            pure = pure.Where(h => h.Output.Satoshis == wanted).ToList();
            if (pure.Count == 0)
                return VintagePlanResult.Fail($"That vintage holds no whole output of exactly {wanted:N0} satoshis.");
        }
        if (coinCount > pure.Count)
            return VintagePlanResult.Fail($"That vintage holds {pure.Count:N0} whole output{(pure.Count == 1 ? "" : "s")} — fewer than the {coinCount:N0} asked for.");

        var chosen = pure.Take(coinCount).ToList();
        var total = chosen.Sum(c => c.Output.Satoshis);
        if (feeSatoshis >= total)
            return VintagePlanResult.Fail($"The fee ({feeSatoshis:N0}) would consume the whole output ({total:N0} satoshis).");

        // ONE output, no change: everything the inputs carry goes to the recipient.
        var outputs = new List<PlannedOutput> { new(recipient, total - feeSatoshis, OutputRole.Vintage) };

        var warnings = new List<string>();
        if (feeSatoshis == 0)
            warnings.Add("Zero fee — the output arrives EXACTLY as it was minted, satoshi for satoshi. Free relay makes this possible.");
        else
            warnings.Add($"A {feeSatoshis:N0}-satoshi fee is taken from the output itself, so it arrives short of its minted value. Use a zero fee to deliver it whole.");

        if (chosen.Count == 1)
        {
            var block = chosen[0].Runs.FirstOrDefault()?.MintHeight;
            warnings.Add($"Sending the entire coinbase of block {block:N0} — {total:N0} satoshis, with no change back to you.");
        }
        else
        {
            warnings.Add($"{chosen.Count} whole outputs are being sent as ONE output — they merge on arrival and cannot be separated again.");
        }

        // Every output must clear the dust floor or the network rejects the whole tx.
        if (DustProblem(outputs, dustThresholdSatoshis) is { } dust) return VintagePlanResult.Fail(dust);

        return VintagePlanResult.Ok(new VintageSendPlan(targetHeight, heights,
            chosen.Select(c => new PlannedInput(c.Output.TxId, c.Output.Vout, c.Output.Satoshis)).ToList(),
            outputs, feeSatoshis, warnings));
    }

    /// <summary>The whole pure coins of a vintage, smallest first — what the picker offers.</summary>
    public static IReadOnlyList<VintageHolding> WholeCoins(ProvenanceReport report, IReadOnlySet<long> heights) =>
        Holdings(report, heights).Where(h => h.IsPure).OrderBy(h => h.Output.Satoshis).ToList();

    /// <summary>
    /// The distinct SIZES of whole coin a vintage holds, largest first, with how many of
    /// each. A mining wallet has thousands of identical coinbases plus the odd small one,
    /// so choosing by size is far more useful than choosing from a list of every coin.
    /// </summary>
    public static IReadOnlyList<(long Satoshis, int Count)> WholeCoinSizes(
        ProvenanceReport report, IReadOnlySet<long> heights) =>
        WholeCoins(report, heights)
            .GroupBy(h => h.Output.Satoshis)
            .Select(g => (Satoshis: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Satoshis)
            .ToList();

    /// <summary>
    /// The blocks this wallet can send WHOLE — those it holds outputs from that are
    /// entirely that block's. Largest reward first, since the big ones are the collector
    /// items. Optionally narrowed to one vintage. Without this a whole-block send would
    /// need a height typed from memory, and a mining wallet spans tens of thousands.
    /// </summary>
    public static IReadOnlyList<(long Height, int Outputs, long Satoshis)> BlocksHeld(
        ProvenanceReport report, IReadOnlySet<long>? within = null)
    {
        var acc = new Dictionary<long, (int Outputs, long Satoshis)>();
        foreach (var output in report.Outputs)
        {
            var byHeight = output.FifoByHeight();
            if (byHeight.Count != 1) continue;                       // spans blocks — not one block's
            var height = byHeight.Keys.First();
            if (byHeight[height] != output.Satoshis) continue;       // part of it is untraced
            if (within is not null && !within.Contains(height)) continue;

            var cur = acc.GetValueOrDefault(height);
            acc[height] = (cur.Outputs + 1, cur.Satoshis + output.Satoshis);
        }
        return acc
            .Select(kv => (Height: kv.Key, kv.Value.Outputs, kv.Value.Satoshis))
            .OrderByDescending(x => x.Satoshis).ThenBy(x => x.Height)
            .ToList();
    }

    /// <summary>
    /// Sends an entire BLOCK's reward — every coin this wallet holds that was minted by
    /// one block, together, as a single output with no change. A coinbase can pay several
    /// outputs (block 413 paid three), so "the whole block" is a bigger, rarer collector
    /// unit than "a whole coin". Only coins purely of that block qualify.
    /// </summary>
    public static VintagePlanResult PlanWholeBlock(
        ProvenanceReport report, long height, string recipient, long feeSatoshis,
        long dustThresholdSatoshis = DustThresholdSatoshis)
    {
        if (feeSatoshis < 0) return VintagePlanResult.Fail("Fee cannot be negative.");
        if (string.IsNullOrWhiteSpace(recipient)) return VintagePlanResult.Fail("A recipient address is required.");

        var heights = new HashSet<long> { height };
        var coins = Holdings(report, heights).Where(h => h.IsPure).ToList();
        if (coins.Count == 0)
            return VintagePlanResult.Fail($"This wallet holds no whole outputs minted by block {height:N0}.");

        var total = coins.Sum(c => c.Output.Satoshis);
        if (feeSatoshis >= total)
            return VintagePlanResult.Fail($"The fee ({feeSatoshis:N0}) would consume the block's reward ({total:N0} satoshis).");

        var outputs = new List<PlannedOutput> { new(recipient, total - feeSatoshis, OutputRole.Vintage) };

        var warnings = new List<string>
        {
            $"Sending every coin this wallet holds from block {height:N0} — {coins.Count} output{(coins.Count == 1 ? "" : "s")}, {total:N0} satoshis, with no change back to you."
        };
        if (coins.Count > 1)
            warnings.Add("A coinbase can pay several outputs; these arrive merged as one, which cannot be undone.");
        warnings.Add(feeSatoshis == 0
            ? "Zero fee — the block's reward arrives EXACTLY as it was minted. Free relay makes this possible."
            : $"A {feeSatoshis:N0}-satoshi fee comes out of the reward itself, so it arrives short of what the block minted. Use a zero fee to send it whole.");

        // Every output must clear the dust floor or the network rejects the whole tx.
        if (DustProblem(outputs, dustThresholdSatoshis) is { } dust) return VintagePlanResult.Fail(dust);

        return VintagePlanResult.Ok(new VintageSendPlan(height, heights,
            coins.Select(c => new PlannedInput(c.Output.TxId, c.Output.Vout, c.Output.Satoshis)).ToList(),
            outputs, feeSatoshis, warnings));
    }

    /// <summary>Whole pure coins: the recipient's output takes the front of the stream.</summary>
    private static VintagePlanResult PlanFromPureCoins(
        long height, IReadOnlySet<long> heights, List<VintageHolding> pure, long amount,
        string recipient, string changeAddress, long fee, long dustThresholdSatoshis)
    {
        // Smallest-first among the pure coins that still covers the amount keeps the
        // fewest collector coins in play (spending one destroys its purity forever).
        var chosen = new List<VintageHolding>();
        long total = 0;
        foreach (var h in pure.OrderBy(h => h.Output.Satoshis))
        {
            chosen.Add(h);
            total += h.VintageSatoshis;
            if (total >= amount + fee || total >= amount) break;
        }

        // The fee comes out of the tail, so the inputs must cover amount + fee.
        if (total < amount + fee)
        {
            foreach (var h in pure.OrderBy(x => x.Output.Satoshis))
            {
                if (chosen.Contains(h)) continue;
                chosen.Add(h);
                total += h.VintageSatoshis;
                if (total >= amount + fee) break;
            }
        }
        if (total < amount + fee)
            return VintagePlanResult.Fail(
                $"Pure coins of that vintage total {total:N0} satoshis — not enough to cover {amount:N0} plus a {fee:N0} fee.");

        var outputs = new List<PlannedOutput> { new(recipient, amount, OutputRole.Vintage) };
        var change = total - amount - fee;
        if (change > 0) outputs.Add(new PlannedOutput(changeAddress, change, OutputRole.Change));

        var warnings = new List<string>();
        if (chosen.Count > 1)
            warnings.Add($"{chosen.Count} pure outputs are being spent together — they merge into one lineage stream and cannot be separated again.");
        if (change > 0)
            warnings.Add($"{change:N0} satoshis of this vintage return as change (still this vintage, but now a different output).");
        if (fee > 0)
            warnings.Add($"The {fee:N0}-satoshi fee is taken from the tail, so it is paid in this vintage's satoshis.");

        // Every output must clear the dust floor or the network rejects the whole tx.
        if (DustProblem(outputs, dustThresholdSatoshis) is { } dust) return VintagePlanResult.Fail(dust);

        return VintagePlanResult.Ok(new VintageSendPlan(height, heights,
            chosen.Select(c => new PlannedInput(c.Output.TxId, c.Output.Vout, c.Output.Satoshis)).ToList(),
            outputs, fee, warnings));
    }

    /// <summary>
    /// One blended output, sliced: leading change absorbs everything before the run so
    /// the recipient's output lands exactly on it.
    /// </summary>
    private static VintagePlanResult PlanFromSlice(
        long height, IReadOnlySet<long> heights, VintageHolding holding, SatSegment run, long amount,
        string recipient, string changeAddress, long fee, long dustThresholdSatoshis)
    {
        var value = holding.Output.Satoshis;
        var lead = run.Offset;                       // sats before the vintage run
        var trailing = value - lead - amount;        // everything after what we're sending

        if (trailing < fee)
            return VintagePlanResult.Fail(
                $"The fee would eat into the vintage: only {trailing:N0} satoshis sit after the run, " +
                $"and the fee is {fee:N0}. Send a smaller amount or add an input.");

        var outputs = new List<PlannedOutput>();
        if (lead > 0) outputs.Add(new PlannedOutput(changeAddress, lead, OutputRole.LeadingChange));
        outputs.Add(new PlannedOutput(recipient, amount, OutputRole.Vintage));
        var change = trailing - fee;
        if (change > 0) outputs.Add(new PlannedOutput(changeAddress, change, OutputRole.Change));

        var warnings = new List<string>
        {
            "This output is blended: the send is an exact slice, so output ORDER must be preserved when the transaction is built."
        };
        if (lead > 0)
            warnings.Add($"{lead:N0} satoshis of other lineage return to you first — that leading change output is what positions the vintage.");
        if (change > 0)
            warnings.Add($"{change:N0} satoshis of other lineage return as trailing change.");

        // Every output must clear the dust floor or the network rejects the whole tx.
        if (DustProblem(outputs, dustThresholdSatoshis) is { } dust) return VintagePlanResult.Fail(dust);

        return VintagePlanResult.Ok(new VintageSendPlan(height, heights,
            new[] { new PlannedInput(holding.Output.TxId, holding.Output.Vout, value) },
            outputs, fee, warnings));
    }

    /// <summary>
    /// Re-runs the FIFO stream over the plan and reports what the recipient's output
    /// would ACTUALLY carry — the safety check before anything is broadcast. Returns the
    /// satoshis of the target vintage in the vintage output (equal to the send amount
    /// when the plan is exact).
    /// </summary>
    public static long VerifyVintageDelivered(VintageSendPlan plan, ProvenanceReport report)
    {
        // Rebuild the input stream in input order, exactly as the tracer does.
        var stream = new List<SatSegment>();
        long length = 0;
        foreach (var input in plan.Inputs)
        {
            var source = report.Outputs.FirstOrDefault(o => o.TxId == input.TxId && o.Vout == input.Vout);
            if (source is null) return 0;                       // can't verify → claim nothing
            foreach (var s in source.Segments)
                stream.Add(s with { Offset = length + s.Offset });
            length += input.Satoshis;
        }

        // Walk the outputs in order and total the target vintage inside the vintage output.
        long start = 0, delivered = 0;
        foreach (var output in plan.Outputs)
        {
            if (output.Role == OutputRole.Vintage)
            {
                var end = start + output.Satoshis;
                foreach (var s in stream)
                {
                    // Any block of the chosen vintage counts — a year spans many.
                    if (s.MintHeight is not { } h || !plan.TargetHeights.Contains(h)) continue;
                    var from = Math.Max(s.Offset, start);
                    var to = Math.Min(s.Offset + s.Length, end);
                    if (to > from) delivered += to - from;
                }
            }
            start += output.Satoshis;
        }
        return delivered;
    }
}

namespace BlazecoinWallet.Core.Services.Provenance.Explorer;

/// <summary>One of an output's lineage buckets as the explorer stores it (its YearKey, satoshis).</summary>
public sealed record ExplorerOutputVintageRow(int YearKey, long Satoshis);

/// <summary>One unspent output of an address with its stored lineage split.</summary>
public sealed record ExplorerOutputVintage(string TxId, int Vout, long Satoshis, IReadOnlyList<ExplorerOutputVintageRow> Vintages);

/// <summary>The explorer's per-output answer for one address.</summary>
public sealed record ExplorerAddressOutputs(string Address, long ComputedToHeight, IReadOnlyList<ExplorerOutputVintage> Outputs);

/// <summary>
/// One unspent output this wallet holds, with how much of the chosen vintage the explorer
/// says it carries. <see cref="Purity"/> is that share of the output's value (0–1).
/// </summary>
public sealed record ExplorerHolding(string TxId, int Vout, long Satoshis, long VintageSatoshis)
{
    public double Purity => Satoshis <= 0 ? 0 : (double)VintageSatoshis / Satoshis;
}

/// <summary>A vintage the explorer says this wallet's unspent outputs carry, for the send picker.</summary>
public sealed record ExplorerSendableVintage(
    string Key, string Label, VintageKind Kind, long Height,
    long Satoshis, int OutputCount, int PureCount)
{
    public bool IsSpecial => Kind != VintageKind.Year;
}

/// <summary>
/// "Send by explorer lineage" (2026-10-31). The local FIFO walk is the only source of
/// PEDIGREE (which contiguous satoshis in which coin descend from which block), and it is
/// what the ordinary vintage send verifies against. A wallet whose lineage runs through deep
/// consolidation history never gets a pedigree inside the read budget, so this is the other
/// way: the explorer stores every output's lineage under its PROPORTIONAL model (the maths
/// the website's badges use), and this picks whole outputs by that share. It is explicitly
/// not pedigree — a 92 %-pure coin is "mostly 2014 by the explorer's model", never "a 2014
/// coin" — so plans built here say so, cannot be verified against the local stream, and are
/// kept to whole outputs with no change (the collector shape), which keeps the arithmetic
/// honest: what arrives is exactly what the explorer described, minus the fee.
/// </summary>
public static class ExplorerVintageSend
{
    /// <summary>
    /// The explorer's outputs that this wallet still holds unspent (the local list is the
    /// authority on what can be spent; the explorer may lag a block or two), with each output's
    /// lineage keyed the wallet's way. Outputs the explorer has not reached yet (no rows)
    /// are kept with an empty split so the catalogue can count them as unknown.
    /// </summary>
    public static IReadOnlyList<(UnspentOutput Output, IReadOnlyDictionary<string, long> ByVintage)> Join(
        IEnumerable<ExplorerAddressOutputs> answers, IReadOnlyList<UnspentOutput> localUnspent)
    {
        var ours = localUnspent.ToDictionary(u => (u.TxId, u.Vout));
        var result = new List<(UnspentOutput, IReadOnlyDictionary<string, long>)>();
        var seen = new HashSet<(string, int)>();
        foreach (var answer in answers)
        {
            foreach (var o in answer.Outputs)
            {
                var id = (o.TxId, o.Vout);
                if (!ours.TryGetValue(id, out var local) || !seen.Add(id)) continue;
                var split = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var row in o.Vintages)
                {
                    var resolved = ExplorerVintageKeys.Resolve(row.YearKey);
                    if (resolved is null || row.Satoshis <= 0) continue;
                    split[resolved.Vintage.Key] = split.GetValueOrDefault(resolved.Vintage.Key) + row.Satoshis;
                }
                // The explorer's value is authoritative for the split; the local value for what is spent.
                result.Add((local, split));
            }
        }
        return result;
    }

    /// <summary>
    /// The vintages the joined outputs carry, one row per vintage: total lineage, how many
    /// outputs hold any of it, and how many are pure at <paramref name="minPurity"/>.
    /// Years first (oldest first), then specials by block.
    /// </summary>
    public static IReadOnlyList<ExplorerSendableVintage> Catalogue(
        IReadOnlyList<(UnspentOutput Output, IReadOnlyDictionary<string, long> ByVintage)> joined, double minPurity)
    {
        var acc = new Dictionary<string, (long Sats, int Count, int Pure)>(StringComparer.Ordinal);
        foreach (var (output, split) in joined)
        {
            foreach (var (key, sats) in split)
            {
                var cur = acc.GetValueOrDefault(key);
                var pure = output.Satoshis > 0 && (double)sats / output.Satoshis >= minPurity ? 1 : 0;
                acc[key] = (cur.Sats + sats, cur.Count + 1, cur.Pure + pure);
            }
        }

        var rows = new List<ExplorerSendableVintage>();
        foreach (var (key, v) in acc)
        {
            var resolved = ResolveKey(key);
            if (resolved is null) continue;
            rows.Add(new ExplorerSendableVintage(key, resolved.Vintage.Label, resolved.Vintage.Kind, resolved.Height,
                v.Sats, v.Count, v.Pure));
        }
        return rows
            .OrderBy(r => r.IsSpecial ? 1 : 0)
            .ThenBy(r => r.IsSpecial ? r.Height : 0)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The outputs carrying any of <paramref name="vintageKey"/>, purest first then smallest first.</summary>
    public static IReadOnlyList<ExplorerHolding> Holdings(
        IReadOnlyList<(UnspentOutput Output, IReadOnlyDictionary<string, long> ByVintage)> joined, string vintageKey)
        => joined
            .Select(j => new ExplorerHolding(j.Output.TxId, j.Output.Vout, j.Output.Satoshis, j.ByVintage.GetValueOrDefault(vintageKey)))
            .Where(h => h.VintageSatoshis > 0)
            .OrderByDescending(h => h.Purity)
            .ThenBy(h => h.Satoshis)
            .ToList();

    /// <summary>Distinct output sizes among the holdings at or above the purity, with counts, largest first.</summary>
    public static IReadOnlyList<(long Satoshis, int Count)> Sizes(IReadOnlyList<ExplorerHolding> holdings, double minPurity)
        => holdings.Where(h => h.Purity >= minPurity)
            .GroupBy(h => h.Satoshis)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Key)
            .ToList();

    /// <summary>
    /// Plans a collector send of whole outputs chosen by the explorer's proportional lineage:
    /// outputs at or above <paramref name="minPurity"/>, purest first (smallest first among
    /// equals, unless an exact size is named), <paramref name="coinCount"/> of them, as ONE
    /// output to the recipient with no change. The plan's warnings carry the model caveat;
    /// the page shows it in place of the pedigree verification it cannot do.
    /// </summary>
    public static VintagePlanResult PlanWholeCoins(
        IReadOnlyList<ExplorerHolding> holdings,
        ExplorerSendableVintage vintage,
        int coinCount,
        double minPurity,
        string recipient,
        long feeSatoshis,
        long? coinValueSatoshis = null,
        long dustThresholdSatoshis = VintageCoinControl.DustThresholdSatoshis)
    {
        if (coinCount <= 0) return VintagePlanResult.Fail("Choose at least one output.");
        if (feeSatoshis < 0) return VintagePlanResult.Fail("Fee cannot be negative.");
        if (PayableAddress.Problem(recipient, "recipient") is { } badRecipient) return VintagePlanResult.Fail(badRecipient);
        if (minPurity is < 0 or > 1) return VintagePlanResult.Fail("The purity must be between 0 and 100 %.");

        var candidates = holdings.Where(h => h.Purity >= minPurity).ToList();
        if (candidates.Count == 0)
            return VintagePlanResult.Fail(
                $"No output is at least {minPurity:P0} {vintage.Label} by the explorer's model — lower the purity, or that lineage is spread thinly across mixed coins.");

        if (coinValueSatoshis is { } wanted)
        {
            candidates = candidates.Where(h => h.Satoshis == wanted).ToList();
            if (candidates.Count == 0)
                return VintagePlanResult.Fail($"No output of exactly {wanted:N0} satoshis is at least {minPurity:P0} {vintage.Label}.");
        }
        if (coinCount > candidates.Count)
            return VintagePlanResult.Fail(
                $"{candidates.Count:N0} output{(candidates.Count == 1 ? "" : "s")} qualif{(candidates.Count == 1 ? "ies" : "y")} — fewer than the {coinCount:N0} asked for.");

        var chosen = candidates.Take(coinCount).ToList();
        var total = chosen.Sum(c => c.Satoshis);
        var lineage = chosen.Sum(c => c.VintageSatoshis);
        if (feeSatoshis >= total)
            return VintagePlanResult.Fail($"The fee ({feeSatoshis:N0}) would consume the whole output ({total:N0} satoshis).");

        var sendAmount = total - feeSatoshis;
        var recipientFloor = DustPolicy.FloorFor(recipient, dustThresholdSatoshis);
        if (sendAmount < recipientFloor)
            return VintagePlanResult.Fail($"The recipient's output ({sendAmount:N0} satoshis) is below the {recipientFloor:N0}-satoshi dust limit.");

        var outputs = new List<PlannedOutput> { new(recipient, sendAmount, OutputRole.Vintage) };
        var share = total > 0 ? (double)lineage / total : 0;

        var warnings = new List<string>
        {
            $"By the explorer's PROPORTIONAL model these {chosen.Count:N0} output{(chosen.Count == 1 ? "" : "s")} carry " +
            $"{lineage:N0} of {total:N0} satoshis as {vintage.Label} lineage ({share:P1}). That is a share by value across " +
            "mixed ancestry, not a pedigree — this wallet cannot check it against its own trace, and the recipient's wallet " +
            "will see the same proportional figure on the explorer, not a contiguous run.",
        };
        if (feeSatoshis == 0)
            warnings.Add("Zero fee — the output arrives EXACTLY as held, satoshi for satoshi. Free relay makes this possible.");
        else
            warnings.Add($"A {feeSatoshis:N0}-satoshi fee is taken from the output itself, so it arrives short of its held value.");
        if (chosen.Count > 1)
            warnings.Add($"{chosen.Count} whole outputs are being sent as ONE output — they merge on arrival and cannot be separated again.");

        return VintagePlanResult.Ok(new VintageSendPlan(
            vintage.Height,
            new HashSet<long>(),   // no pedigree heights: nothing for the FIFO verifier to check
            chosen.Select(c => new PlannedInput(c.TxId, c.Vout, c.Satoshis)).ToList(),
            outputs, feeSatoshis, warnings));
    }

    /// <summary>Round-trips a wallet vintage key back to its bucket (years are plain numbers).</summary>
    private static ExplorerVintageKeys.Resolved? ResolveKey(string key)
    {
        if (int.TryParse(key, out var year) && year >= ExplorerVintageKeys.YearKeyMin)
            return ExplorerVintageKeys.Resolve(year);
        // Specials: find the explorer key that maps to this wallet key (small fixed set + series).
        foreach (var k in CandidateExplorerKeys())
        {
            var r = ExplorerVintageKeys.Resolve(k);
            if (r is not null && r.Vintage.Key == key) return r;
        }
        return null;
    }

    private static IEnumerable<int> CandidateExplorerKeys()
    {
        for (var k = 0; k <= 8; k++) yield return k;
        for (var k = ExplorerVintageKeys.HalvingKeyBase + 1; k < ExplorerVintageKeys.RepdigitKeyBase; k++) yield return k;
        for (var k = ExplorerVintageKeys.RepdigitKeyBase + 1; k < ExplorerVintageKeys.LeapKeyBase; k++) yield return k;
        for (var k = ExplorerVintageKeys.LeapKeyBase + 1; k < ExplorerVintageKeys.MilestoneKeyBase; k++) yield return k;
        for (var k = ExplorerVintageKeys.MilestoneKeyBase + 1; k < ExplorerVintageKeys.YearKeyMin; k++) yield return k;
    }
}

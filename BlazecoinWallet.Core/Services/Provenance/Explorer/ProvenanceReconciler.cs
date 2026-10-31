namespace BlazecoinWallet.Core.Services.Provenance.Explorer;

/// <summary>One vintage summed across every address the explorer was asked about.</summary>
public sealed record ExplorerVintageEntry(
    string Key, string Label, VintageKind Kind, long Height, long UnspentSatoshis, long EverHeldSatoshis);

/// <summary>
/// The explorer's view of a whole wallet: years and specials (by the explorer's
/// proportional model), totals, the lowest watermark among the addresses asked, and how
/// many rows carried a key this build could not name.
/// </summary>
public sealed record ExplorerVintageSummary(
    IReadOnlyList<ExplorerVintageEntry> Years,
    IReadOnlyList<ExplorerVintageEntry> Specials,
    long UnspentSatoshis,
    long EverHeldSatoshis,
    long ComputedToHeight,
    int AddressCount,
    int UnknownKeys);

/// <summary>Sums per-address explorer answers into one wallet-wide catalogue.</summary>
public static class ExplorerVintageAggregator
{
    public static ExplorerVintageSummary Aggregate(IEnumerable<ExplorerAddressVintage> perAddress)
    {
        var acc = new Dictionary<string, (ExplorerVintageKeys.Resolved Resolved, long Unspent, long Ever)>(StringComparer.Ordinal);
        long computedTo = long.MaxValue, unspentTotal = 0, everTotal = 0;
        int addresses = 0, unknown = 0;

        foreach (var answer in perAddress)
        {
            addresses++;
            if (answer.ComputedToHeight < computedTo) computedTo = answer.ComputedToHeight;
            foreach (var row in answer.Rows)
            {
                var resolved = ExplorerVintageKeys.Resolve(row.YearKey);
                if (resolved is null) { unknown++; continue; }
                unspentTotal += row.UnspentSatoshis;
                everTotal += row.EverHeldSatoshis;
                var key = resolved.Vintage.Key;
                acc[key] = acc.TryGetValue(key, out var cur)
                    ? (cur.Resolved, cur.Unspent + row.UnspentSatoshis, cur.Ever + row.EverHeldSatoshis)
                    : (resolved, row.UnspentSatoshis, row.EverHeldSatoshis);
            }
        }

        var entries = acc.Values
            .Select(v => new ExplorerVintageEntry(v.Resolved.Vintage.Key, v.Resolved.Vintage.Label, v.Resolved.Vintage.Kind,
                                                  v.Resolved.Height, v.Unspent, v.Ever))
            .ToList();

        var years = entries.Where(e => e.Kind == VintageKind.Year)
            .OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        var specials = entries.Where(e => e.Kind != VintageKind.Year)
            .OrderBy(e => e.Height).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();

        return new ExplorerVintageSummary(years, specials, unspentTotal, everTotal,
            addresses == 0 ? -1 : computedTo, addresses, unknown);
    }
}

/// <summary>How a vintage fared across the two sources.</summary>
public enum ReconcileStatus
{
    /// <summary>Both the explorer and the local walk found this lineage.</summary>
    Verified,
    /// <summary>Only the explorer reports it — the local walk never reached it (budget) or holds none of it.</summary>
    ExplorerOnly,
    /// <summary>Only the local walk reports it — usually coins newer than the explorer's watermark.</summary>
    LocalOnly,
}

/// <summary>A vintage with the explorer's figure, the local proportional figure, and the verdict.</summary>
public sealed record ReconciledVintage(
    string Key, string Label, VintageKind Kind, long Height,
    long? ExplorerSatoshis, long? LocalSatoshis, ReconcileStatus Status);

public sealed record ReconciledCatalogue(IReadOnlyList<ReconciledVintage> Years, IReadOnlyList<ReconciledVintage> Specials)
{
    public int VerifiedCount => Years.Count(r => r.Status == ReconcileStatus.Verified) + Specials.Count(r => r.Status == ReconcileStatus.Verified);
    public int ExplorerOnlyCount => Years.Count(r => r.Status == ReconcileStatus.ExplorerOnly) + Specials.Count(r => r.Status == ReconcileStatus.ExplorerOnly);
    public int LocalOnlyCount => Years.Count(r => r.Status == ReconcileStatus.LocalOnly) + Specials.Count(r => r.Status == ReconcileStatus.LocalOnly);
}

/// <summary>
/// Lays the explorer's catalogue beside the local walk's. The comparison uses the local
/// PROPORTIONAL (haircut) model because that is the explorer's own maths; the figures are
/// not expected to match exactly (a budget-cut local walk under-counts by design), so a
/// vintage counts as verified when both sources hold any of it, never on equal amounts.
/// </summary>
public static class ProvenanceReconciler
{
    public static ReconciledCatalogue Reconcile(ExplorerVintageSummary explorer, ProvenanceReport? local)
    {
        var localYears = Index(local?.Haircut.Years);
        var localSpecials = Index(local?.Haircut.Specials);
        return new ReconciledCatalogue(
            Merge(explorer.Years, localYears, byHeight: false),
            Merge(explorer.Specials, localSpecials, byHeight: true));
    }

    private static Dictionary<string, VintageRow> Index(IEnumerable<VintageRow>? rows)
        => rows is null
            ? new Dictionary<string, VintageRow>(StringComparer.Ordinal)
            : rows.Where(r => r.Satoshis > 0).ToDictionary(r => r.Key, StringComparer.Ordinal);

    private static List<ReconciledVintage> Merge(IReadOnlyList<ExplorerVintageEntry> explorer, Dictionary<string, VintageRow> local, bool byHeight)
    {
        var rows = new List<ReconciledVintage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in explorer)
        {
            seen.Add(e.Key);
            if (local.TryGetValue(e.Key, out var l))
                rows.Add(new ReconciledVintage(e.Key, e.Label, e.Kind, e.Height, e.UnspentSatoshis, l.Satoshis,
                    e.UnspentSatoshis > 0 ? ReconcileStatus.Verified : ReconcileStatus.LocalOnly));
            else if (e.UnspentSatoshis > 0)
                rows.Add(new ReconciledVintage(e.Key, e.Label, e.Kind, e.Height, e.UnspentSatoshis, null, ReconcileStatus.ExplorerOnly));
        }

        foreach (var l in local.Values)
        {
            if (seen.Contains(l.Key)) continue;
            rows.Add(new ReconciledVintage(l.Key, l.Label, l.Kind, l.FirstHeight, null, l.Satoshis, ReconcileStatus.LocalOnly));
        }

        return byHeight
            ? rows.OrderBy(r => r.Height).ThenBy(r => r.Key, StringComparer.Ordinal).ToList()
            : rows.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }
}

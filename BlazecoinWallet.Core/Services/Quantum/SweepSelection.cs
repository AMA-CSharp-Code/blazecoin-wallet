using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>
/// The coins a sweep will move, as chosen on the Quantum Exposure page: one address's outputs, or
/// every legacy output in the wallet. Post-quantum rows are never part of a selection — they have
/// nothing to move to.
/// </summary>
public sealed record SweepSelection(string Title, IReadOnlyList<UnspentOutput> Outputs, bool ContainsExposed)
{
    public long Satoshis => Outputs.Sum(o => o.Satoshis);
    public int OutputCount => Outputs.Count;

    /// <summary>One address's outputs. Null for a post-quantum row.</summary>
    public static SweepSelection? FromAddress(AddressExposure a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Status == ExposureStatus.PostQuantum || a.Outputs.Count == 0) return null;
        return new SweepSelection(a.Address, a.Outputs, a.Status == ExposureStatus.Exposed);
    }

    /// <summary>Every legacy output the scan found (exposed and unexposed). Null when there are none.</summary>
    public static SweepSelection? AllLegacy(ExposureReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = report.Addresses.Where(a => a.Status != ExposureStatus.PostQuantum && a.Outputs.Count > 0).ToList();
        if (rows.Count == 0) return null;
        return new SweepSelection(
            $"every legacy address ({rows.Count:N0})",
            rows.SelectMany(a => a.Outputs).ToList(),
            rows.Any(a => a.Status == ExposureStatus.Exposed));
    }
}

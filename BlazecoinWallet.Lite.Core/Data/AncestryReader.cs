namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Optional gateway capability (the <see cref="IHeaderReader"/> pattern): the
/// mint-ancestry catalogue the Lite.Gateway computes server-side with the desktop
/// wallet's AncestryTracer. Resolve with <c>as IAncestryReader</c> — the personal-node
/// data service deliberately does NOT implement it, because a personal node's owner has
/// a full node, where the desktop Provenance page traces locally and trustlessly.
///
/// Trust posture: unlike balances (C1/M3-verified), ancestry is TRUSTED display data —
/// a lying gateway could mislabel lineage, but it can never move funds.
/// </summary>
public interface IAncestryReader
{
    Task<LiteAncestryResult> GetAncestryAsync(string address, CancellationToken ct = default);
}

/// <summary>Success carries the report; failure carries a user-showable reason (the
/// server names its refusals: busy, syncing, too many outputs).</summary>
public sealed record LiteAncestryResult(LiteAncestryReport? Report, string? Error)
{
    public static LiteAncestryResult Ok(LiteAncestryReport report) => new(report, null);
    public static LiteAncestryResult Fail(string error) => new(null, error);
}

/// <summary>One catalogue row — a year, one of its months, or a special single block.</summary>
public sealed record LiteVintageRow(
    string Key, string Label, string Kind, long Satoshis, int OutputCount,
    long FirstHeight, DateTime FirstTimeUtc, DateTime LastTimeUtc);

/// <summary>One accounting model's catalogue (FIFO pedigree or proportional haircut).</summary>
public sealed record LiteVintageModel(
    long AttributedSatoshis,
    IReadOnlyList<LiteVintageRow> Years,
    IReadOnlyList<LiteVintageRow> Months,
    IReadOnlyList<LiteVintageRow> Specials);

/// <summary>The gateway's trace of ONE address; the page merges these across the
/// wallet's funded addresses. <paramref name="Truncated"/> means the server's read
/// budget was exhausted mid-walk — unwalked ancestry counted as unproven, so every
/// figure is a floor (older gateways omit the field → false).</summary>
public sealed record LiteAncestryReport(
    string Address, long TotalSatoshis, int OutputCount, int TransactionsRead,
    LiteVintageModel Fifo, LiteVintageModel Haircut, bool Truncated = false);

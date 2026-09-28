using System.Globalization;

namespace BlazecoinWallet.Core.Services.Provenance;

/// <summary>Which collector bucket a minting block falls in.</summary>
public enum VintageKind
{
    /// <summary>An ordinary block — the vintage is its calendar year (and month).</summary>
    Year,
    Genesis,
    Premine,
    FirstStrike,
    Block413,
    PiBlock,
    Repdigit,
    Halving,
    Milestone,
    LeapDay,
    Resurrection,
    FirstV2,
    /// <summary>Block 4,194,001 — the first block mined under the Phoenix-413 per-block
    /// ASERT retarget (Era 3, activated 2026-08-26). "The Rekindling."</summary>
    Phoenix413,
    /// <summary>Block 4,250,880 — the first block whose coinbase pays a post-quantum
    /// BQ (P2PQH, ML-DSA-44) address, ~7 h after the PQ hard fork at H_Q = 4,250,000.
    /// "Quantum Epoch."</summary>
    QuantumEpoch,
}

/// <summary>
/// Classifies a minting block into its vintage. The rules MIRROR the website's engine
/// (Blazecoin_Indexer_API VintageYears/ComputeYearKey) so wallet provenance and the
/// site's collector board agree on what a coin is; height-based specials deliberately
/// outrank the Feb-29 date check (a halving mined on a leap day stays a halving).
/// Labels here are deliberately plain and factual — the wallet page is a catalogue,
/// not a badge wall.
/// </summary>
public static class VintageClassifier
{
    public const long GenesisHeight = 0;
    public const long PremineHeight = 1;
    public const long FirstStrikeHeight = 2;
    public const long Block413Height = 413;
    public const long PiBlockHeight = 3_141_592;
    public const long ResurrectionHeight = 3_712_545;
    public const long FirstV2Height = 4_113_625;
    public const long Phoenix413Height = 4_194_001;
    public const long QuantumEpochHeight = 4_250_880;
    public const long HalvingIntervalBlocks = 1_051_200;
    public const long MilestoneIntervalBlocks = 500_000;
    public const long RepdigitIntervalBlocks = 1_111_111;

    /// <summary>The bucket + a stable key. Blocks sharing a key aggregate into one row.</summary>
    public static MintVintage Classify(long height, DateTime timeUtc)
    {
        if (height == GenesisHeight) return new(VintageKind.Genesis, "genesis", "Genesis (unspendable)");
        if (height == PremineHeight) return new(VintageKind.Premine, "premine", "Premine");
        if (height == FirstStrikeHeight) return new(VintageKind.FirstStrike, "first-strike", "First Strike");
        if (height == FirstV2Height) return new(VintageKind.FirstV2, "first-v2", "First V2 Block");
        if (height == ResurrectionHeight) return new(VintageKind.Resurrection, "resurrection", "The Resurrection");
        if (height == Block413Height) return new(VintageKind.Block413, "block-413", "Block 413");
        if (height == PiBlockHeight) return new(VintageKind.PiBlock, "pi-block", "The Pi Block");
        if (height == Phoenix413Height) return new(VintageKind.Phoenix413, "phoenix-413", "Phoenix-413 — The Rekindling");
        if (height == QuantumEpochHeight) return new(VintageKind.QuantumEpoch, "quantum-epoch", "Quantum Epoch");

        if (height > 0 && height % RepdigitIntervalBlocks == 0)
        {
            var d = height / RepdigitIntervalBlocks;
            return new(VintageKind.Repdigit, $"repdigit-{d}", $"Repdigit — Block {height:N0}");
        }
        if (height > 0 && height % HalvingIntervalBlocks == 0)
        {
            var n = height / HalvingIntervalBlocks;
            return new(VintageKind.Halving, $"halving-{n}", $"Halving {Roman(n)}");
        }
        if (height > 0 && height % MilestoneIntervalBlocks == 0)
            return new(VintageKind.Milestone, $"block-{height}", $"Block {height:N0}");

        if (timeUtc is { Month: 2, Day: 29 })
            return new(VintageKind.LeapDay, $"leap-{timeUtc.Year}", $"Leap Day {timeUtc.Year}");

        return new(VintageKind.Year, timeUtc.Year.ToString(CultureInfo.InvariantCulture), timeUtc.Year.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>True for every bucket that isn't a plain calendar year.</summary>
    public static bool IsSpecial(VintageKind kind) => kind != VintageKind.Year;

    internal static string Roman(long n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V",
        6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", 10 => "X",
        _ => n.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>The vintage a minting block belongs to: its kind, aggregation key and label.</summary>
public sealed record MintVintage(VintageKind Kind, string Key, string Label);

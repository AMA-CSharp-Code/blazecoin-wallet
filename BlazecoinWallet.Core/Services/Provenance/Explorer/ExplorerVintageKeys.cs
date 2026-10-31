using System.Globalization;

namespace BlazecoinWallet.Core.Services.Provenance.Explorer;

/// <summary>
/// Translates the explorer's numeric vintage keys into the wallet's own
/// <see cref="MintVintage"/> buckets, so a row the explorer reports lands on the same key
/// the local tracer would have produced and the two can be laid side by side.
/// Mirrors the Indexer's <c>VintageYears</c>: 0–8 are one-off blocks, 9+N a halving,
/// 50+d a repdigit, 60+i a leap day, 100+m a 500,000-block milestone, and anything from
/// 2000 up is a calendar year. If the Indexer adds a bucket this map does not know, the
/// row is reported as unknown rather than guessed.
/// </summary>
public static class ExplorerVintageKeys
{
    public const int PremineKey = 0;
    public const int FirstV2Key = 1;
    public const int FirstStrikeKey = 2;
    public const int ResurrectionKey = 3;
    public const int GenesisKey = 4;
    public const int Block413Key = 5;
    public const int PiKey = 6;
    public const int Phoenix413Key = 7;
    public const int QuantumEpochKey = 8;
    public const int HalvingKeyBase = 9;
    public const int RepdigitKeyBase = 50;
    public const int LeapKeyBase = 60;
    public const int MilestoneKeyBase = 100;
    public const int YearKeyMin = 2000;
    public const int LeapEpochYear = 2012;

    /// <summary>First block mined on each leap day the chain has seen (chain facts, as the Indexer pads its catalogue).</summary>
    private static readonly IReadOnlyDictionary<int, long> LeapFirstHeights = new Dictionary<int, long>
    {
        [2016] = 1_254_539,
        [2020] = 2_565_317,
    };

    /// <summary>A resolved explorer key: the wallet's bucket plus the block it names (0 for a year or an unknown leap day).</summary>
    public sealed record Resolved(MintVintage Vintage, long Height);

    /// <summary>Resolves an explorer key, or null when the key is not one this build understands.</summary>
    public static Resolved? Resolve(int key)
    {
        switch (key)
        {
            case GenesisKey: return Single(VintageClassifier.GenesisHeight);
            case PremineKey: return Single(VintageClassifier.PremineHeight);
            case FirstStrikeKey: return Single(VintageClassifier.FirstStrikeHeight);
            case ResurrectionKey: return Single(VintageClassifier.ResurrectionHeight);
            case Block413Key: return Single(VintageClassifier.Block413Height);
            case PiKey: return Single(VintageClassifier.PiBlockHeight);
            case FirstV2Key: return Single(VintageClassifier.FirstV2Height);
            case Phoenix413Key: return Single(VintageClassifier.Phoenix413Height);
            case QuantumEpochKey: return Single(VintageClassifier.QuantumEpochHeight);
        }

        if (key > HalvingKeyBase && key < RepdigitKeyBase)
            return Single((key - HalvingKeyBase) * VintageClassifier.HalvingIntervalBlocks);

        if (key > RepdigitKeyBase && key < LeapKeyBase)
            return Single((key - RepdigitKeyBase) * VintageClassifier.RepdigitIntervalBlocks);

        if (key > LeapKeyBase && key < MilestoneKeyBase)
        {
            var year = LeapEpochYear + (key - LeapKeyBase) * 4;
            var height = LeapFirstHeights.GetValueOrDefault(year, 0L);
            return new Resolved(new MintVintage(VintageKind.LeapDay, $"leap-{year}", $"Leap Day {year}"), height);
        }

        if (key > MilestoneKeyBase && key < YearKeyMin)
            return Single((key - MilestoneKeyBase) * VintageClassifier.MilestoneIntervalBlocks);

        if (key >= YearKeyMin)
        {
            var text = key.ToString(CultureInfo.InvariantCulture);
            return new Resolved(new MintVintage(VintageKind.Year, text, text), 0);
        }

        return null;
    }

    /// <summary>
    /// A single-block bucket classified by the wallet's own rules, so its key and label can
    /// never drift from what the local tracer shows for the same block. Height-based
    /// specials ignore the timestamp, so a placeholder date is safe here.
    /// </summary>
    private static Resolved Single(long height)
        => new(VintageClassifier.Classify(height, DateTime.UnixEpoch), height);
}

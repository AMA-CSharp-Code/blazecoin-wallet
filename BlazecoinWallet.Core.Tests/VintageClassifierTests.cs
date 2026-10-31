using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Vintage classification. These rules MIRROR the website engine's buckets
/// (Blazecoin_Indexer_API VintageYears/ComputeYearKey) — if they drift, the wallet and
/// the site disagree about what a coin is. Pins the precedence too: height-based
/// specials outrank the Feb-29 date check.
/// </summary>
public class VintageClassifierTests
{
    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 2014, 5, 18, VintageKind.Genesis, "genesis")]
    [InlineData(1, 2014, 5, 24, VintageKind.Premine, "premine")]
    [InlineData(2, 2014, 5, 24, VintageKind.FirstStrike, "first-strike")]
    [InlineData(413, 2014, 5, 24, VintageKind.Block413, "block-413")]
    [InlineData(3_141_592, 2022, 1, 5, VintageKind.PiBlock, "pi-block")]
    [InlineData(3_712_545, 2024, 6, 28, VintageKind.Resurrection, "resurrection")]
    [InlineData(4_113_625, 2026, 6, 5, VintageKind.FirstV2, "first-v2")]
    [InlineData(4_194_001, 2026, 8, 26, VintageKind.Phoenix413, "phoenix-413")]
    [InlineData(4_250_880, 2026, 9, 16, VintageKind.QuantumEpoch, "quantum-epoch")]
    [InlineData(1_051_200, 2015, 8, 14, VintageKind.Halving, "halving-1")]
    [InlineData(3_153_600, 2022, 1, 17, VintageKind.Halving, "halving-3")]
    [InlineData(500_000, 2014, 11, 25, VintageKind.Milestone, "block-500000")]
    [InlineData(4_000_000, 2026, 2, 16, VintageKind.Milestone, "block-4000000")]
    [InlineData(1_111_111, 2015, 11, 1, VintageKind.Repdigit, "repdigit-1")]
    [InlineData(3_333_333, 2022, 8, 30, VintageKind.Repdigit, "repdigit-3")]
    public void special_blocks_get_their_buckets(long height, int y, int m, int d, VintageKind kind, string key)
    {
        var v = VintageClassifier.Classify(height, Utc(y, m, d));
        Assert.Equal(kind, v.Kind);
        Assert.Equal(key, v.Key);
        Assert.True(VintageClassifier.IsSpecial(v.Kind));
    }

    [Theory]
    [InlineData(2016, "leap-2016", "Leap Day 2016")]
    [InlineData(2020, "leap-2020", "Leap Day 2020")]
    public void a_february_29_block_is_a_leap_day_vintage(int year, string key, string label)
    {
        var v = VintageClassifier.Classify(1_254_539, new DateTime(year, 2, 29, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(VintageKind.LeapDay, v.Kind);
        Assert.Equal(key, v.Key);
        Assert.Equal(label, v.Label);
    }

    [Fact]
    public void a_height_special_outranks_the_leap_day_date()
    {
        // A halving that happens to land on Feb 29 stays a halving.
        var v = VintageClassifier.Classify(2_102_400, new DateTime(2020, 2, 29, 18, 0, 0, DateTimeKind.Utc));
        Assert.Equal(VintageKind.Halving, v.Kind);
        Assert.Equal("halving-2", v.Key);
        Assert.Equal("Halving II", v.Label);
    }

    [Fact]
    public void an_ordinary_block_is_just_its_year()
    {
        var v = VintageClassifier.Classify(3_712_544, Utc(2024, 6, 28));
        Assert.Equal(VintageKind.Year, v.Kind);
        Assert.Equal("2024", v.Key);
        Assert.False(VintageClassifier.IsSpecial(v.Kind));
    }

    [Fact]
    public void genesis_is_classified_even_though_nobody_can_ever_hold_it()
    {
        // The block-0 coinbase never enters the UTXO set, so this bucket can only ever
        // appear as a label — never with satoshis behind it.
        var v = VintageClassifier.Classify(0, Utc(2014, 5, 18));
        Assert.Equal(VintageKind.Genesis, v.Kind);
        Assert.Contains("unspendable", v.Label);
    }
}

using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Display-unit format + parse. The wallet's internal amount is always satoshis; these only
/// translate at the UI edge, so a round trip (parse then format) must be lossless and a unit
/// change can never alter what's actually signed.
/// </summary>
public class LiteUnitsTests
{
    [Theory]
    [InlineData(5_000_000L, LiteUnit.Blz, "0.05")]
    [InlineData(5_000_000L, LiteUnit.MilliBlz, "50")]
    [InlineData(5_000_000L, LiteUnit.Sat, "5000000")]
    [InlineData(100_000_000L, LiteUnit.Blz, "1")]
    [InlineData(1L, LiteUnit.Sat, "1")]
    public void Format_renders_the_amount_in_the_unit(long sats, LiteUnit unit, string expected)
        => Assert.Equal(expected, LiteUnits.Format(sats, unit));

    [Theory]
    [InlineData("0.05", LiteUnit.Blz, 5_000_000L)]
    [InlineData("50", LiteUnit.MilliBlz, 5_000_000L)]
    [InlineData("5000000", LiteUnit.Sat, 5_000_000L)]
    public void Parse_converts_a_unit_amount_to_satoshis(string text, LiteUnit unit, long expected)
    {
        Assert.True(LiteUnits.TryParseToSats(text, unit, out var sats));
        Assert.Equal(expected, sats);
    }

    [Theory]
    [InlineData("5.5", LiteUnit.Sat)]        // fractional satoshi is meaningless
    [InlineData("0", LiteUnit.Blz)]          // zero / non-positive
    [InlineData("-1", LiteUnit.Blz)]
    [InlineData("abc", LiteUnit.Blz)]
    [InlineData("999999999999", LiteUnit.Blz)] // above the money supply
    public void Parse_rejects_bad_amounts(string text, LiteUnit unit)
        => Assert.False(LiteUnits.TryParseToSats(text, unit, out _));

    [Theory]
    [InlineData(LiteUnit.Blz)]
    [InlineData(LiteUnit.MilliBlz)]
    [InlineData(LiteUnit.Sat)]
    public void Format_then_parse_is_lossless(LiteUnit unit)
    {
        const long sats = 123_456_789L;
        Assert.True(LiteUnits.TryParseToSats(LiteUnits.Format(sats, unit), unit, out var back));
        Assert.Equal(sats, back);
    }

    [Fact]
    public void Symbols_are_stable()
    {
        Assert.Equal("BLZ", LiteUnits.Symbol(LiteUnit.Blz));
        Assert.Equal("mBLZ", LiteUnits.Symbol(LiteUnit.MilliBlz));
        Assert.Equal("sat", LiteUnits.Symbol(LiteUnit.Sat));
    }
}

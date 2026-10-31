using System.Globalization;
using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Tests for <see cref="AmountFormat"/> — the BLZ/mBLZ display formatter used by the
/// amount cells. Pins the unit conversion (×1000 for mBLZ), the decimal-place control, and the
/// use of invariant culture (so a German/French locale can't inject a comma decimal separator).</summary>
public class AmountFormatTests
{
    [Fact]
    public void Formats_blz_with_default_decimals()
        => Assert.Equal("1.23456789 BLZ", AmountFormat.Format(1.23456789m, "BLZ", 8));

    [Fact]
    public void mBLZ_multiplies_by_1000_and_suffixes()
        => Assert.Equal("1,234.56789000 mBLZ", AmountFormat.Format(1.23456789m, "mBLZ", 8));

    [Theory]
    [InlineData(0, "1 BLZ")]
    [InlineData(2, "1.00 BLZ")]
    [InlineData(4, "1.0000 BLZ")]
    public void Honours_the_decimal_place_setting(int decimals, string expected)
        => Assert.Equal(expected, AmountFormat.Format(1m, "BLZ", decimals));

    [Fact]
    public void Unknown_unit_falls_back_to_blz()
        => Assert.Equal("5.00000000 BLZ", AmountFormat.Format(5m, "satoshi", 8));

    [Fact]
    public void Uses_invariant_culture_regardless_of_current_thread_culture()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // comma decimals
            Assert.Equal("1,234.50000000 BLZ", AmountFormat.Format(1234.5m, "BLZ", 8));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }

    [Fact]
    public void Defaults_are_blz_and_eight_decimals()
    {
        Assert.Equal("BLZ", AmountFormat.DefaultUnit);
        Assert.Equal(8, AmountFormat.DefaultDecimals);
    }
}

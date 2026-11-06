using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// <see cref="AmountText"/> — the one amount reader for typed amounts and payment links
/// (bug-bounty report 2026-11-06: the lenient number parse read "1,5" as 15). Pins the narrow
/// grammar: digits and at most one decimal mark; a separator is never a thousands separator.
/// </summary>
public class AmountTextTests
{
    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("1,5", "1.5")]          // the reported case: one and a half, not fifteen
    [InlineData("0,5", "0.5")]
    [InlineData("2,5", "2.5")]
    [InlineData("1,500", "1.5")]        // a lone comma is ALWAYS the decimal mark
    [InlineData("1.500", "1.5")]
    [InlineData("15", "15")]
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5")]
    [InlineData("  3.25 ", "3.25")]     // outer whitespace only
    [InlineData("0.00000001", "0.00000001")]
    public void Typed_amount_reads_one_decimal_mark(string text, string expected)
    {
        Assert.True(AmountText.TryParseTyped(text, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("1,234.5")]             // two marks — which is the decimal one? refuse
    [InlineData("1.234,5")]
    [InlineData("1,2,3")]
    [InlineData("1..5")]
    [InlineData("1 000")]
    [InlineData("1'000")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData(".")]
    [InlineData(",")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("١٥")]                  // non-ASCII digits are not amounts
    [InlineData("111111111111111111111111111111111")] // longer than any real amount
    public void Typed_amount_refuses_everything_else(string text)
        => Assert.False(AmountText.TryParseTyped(text, out _));

    [Fact]
    public void Typed_amount_refuses_null() => Assert.False(AmountText.TryParseTyped(null, out _));

    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("15", "15")]
    [InlineData("0.00000001", "0.00000001")]
    public void Payment_link_amount_reads_a_dot_decimal(string text, string expected)
    {
        Assert.True(AmountText.TryParsePaymentLink(text, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("1,5")]                 // BIP21 has no comma: malformed, never repaired to 1.5 or 15
    [InlineData("1,500")]
    [InlineData("1,234.5")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("")]
    public void Payment_link_amount_refuses_a_comma_and_anything_odd(string text)
        => Assert.False(AmountText.TryParsePaymentLink(text, out _));

    [Fact]
    public void A_comma_never_multiplies_the_amount()
    {
        // The property behind the report: no accepted text may read as more than the same
        // text with its comma taken as a decimal point.
        foreach (var text in new[] { "1,5", "0,5", "2,5", "12,75", "1,500", "100,1" })
        {
            Assert.True(AmountText.TryParseTyped(text, out var value));
            var asDecimalPoint = decimal.Parse(text.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(asDecimalPoint, value);
        }
    }
}

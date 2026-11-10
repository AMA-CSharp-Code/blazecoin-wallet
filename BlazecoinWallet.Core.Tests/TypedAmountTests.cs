using System.Globalization;
using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The desktop wallet's typed amount fields (bug-bounty report 2026-11-09). The Send page's
/// amount was a browser number field, which silently drops a decimal comma on an
/// English-language machine: "1,5" became 15. The field is now text, read by one grammar.
/// </summary>
public class TypedAmountTests
{
    private static TypedAmount Typed(string? text)
    {
        var a = new TypedAmount();
        a.SetText(text);
        return a;
    }

    [Theory]
    [InlineData("1,5", "1.5")]            // the report: was 15
    [InlineData("0,5", "0.5")]
    [InlineData("2,25", "2.25")]
    [InlineData("100,25", "100.25")]
    [InlineData("0,00000001", "0.00000001")]   // one satoshi, not one coin
    [InlineData("1.5", "1.5")]
    [InlineData("1,500", "1.5")]          // never a thousands separator: a lone comma is the decimal mark
    [InlineData(" 7 ", "7")]
    [InlineData("1.50", "1.5")]
    public void A_comma_or_a_dot_is_the_decimal_mark(string typed, string expected)
    {
        var a = Typed(typed);

        Assert.False(a.IsInvalid);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), a.Value);
        Assert.Equal(expected, a.Text);                        // the field shows what was understood
    }

    [Theory]
    [InlineData("1,234.5")]               // two marks: which one is the decimal?
    [InlineData("1.234,5")]
    [InlineData("1 5")]
    [InlineData("1e3")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("abc")]
    [InlineData(",")]
    [InlineData("0.123456789")]           // a ninth decimal place cannot be paid
    [InlineData("1'000")]
    public void Anything_else_is_refused_out_loud_and_never_becomes_a_number(string typed)
    {
        var a = Typed(typed);

        Assert.True(a.IsInvalid);
        Assert.Null(a.Value);                                  // nothing to review, nothing to send
        Assert.Equal(typed.Trim(), a.Text);                    // left as typed so it can be fixed
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_field_is_empty_not_wrong(string? typed)
    {
        var a = Typed(typed);

        Assert.False(a.IsInvalid);
        Assert.Null(a.Value);
        Assert.Equal("", a.Text);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("id-ID")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void The_machine_locale_changes_nothing(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal(1.5m, Typed("1,5").Value);
            Assert.Equal(1.5m, Typed("1.5").Value);
            Assert.Equal(0.00000001m, Typed("0.00000001").Value);
            var set = new TypedAmount();
            set.SetValue(1234.5m);
            Assert.Equal("1234.5", set.Text);                  // no locale grouping or comma on the way out
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void A_value_set_by_the_app_shows_in_the_field_and_clears_an_error()
    {
        var a = Typed("abc");

        a.SetValue(0.00000001m);

        Assert.False(a.IsInvalid);
        Assert.Equal("0.00000001", a.Text);                    // not 1E-8
        Assert.Equal(0.00000001m, a.Value);

        a.SetValue(null);
        Assert.Equal("", a.Text);
        Assert.Null(a.Value);
    }

    [Fact]
    public void A_bad_edit_after_a_good_amount_leaves_no_amount_behind()
    {
        var a = Typed("1.5");

        a.SetText("1.5.");

        Assert.True(a.IsInvalid);
        Assert.Null(a.Value);                                  // the old 1.5 must not be what gets sent
    }
}

using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// One payment-link reader for both wallets (audit 2026-11-09). The desktop Send page had its own
/// inline copy: it silently DROPPED an <c>amount=</c> it could not read and had no upper bound.
/// These drive the desktop options; the lite wallet's own tests (LitePaymentLinkTests) drive the
/// same code through its options.
/// </summary>
public class PaymentLinkTests
{
    private static readonly string Addr = TestAddr.Recipient;
    private static PaymentRequest Desktop(string text) => PaymentLink.Parse(text, PaymentLinkOptions.Desktop);

    [Fact]
    public void A_bare_address_is_the_destination()
    {
        var r = Desktop($"  {Addr} ");

        Assert.Equal(new PaymentRequest(Addr, null, false, null), r);
    }

    [Theory]
    [InlineData("blazecoin:{0}?amount=1.5", 150_000_000L)]
    [InlineData("BLAZECOIN:{0}?label=shop&amount=15", 1_500_000_000L)]
    [InlineData("blz:{0}?amount=0.00000001", 1L)]
    [InlineData("bitcoin:{0}?amount=2", 200_000_000L)]
    [InlineData("blazecoin:{0}?amount=2065000000", 206_500_000_000_000_000L)]   // the whole supply, and not a satoshi more
    public void A_well_formed_amount_is_honoured(string template, long expectedSats)
    {
        var r = Desktop(string.Format(template, Addr));

        Assert.Equal(Addr, r.Destination);
        Assert.Equal(expectedSats, r.AmountSatoshis);
        Assert.False(r.AmountRejected);
    }

    [Theory]
    [InlineData("blazecoin:{0}?amount=1,5")]            // was: dropped without a word
    [InlineData("blazecoin:{0}?amount=1,234.5")]
    [InlineData("blazecoin:{0}?amount=-1")]
    [InlineData("blazecoin:{0}?amount=0")]
    [InlineData("blazecoin:{0}?amount=abc")]
    [InlineData("blazecoin:{0}?amount=")]
    [InlineData("blazecoin:{0}?amount=0.000000001")]    // finer than a satoshi (was: taken, and rounded later)
    [InlineData("blazecoin:{0}?amount=2065000000.00000001")] // above the money supply (was: taken)
    [InlineData("blazecoin:{0}?amount=1.5&amount=1,5")] // one bad amount poisons the request (was: 1.5)
    [InlineData("blazecoin:{0}?amount=1%2C5")]          // the comma, percent-encoded
    public void A_malformed_amount_is_reported_not_dropped(string template)
    {
        var r = Desktop(string.Format(template, Addr));

        Assert.Equal(Addr, r.Destination);              // the address still arrives
        Assert.Null(r.AmountSatoshis);
        Assert.True(r.AmountRejected);                  // was: nothing said - the field kept whatever it held
    }

    [Fact]
    public void A_label_or_a_message_comes_along_percent_decoded()
    {
        Assert.Equal("Corner shop", Desktop($"blazecoin:{Addr}?amount=1&label=Corner%20shop").Label);
        Assert.Equal("rent", Desktop($"blazecoin:{Addr}?message=rent").Label);
        Assert.Equal("first", Desktop($"blazecoin:{Addr}?label=first&message=second").Label);
        Assert.Equal(100_000_000, Desktop($"blazecoin:{Addr}?am%6Funt=1").AmountSatoshis);   // keys are decoded too
    }

    [Fact]
    public void A_scheme_that_is_not_ours_is_not_a_link()
    {
        var text = $"litecoin:{Addr}?amount=5";

        Assert.Equal(new PaymentRequest(text, null, false, null), Desktop(text));   // left whole, for the address check to refuse
    }

    [Fact]
    public void The_two_wallets_differ_only_in_what_their_options_say()
    {
        var lite = new PaymentLinkOptions(new[] { "blazecoin" }, PercentDecode: false, PaymentLink.MaxMoneySatoshis);

        Assert.Equal(150_000_000, PaymentLink.Parse($"blazecoin:{Addr}?amount=1.5", lite).AmountSatoshis);
        Assert.True(PaymentLink.Parse($"blazecoin:{Addr}?amount=1,5", lite).AmountRejected);
        // Not the lite wallet's scheme, and it does not decode: its answers, by its options alone.
        Assert.Equal($"blz:{Addr}?amount=1.5", PaymentLink.Parse($"blz:{Addr}?amount=1.5", lite).Destination);
        Assert.Null(PaymentLink.Parse($"blazecoin:{Addr}?am%6Funt=1", lite).AmountSatoshis);
        Assert.Equal(new[] { "blazecoin", "blz", "bitcoin" }, PaymentLinkOptions.Desktop.Schemes);
        Assert.True(PaymentLinkOptions.Desktop.PercentDecode);
    }
}

using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// <see cref="LitePaymentLink"/> — the scanned/pasted payment request reader, and the amount
/// parse it shares with the amount box (bug-bounty report 2026-11-06: "1,5" was read as 15 in
/// both places).
/// </summary>
public class LitePaymentLinkTests
{
    private const string Addr = "Bgn3jBCv6MiVaKWyMHsgUfBi266fkZs5sM";

    [Fact]
    public void A_bare_address_is_the_destination()
    {
        var r = LitePaymentLink.Parse($"  {Addr} ");
        Assert.Equal(Addr, r.Destination);
        Assert.Null(r.AmountSatoshis);
        Assert.False(r.AmountRejected);
    }

    [Theory]
    [InlineData("blazecoin:{0}?amount=1.5", 150_000_000L)]
    [InlineData("BLAZECOIN:{0}?label=shop&amount=15", 1_500_000_000L)]
    [InlineData("blazecoin:{0}?amount=0.00000001", 1L)]
    public void A_well_formed_amount_is_honoured(string template, long expectedSats)
    {
        var r = LitePaymentLink.Parse(string.Format(template, Addr));
        Assert.Equal(Addr, r.Destination);
        Assert.Equal(expectedSats, r.AmountSatoshis);
        Assert.False(r.AmountRejected);
    }

    [Theory]
    [InlineData("blazecoin:{0}?amount=1,5")]            // the reported case — was 15 BLZ
    [InlineData("blazecoin:{0}?amount=1,500")]
    [InlineData("blazecoin:{0}?amount=1,234.5")]
    [InlineData("blazecoin:{0}?amount=-1")]
    [InlineData("blazecoin:{0}?amount=0")]
    [InlineData("blazecoin:{0}?amount=abc")]
    [InlineData("blazecoin:{0}?amount=")]
    [InlineData("blazecoin:{0}?amount=0.000000001")]    // finer than a satoshi
    [InlineData("blazecoin:{0}?amount=99999999999999")] // above the money supply
    [InlineData("blazecoin:{0}?amount=1.5&amount=1,5")] // one bad amount poisons the request
    public void A_malformed_amount_is_rejected_not_repaired(string template)
    {
        var r = LitePaymentLink.Parse(string.Format(template, Addr));
        Assert.Equal(Addr, r.Destination);                // the address still arrives
        Assert.Null(r.AmountSatoshis);
        Assert.True(r.AmountRejected);
    }

    [Fact]
    public void A_link_without_an_amount_rejects_nothing()
    {
        var r = LitePaymentLink.Parse($"blazecoin:{Addr}?label=coffee");
        Assert.Equal(Addr, r.Destination);
        Assert.Null(r.AmountSatoshis);
        Assert.False(r.AmountRejected);
    }

    // ── the amount box (LiteUnits.TryParseToSats) ──

    [Theory]
    [InlineData("1,5", LiteUnit.Blz, 150_000_000L)]       // was 1,500,000,000 (15 BLZ)
    [InlineData("0,5", LiteUnit.Blz, 50_000_000L)]        // was 5 BLZ
    [InlineData("1,500", LiteUnit.Blz, 150_000_000L)]     // was 1,500 BLZ
    [InlineData("1,5", LiteUnit.MilliBlz, 150_000L)]
    [InlineData("1.5", LiteUnit.Blz, 150_000_000L)]
    public void A_typed_comma_is_the_decimal_mark(string text, LiteUnit unit, long expectedSats)
    {
        Assert.True(LiteUnits.TryParseToSats(text, unit, out var sats));
        Assert.Equal(expectedSats, sats);
    }

    [Theory]
    [InlineData("1,234.5", LiteUnit.Blz)]   // was 1,234.5 BLZ
    [InlineData("1,5", LiteUnit.Sat)]       // 1.5 sat is meaningless — was 15 sat
    [InlineData("1 000", LiteUnit.Blz)]
    [InlineData("1e3", LiteUnit.Blz)]
    public void A_typed_amount_with_a_thousands_separator_is_refused(string text, LiteUnit unit)
        => Assert.False(LiteUnits.TryParseToSats(text, unit, out _));
}

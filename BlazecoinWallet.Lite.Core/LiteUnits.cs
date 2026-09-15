using System.Globalization;

namespace BlazecoinWallet.Lite;

/// <summary>The display unit for amounts. All wallet math stays in satoshis; this is display only.</summary>
public enum LiteUnit
{
    /// <summary>1 BLZ = 100,000,000 sat (8 decimals).</summary>
    Blz,
    /// <summary>1 mBLZ = 100,000 sat (millicoin).</summary>
    MilliBlz,
    /// <summary>The base unit — 1 sat, integer.</summary>
    Sat,
}

/// <summary>
/// Format + parse amounts in the user's chosen display unit. The wallet's internal amount is
/// ALWAYS satoshis (long); these helpers only translate at the UI edge, so a unit change can
/// never affect what's actually signed.
/// </summary>
public static class LiteUnits
{
    private const long Coin = BlazecoinChain.Coin;      // 100,000,000
    private const long MilliCoin = Coin / 1000;         // 100,000

    public static string Symbol(LiteUnit unit) => unit switch
    {
        LiteUnit.Blz => "BLZ",
        LiteUnit.MilliBlz => "mBLZ",
        LiteUnit.Sat => "sat",
        _ => "BLZ",
    };

    /// <summary>Render a satoshi amount in the given unit (trailing zeros trimmed; sat is integer).</summary>
    public static string Format(long satoshis, LiteUnit unit) => unit switch
    {
        LiteUnit.Blz => (satoshis / (decimal)Coin).ToString("0.########", CultureInfo.InvariantCulture),
        LiteUnit.MilliBlz => (satoshis / (decimal)MilliCoin).ToString("0.#####", CultureInfo.InvariantCulture),
        LiteUnit.Sat => satoshis.ToString("0", CultureInfo.InvariantCulture),
        _ => (satoshis / (decimal)Coin).ToString("0.########", CultureInfo.InvariantCulture),
    };

    /// <summary>Satoshis per one whole unit (for parsing + bounds).</summary>
    public static long SatsPerUnit(LiteUnit unit) => unit switch
    {
        LiteUnit.Blz => Coin,
        LiteUnit.MilliBlz => MilliCoin,
        LiteUnit.Sat => 1,
        _ => Coin,
    };

    /// <summary>The money-supply cap expressed in this unit (for the Send input bound).</summary>
    public static decimal MaxInput(LiteUnit unit) => BlazecoinChain.MaxMoney / (decimal)SatsPerUnit(unit);

    /// <summary>
    /// Convert a user-entered amount in the given unit to satoshis. Returns false when the
    /// text isn't a positive number, exceeds the supply, or (for sat) isn't a whole number.
    /// </summary>
    public static bool TryParseToSats(string text, LiteUnit unit, out long satoshis)
    {
        satoshis = 0;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;
        if (value > MaxInput(unit))
            return false;

        var sats = value * SatsPerUnit(unit);
        if (sats != Math.Floor(sats)) // e.g. "5.5" sat is meaningless
            return false;

        satoshis = (long)sats;
        return satoshis > 0;
    }
}

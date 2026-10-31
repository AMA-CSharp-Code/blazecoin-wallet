using System.Globalization;

namespace BlazecoinWallet.Core.Services;

/// <summary>Formats a BLZ amount per the user's Amount-display preferences
/// (unit BLZ/mBLZ + decimal places, set on the Preferences page and stored in
/// localStorage as amount_unit / amount_decimals).</summary>
public static class AmountFormat
{
    public const string DefaultUnit = "BLZ";
    public const int DefaultDecimals = 8;

    public static string Format(decimal blz, string unit, int decimals)
    {
        var (value, suffix) = unit == "mBLZ" ? (blz * 1000m, "mBLZ") : (blz, "BLZ");
        return value.ToString("N" + decimals, CultureInfo.InvariantCulture) + " " + suffix;
    }
}

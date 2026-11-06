using System.Globalization;

namespace BlazecoinWallet.Core.Services;

/// <summary>
/// Reads an amount a person typed, or one carried in a payment link, into a number — the ONE
/// place both wallets do it. The grammar is deliberately narrow: digits with at most one decimal
/// mark. No thousands separators, no sign, no exponent, no spaces inside.
/// <para>Why (bug-bounty report 2026-11-06): <c>decimal.TryParse</c> with
/// <see cref="NumberStyles.Number"/> allows a group separator ANYWHERE, so "1,5" — one and a half,
/// as most of Europe writes it — parsed as 15, ten times what was typed. A separator that can
/// mean either "decimal" or "thousands" must never be guessed at where money is concerned.</para>
/// </summary>
public static class AmountText
{
    /// <summary>Longest amount text worth looking at (the supply has 11 integer digits + 8 decimals).</summary>
    private const int MaxLength = 32;

    /// <summary>
    /// An amount typed by hand: digits with ONE decimal mark, which may be a dot or a comma (a
    /// phone set to a comma-decimal locale offers only the comma on its number pad). Because no
    /// thousands separator is ever accepted, a lone comma can only be the decimal mark:
    /// "1,5" is 1.5 and "1,500" is 1.5 — never 15 or 1500. Two marks ("1,234.5") are refused.
    /// </summary>
    public static bool TryParseTyped(string? text, out decimal value)
        => TryParse(text, allowComma: true, out value);

    /// <summary>
    /// An amount from a payment link (BIP21 <c>amount=</c>): digits with at most one DOT. The
    /// grammar has no comma at all, so a link carrying one is malformed and is refused, not repaired.
    /// </summary>
    public static bool TryParsePaymentLink(string? text, out decimal value)
        => TryParse(text, allowComma: false, out value);

    private static bool TryParse(string? text, bool allowComma, out decimal value)
    {
        value = 0m;
        if (text is null) return false;
        var s = text.Trim();
        if (s.Length == 0 || s.Length > MaxLength) return false;

        var marks = 0;
        var digits = 0;
        Span<char> normalised = stackalloc char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= '0' and <= '9') { digits++; normalised[i] = c; }
            else if (c == '.' || (allowComma && c == ',')) { marks++; normalised[i] = '.'; }
            else return false;                       // sign, space, exponent, apostrophe, anything else
        }
        if (digits == 0 || marks > 1) return false;

        return decimal.TryParse(normalised, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}

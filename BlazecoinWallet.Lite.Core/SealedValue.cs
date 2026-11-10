using System.Security.Cryptography;
using System.Text;

namespace BlazecoinWallet.Lite;

/// <summary>
/// A value the wallet keeps OUTSIDE the vault, in ordinary app storage (browser localStorage,
/// Android preferences), followed by <c>;m:&lt;hex&gt;</c> — an HMAC-SHA256 over the value keyed
/// from the wallet's seed (<see cref="LiteHdWallet.DeriveStateKey"/>, one purpose per value).
/// The seed sits in the encrypted vault, not in that storage, so whoever can only WRITE the
/// store cannot make a seal, and a value there is never adopted on its format alone.
/// A value whose seal does not open is not an error: it is ignored and the wallet redoes the work.
/// <para>First used for the remembered proven coins (<see cref="RememberedCoinsSeal"/>, bug-bounty
/// report 2026-11-09); shared since the audit of the same day sealed the header chain's stored
/// state the same way.</para>
/// </summary>
public static class SealedValue
{
    private const string Tag = ";m:";

    /// <summary>The value followed by its seal — the text that is stored.</summary>
    public static string Seal(byte[] key, string value) =>
        value + Tag + Convert.ToHexString(Mac(key, value)).ToLowerInvariant();

    /// <summary>The value of a stored text, when its seal was made with <paramref name="key"/> over
    /// exactly that value. False for anything else: no seal, a bad seal, another wallet's seal.</summary>
    public static bool TryOpen(byte[] key, string? stored, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(stored)) return false;
        var at = stored.LastIndexOf(Tag, StringComparison.Ordinal);
        if (at < 0) return false;

        byte[] given;
        try { given = Convert.FromHexString(stored.AsSpan(at + Tag.Length)); }
        catch (FormatException) { return false; }

        var body = stored[..at];
        if (!CryptographicOperations.FixedTimeEquals(given, Mac(key, body))) return false;
        value = body;
        return true;
    }

    private static byte[] Mac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}

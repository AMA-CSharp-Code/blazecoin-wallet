namespace BlazecoinWallet.Lite;

/// <summary>
/// The seal on the wallet's remembered set of proven coins (bug-bounty report 2026-11-09).
/// <para>The set lives in ordinary app storage — browser localStorage, Android preferences — so
/// that a restart does not have to prove every coin again. Until now an entry there was adopted
/// on its format alone: anything able to write that storage (malware on the device, script
/// running in the web wallet's origin) could add a line for a coin that exists nowhere, and if a
/// gateway then listed the same coin it counted as verified balance with the chain never asked.</para>
/// <para>The set is now followed by <c>;m:&lt;hex&gt;</c>, an HMAC-SHA256 over the entries keyed
/// from the wallet's seed (<see cref="LiteHdWallet.DeriveStateKey"/>). The seed sits in the
/// encrypted vault, not in that storage, so whoever can only write the store cannot make a seal.
/// A set whose seal does not open is not an error: it is ignored, and the coins are proven again.
/// The seal itself is <see cref="SealedValue"/>, shared with the other values kept there.</para>
/// </summary>
public static class RememberedCoinsSeal
{
    /// <summary>The <see cref="LiteHdWallet.DeriveStateKey"/> purpose for this seal.</summary>
    public const string Purpose = "verified-coins/v1";

    /// <summary>The entries followed by their seal — the text that is stored.</summary>
    public static string Seal(byte[] key, string entries) => SealedValue.Seal(key, entries);

    /// <summary>The entries of a stored set, when its seal was made with <paramref name="key"/>
    /// over exactly that text. False for anything else: no seal, a bad seal, another wallet's seal.</summary>
    public static bool TryOpen(byte[] key, string? stored, out string entries) => SealedValue.TryOpen(key, stored, out entries);
}

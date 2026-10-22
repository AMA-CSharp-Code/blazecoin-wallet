using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>
/// Watch-only extended public key (xpub) support: derive the external-chain receive
/// addresses from an account-level xpub so a balance can be aggregated with no keys on the
/// device. Blazecoin uses the standard `xpub` prefix, so a wallet's exported account xpub
/// (m/44'/413'/0') parses directly. Derivation is `0/i` (external chain) — the same path the
/// HD wallet reveals, so the watched balance matches the real wallet.
/// </summary>
public static class WatchXpub
{
    /// <summary>True when <paramref name="value"/> is a parseable Blazecoin account xpub.</summary>
    public static bool IsXpub(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { _ = new BitcoinExtPubKey(value.Trim(), BlazecoinNetwork.Instance); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>The external-chain (receive) addresses derived from the xpub, first
    /// <paramref name="count"/> indices.</summary>
    public static IEnumerable<string> ExternalAddresses(string xpub, int count, Network? network = null)
    {
        network ??= BlazecoinNetwork.Instance;
        var external = new BitcoinExtPubKey(xpub.Trim(), network).ExtPubKey.Derive(0); // change=0
        for (var i = 0; i < count; i++)
            yield return external.Derive((uint)i).PubKey.GetAddress(ScriptPubKeyType.Legacy, network).ToString();
    }
}

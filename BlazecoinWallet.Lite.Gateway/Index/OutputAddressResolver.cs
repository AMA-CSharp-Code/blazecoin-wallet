using BlazecoinWallet.Lite.Gateway.Rpc;
using BlazecoinWallet.Lite.Pq;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Gateway.Index;

/// <summary>
/// The ONE place the gateway turns a daemon output into an address string for the index.
/// The daemon's own <c>address</c> field wins when present (legacy P2PKH/P2SH). When the
/// daemon serves none — a pre-fork daemon does not decode the P2PQH template, and even a
/// post-fork one may not on every RPC — the template is recognised gateway-side from the
/// script hex (exact: 34 bytes, 0x20 … 0xba; PQ_SIGNATURES.md §3.2) and rendered as the
/// BQ (mainnet) / TQ (regtest) address, so a wallet's BQ receive address is indexed and
/// served exactly like a legacy one. Anything else stays null: not spendable-by-address.
/// </summary>
public static class OutputAddressResolver
{
    public static string? Resolve(RpcScriptPubKey? scriptPubKey, bool mainnet)
    {
        if (scriptPubKey == null) return null;
        var fromDaemon = scriptPubKey.EffectiveAddress;
        if (fromDaemon != null) return fromDaemon;
        var script = TryDecodeHex(scriptPubKey.Hex);
        return script == null ? null
            : PqAddress.FromScriptPubKey(script, mainnet ? BlazecoinNetwork.Instance : BlazecoinNetwork.Regtest);
    }

    /// <summary>Normalised lower-case script hex to store, or null when the daemon sent none / garbage.</summary>
    public static string? NormalizeHex(RpcScriptPubKey? scriptPubKey)
    {
        var script = TryDecodeHex(scriptPubKey?.Hex);
        return script == null ? null : Encoders.Hex.EncodeData(script);
    }

    private static byte[]? TryDecodeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return Encoders.Hex.DecodeData(hex.Trim()); }
        catch (FormatException) { return null; }
    }
}

using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>Address utilities — pure functions the UI can call without coupling to the
/// stateful wallet facade (audit round-3 F2). Two address types exist on this chain:
/// legacy P2PKH "B…" (version byte 26) and, from the post-quantum fork, P2PQH "BQ…"
/// (PQ_SIGNATURES.md §5). Whether a BQ address may be PAID today is the wallet's activation
/// gate, not this class's — here "valid" means "well-formed for this chain".</summary>
public static class BlazecoinAddress
{
    /// <summary>True when <paramref name="address"/> is a well-formed Blazecoin address of
    /// either type — for the address book, watch list, and send validation.</summary>
    public static bool IsValid(string? address) => IsLegacy(address) || IsPostQuantum(address);

    /// <summary>True for a legacy P2PKH "B…" address.</summary>
    public static bool IsLegacy(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        try { _ = new BitcoinPubKeyAddress(address.Trim(), BlazecoinNetwork.Instance); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>True for a post-quantum P2PQH "BQ…" address (mainnet prefix, valid checksum).</summary>
    public static bool IsPostQuantum(string? address) => Pq.PqAddress.IsValid(address);

    /// <summary>The scriptPubKey an address commits to — the ONE place an address string
    /// becomes script bytes: "B…" → 25-byte P2PKH, "BQ…" → 34-byte P2PQH. Null for anything
    /// else (P2SH, a foreign chain, garbage), which callers treat as "not ours / not payable".</summary>
    public static byte[]? ScriptPubKeyFor(string? address, Network? network = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        network ??= BlazecoinNetwork.Instance;
        var pq = Pq.PqAddress.TryDecode(address);
        if (pq != null)
            return pq.Value.Mainnet == (network.ChainName == ChainName.Mainnet) ? Pq.PqScript.BuildP2pqh(pq.Value.KeyHash) : null;
        try { return new BitcoinPubKeyAddress(address.Trim(), network).ScriptPubKey.ToBytes(); }
        catch (FormatException) { return null; }
    }
}

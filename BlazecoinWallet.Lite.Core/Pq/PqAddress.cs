using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Pq;

/// <summary>
/// The BQ address codec (PQ_SIGNATURES.md §5): Base58Check over a two-byte version prefix
/// and the 32-byte pqkh. Mainnet prefix 0x46 0x50 → 52 characters, always starting "BQ";
/// testnet/regtest prefix 0xb2 0x73 → starts "TQ". The 4-byte double-SHA256 checksum is the
/// same protection every legacy address has. Legacy "B…" P2PKH (version byte 26) is untouched.
/// </summary>
public static class PqAddress
{
    public static readonly byte[] MainnetPrefix = [0x46, 0x50];
    public static readonly byte[] TestPrefix = [0xb2, 0x73];

    /// <summary>Every BQ/TQ address is exactly this long (verified across the whole payload range).</summary>
    public const int Length = 52;

    /// <summary>Base58Check(prefix || pqkh) for the network's chain (mainnet → "BQ…", else "TQ…").</summary>
    public static string Encode(ReadOnlySpan<byte> pqkh, Network? network = null)
    {
        if (pqkh.Length != 32) throw new ArgumentException("pqkh must be 32 bytes.", nameof(pqkh));
        var prefix = IsMainnet(network) ? MainnetPrefix : TestPrefix;
        var payload = new byte[2 + 32];
        prefix.CopyTo(payload, 0);
        pqkh.CopyTo(payload.AsSpan(2));
        return Encoders.Base58Check.EncodeData(payload);
    }

    /// <summary>Decodes a BQ/TQ address: the pqkh and whether it carried the mainnet prefix.
    /// Null for anything that is not a well-formed P2PQH address (wrong prefix, length or
    /// checksum) — a legacy "B…" address is simply "not a PQ address" here.</summary>
    public static (byte[] KeyHash, bool Mainnet)? TryDecode(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var s = address.Trim();
        if (s.Length != Length) return null;
        byte[] payload;
        try { payload = Encoders.Base58Check.DecodeData(s); }
        catch (FormatException) { return null; }
        if (payload.Length != 34) return null;
        if (payload[0] == MainnetPrefix[0] && payload[1] == MainnetPrefix[1]) return (payload[2..], true);
        if (payload[0] == TestPrefix[0] && payload[1] == TestPrefix[1]) return (payload[2..], false);
        return null;
    }

    /// <summary>Decodes for a specific network, refusing the other chain's prefix.</summary>
    public static byte[] Decode(string address, Network? network = null)
    {
        var decoded = TryDecode(address);
        if (decoded == null || decoded.Value.Mainnet != IsMainnet(network))
            throw new FormatException($"'{address}' is not a valid Blazecoin post-quantum (BQ) address for this network.");
        return decoded.Value.KeyHash;
    }

    /// <summary>True for a well-formed P2PQH address on the given network (mainnet by default).</summary>
    public static bool IsValid(string? address, Network? network = null)
    {
        var decoded = TryDecode(address);
        return decoded != null && decoded.Value.Mainnet == IsMainnet(network);
    }

    /// <summary>The 34-byte scriptPubKey a payment to this address must carry.</summary>
    public static byte[] ToScriptPubKey(string address, Network? network = null)
        => PqScript.BuildP2pqh(Decode(address, network));

    /// <summary>Address for a P2PQH scriptPubKey, or null for any other script.</summary>
    public static string? FromScriptPubKey(ReadOnlySpan<byte> scriptPubKey, Network? network = null)
    {
        var pqkh = PqScript.TryParseP2pqh(scriptPubKey);
        return pqkh == null ? null : Encode(pqkh, network);
    }

    private static bool IsMainnet(Network? network) => (network ?? BlazecoinNetwork.Instance).ChainName == ChainName.Mainnet;
}

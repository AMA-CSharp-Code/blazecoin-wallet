using System.Text;
using NBitcoin;
using NBitcoin.Crypto;

namespace BlazecoinWallet.Lite;

/// <summary>
/// Bitcoin-style signed messages, using Blazecoin's OWN magic so signatures are
/// interoperable with the daemon's <c>signmessage</c>/<c>verifymessage</c> and any other
/// Blazecoin tool — the daemon's <c>MESSAGE_MAGIC</c> is "Blazecoin Signed Message:\n"
/// (src/common/signmessage.cpp), NOT Bitcoin's, so NBitcoin's built-in helper would produce
/// signatures nothing else on this chain accepts. The signed digest is
/// DoubleSHA256(varstr(magic) ++ varstr(message)); the wire signature is the classic 65-byte
/// [header][r||s] base64 (header = 27 + recid + 4-if-compressed).
/// </summary>
public static class BlazecoinMessage
{
    private const string Magic = "Blazecoin Signed Message:\n";

    /// <summary>Verify against the mainnet network (the common case; pages call this
    /// directly rather than through the wallet facade — audit round-3 F2).</summary>
    public static bool Verify(string address, string message, string signature)
        => Verify(address, message, signature, BlazecoinNetwork.Instance);

    private static uint256 Digest(string message)
    {
        using var ms = new MemoryStream();
        var stream = new BitcoinStream(ms, true);
        var magic = Encoding.UTF8.GetBytes(Magic);
        var body = Encoding.UTF8.GetBytes(message ?? string.Empty);
        stream.ReadWriteAsVarString(ref magic);
        stream.ReadWriteAsVarString(ref body);
        return Hashes.DoubleSHA256(ms.ToArray());
    }

    public static string Sign(Key key, string message)
    {
        var sig = key.SignCompact(Digest(message));
        var full = new byte[65];
        full[0] = (byte)(27 + sig.RecoveryId + (key.PubKey.IsCompressed ? 4 : 0));
        Array.Copy(sig.Signature, 0, full, 1, 64);
        return Convert.ToBase64String(full);
    }

    public static bool Verify(string address, string message, string signature, Network network)
    {
        byte[] full;
        try { full = Convert.FromBase64String(signature.Trim()); }
        catch (FormatException) { return false; }
        if (full.Length != 65) return false;

        int header = full[0];
        if (header is < 27 or > 34) return false;
        var compressed = header >= 31;
        var recid = (header - 27) & 3;
        var rs = new byte[64];
        Array.Copy(full, 1, rs, 0, 64);

        try
        {
            var recovered = PubKey.RecoverCompact(Digest(message), new CompactSignature(recid, rs));
            recovered = compressed ? recovered.Compress() : recovered.Decompress();
            return recovered.GetAddress(ScriptPubKeyType.Legacy, network).ToString() == address.Trim();
        }
        catch { return false; }
    }
}

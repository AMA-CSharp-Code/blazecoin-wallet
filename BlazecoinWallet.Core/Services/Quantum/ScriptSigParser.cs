using BlazecoinWallet.Core.Services.Mining;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>
/// Reads the public key out of a legacy P2PKH scriptSig. On this chain every spend is
/// P2PKH (segwit is consensus-disabled), and a P2PKH scriptSig is exactly two pushes:
/// &lt;signature&gt; &lt;pubkey&gt;. The moment that scriptSig is mined the key is public, and a
/// large enough quantum computer could derive the private key from it. Address hashes
/// on their own reveal nothing: that is the whole asymmetry this feature measures.
/// Pure and side-effect-free; the vectors are pinned in the tests against a real
/// Blazecoin spend.
/// </summary>
public static class ScriptSigParser
{
    /// <summary>
    /// Returns the pubkey (33-byte compressed or 65-byte uncompressed) if the scriptSig is a
    /// well-formed P2PKH spend, else null (coinbase, P2SH, malformed, or empty).
    /// </summary>
    public static byte[]? TryExtractPubKey(string? scriptSigHex)
    {
        if (string.IsNullOrWhiteSpace(scriptSigHex) || (scriptSigHex.Length & 1) == 1) return null;
        byte[] script;
        try { script = BitcoinProtocol.HexToBytes(scriptSigHex); }
        catch (FormatException) { return null; }

        var pushes = ParsePushes(script);
        if (pushes is null || pushes.Count != 2) return null;

        var candidate = pushes[1];
        return IsPlausiblePubKey(candidate) ? candidate : null;
    }

    /// <summary>The P2PKH address a pubkey pays to on Blazecoin mainnet ('B…', version byte 26).</summary>
    public static string PubKeyToAddress(byte[] pubKey) => BitcoinProtocol.PubKeyToAddress(pubKey);

    /// <summary>
    /// Splits a script into its pushed data items. Only direct pushes (1–75 bytes) and
    /// OP_PUSHDATA1/2 are accepted; any opcode that is not a push, or a push that runs off
    /// the end, makes the script "not a plain P2PKH spend" and returns null.
    /// </summary>
    internal static List<byte[]>? ParsePushes(byte[] script)
    {
        var items = new List<byte[]>();
        int i = 0;
        while (i < script.Length)
        {
            int op = script[i++];
            int len;
            if (op >= 1 && op <= 75) len = op;
            else if (op == 0x4c) { if (i >= script.Length) return null; len = script[i++]; }
            else if (op == 0x4d) { if (i + 1 >= script.Length) return null; len = script[i] | (script[i + 1] << 8); i += 2; }
            else return null;                       // OP_0, OP_PUSHDATA4, or a real opcode: not P2PKH
            if (i + len > script.Length) return null;
            var item = new byte[len];
            Buffer.BlockCopy(script, i, item, 0, len);
            items.Add(item);
            i += len;
        }
        return items;
    }

    private static bool IsPlausiblePubKey(byte[] b) =>
        (b.Length == 33 && (b[0] == 0x02 || b[0] == 0x03)) ||
        (b.Length == 65 && b[0] == 0x04);
}

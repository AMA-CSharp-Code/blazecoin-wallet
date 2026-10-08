namespace BlazecoinWallet.Lite.Pq;

/// <summary>
/// The P2PQH output type's script shapes (PQ_SIGNATURES.md §3.1–§3.3), byte-exact:
/// key blob, key hash, the 34-byte scriptPubKey template, the two-push scriptSig, and the
/// push-only parser the consensus check (<see cref="PqInputVerifier"/>) runs on a spend.
/// </summary>
public static class PqScript
{
    /// <summary>Algorithm ID byte for ML-DSA-44 — the only ACTIVE ID in v1 (§3.1).</summary>
    public const byte AlgoIdMlDsa44 = 0x01;

    /// <summary>OP_CHECKPQSIG — one past OP_NOP10, "bad opcode" in every pre-fork interpreter (§3.2).</summary>
    public const byte OpCheckPqSig = 0xba;

    /// <summary>0x20 &lt;pqkh:32&gt; OP_CHECKPQSIG.</summary>
    public const int ScriptPubKeyLength = 34;

    /// <summary>algo_id (1) + ML-DSA-44 public key (1,312).</summary>
    public const int KeyBlobLength = 1 + MlDsa44.PublicKeyLength;

    /// <summary>PUSHDATA2 sig (3 + 2,420) + PUSHDATA2 keyblob (3 + 1,313) = 3,739 bytes (§3.3).</summary>
    public const int ScriptSigLength = 3 + MlDsa44.SignatureLength + 3 + KeyBlobLength;

    /// <summary>Outpoint (36) + scriptSig length varint (3) + scriptSig + sequence (4) = 3,782 bytes (§3.3).</summary>
    public const int InputLength = 36 + 3 + ScriptSigLength + 4;

    /// <summary>MAX_SCRIPT_ELEMENT_SIZE in the PQ interpreter context (§3.4 step 1).</summary>
    public const int MaxElementSize = 5000;

    /// <summary>The standard-size limit (100 KB serialized) admits 26 ML-DSA-44 inputs per transaction (§4).</summary>
    public const int MaxPqInputsPerStandardTx = 26;

    /// <summary>MAX_STANDARD_TX_WEIGHT / 4 — a transaction above this many serialized bytes is non-standard.</summary>
    public const int MaxStandardTxBytes = 100_000;

    private const byte OpPushData1 = 0x4c;
    private const byte OpPushData2 = 0x4d;
    private const byte OpPushData4 = 0x4e;
    private const byte Op1Negate = 0x4f;
    private const byte OpReserved = 0x50;
    private const byte Op1 = 0x51;
    private const byte Op16 = 0x60;

    /// <summary>keyblob = 0x01 || pubkey(1,312).</summary>
    public static byte[] BuildKeyBlob(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != MlDsa44.PublicKeyLength)
            throw new ArgumentException($"ML-DSA-44 public key must be {MlDsa44.PublicKeyLength} bytes.", nameof(publicKey));
        var blob = new byte[KeyBlobLength];
        blob[0] = AlgoIdMlDsa44;
        publicKey.CopyTo(blob.AsSpan(1));
        return blob;
    }

    /// <summary>pqkh = TaggedHash("Blazecoin/PQKH/v1", keyblob) — commits to the algorithm ID too.</summary>
    public static byte[] ComputeKeyHash(ReadOnlySpan<byte> keyBlob) => TaggedHash.Compute(TaggedHash.PqkhTag, keyBlob);

    /// <summary>The 34-byte scriptPubKey: 0x20 &lt;pqkh&gt; 0xba.</summary>
    public static byte[] BuildP2pqh(ReadOnlySpan<byte> pqkh)
    {
        if (pqkh.Length != 32) throw new ArgumentException("pqkh must be 32 bytes.", nameof(pqkh));
        var script = new byte[ScriptPubKeyLength];
        script[0] = 0x20;
        pqkh.CopyTo(script.AsSpan(1));
        script[33] = OpCheckPqSig;
        return script;
    }

    /// <summary>Exact template recognition (§3.2): length 34, [0] == 0x20, [33] == 0xba.</summary>
    public static bool IsP2pqh(ReadOnlySpan<byte> scriptPubKey)
        => scriptPubKey.Length == ScriptPubKeyLength && scriptPubKey[0] == 0x20 && scriptPubKey[33] == OpCheckPqSig;

    /// <summary>The committed key hash of a P2PQH template, or null for any other script.</summary>
    public static byte[]? TryParseP2pqh(ReadOnlySpan<byte> scriptPubKey)
        => IsP2pqh(scriptPubKey) ? scriptPubKey.Slice(1, 32).ToArray() : null;

    /// <summary>scriptSig = PUSHDATA2(sig) PUSHDATA2(keyblob) — exactly two pushes (§3.3).
    /// Always PUSHDATA2 for these sizes (both exceed 255 bytes), so the encoding is canonical.</summary>
    public static byte[] BuildScriptSig(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> keyBlob)
    {
        var script = new byte[3 + signature.Length + 3 + keyBlob.Length];
        var o = 0;
        o = WritePushData2(script, o, signature);
        WritePushData2(script, o, keyBlob);
        return script;
    }

    private static int WritePushData2(byte[] dst, int offset, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 256 or > ushort.MaxValue)
            throw new ArgumentException("PUSHDATA2 is used for 256..65535-byte pushes.", nameof(data));
        dst[offset] = OpPushData2;
        dst[offset + 1] = (byte)(data.Length & 0xff);
        dst[offset + 2] = (byte)(data.Length >> 8);
        data.CopyTo(dst.AsSpan(offset + 3));
        return offset + 3 + data.Length;
    }

    /// <summary>The (signature, keyblob) pair of a well-formed P2PQH scriptSig, or null when
    /// the script is not push-only or does not leave exactly two elements.</summary>
    public static (byte[] Signature, byte[] KeyBlob)? TryParseScriptSig(ReadOnlySpan<byte> scriptSig)
    {
        var pushes = TryParsePushOnly(scriptSig);
        return pushes is { Count: 2 } ? (pushes[0], pushes[1]) : null;
    }

    /// <summary>
    /// Push-only parse in the sense of Bitcoin's <c>IsPushOnly</c> (every opcode ≤ OP_16).
    /// Returns the stack the script leaves, or null when any opcode is not a push or a push is
    /// truncated. Element SIZE is the verifier's check (<see cref="MaxElementSize"/>), as in
    /// the interpreter. OP_RESERVED (0x50) fails here: it is numerically ≤ OP_16 but executing
    /// it is a bad opcode, so a spend carrying it can never validate.
    /// </summary>
    public static List<byte[]>? TryParsePushOnly(ReadOnlySpan<byte> script)
    {
        var stack = new List<byte[]>();
        var i = 0;
        while (i < script.Length)
        {
            var op = script[i++];
            int len;
            if (op == 0) { stack.Add([]); continue; }
            if (op <= 0x4b) len = op;
            else if (op == OpPushData1)
            {
                if (i + 1 > script.Length) return null;
                len = script[i]; i += 1;
            }
            else if (op == OpPushData2)
            {
                if (i + 2 > script.Length) return null;
                len = script[i] | (script[i + 1] << 8); i += 2;
            }
            else if (op == OpPushData4)
            {
                if (i + 4 > script.Length) return null;
                var l = (uint)(script[i] | (script[i + 1] << 8) | (script[i + 2] << 16) | (script[i + 3] << 24));
                if (l > int.MaxValue) return null;
                len = (int)l; i += 4;
            }
            else if (op == Op1Negate) { stack.Add([0x81]); continue; }
            else if (op >= Op1 && op <= Op16) { stack.Add([(byte)(op - Op1 + 1)]); continue; }
            else return null; // OP_RESERVED or anything above OP_16: not push-only
            if (i + len > script.Length) return null;
            stack.Add(script.Slice(i, len).ToArray());
            i += len;
        }
        return stack;
    }
}

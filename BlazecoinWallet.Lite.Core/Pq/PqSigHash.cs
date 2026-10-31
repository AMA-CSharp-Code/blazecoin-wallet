using NBitcoin;
using NBitcoin.Crypto;

namespace BlazecoinWallet.Lite.Pq;

/// <summary>
/// The P2PQH signature digest, PQ_SIGNATURES.md §3.5 — BIP-143 shape, tagged:
/// <code>
/// preimage = nVersion(4 LE) || SHA256d(all outpoints) || SHA256d(all nSequence LE)
///         || this outpoint(36) || varint(34)+scriptPubKey being spent || amount(8 LE)
///         || this nSequence(4 LE) || SHA256d(all outputs serialized) || nLockTime(4 LE)
///         || sighash_type 0x00000001 (4 LE)
/// msg = TaggedHash("Blazecoin/PQSig/v1", preimage)
/// </code>
/// It commits to the amount (a lite signer proves fees without the parent transactions),
/// is O(n) per transaction, and admits SIGHASH_ALL only in v1. The scriptSigs of the
/// transaction play no part, so inputs can be signed in any order.
/// </summary>
public static class PqSigHash
{
    /// <summary>The only sighash type in v1 (§3.5).</summary>
    public const uint SigHashAll = 0x00000001;

    /// <summary>The 191-byte preimage for input <paramref name="inputIndex"/> spending a
    /// prevout of <paramref name="amountSatoshis"/> locked by <paramref name="scriptPubKey"/>.</summary>
    public static byte[] Preimage(Transaction tx, int inputIndex, long amountSatoshis, ReadOnlySpan<byte> scriptPubKey)
    {
        if (inputIndex < 0 || inputIndex >= tx.Inputs.Count)
            throw new ArgumentOutOfRangeException(nameof(inputIndex));
        if (scriptPubKey.Length != PqScript.ScriptPubKeyLength)
            throw new ArgumentException("scriptCode must be the 34-byte P2PQH scriptPubKey being spent.", nameof(scriptPubKey));

        using var ms = new MemoryStream(4 + 32 + 32 + 36 + 1 + 34 + 8 + 4 + 32 + 4 + 4);
        WriteUInt32(ms, tx.Version);
        ms.Write(HashPrevouts(tx));
        ms.Write(HashSequence(tx));
        ms.Write(tx.Inputs[inputIndex].PrevOut.ToBytes());
        ms.WriteByte((byte)scriptPubKey.Length); // varint(34)
        ms.Write(scriptPubKey);
        WriteUInt64(ms, (ulong)amountSatoshis);
        WriteUInt32(ms, tx.Inputs[inputIndex].Sequence.Value);
        ms.Write(HashOutputs(tx));
        WriteUInt32(ms, tx.LockTime.Value);
        WriteUInt32(ms, SigHashAll);
        return ms.ToArray();
    }

    /// <summary>The 32-byte message ML-DSA signs and verifies (§3.4 step 5).</summary>
    public static byte[] Digest(Transaction tx, int inputIndex, long amountSatoshis, ReadOnlySpan<byte> scriptPubKey)
        => TaggedHash.Compute(TaggedHash.SigHashTag, Preimage(tx, inputIndex, amountSatoshis, scriptPubKey));

    private static byte[] HashPrevouts(Transaction tx)
    {
        using var ms = new MemoryStream(36 * tx.Inputs.Count);
        foreach (var input in tx.Inputs) ms.Write(input.PrevOut.ToBytes());
        return Sha256d(ms.ToArray());
    }

    private static byte[] HashSequence(Transaction tx)
    {
        using var ms = new MemoryStream(4 * tx.Inputs.Count);
        foreach (var input in tx.Inputs) WriteUInt32(ms, input.Sequence.Value);
        return Sha256d(ms.ToArray());
    }

    private static byte[] HashOutputs(Transaction tx)
    {
        using var ms = new MemoryStream();
        foreach (var output in tx.Outputs) ms.Write(output.ToBytes());
        return Sha256d(ms.ToArray());
    }

    private static byte[] Sha256d(byte[] data) => Hashes.DoubleSHA256(data).ToBytes();

    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteUInt64(Stream s, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        s.Write(b);
    }
}

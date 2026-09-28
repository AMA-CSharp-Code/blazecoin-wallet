using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Pq;

/// <summary>
/// The verdict of <see cref="PqInputVerifier"/> — the C# twin of the daemon's interpreter
/// context for a P2PQH input (PQ_SIGNATURES.md §3.4), one value per failure step so the
/// shared spend vectors pin the ORDER of the checks as well as their outcome.
/// </summary>
public enum PqInputVerdict
{
    /// <summary>The spend validates: OP_CHECKPQSIG pushed true.</summary>
    OK,
    /// <summary>Step 3: TaggedHash("Blazecoin/PQKH/v1", keyblob) != the pqkh in the scriptPubKey.</summary>
    PQ_KEYHASH_MISMATCH,
    /// <summary>Step 4: keyblob[0] is not an ACTIVE algorithm ID (v1: only 0x01).</summary>
    PQ_ALGO_INACTIVE,
    /// <summary>Step 4: the public key is not exactly the algorithm's length (1,312 for ML-DSA-44).</summary>
    PQ_PUBKEY_LENGTH,
    /// <summary>Step 1: the push-only scriptSig does not leave exactly two stack elements.</summary>
    PQ_STACK_SIZE,
    /// <summary>Step 1: the scriptSig contains a non-push opcode, a truncated push, or a push
    /// above the PQ element cap (the interpreter's SCRIPT_ERR_PUSH_SIZE / SIG_PUSHONLY).</summary>
    SIG_PUSHONLY,
    /// <summary>Step 6: ML-DSA-44.Verify(pubkey, PQSigHash, sig, ctx) failed — wrong digest,
    /// wrong context, tampered transaction, or a malformed signature.</summary>
    PQ_SIG_INVALID,
    /// <summary>§3.6: evaluated before H_Q (SigVersion::BASE) — OP_CHECKPQSIG is a bad opcode
    /// and the pushes exceed 520 bytes, so every pre-fork node rejects the spend.</summary>
    PRE_ACTIVATION,
}

/// <summary>
/// Consensus verification of ONE P2PQH input, exactly the seven steps of PQ_SIGNATURES.md
/// §3.4 in the daemon's order. The lite wallet runs this on every PQ input it signs (in place
/// of NBitcoin's <c>builder.Verify</c>, which knows nothing of OP_CHECKPQSIG) and the test
/// suite runs it over the shared spend vectors — so the wallet cannot broadcast a transaction
/// the daemon would refuse, and the two implementations cannot drift apart silently.
/// </summary>
public static class PqInputVerifier
{
    /// <summary>Verifies input <paramref name="inputIndex"/> of the serialized transaction
    /// <paramref name="txHex"/> spending a prevout of <paramref name="amountSatoshis"/> locked
    /// by the P2PQH <paramref name="scriptPubKey"/>. <paramref name="activated"/> is "the
    /// interpreter runs in SigVersion::PQ" — false reproduces every pre-fork node's verdict.</summary>
    public static PqInputVerdict Verify(string txHex, int inputIndex, long amountSatoshis, ReadOnlySpan<byte> scriptPubKey, bool activated, Network? network = null)
    {
        Transaction tx;
        try { tx = Transaction.Parse(txHex, network ?? BlazecoinNetwork.Instance); }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentException)
        {
            throw new ArgumentException("The transaction hex is malformed.", nameof(txHex), ex);
        }
        return Verify(tx, inputIndex, amountSatoshis, scriptPubKey, activated);
    }

    /// <inheritdoc cref="Verify(string,int,long,ReadOnlySpan{byte},bool,Network?)"/>
    public static PqInputVerdict Verify(Transaction tx, int inputIndex, long amountSatoshis, ReadOnlySpan<byte> scriptPubKey, bool activated)
    {
        if (inputIndex < 0 || inputIndex >= tx.Inputs.Count)
            throw new ArgumentOutOfRangeException(nameof(inputIndex));
        var pqkh = PqScript.TryParseP2pqh(scriptPubKey)
            ?? throw new ArgumentException("The prevout is not a P2PQH template; this verifier only judges P2PQH inputs.", nameof(scriptPubKey));

        // §3.6 — before H_Q the interpreter runs in SigVersion::BASE: 0xba is a bad opcode and
        // both pushes exceed MAX_SCRIPT_ELEMENT_SIZE 520. Nothing below is even reached.
        if (!activated) return PqInputVerdict.PRE_ACTIVATION;

        // Step 1 — the PQ context: push-only scriptSig, elements ≤ 5,000 bytes, exactly two left.
        var stack = PqScript.TryParsePushOnly(tx.Inputs[inputIndex].ScriptSig.ToBytes());
        if (stack == null || stack.Any(e => e.Length > PqScript.MaxElementSize))
            return PqInputVerdict.SIG_PUSHONLY;
        if (stack.Count != 2) return PqInputVerdict.PQ_STACK_SIZE;

        // Step 2 — OP_CHECKPQSIG pops pqkh (pushed by the scriptPubKey), keyblob, sig.
        var sig = stack[0];
        var keyBlob = stack[1];

        // Step 3 — the key blob must hash to the committed pqkh (this commits to the algo ID too).
        if (!PqScript.ComputeKeyHash(keyBlob).AsSpan().SequenceEqual(pqkh))
            return PqInputVerdict.PQ_KEYHASH_MISMATCH;

        // Step 4 — an ACTIVE algorithm ID with exactly its public-key length.
        if (keyBlob.Length == 0 || keyBlob[0] != PqScript.AlgoIdMlDsa44)
            return PqInputVerdict.PQ_ALGO_INACTIVE;
        if (keyBlob.Length - 1 != MlDsa44.PublicKeyLength)
            return PqInputVerdict.PQ_PUBKEY_LENGTH;

        // Steps 5 + 6 — the tagged BIP-143-shaped digest, verified under the FIPS 204 context.
        var digest = PqSigHash.Digest(tx, inputIndex, amountSatoshis, scriptPubKey);
        return MlDsa44.Verify(keyBlob.AsSpan(1), digest, sig)
            ? PqInputVerdict.OK          // step 7: push true
            : PqInputVerdict.PQ_SIG_INVALID;
    }

    /// <summary>Hex convenience for callers holding the scriptPubKey as the gateway serves it.</summary>
    public static PqInputVerdict Verify(Transaction tx, int inputIndex, long amountSatoshis, string scriptPubKeyHex, bool activated)
        => Verify(tx, inputIndex, amountSatoshis, Encoders.Hex.DecodeData(scriptPubKeyHex), activated);
}

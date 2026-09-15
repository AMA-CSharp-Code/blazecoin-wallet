using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Data;

/// <summary>Result of verifying one claimed UTXO against the chain.</summary>
/// <param name="Verified">True when the real value was cryptographically confirmed.</param>
/// <param name="RealSatoshis">The TRUE output value parsed from the txid-committed raw tx
/// (equals the claim when honest; the value the wallet must build with).</param>
/// <param name="IncludedInBlock">True when a merkle proof placed the tx in a PoW-valid block.</param>
/// <param name="Error">Why verification failed, when it did.</param>
public sealed record UtxoVerification(bool Verified, long RealSatoshis, bool IncludedInBlock, string? Error);

/// <summary>
/// TRUSTLESS input verification — the C1/M3 fix. A txid is the double-SHA256 of the whole
/// serialized transaction, so it CRYPTOGRAPHICALLY COMMITS to every output value. Therefore,
/// given a claimed UTXO (txid, vout, amount) from the semi-trusted indexer, the wallet:
///   1. fetches the raw funding tx, recomputes its txid, and checks it equals the claimed
///      txid — this binds the hex to reality (the indexer cannot substitute a different tx);
///   2. parses the REAL output value from that verified hex — if it differs from the claim,
///      the indexer lied, and the wallet builds with the REAL value (so no excess can leak
///      to miner fee, closing C1);
///   3. (M3) optionally verifies a merkle proof: the tx's branch must reproduce the block
///      header's merkle root AND the header must satisfy its own PoW — proving the tx is
///      really mined (a fabricated "confirmed" tx has no such proof).
/// Personal-node mode skips this (own-chainstate values are already authoritative).
/// </summary>
public sealed class LiteTxVerifier
{
    private readonly IChainReader _reader;
    private readonly Network _network;
    private readonly HeaderChainSync? _headerChain;

    public LiteTxVerifier(IChainReader reader, Network? network = null, HeaderChainSync? headerChain = null)
    {
        _reader = reader;
        _network = network ?? BlazecoinNetwork.Instance;
        _headerChain = headerChain;
    }

    /// <summary>
    /// Verifies the claimed value of one output. On success returns the REAL value to build
    /// with. <paramref name="requireInclusionProof"/> also demands a valid merkle+PoW proof
    /// (M3 — proves the tx is genuinely mined, not fabricated). When
    /// <paramref name="expectedAddress"/> is supplied, the output's scriptPubKey must belong
    /// to it — closing the latent M1: a lying indexer can't slip a real THIRD-PARTY output
    /// into the wallet's input list (which the wallet couldn't sign, so the whole tx would
    /// have failed at build time — an availability gap, and a theft-adjacent hole if the key
    /// resolver ever loosened). Caught here instead, before signing.
    /// </summary>
    public async Task<UtxoVerification> VerifyAsync(
        string txId, int vout, long claimedSatoshis, bool requireInclusionProof,
        string? expectedAddress = null, CancellationToken ct = default)
    {
        // 1. Fetch the raw funding tx and bind it to the claimed txid.
        var hex = await _reader.GetRawTransactionHexAsync(txId, ct);
        if (string.IsNullOrWhiteSpace(hex))
            return new UtxoVerification(false, 0, false, "Could not fetch the funding transaction for verification.");

        Transaction fundingTx;
        try { fundingTx = Transaction.Parse(hex, _network); }
        catch { return new UtxoVerification(false, 0, false, "The funding transaction hex was malformed."); }

        // The txid is the double-SHA256 of the serialized tx — this is the crux: a lying
        // indexer cannot hand back a tx that hashes to txId yet carries different values.
        if (!string.Equals(fundingTx.GetHash().ToString(), txId, StringComparison.OrdinalIgnoreCase))
            return new UtxoVerification(false, 0, false, "The funding transaction does not hash to its claimed id — the data source is untrustworthy.");

        if (vout < 0 || vout >= fundingTx.Outputs.Count)
            return new UtxoVerification(false, 0, false, "The claimed output index does not exist in the funding transaction.");

        // 2. The REAL value, committed by the txid. Build with THIS, never the claim.
        var realSatoshis = fundingTx.Outputs[vout].Value.Satoshi;

        // 2b. (M1) Ownership: the output must actually pay the address the wallet believes
        // owns it — otherwise the indexer injected someone else's coin into our inputs.
        // Compared as SCRIPT BYTES against the script the address itself implies (P2PKH for
        // a legacy "B…" address, the 34-byte P2PQH template for a post-quantum "BQ…" one) —
        // NBitcoin's GetDestinationAddress knows nothing of OP_CHECKPQSIG, and an exact
        // byte comparison also can't be fooled by a look-alike script that decodes to the
        // same address string.
        if (expectedAddress != null)
        {
            var expectedScript = ScriptForAddress(expectedAddress, _network);
            if (expectedScript == null || !fundingTx.Outputs[vout].ScriptPubKey.ToBytes().AsSpan().SequenceEqual(expectedScript))
                return new UtxoVerification(false, realSatoshis, false,
                    "A coin in your inputs is not owned by your wallet — the data source is untrustworthy.");
        }

        // 3. (M3) Optional inclusion proof: prove the tx is really mined.
        var included = false;
        if (requireInclusionProof)
        {
            var proofHex = await _reader.GetTxOutProofAsync(txId, ct);
            if (string.IsNullOrWhiteSpace(proofHex))
                return new UtxoVerification(false, realSatoshis, false, "No merkle inclusion proof was available for the funding transaction.");
            if (!VerifyInclusion(proofHex, txId))
                return new UtxoVerification(false, realSatoshis, false, "The merkle inclusion proof did not verify — the transaction may not be mined.");
            included = true;
        }

        return new UtxoVerification(true, realSatoshis, included, null);
    }

    /// <summary>The scriptPubKey an address commits to — the ONE place the wallet maps an
    /// address string to script bytes for ownership checks: legacy "B…" → 25-byte P2PKH,
    /// post-quantum "BQ…" → 34-byte P2PQH. Null for anything else (P2SH, foreign, garbage),
    /// which the caller treats as "not ours".</summary>
    public static byte[]? ScriptForAddress(string address, Network? network = null)
        => BlazecoinAddress.ScriptPubKeyFor(address, network);

    /// <summary>
    /// Verifies a serialized MerkleBlock proof: the branch reproduces the header's merkle
    /// root AND the header meets its own proof-of-work target. Forging this requires real
    /// mining work, so a semi-trusted indexer cannot fabricate it.
    /// </summary>
    private bool VerifyInclusion(string proofHex, string txId) => CheckProof(proofHex, txId).Ok;

    /// <summary>Shared proof check: parses the MerkleBlock, verifies scrypt-PoW + merkle root +
    /// txid membership, and returns the proof's block hash (SHA256d) on success so a caller can
    /// locate it in the verified header chain.</summary>
    private (bool Ok, string? BlockHash) CheckProof(string proofHex, string txId)
    {
        try
        {
            var merkleBlock = new MerkleBlock();
            var stream = new BitcoinStream(Encoders.Hex.DecodeData(proofHex))
            {
                ConsensusFactory = _network.Consensus.ConsensusFactory,
            };
            merkleBlock.ReadWrite(stream);

            // Header PoW: the header's SCRYPT hash must satisfy the target in nBits. This is
            // the anchor — a fake header with a chosen merkle root can't clear it. (NBitcoin's
            // CheckProofOfWork checks SHA256d, which is wrong for this scrypt chain.)
            if (!BlazecoinPoW.MeetsTarget(merkleBlock.Header))
                return (false, null);

            // The partial tree must resolve to the header's committed merkle root (Check
            // recomputes it), and the proven leaf set must actually contain our txid.
            if (!merkleBlock.PartialMerkleTree.Check(merkleBlock.Header.HashMerkleRoot))
                return (false, null);

            var matches = merkleBlock.PartialMerkleTree.GetMatchedTransactions()
                .Any(h => string.Equals(h.ToString(), txId, StringComparison.OrdinalIgnoreCase));
            return matches ? (true, merkleBlock.Header.GetHash().ToString()) : (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>Where a confirmed tx sits relative to the wallet's OWN verified header chain.</summary>
    /// <param name="ProofValid">The merkle+PoW inclusion proof itself verified.</param>
    /// <param name="OnVerifiedChain">The proof's block is a block on the PoW-verified header
    /// chain (not just any header meeting its own target) — the strongest guarantee.</param>
    /// <param name="Height">The tx's TRUSTLESS block height, derived purely from the proof's
    /// header matched into the verified chain (never the gateway's claim); null when the block
    /// isn't in the verified window.</param>
    public sealed record ChainInclusion(bool ProofValid, bool OnVerifiedChain, long? Height, string? Error);

    /// <summary>
    /// Per-tx trustless height (the M3 polish): fetch the tx's merkle proof, verify it
    /// (scrypt-PoW + merkle + txid), then LOCATE the proof's block in the wallet's own verified
    /// header chain. If found, the tx's height is known with zero gateway trust — the gateway
    /// never gets to assert which block, or which height, a coin is in. When the header chain is
    /// absent or hasn't verified this block's height yet, the proof still stands on its own PoW
    /// (OnVerifiedChain = false) — this only ever ADDS certainty, never rejects a valid coin.
    /// </summary>
    public async Task<ChainInclusion> VerifyChainInclusionAsync(string txId, CancellationToken ct = default)
    {
        var proofHex = await _reader.GetTxOutProofAsync(txId, ct);
        if (string.IsNullOrWhiteSpace(proofHex))
            return new ChainInclusion(false, false, null, "No merkle inclusion proof was available.");

        var (ok, blockHash) = CheckProof(proofHex, txId);
        if (!ok || blockHash == null)
            return new ChainInclusion(false, false, null, "The merkle inclusion proof did not verify.");

        var height = _headerChain?.VerifiedHeightOf(blockHash);
        return new ChainInclusion(true, height != null, height, null);
    }
}

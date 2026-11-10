using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Data;

/// <summary>Result of verifying one claimed UTXO against the chain.</summary>
/// <param name="Verified">True when the real value was cryptographically confirmed.</param>
/// <param name="RealSatoshis">The TRUE output value parsed from the txid-committed raw tx
/// (equals the claim when honest; the value the wallet must build with).</param>
/// <param name="IncludedInBlock">True when a merkle proof placed the tx in a block — proven to be on
/// the chain whenever a header feed is wired (see <see cref="LiteTxVerifier"/>).</param>
/// <param name="Error">Why verification failed, when it did.</param>
/// <param name="IsCoinbase">Whether the funding transaction is a coinbase — read from the transaction
/// itself, which its txid commits to, never from the gateway's row (bug-bounty report 2026-11-06,
/// fourth round: a gateway marking an ordinary coin "coinbase" locked it as immature).</param>
/// <param name="VerifiedHeight">The height the wallet's own header chain places the coin's block at,
/// when a header feed is wired and the block was placed; null otherwise (the web head).</param>
public sealed record UtxoVerification(bool Verified, long RealSatoshis, bool IncludedInBlock, string? Error,
    bool IsCoinbase = false, long? VerifiedHeight = null);

/// <summary>What the evidence for a claimed spend showed (<see cref="LiteTxVerifier.JudgeSpendAsync"/>).</summary>
public enum SpendProof
{
    /// <summary>The spending transaction is real, names the output, and is mined on the chain.</summary>
    Proven,
    /// <summary>The evidence was checked and does not hold (or was asked for and not served).</summary>
    Failed,
    /// <summary>The evidence looked right but its block could not be placed: the headers were not
    /// to be had. Nothing was learned about the claim.</summary>
    CannotCheck,
}

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
///      header's merkle root AND the header must satisfy its own in-range PoW — and, whenever
///      a header feed is wired, the block must be PROVEN on the chain at its claimed height
///      (<see cref="HeaderChainSync.LocateAsync"/>: its hash links forward to a trusted anchor).
///      The chain check is the part that matters: a standalone header that meets its own target
///      costs only ~2^20 scrypt hashes to forge (bug-bounty report 2026-10-25), so without a
///      header feed the proof is a sanity check, not proof of mining.
/// Steps 1–2 are what protect funds: a forged proof could at worst make the wallet sign a
/// spend of a coin that doesn't exist, which the network rejects — nothing can be stolen.
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
    /// whose block is proven on the chain at <paramref name="claimedBlockHeight"/> (the
    /// gateway's height for the coin, checked, never trusted) when a header feed is wired —
    /// M3: proves the tx is genuinely mined, not fabricated. When
    /// <paramref name="expectedAddress"/> is supplied, the output's scriptPubKey must belong
    /// to it — closing the latent M1: a lying indexer can't slip a real THIRD-PARTY output
    /// into the wallet's input list (which the wallet couldn't sign, so the whole tx would
    /// have failed at build time — an availability gap, and a theft-adjacent hole if the key
    /// resolver ever loosened). Caught here instead, before signing.
    /// </summary>
    public async Task<UtxoVerification> VerifyAsync(
        string txId, int vout, long claimedSatoshis, bool requireInclusionProof,
        string? expectedAddress = null, long claimedBlockHeight = 0, CancellationToken ct = default)
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
        long? height = null;
        if (requireInclusionProof)
        {
            var inclusion = await InclusionAsync(txId, claimedBlockHeight, ct);
            if (inclusion.Error is string notMined)
                return new UtxoVerification(false, realSatoshis, false, notMined);
            included = true;
            height = inclusion.Height;
        }

        // 4. What kind of coin it is, and where: both from what was just proven, not from the row.
        return new UtxoVerification(true, realSatoshis, included, null, fundingTx.IsCoinBase, height);
    }

    /// <summary>Step 3 on its own: the tx's merkle proof must verify, and — whenever a header feed
    /// is wired — its block must be proven on the chain at <paramref name="claimedBlockHeight"/>.
    /// A null error means the tx is proven mined; the height is where the wallet's own header
    /// chain places the block (null without a header feed).</summary>
    private async Task<(string? Error, long? Height)> InclusionAsync(string txId, long claimedBlockHeight, CancellationToken ct)
    {
        var proofHex = await _reader.GetTxOutProofAsync(txId, ct);
        if (string.IsNullOrWhiteSpace(proofHex))
            return ("No merkle inclusion proof was available for the funding transaction.", null);
        var (error, height, _) = await InclusionFromProofAsync(proofHex, txId, claimedBlockHeight, ct);
        return (error, height);
    }

    /// <summary>The same test for a proof already in hand (whoever served it). The membership is
    /// the header chain's verdict on the proof's block (null when the proof itself failed, or no
    /// header feed is wired); <paramref name="notFrom"/> keeps the headers that place the block
    /// away from those gateways (<see cref="HeaderChainSync.LocateAsync"/>).</summary>
    private async Task<(string? Error, long? Height, ChainMembership? Membership)> InclusionFromProofAsync(
        string proofHex, string txId, long claimedBlockHeight, CancellationToken ct, IReadOnlyCollection<Uri>? notFrom = null)
    {
        var (proofOk, blockHash) = CheckProof(proofHex, txId);
        if (!proofOk || blockHash == null)
            return ("The merkle inclusion proof did not verify — the transaction may not be mined.", null, null);
        if (_headerChain is { Available: true })
        {
            var where = await _headerChain.LocateAsync(blockHash, claimedBlockHeight, ct, notFrom);
            if (where.Membership != ChainMembership.OnChain) return (ChainError(where.Membership), null, where.Membership);
            return (null, where.Height, where.Membership);
        }
        return (null, null, null);
    }

    /// <summary>
    /// Checks a claim that an output has been SPENT (bug-bounty report 2026-11-06, sixth round).
    /// Everything above proves a coin was created; none of it can show the coin is still there,
    /// and a gateway that went on listing a spent coin had it counted as balance. Unspentness
    /// cannot be proven without the whole chain — but a spend can: the spending transaction
    /// must hash to its id (so it is the transaction, not a description of one), must name this
    /// exact output among its inputs, and must be mined — the same inclusion test a coin's own
    /// transaction passes, against the header chain wherever one is wired. The evidence comes
    /// with the claim (<see cref="LiteSpendClaim"/>), served by a gateway other than the one that
    /// listed the coin, so the lister cannot withhold it.
    /// <para>The spender's signature is not evaluated: with a header chain the block's work is
    /// what makes the transaction real, and a head without one already treats every proof as a
    /// sanity check (see the class summary).</para>
    /// </summary>
    public async Task<bool> VerifySpendAsync(LiteSpendClaim claim, CancellationToken ct = default) =>
        await JudgeSpendAsync(claim, null, ct) == SpendProof.Proven;

    /// <summary>
    /// <see cref="VerifySpendAsync"/> with the two things a caller checking a gateway's listing
    /// needs (audit 2026-11-09). <paramref name="notFrom"/> — the gateway that LISTED the coin:
    /// the headers that place the spender's block are fetched like the evidence was, from another
    /// server, because the lister could otherwise leave the spend unproven by serving none of
    /// them. And the answer tells a proof that was checked and FAILED from one that could not be
    /// checked for want of headers (<see cref="SpendProof.CannotCheck"/>), which says nothing
    /// about the claim either way. A claim that names no height can never be checked: it fails.
    /// </summary>
    public async Task<SpendProof> JudgeSpendAsync(LiteSpendClaim claim, IReadOnlyCollection<Uri>? notFrom, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(claim.SpenderHex) || string.IsNullOrWhiteSpace(claim.SpenderProofHex))
            return SpendProof.Failed;

        Transaction spender;
        try { spender = Transaction.Parse(claim.SpenderHex, _network); }
        catch { return SpendProof.Failed; }

        if (!string.Equals(spender.GetHash().ToString(), claim.SpenderTxId, StringComparison.OrdinalIgnoreCase))
            return SpendProof.Failed;
        if (claim.Vout < 0 || !spender.Inputs.Any(i => i.PrevOut.N == (uint)claim.Vout &&
                string.Equals(i.PrevOut.Hash.ToString(), claim.TxId, StringComparison.OrdinalIgnoreCase)))
            return SpendProof.Failed;

        var inclusion = await InclusionFromProofAsync(claim.SpenderProofHex, claim.SpenderTxId, claim.SpenderHeight, ct, notFrom);
        if (inclusion.Error == null) return SpendProof.Proven;
        return inclusion.Membership == ChainMembership.CannotCheck && claim.SpenderHeight > 0
            ? SpendProof.CannotCheck
            : SpendProof.Failed;
    }

    /// <summary>
    /// Verifies a RECEIVED history row (bug-bounty report 2026-11-06: history showed whatever
    /// rows the gateway served). The raw transaction must hash to <paramref name="txId"/>, must
    /// pay at least <paramref name="claimedSatoshis"/> to addresses in
    /// <paramref name="ownedAddresses"/>, and must be proven mined (as in
    /// <see cref="VerifyAsync"/>). <see cref="UtxoVerification.RealSatoshis"/> is what the
    /// transaction really pays the wallet. "At least", because the row is a NET amount: a
    /// transaction that also spends the wallet's own coins nets lower than its outputs.
    /// </summary>
    public async Task<UtxoVerification> VerifyReceiptAsync(
        string txId, IReadOnlyCollection<string> ownedAddresses, long claimedSatoshis,
        long claimedBlockHeight = 0, CancellationToken ct = default)
    {
        var hex = await _reader.GetRawTransactionHexAsync(txId, ct);
        if (string.IsNullOrWhiteSpace(hex))
            return new UtxoVerification(false, 0, false, "Could not fetch the transaction for verification.");

        Transaction tx;
        try { tx = Transaction.Parse(hex, _network); }
        catch { return new UtxoVerification(false, 0, false, "The transaction hex was malformed."); }

        if (!string.Equals(tx.GetHash().ToString(), txId, StringComparison.OrdinalIgnoreCase))
            return new UtxoVerification(false, 0, false, "The transaction does not hash to its claimed id — the data source is untrustworthy.");

        var ownedScripts = ownedAddresses
            .Select(a => ScriptForAddress(a, _network))
            .Where(s => s != null)
            .ToList();
        long paid = 0;
        foreach (var output in tx.Outputs)
        {
            var script = output.ScriptPubKey.ToBytes();
            if (ownedScripts.Any(s => s!.AsSpan().SequenceEqual(script)))
                paid += output.Value.Satoshi;
        }
        if (paid < claimedSatoshis)
            return new UtxoVerification(false, paid, false,
                "The transaction does not pay this wallet the amount shown — the data source is untrustworthy.");

        var inclusion = await InclusionAsync(txId, claimedBlockHeight, ct);
        if (inclusion.Error is string notMined)
            return new UtxoVerification(false, paid, false, notMined);

        return new UtxoVerification(true, paid, true, null, tx.IsCoinBase, inclusion.Height);
    }

    /// <summary>The scriptPubKey an address commits to — the ONE place the wallet maps an
    /// address string to script bytes for ownership checks: legacy "B…" → 25-byte P2PKH,
    /// post-quantum "BQ…" → 34-byte P2PQH. Null for anything else (P2SH, foreign, garbage),
    /// which the caller treats as "not ours".</summary>
    public static byte[]? ScriptForAddress(string address, Network? network = null)
        => BlazecoinAddress.ScriptPubKeyFor(address, network);

    /// <summary>Why a proof's block didn't count, in the words the send/tx pages show.</summary>
    private static string ChainError(ChainMembership membership) => membership switch
    {
        ChainMembership.NotOnChain => "The coin's block is not on your verified chain — the data source is untrustworthy.",
        ChainMembership.NotYetVerified => "Your wallet is still verifying the newest blocks — try again in a minute.",
        _ => "Could not check the coin's block against your verified chain.",
    };

    /// <summary>Shared proof check: parses the MerkleBlock, verifies in-range scrypt-PoW + merkle
    /// root + txid membership, and returns the proof's block hash (SHA256d) on success so a
    /// caller can locate it on the verified chain. Rejects junk and absurd-difficulty headers,
    /// but an in-range standalone header is cheap to forge — chain membership is the real test.</summary>
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

            // Header PoW: nBits in range and the header's SCRYPT hash meeting it. (NBitcoin's
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
    /// <param name="ProofValid">The merkle+PoW inclusion proof is well-formed against its own
    /// header. NOT a trust signal on its own (a standalone header is cheap to forge) — only
    /// <paramref name="OnVerifiedChain"/> is.</param>
    /// <param name="OffChain">The proof's block was proven NOT to be the chain's block at the
    /// claimed height — the gateway's data contradicts the real chain.</param>
    /// <param name="OnVerifiedChain">The proof's block is a block on the PoW-verified header
    /// chain (not just any header meeting its own target) — the strongest guarantee.</param>
    /// <param name="Height">The tx's TRUSTLESS block height, derived purely from the proof's
    /// header matched into the verified chain (never the gateway's claim); null when the block
    /// isn't in the verified window.</param>
    public sealed record ChainInclusion(bool ProofValid, bool OnVerifiedChain, long? Height, string? Error, bool OffChain = false);

    /// <summary>
    /// Per-tx trustless height (the M3 polish): fetch the tx's merkle proof, verify it
    /// (scrypt-PoW + merkle + txid), then LOCATE the proof's block on the wallet's own verified
    /// chain — directly when the sync has placed it, else by linking it from
    /// <paramref name="claimedBlockHeight"/> (the gateway's height, checked, never trusted) to a
    /// trusted anchor. If found, the tx's height is known with zero gateway trust. When the
    /// chain can't place it (no header feed, no height, not synced that far), OnVerifiedChain =
    /// false and the tx is simply NOT independently confirmed — callers must not present
    /// ProofValid alone as verification; a proven contradiction sets OffChain.
    /// </summary>
    public async Task<ChainInclusion> VerifyChainInclusionAsync(string txId, long claimedBlockHeight = 0, CancellationToken ct = default)
    {
        var proofHex = await _reader.GetTxOutProofAsync(txId, ct);
        if (string.IsNullOrWhiteSpace(proofHex))
            return new ChainInclusion(false, false, null, "No merkle inclusion proof was available.");

        var (ok, blockHash) = CheckProof(proofHex, txId);
        if (!ok || blockHash == null)
            return new ChainInclusion(false, false, null, "The merkle inclusion proof did not verify.");

        if (_headerChain is not { Available: true })
        {
            var placed = _headerChain?.VerifiedHeightOf(blockHash);
            return new ChainInclusion(true, placed != null, placed, null);
        }

        var where = await _headerChain.LocateAsync(blockHash, claimedBlockHeight, ct);
        return where.Membership switch
        {
            ChainMembership.OnChain => new ChainInclusion(true, true, where.Height, null),
            ChainMembership.NotOnChain => new ChainInclusion(true, false, null, ChainError(where.Membership), OffChain: true),
            _ => new ChainInclusion(true, false, null, ChainError(where.Membership)),
        };
    }
}

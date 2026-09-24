using System.Collections.Concurrent;
using BlazecoinWallet.Lite.Data;
using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>
/// The send tail shared by every spend mode (fixed send, send-max, WIF sweep): chain-verify
/// each input (C1/M3 — build with the REAL txid-committed value), sign with the caller's
/// key resolver, relay with the duplicate-recovery and P2P outage fallback, and remember
/// the session's own txids so the payment watcher can tell our change from real income.
/// Extracted from LiteWalletService (2026-07-25 audit F1) — this class owns HOW a spend
/// executes; the service owns WHAT to spend.
/// </summary>
internal sealed class LiteSendPipeline
{
    private readonly ITxRelay _relay;
    private readonly IP2PBroadcaster? _p2p;
    private readonly IReadOnlyList<string>? _p2pNodes;
    private readonly LiteTxVerifier? _verifier;
    private readonly ConcurrentDictionary<string, byte> _sessionSentTxIds = new(StringComparer.OrdinalIgnoreCase);

    public LiteSendPipeline(ITxRelay relay, IP2PBroadcaster? p2p, IReadOnlyList<string>? p2pNodes, LiteTxVerifier? verifier)
    {
        _relay = relay;
        _p2p = p2p;
        _p2pNodes = p2pNodes;
        _verifier = verifier;
    }

    /// <summary>Txids this session broadcast itself (sends + sweeps).</summary>
    public IReadOnlyCollection<string> SessionSentTxIds => _sessionSentTxIds.Keys.ToList();

    public void Reset() => _sessionSentTxIds.Clear();

    /// <summary>
    /// Executes a spend of <paramref name="inputs"/>. <paramref name="amountSatoshis"/> is
    /// the fixed amount to pay — or null for a SWEEP, which pays the whole verified input
    /// total in one output with no change (audit F4: the mode is the parameter's shape,
    /// not a flag + sentinel pair).
    /// </summary>
    /// <param name="onChangeUsed">Invoked once, after a SUCCESSFUL broadcast, iff a change
    /// output was actually paid to <paramref name="changeAddress"/> — the service advances its
    /// change-chain index here so a fresh change address is used next time (BIP44 change
    /// chain), with no gaps for a restore to trip over.</param>
    /// <param name="pqKeyResolver">Supplies the ML-DSA-44 key owning a post-quantum (BQ…)
    /// input address. Null when the caller owns no PQ coins (a WIF sweep); a PQ input with no
    /// resolver is a build error, never a silent skip.</param>
    public async Task<LiteSendResult> ExecuteAsync(
        string destinationAddress, List<LiteUtxo> inputs, long? amountSatoshis,
        string changeAddress, Func<string, Key> keyResolver, CancellationToken ct,
        Func<Task>? onChangeUsed = null, Func<string, Pq.PqKey>? pqKeyResolver = null)
    {
        // ── Trustless verification (C1/M3) ──────────────────────────────────────────────
        // Before signing, confirm each selected input against the chain: the funding tx must
        // hash to its claimed txid (binding the real output value) AND carry a merkle+PoW
        // inclusion proof (proving it's really mined). Build with the VERIFIED value so a
        // lying indexer can't inflate the fee. Skipped when no verifier (trusted personal
        // node) is wired.
        long inputTotal = inputs.Sum(u => u.Satoshis);
        if (_verifier != null)
        {
            var verified = new List<LiteUtxo>(inputs.Count);
            long verifiedTotal = 0;
            foreach (var u in inputs)
            {
                var v = await _verifier.VerifyAsync(u.TxId, u.Vout, u.Satoshis,
                    requireInclusionProof: true, expectedAddress: u.Address, ct);
                if (!v.Verified)
                    return new LiteSendResult(false, null,
                        $"Could not verify your coins against the chain: {v.Error} Refresh and try again.");
                verified.Add(u with { Satoshis = v.RealSatoshis }); // build with the REAL value
                verifiedTotal += v.RealSatoshis;
            }
            inputs = verified;
            inputTotal = verifiedTotal;
        }

        // A sweep pays the whole (verified) input total, leaving no change; a fixed send
        // pays the request and any remainder returns as change.
        var sendAmount = amountSatoshis ?? inputTotal;
        if (amountSatoshis != null && inputTotal < amountSatoshis)
            return new LiteSendResult(false, null,
                "Your verified balance is lower than expected — the coin data may be stale. Refresh and try again.");

        SignedTransaction signed;
        try
        {
            signed = LiteTransactionBuilder.BuildAndSign(
                inputs, destinationAddress, sendAmount,
                changeAddress: changeAddress,
                keyForAddress: keyResolver,
                pqKeyForAddress: pqKeyResolver);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return new LiteSendResult(false, null, ex.Message);
        }

        async Task NoteChangeUsedAsync()
        {
            if (signed.ChangeCreated && onChangeUsed != null) await onChangeUsed();
        }

        var relay = await _relay.BroadcastAsync(signed.Hex, ct);
        if (relay.Success)
        {
            _sessionSentTxIds.TryAdd(relay.TxId ?? signed.TxId, 0);
            await NoteChangeUsedAsync();
            return new LiteSendResult(true, relay.TxId, null);
        }

        // Typed verdicts (F1 2026-07-20) — the relays classify, we just switch:
        // DUPLICATE: attempt #1 delivered the tx but its response was lost — the send
        // WORKED, and we know our own txid (we computed it while signing).
        if (relay.Failure == BroadcastFailureKind.Duplicate)
        {
            _sessionSentTxIds.TryAdd(signed.TxId, 0);
            await NoteChangeUsedAsync();
            return new LiteSendResult(true, signed.TxId, null);
        }

        // Total-backend-outage fallback: the gateways were UNREACHABLE (nobody judged the
        // tx), so push it over the raw P2P protocol to any listening full node. One
        // accepting node relays it network-wide. A REJECTION never comes down this path.
        if (relay.Failure == BroadcastFailureKind.Unreachable && _p2p != null)
        {
            var acceptedBy = await _p2p.TryBroadcastAsync(signed.Hex, _p2pNodes, ct: ct);
            if (acceptedBy != null)
            {
                _sessionSentTxIds.TryAdd(signed.TxId, 0);
                await NoteChangeUsedAsync();
                return new LiteSendResult(true, signed.TxId, null, Via: "p2p");
            }
        }

        return new LiteSendResult(false, null, relay.Error);
    }
}

using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>How a sweep broadcast ended.</summary>
public enum SweepSendStatus
{
    /// <summary>Every batch was accepted by the node.</summary>
    AllSent,
    /// <summary>The node could not build a batch (nothing of that batch left the machine).</summary>
    BuildFailed,
    /// <summary>Signing failed or was incomplete — usually a locked wallet (nothing of that batch was sent).</summary>
    SignFailed,
    /// <summary>The node answered and refused the batch (it was not sent).</summary>
    Rejected,
    /// <summary>The node's RPC queue was full; the request provably never ran (not sent, safe to try later).</summary>
    NotSentDaemonBusy,
    /// <summary>The send call timed out or the connection broke: the batch MAY have gone out. Never retried.</summary>
    Unknown,
}

/// <summary>The outcome of one broadcast: what was sent, and where and why it stopped.</summary>
public sealed record SweepBroadcastResult(
    SweepSendStatus Status,
    IReadOnlyList<string> SentTxIds,
    int TotalBatches,
    /// <summary>1-based batch that stopped the run; 0 when everything was sent.</summary>
    int FailedBatch,
    string? Detail)
{
    public bool Succeeded => Status == SweepSendStatus.AllSent;

    /// <summary>The sentence the page shows. The UNKNOWN wording is the 2026-08-15 rule: a timed-out
    /// send is not a failed send, and retrying one is how coins get paid twice.</summary>
    public string Describe()
    {
        if (Succeeded)
            return $"Sent {SentTxIds.Count} transaction{(SentTxIds.Count == 1 ? "" : "s")}. Rescan to see the new picture.";
        var already = SentTxIds.Count > 0
            ? $" {SentTxIds.Count} earlier batch{(SentTxIds.Count == 1 ? " was" : "es were")} already sent."
            : "";
        var why = string.IsNullOrWhiteSpace(Detail) ? "" : $" ({Detail})";
        return Status switch
        {
            SweepSendStatus.Unknown =>
                $"Batch {FailedBatch} of {TotalBatches}: result UNKNOWN{why} — it may have gone out. Do NOT retry: rescan, and look the coins up on the explorer first.{already}",
            SweepSendStatus.BuildFailed => $"Batch {FailedBatch} of {TotalBatches}: the node couldn't build the transaction{why}; it was not sent.{already} Rescan before trying again.",
            SweepSendStatus.SignFailed => $"Batch {FailedBatch} of {TotalBatches}: signing failed or was incomplete — is the wallet unlocked?{why} It was not sent.{already} Rescan before trying again.",
            SweepSendStatus.Rejected => $"Batch {FailedBatch} of {TotalBatches}: the node refused the transaction{why}; it was not sent.{already} Rescan before trying again.",
            SweepSendStatus.NotSentDaemonBusy => $"Batch {FailedBatch} of {TotalBatches}: the node was too busy to take it; it was not sent.{already} Rescan, then try again in a minute.",
            _ => $"Batch {FailedBatch} of {TotalBatches}: stopped.{already}",
        };
    }
}

/// <summary>Builds, signs and broadcasts a sweep plan batch by batch.</summary>
public interface ISweepBroadcaster
{
    Task<SweepBroadcastResult> BroadcastAsync(SweepPlan plan, CancellationToken ct = default);
}

/// <summary>
/// Raw-transaction path (explicit inputs, explicit single output) over <see cref="IVintageSendRpc"/>.
/// Stops at the first batch that does not go out, and NEVER calls send twice for one batch: a
/// timeout or broken connection during <c>sendrawtransaction</c> is reported as UNKNOWN.
/// </summary>
public sealed class SweepBroadcaster : ISweepBroadcaster
{
    private readonly IVintageSendRpc _rpc;

    public SweepBroadcaster(IVintageSendRpc rpc) => _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));

    public async Task<SweepBroadcastResult> BroadcastAsync(SweepPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sent = new List<string>();
        var total = plan.Transactions.Count;
        SweepBroadcastResult Stop(SweepSendStatus s, int batch, string? detail) => new(s, sent, total, batch, detail);

        if (!plan.Balances) return Stop(SweepSendStatus.BuildFailed, 1, "the plan does not balance");

        for (var i = 0; i < total; i++)
        {
            var tx = plan.Transactions[i];
            var batch = i + 1;

            string? raw;
            try
            {
                raw = await _rpc.CreateRawTransactionAsync(
                    tx.Inputs.Select(x => (x.TxId, x.Vout)).ToList(),
                    new[] { (tx.Destination, tx.OutputSatoshis) }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Stop(SweepSendStatus.BuildFailed, batch, ex.Message); }
            if (string.IsNullOrEmpty(raw)) return Stop(SweepSendStatus.BuildFailed, batch, null);

            string? signed;
            try { signed = await _rpc.SignRawTransactionAsync(raw, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Stop(SweepSendStatus.SignFailed, batch, ex.Message); }
            if (string.IsNullOrEmpty(signed)) return Stop(SweepSendStatus.SignFailed, batch, null);

            // From here on the transaction exists, signed. Exactly ONE send attempt.
            string? txid;
            try { txid = await _rpc.SendRawTransactionAsync(signed, CancellationToken.None); }
            catch (RpcException ex) { return Stop(SweepSendStatus.Rejected, batch, $"{ex.Code}: {ex.Message}"); }
            catch (DaemonBusyException) { return Stop(SweepSendStatus.NotSentDaemonBusy, batch, null); }
            catch (Exception ex) when (ex is TimeoutException or HttpRequestException or TaskCanceledException)
            {
                return Stop(SweepSendStatus.Unknown, batch, ex.Message);
            }
            if (string.IsNullOrEmpty(txid)) return Stop(SweepSendStatus.Rejected, batch, null);
            sent.Add(txid);
        }
        return new SweepBroadcastResult(SweepSendStatus.AllSent, sent, total, 0, null);
    }
}

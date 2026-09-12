namespace BlazecoinWallet.Lite.Data;

/// <summary>Address summary as served by the data source.</summary>
public sealed record LiteAddressSummary(long Balance, long TotalReceived, long TotalSent, int TxCount);

/// <summary>One history row from the data source's address ledger.</summary>
public sealed record LiteHistoryEntry(string TxId, long BlockHeight, DateTime Timestamp, long Amount, string Type);

/// <summary>A spendable output plus the maturity facts the wallet needs to filter on.</summary>
public sealed record LiteChainUtxo(string TxId, int OutputIndex, long Amount, int Confirmations, long BlockHeight, bool IsCoinbase);

/// <summary>Why a broadcast did not succeed — typed so callers never parse error strings (F1).</summary>
public enum BroadcastFailureKind
{
    /// <summary>It succeeded.</summary>
    None,
    /// <summary>The network JUDGED the transaction and said no. Never retry elsewhere.</summary>
    Rejected,
    /// <summary>The network already has this transaction — an earlier attempt delivered it.
    /// The send actually WORKED (the caller knows its own txid).</summary>
    Duplicate,
    /// <summary>Nobody judged anything — the relay could not be reached. Fallback-eligible.</summary>
    Unreachable,
}

/// <summary>Result of relaying a signed transaction.</summary>
/// <param name="Success">True when the network accepted the transaction.</param>
/// <param name="TxId">The network txid on success.</param>
/// <param name="Error">Human-readable reason on failure.</param>
/// <param name="Failure">Typed failure classification (see <see cref="BroadcastFailureKind"/>).</param>
public sealed record LiteBroadcastResult(bool Success, string? TxId, string? Error,
    BroadcastFailureKind Failure = BroadcastFailureKind.None)
{
    public static LiteBroadcastResult Ok(string txId) => new(true, txId, null);
    public static LiteBroadcastResult Fail(string error, BroadcastFailureKind kind) => new(false, null, error, kind);
}

/// <summary>
/// Read side of the lite wallet's chain window (ISP — the Dashboard needs only this).
/// </summary>
public interface IChainReader
{
    /// <summary>False when this source cannot serve history at all (e.g. a bare personal
    /// node has no address index) — the UI must say "unsupported", not "no activity" (F2).</summary>
    bool SupportsHistory { get; }

    /// <summary>Address summary; null when the address has never been seen on-chain.</summary>
    Task<LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default);

    /// <summary>Unspent outputs for an address (empty when none / unknown address).</summary>
    Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default);

    /// <summary>Most-recent-first history page (always empty when <see cref="SupportsHistory"/> is false).</summary>
    Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default);

    /// <summary>
    /// True when this source can supply raw funding transactions + merkle proofs for
    /// trustless input verification (gateway mode). False for a source whose values are
    /// already authoritative (personal-node: own chainstate) — no verification needed.
    /// </summary>
    bool SupportsChainVerification { get; }

    /// <summary>Raw serialized transaction hex (double-SHA256 = txid), or null if unavailable.</summary>
    Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default);

    /// <summary>Serialized merkle inclusion proof (a MerkleBlock) for a confirmed txid, or null.</summary>
    Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default);
}

/// <summary>Write side: relays a client-signed raw transaction to the network (ISP).</summary>
public interface ITxRelay
{
    Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default);
}

/// <summary>
/// A full data source (reads + relay) — what the concrete gateway / personal-node
/// services implement; consumers should depend on the narrowest slice they need.
/// </summary>
public interface ILiteWalletData : IChainReader, ITxRelay;

/// <summary>
/// Shared broadcast-failure classifier so every relay maps daemon reasons the same way —
/// the duplicate-detection strings live HERE, once (F1).
/// </summary>
public static class BroadcastReasons
{
    public static BroadcastFailureKind Classify(string? reason)
    {
        if (reason == null) return BroadcastFailureKind.Rejected;
        return reason.Contains("already in", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("already known", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("txn-already", StringComparison.OrdinalIgnoreCase)
            ? BroadcastFailureKind.Duplicate
            : BroadcastFailureKind.Rejected;
    }
}

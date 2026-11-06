namespace BlazecoinWallet.Lite.Data;

/// <summary>Address summary as served by the data source.</summary>
public sealed record LiteAddressSummary(long Balance, long TotalReceived, long TotalSent, int TxCount);

/// <summary>One history row from the data source's address ledger.</summary>
/// <param name="Check">Where a RECEIVED row stands against the chain (set by the wallet service,
/// never by a data source): see <see cref="LiteHistoryCheck"/>.</param>
public sealed record LiteHistoryEntry(string TxId, long BlockHeight, DateTime Timestamp, long Amount, string Type,
    LiteHistoryCheck Check = LiteHistoryCheck.NotChecked);

/// <summary>
/// How far the wallet has verified a history row against the chain (bug-bounty report
/// 2026-11-06: a gateway could serve a "received" row for a transaction that does not exist).
/// </summary>
public enum LiteHistoryCheck
{
    /// <summary>Nothing to check: a sent row, or a source whose word is authoritative (the user's
    /// own node).</summary>
    NotChecked,
    /// <summary>A received row not yet in a block — it cannot be proven until it is mined.</summary>
    Unconfirmed,
    /// <summary>A received row whose check has not finished.</summary>
    Pending,
    /// <summary>Proven: the transaction is on the chain and pays this wallet at least the amount shown.</summary>
    Verified,
    /// <summary>Checked and NOT proven — shown as such, never as plain income.</summary>
    Unverified,
}

/// <summary>A spendable output plus the maturity facts the wallet needs to filter on.</summary>
/// <param name="ScriptPubKey">The output's scriptPubKey as hex, when the source serves it
/// (newer gateways do; null from older ones or a personal node). The wallet derives the
/// script from the owning address anyway — P2PKH for "B…", P2PQH for "BQ…" — so this is
/// informational, and its absence is never an error.</param>
public sealed record LiteChainUtxo(string TxId, int OutputIndex, long Amount, int Confirmations, long BlockHeight, bool IsCoinbase,
    string? ScriptPubKey = null);

/// <summary>Why a broadcast did not succeed — typed so callers never parse error strings (F1).</summary>
public enum BroadcastFailureKind
{
    /// <summary>It succeeded.</summary>
    None,
    /// <summary>The network JUDGED the transaction and said no. Never retry elsewhere. A gateway
    /// relay reports this only as far as it could check: see
    /// <see cref="LiteBroadcastResult.Uncorroborated"/>.</summary>
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
/// <param name="Uncorroborated">True when this verdict is ONE gateway's word and no second
/// gateway could back it (bug-bounty reports 2026-11-06). A relay whose answer is authoritative
/// — the user's own node — never sets it. The send pipeline uses it to hand a transaction that
/// only one gateway claims to have relayed straight to network nodes as well.</param>
public sealed record LiteBroadcastResult(bool Success, string? TxId, string? Error,
    BroadcastFailureKind Failure = BroadcastFailureKind.None, bool Uncorroborated = false)
{
    public static LiteBroadcastResult Ok(string txId, bool uncorroborated = false) => new(true, txId, null, Uncorroborated: uncorroborated);
    public static LiteBroadcastResult Fail(string error, BroadcastFailureKind kind, bool uncorroborated = false)
        => new(false, null, error, kind, uncorroborated);
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

/// <summary>
/// A read source that can put "has this address ever been used?" to more than one gateway
/// (bug-bounty report 2026-11-06, third round). Restore discovery stops after a run of unused
/// addresses, so one gateway answering "unused" for a used address hid every coin beyond it.
/// "Used" is the safe answer to believe — it only makes the scan go further — so the address
/// counts as used when ANY gateway asked reports activity. A source with one gateway (a custom
/// setup, the user's own node) simply has nobody else to ask.
/// </summary>
public interface IAddressUsageCrossCheck
{
    Task<bool> IsAddressUsedByAnyGatewayAsync(string address, CancellationToken ct = default);
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

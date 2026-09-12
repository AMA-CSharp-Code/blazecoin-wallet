namespace BlazecoinWallet.Core.Services.Provenance;

/// <summary>
/// The narrow daemon slice the provenance tracer needs (SOLID audit #3 segregation —
/// see RpcInterfaces.cs): the wallet's unspent outputs, arbitrary transactions by id,
/// and block height/time by hash. Deliberately read-only.
/// </summary>
public interface IAncestryRpc
{
    /// <summary>The active wallet's UTXOs (`listunspent`).</summary>
    Task<IReadOnlyList<UnspentOutput>> ListUnspentAsync(CancellationToken ct = default);

    /// <summary>
    /// One transaction by id, or null when it can't be read. Tries `getrawtransaction`
    /// (needs `txindex=1` for transactions the wallet doesn't own) and falls back to the
    /// wallet's own `gettransaction`, so a wallet full of coinbases traces fine on a node
    /// with no tx index at all.
    /// </summary>
    Task<RawTransaction?> GetTransactionAsync(string txid, CancellationToken ct = default);

    /// <summary>Height + timestamp for a block hash (`getblockheader`), or null.</summary>
    Task<BlockRef?> GetBlockRefAsync(string blockHash, CancellationToken ct = default);
}

/// <summary>
/// Building and broadcasting a vintage send. Deliberately raw-transaction based: the
/// convenience RPCs (`sendtoaddress`, `fundrawtransaction`) choose their own inputs and
/// may re-order outputs, either of which silently destroys a FIFO slice.
/// </summary>
public interface IVintageSendRpc
{
    /// <summary>A fresh address of this wallet for change.</summary>
    Task<string?> GetChangeAddressAsync(CancellationToken ct = default);

    /// <summary>`createrawtransaction` — inputs and outputs kept in the EXACT order given.</summary>
    Task<string?> CreateRawTransactionAsync(
        IReadOnlyList<(string TxId, int Vout)> inputs,
        IReadOnlyList<(string Address, long Satoshis)> outputs,
        CancellationToken ct = default);

    /// <summary>`signrawtransactionwithwallet` — the signed hex, or null if incomplete.</summary>
    Task<string?> SignRawTransactionAsync(string rawHex, CancellationToken ct = default);

    /// <summary>`sendrawtransaction` — the txid once accepted by the network.</summary>
    Task<string?> SendRawTransactionAsync(string signedHex, CancellationToken ct = default);
}

/// <summary>One row of `listunspent`, amount already in satoshis.</summary>
public sealed record UnspentOutput(string TxId, int Vout, long Satoshis, string? Address, int Confirmations);

/// <summary>A block's identity for vintage classification.</summary>
public sealed record BlockRef(long Height, DateTime TimeUtc);

/// <summary>An input, referencing the output it spends (null txid = coinbase).</summary>
public sealed record RawTxInput(string? TxId, int Vout);

/// <summary>An output: its index in the transaction and its value in satoshis.</summary>
public sealed record RawTxOutput(int N, long Satoshis);

/// <summary>
/// A decoded transaction, normalised across `getrawtransaction` and `gettransaction`
/// (the latter carries the height directly; the former needs a `getblockheader` hop).
/// </summary>
public sealed record RawTransaction(
    string TxId,
    bool IsCoinbase,
    string? BlockHash,
    long? BlockHeight,
    DateTime? BlockTimeUtc,
    IReadOnlyList<RawTxInput> Inputs,
    IReadOnlyList<RawTxOutput> Outputs);

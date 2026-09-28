using System.Collections.Concurrent;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Lite.Gateway.Rpc;

namespace BlazecoinWallet.Lite.Gateway.Ancestry;

/// <summary>
/// Process-wide cache for the ancestry reads. Ancestry is immutable once mined, and on
/// this chain everyone's coins funnel through the same pool coinbases — so one caller's
/// walk pre-pays the next's, which matters here in a way it doesn't on the desktop
/// (many users, one daemon, 4 GB box). Crude cap: past the limit the whole cache is
/// dropped rather than LRU-tracked — the refill is exactly the reads we'd have done
/// anyway, and it bounds memory hard.
/// </summary>
public sealed class AncestryReadCache
{
    /// <summary>~200k entries is tens of MB worst case — well inside the box's headroom.</summary>
    private const int MaxTxEntries = 200_000;

    internal readonly ConcurrentDictionary<string, RawTransaction?> Tx = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, BlockRef?> Blocks = new(StringComparer.OrdinalIgnoreCase);

    internal void TrimIfNeeded()
    {
        if (Tx.Count < MaxTxEntries) return;
        Tx.Clear();
        Blocks.Clear();
    }
}

/// <summary>
/// One trace at a time across the whole appliance: a walk is potentially thousands of
/// loopback daemon RPCs, and the daemon's small RPC work queue is the box's scarcest
/// resource — two concurrent whale traces must queue, not interleave.
/// </summary>
public sealed class TraceGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Task<bool> TryEnterAsync(TimeSpan wait, CancellationToken ct) => _gate.WaitAsync(wait, ct);
    public void Exit() => _gate.Release();
}

/// <summary>
/// <see cref="IAncestryRpc"/> over the daemon passthrough — the same read slice the
/// desktop wallet takes from its local node, served here so the lite wallet (no node)
/// can get the identical trace. The tracer itself (<see cref="AncestryTracer"/>) is the
/// desktop's, UNCHANGED, so lite and desktop numbers can never drift.
/// </summary>
public sealed class GatewayAncestryRpc : IAncestryRpc
{
    private readonly DaemonRpcClient _rpc;
    private readonly AncestryReadCache _cache;

    public GatewayAncestryRpc(DaemonRpcClient rpc, AncestryReadCache cache)
    {
        _rpc = rpc;
        _cache = cache;
    }

    /// <summary>Unused: the endpoint hands the tracer its outputs explicitly (from the
    /// SQLite index) — there is no wallet here to listunspent against.</summary>
    public Task<IReadOnlyList<UnspentOutput>> ListUnspentAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UnspentOutput>>([]);

    public async Task<RawTransaction?> GetTransactionAsync(string txid, CancellationToken ct = default)
    {
        if (_cache.Tx.TryGetValue(txid, out var cached)) return cached;
        try
        {
            var tx = await _rpc.GetRawTransactionVerboseAsync(txid, ct);
            var mapped = Map(tx);
            // Only settled facts are cached: a MINED transaction's content and block are
            // immutable (short of a deep reorg — and the endpoint traces confirmed coins
            // only, so the window is the ordinary reorg one, and the cap-flush clears
            // even that eventually).
            if (mapped.BlockHash != null)
            {
                _cache.TrimIfNeeded();
                _cache.Tx[txid] = mapped;
            }
            return mapped;
        }
        catch (DaemonRpcException)
        {
            // A verdict ("no such transaction") — the tracer treats it as unreadable and
            // under-claims. Cached: the daemon's answer for a settled txid won't change.
            _cache.Tx[txid] = null;
            return null;
        }
        // DaemonUnreachableException deliberately propagates: transient outage must
        // surface as 503, never be mistaken for "this coin has no ancestry".
    }

    public async Task<BlockRef?> GetBlockRefAsync(string blockHash, CancellationToken ct = default)
    {
        if (_cache.Blocks.TryGetValue(blockHash, out var cached)) return cached;
        try
        {
            var (height, time) = await _rpc.GetBlockHeaderInfoAsync(blockHash, ct);
            var block = new BlockRef(height, DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime);
            _cache.Blocks[blockHash] = block;
            return block;
        }
        catch (DaemonRpcException)
        {
            _cache.Blocks[blockHash] = null;
            return null;
        }
    }

    private static RawTransaction Map(RpcRawTransaction tx) => new(
        tx.TxId,
        tx.Vin.Any(v => v.Coinbase != null),
        tx.BlockHash,
        BlockHeight: null,   // Core's verbose result has no height; the tracer's header hop fills it
        tx.BlockTime is { } t ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null,
        tx.Vin.Select(v => new RawTxInput(v.TxId, v.Vout ?? 0)).ToList(),
        tx.Vout.Select(o => new RawTxOutput(o.N, o.Satoshis)).ToList());
}

using BlazecoinWallet.Lite.Gateway.Rpc;

namespace BlazecoinWallet.Lite.Gateway.Index;

/// <summary>Live sync facts for the /api/status endpoint and logs. Written by the walker
/// thread, read by request threads — volatile accessors keep cross-thread reads fresh
/// (audit C6); Synced may still observe the fields mid-update, which for a monotonic
/// height pair is at worst one poll interval stale.</summary>
public sealed class WalkerStatus
{
    private long _daemonHeight;
    private long _indexedHeight;
    private int _daemonReachable;

    public long DaemonHeight
    {
        get => Volatile.Read(ref _daemonHeight);
        set => Volatile.Write(ref _daemonHeight, value);
    }

    public long IndexedHeight
    {
        get => Volatile.Read(ref _indexedHeight);
        set => Volatile.Write(ref _indexedHeight, value);
    }

    public bool DaemonReachable
    {
        get => Volatile.Read(ref _daemonReachable) == 1;
        set => Volatile.Write(ref _daemonReachable, value ? 1 : 0);
    }

    /// <summary>Synced = the index is at the daemon tip (mempool freshness rides along).</summary>
    public bool Synced => DaemonReachable && IndexedHeight >= DaemonHeight && DaemonHeight > 0;
}

/// <summary>
/// The RPC block walker: keeps the SQLite index at the daemon tip (reorg-aware within the
/// stored block window) and refreshes the mempool overlay each tick. Single writer by
/// design — endpoints only read. A daemon outage just pauses the loop; nothing breaks.
/// </summary>
public sealed class ChainWalkerService : BackgroundService
{
    private readonly DaemonRpcClient _rpc;
    private readonly UtxoIndexDb _db;
    private readonly MempoolOverlay _mempool;
    private readonly WalkerStatus _status;
    private readonly ILogger<ChainWalkerService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly long _startHeight;

    /// <summary>Cache of mempool tx detail so a tx sitting in the pool isn't re-fetched
    /// every tick (it leaves the cache when it leaves the pool).</summary>
    private readonly Dictionary<string, RpcTransaction> _mempoolTxCache = new(StringComparer.OrdinalIgnoreCase);

    public ChainWalkerService(DaemonRpcClient rpc, UtxoIndexDb db, MempoolOverlay mempool,
        WalkerStatus status, IConfiguration config, ILogger<ChainWalkerService> logger)
    {
        _rpc = rpc;
        _db = db;
        _mempool = mempool;
        _status = status;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(config.GetValue("Index:PollSeconds", 5));
        _startHeight = config.GetValue<long>("Index:StartHeight", 0);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Chain walker starting (poll {Seconds}s, start height {Start})",
            _pollInterval.TotalSeconds, _startHeight);
        using var timer = new PeriodicTimer(_pollInterval);
        do
        {
            try
            {
                await TickAsync(ct);
            }
            catch (DaemonUnreachableException ex)
            {
                _status.DaemonReachable = false;
                _logger.LogWarning("Daemon unreachable, will retry: {Message}", ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A malformed block or logic bug must not kill the service — log loudly, retry.
                _logger.LogError(ex, "Walker tick failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One tick — internal so tests drive it deterministically.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var daemonHeight = await _rpc.GetBlockCountAsync(ct);
        _status.DaemonHeight = daemonHeight;
        _status.DaemonReachable = true; // the call above just proved it

        var tip = _db.GetTip();

        // ── Reorg detection: our stored tip hash must still be the daemon's hash at that
        // height. If not, walk down through the stored window to the fork point and rewind.
        if (tip != null)
        {
            var daemonHashAtTip = tip.Value.Height <= daemonHeight
                ? await _rpc.GetBlockHashAsync(tip.Value.Height, ct)
                : null;
            if (daemonHashAtTip != tip.Value.Hash)
            {
                var fork = await FindForkHeightAsync(tip.Value.Height, ct);
                _logger.LogWarning("Reorg detected at height {Tip}; rewinding to {Fork}", tip.Value.Height, fork);
                _db.RewindToFork(fork);
                tip = _db.GetTip();
            }
        }

        // ── Walk forward to the daemon tip.
        var next = tip?.Height + 1 ?? _startHeight;
        while (next <= daemonHeight && !ct.IsCancellationRequested)
        {
            var hash = await _rpc.GetBlockHashAsync(next, ct);
            var block = await _rpc.GetBlockVerboseAsync(hash, ct);
            _db.ApplyBlock(block);
            _status.IndexedHeight = next;
            if (next % 10_000 == 0)
                _logger.LogInformation("Indexed height {Height} / {Tip}", next, daemonHeight);
            next++;
        }
        _status.IndexedHeight = Math.Max(_status.IndexedHeight, tip?.Height ?? 0);

        await RefreshMempoolAsync(ct);
    }

    private async Task<long> FindForkHeightAsync(long fromHeight, CancellationToken ct)
    {
        for (var h = fromHeight - 1; h > fromHeight - UtxoIndexDb.BlockWindow && h >= 0; h--)
        {
            var stored = _db.GetBlockHash(h);
            if (stored == null) break;
            if (stored == await _rpc.GetBlockHashAsync(h, ct)) return h;
        }
        // Deeper than the window (should never happen on a 30 s chain) — full rebuild.
        _logger.LogError("Reorg deeper than the {Window}-block window — rebuilding the index", UtxoIndexDb.BlockWindow);
        return -1;
    }

    private async Task RefreshMempoolAsync(CancellationToken ct)
    {
        var txIds = await _rpc.GetRawMempoolAsync(ct);
        var live = new HashSet<string>(txIds, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _mempoolTxCache.Keys.Where(k => !live.Contains(k)).ToList())
            _mempoolTxCache.Remove(gone);

        foreach (var txId in txIds)
        {
            if (_mempoolTxCache.ContainsKey(txId)) continue;
            try
            {
                _mempoolTxCache[txId] = await _rpc.GetMempoolTransactionAsync(txId, ct);
            }
            catch (DaemonRpcException)
            {
                // Evicted/mined between the list call and the fetch — next tick sorts it out.
            }
        }

        var outputs = new List<MempoolUtxo>();
        var spends = new List<(string, int)>();
        foreach (var t in _mempoolTxCache.Values)
        {
            foreach (var vout in t.Vout)
            {
                var address = vout.ScriptPubKey?.EffectiveAddress;
                if (address != null) outputs.Add(new MempoolUtxo(t.TxId, vout.N, address, vout.Satoshis));
            }
            foreach (var vin in t.Vin)
                if (vin.TxId != null && vin.Vout != null)
                    spends.Add((vin.TxId, vin.Vout.Value));
        }
        _mempool.Replace(outputs, spends, _mempoolTxCache.Count);
    }
}

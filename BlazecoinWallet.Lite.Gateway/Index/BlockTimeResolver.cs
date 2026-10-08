using System.Collections.Concurrent;
using BlazecoinWallet.Lite.Gateway.Rpc;

namespace BlazecoinWallet.Lite.Gateway.Index;

/// <summary>
/// Resolves block times (unix seconds) by height for history timestamps, caching each
/// permanently — a confirmed block's time never changes, and a reorg only replaces the tip,
/// so a stale cached time for an orphaned height is harmless (the row it belonged to is gone
/// from the index too). A history page spans few distinct heights, so this is a handful of
/// header lookups on first view and free thereafter.
/// </summary>
public sealed class BlockTimeResolver
{
    private readonly DaemonRpcClient _rpc;
    private readonly ConcurrentDictionary<long, long> _cache = new();

    public BlockTimeResolver(DaemonRpcClient rpc) => _rpc = rpc;

    public async Task<long> GetUnixTimeAsync(long height, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(height, out var cached)) return cached;
        var time = await _rpc.GetBlockTimeAsync(height, ct);
        _cache[height] = time;
        return time;
    }
}

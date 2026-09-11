namespace BlazecoinWallet.Core.Services;

public enum DaemonState { Offline, Warmup, Online }

/// <summary>Immutable snapshot of chain/sync state from one
/// getblockchaininfo sample, shared by every component so they all show
/// the exact same numbers.</summary>
public sealed class SyncState
{
    public DaemonState State { get; init; } = DaemonState.Offline;
    public int Blocks { get; init; }
    public int Headers { get; init; }
    public int Behind { get; init; }
    public double Progress { get; init; }   // percent, 0..100
    public bool Synced { get; init; }
    public string Chain { get; init; } = "";
    public int Peers { get; init; }
    public DateTime? WarmupSince { get; init; }
    public string MeridianTip { get; init; } = "";
}

public interface ISyncStateService
{
    SyncState Current { get; }
    event Action? Changed;
}

/// <summary>Single shared poller for chain/sync state. The footer status
/// bar and the dashboard Blockchain card both subscribe to this, so their
/// block heights and sync percentages are always identical (one sample,
/// not two independently-timed polls that drift apart).</summary>
public sealed class SyncStateService : ISyncStateService, IAsyncDisposable
{
    private readonly IChainInfoRpc _rpc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private DateTime? _warmupSince;

    public SyncState Current { get; private set; } = new();
    public event Action? Changed;

    public SyncStateService(IChainInfoRpc rpc)
    {
        _rpc = rpc;
        _loop = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await PollAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await PollAsync();
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task PollAsync()
    {
        var prev = Current;
        try
        {
            var bc = await _rpc.GetBlockchainInfoAsync();

            // Network/peer info is best-effort: a hiccup there must not
            // flip the whole bar to "offline".
            NetworkInfo? net = null;
            try { net = await _rpc.GetNetworkInfoAsync(); } catch { /* optional */ }
            List<PeerSummary> peers = new();
            try { peers = await _rpc.GetPeersAsync(); } catch { /* optional */ }

            int blocks = bc?.Blocks ?? 0;
            int headers = bc?.Headers ?? 0;
            int behind = Math.Max(0, headers - blocks);
            bool ibd = bc?.InitialBlockDownload ?? true;
            bool synced = bc != null && headers > 0 && blocks >= headers && !ibd;

            // During IBD verificationprogress is the honest measure (matches
            // Bitcoin-Qt); once caught up, blocks/headers is meaningful.
            double progress = ibd
                ? (double)(bc?.VerificationProgress ?? 0m)
                : (headers > 0 ? (double)blocks / headers : 0d);
            progress = Math.Clamp(progress * 100d, 0d, 100d);

            _warmupSince = null;
            Current = new SyncState
            {
                State = DaemonState.Online,
                Blocks = blocks,
                Headers = headers,
                Behind = behind,
                Progress = progress,
                Synced = synced,
                Chain = bc?.Chain ?? "",
                Peers = net?.Connections ?? 0,
                WarmupSince = null,
                MeridianTip = BuildTip(bc?.Chain, net?.Connections ?? 0, net?.LocalAddresses, peers),
            };
        }
        catch (RpcException ex) when (ex.Code == -28)
        {
            // RPC_IN_WARMUP: daemon up, loading the block index. Keep the
            // last good numbers; the footer renders the warmup message.
            _warmupSince ??= DateTime.UtcNow;
            Current = new SyncState
            {
                State = DaemonState.Warmup,
                Blocks = prev.Blocks,
                Headers = prev.Headers,
                Behind = prev.Behind,
                Progress = prev.Progress,
                Synced = false,
                Chain = prev.Chain,
                Peers = 0,
                WarmupSince = _warmupSince,
                MeridianTip = prev.MeridianTip,
            };
        }
        catch
        {
            // Truly unreachable — keep last numbers; footer shows offline.
            _warmupSince = null;
            Current = new SyncState
            {
                State = DaemonState.Offline,
                Blocks = prev.Blocks,
                Headers = prev.Headers,
                Behind = prev.Behind,
                Progress = prev.Progress,
                Synced = false,
                Chain = prev.Chain,
                Peers = 0,
                WarmupSince = null,
                MeridianTip = prev.MeridianTip,
            };
        }

        Changed?.Invoke();
    }

    private static string BuildTip(string? chain, int peers, List<LocalAddress>? la, List<PeerSummary> peerList)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Network: ").Append(ChainLabel(chain))
          .Append(" · ").Append(peers).Append(peers == 1 ? " peer" : " peers");

        if (la is { Count: > 0 })
            sb.Append("\nNode IP: ").Append(string.Join(", ", la.Select(a => $"{a.Address}:{a.Port}")));

        if (peerList.Count > 0)
        {
            sb.Append("\nPeers:");
            foreach (var p in peerList.Take(8))
                sb.Append("\n  ").Append(p.Address).Append("  ").Append(p.SubVersion);
            if (peerList.Count > 8)
                sb.Append("\n  +").Append(peerList.Count - 8).Append(" more");
        }
        return sb.ToString();
    }

    public static string ChainLabel(string? chain) => (chain ?? "").ToLowerInvariant() switch
    {
        "main" => "Mainnet",
        "test" => "Testnet",
        "testnet4" => "Testnet4",
        "regtest" => "Regtest",
        "signet" => "Signet",
        "" => "unknown",
        _ => chain!
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { /* ignore shutdown races */ }
        _cts.Dispose();
    }
}

namespace BlazecoinWallet.Core.Services;

public enum DaemonState { Offline, Warmup, Online }

/// <summary>Which stage of catching up the node is in. Core syncs a fresh chain in three passes:
/// a headers PRESYNC that stores nothing (getblockchaininfo says headers=0 the whole time — on this
/// chain ~20 min on an M1, an hour on an old Intel Mac), the real headers download, then blocks.
/// Before this existed the footer showed "0 / 0 blocks · 0.00%" throughout presync and users
/// reported the node as not syncing (2026-09-24).</summary>
public enum SyncPhase { Unknown, Presync, Headers, Blocks, Synced }

/// <summary>Immutable snapshot of chain/sync state from one
/// getblockchaininfo sample, shared by every component so they all show
/// the exact same numbers.</summary>
public sealed class SyncState
{
    public DaemonState State { get; init; } = DaemonState.Offline;
    public int Blocks { get; init; }
    public int Headers { get; init; }
    public int Behind { get; init; }
    public double Progress { get; init; }   // percent, 0..100 — of the CURRENT phase
    public SyncPhase Phase { get; init; } = SyncPhase.Unknown;
    public int PresyncHeaders { get; init; }   // max presynced_headers over peers
    public int TipEstimate { get; init; }      // best guess at the network tip from peers (0 = unknown)
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

            // Peers tell us what getblockchaininfo can't: the presync position and the tip.
            int presync = peers.Count > 0 ? Math.Max(0, peers.Max(p => p.PresyncedHeaders)) : 0;
            int tip = Math.Max(headers, peers.Count > 0
                ? Math.Max(peers.Max(p => p.StartingHeight), Math.Max(peers.Max(p => p.SyncedHeaders), presync))
                : 0);
            var phase = synced ? SyncPhase.Synced
                : headers == 0 && presync > 0 ? SyncPhase.Presync
                : blocks == 0 && headers > 0 && (tip > headers) ? SyncPhase.Headers
                : bc != null && (blocks > 0 || headers > 0) ? SyncPhase.Blocks
                : SyncPhase.Unknown;
            // Progress of the current phase. Presync/headers: against the peers' tip. Blocks: during
            // IBD verificationprogress is the honest measure (matches Bitcoin-Qt); once caught up,
            // blocks/headers is meaningful.
            double progress = phase switch
            {
                SyncPhase.Presync => tip > 0 ? (double)presync / tip : 0d,
                SyncPhase.Headers => tip > 0 ? (double)headers / tip : 0d,
                _ => ibd ? (double)(bc?.VerificationProgress ?? 0m) : (headers > 0 ? (double)blocks / headers : 0d),
            };
            progress = Math.Clamp(progress * 100d, 0d, 100d);

            _warmupSince = null;
            Current = new SyncState
            {
                State = DaemonState.Online,
                Blocks = blocks,
                Headers = headers,
                Behind = behind,
                Progress = progress,
                Phase = phase,
                PresyncHeaders = presync,
                TipEstimate = tip,
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
                Phase = prev.Phase,
                PresyncHeaders = prev.PresyncHeaders,
                TipEstimate = prev.TipEstimate,
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
                Phase = prev.Phase,
                PresyncHeaders = prev.PresyncHeaders,
                TipEstimate = prev.TipEstimate,
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

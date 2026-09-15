using System.Diagnostics;
using BlazecoinWallet.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Tests for <see cref="SyncStateService"/> — the single shared chain/sync poller.
/// Covers the pure <see cref="SyncStateService.ChainLabel"/> mapping and the three state
/// transitions its poll loop produces from one getblockchaininfo sample: Online (+ synced/progress),
/// Warmup on RPC -28, and Offline on any other failure (keeping the last-known numbers).</summary>
public class SyncStateServiceTests
{
    [Theory]
    [InlineData("main", "Mainnet")]
    [InlineData("test", "Testnet")]
    [InlineData("testnet4", "Testnet4")]
    [InlineData("regtest", "Regtest")]
    [InlineData("signet", "Signet")]
    [InlineData("MAIN", "Mainnet")]   // case-insensitive
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("custom", "custom")]  // unrecognised passes through
    public void ChainLabel_maps_known_chains(string? chain, string expected)
        => Assert.Equal(expected, SyncStateService.ChainLabel(chain));

    /// <summary>Spin-wait (the poll runs on a background loop kicked off in the ctor).</summary>
    private static async Task<SyncState> WaitForAsync(ISyncStateService svc, Func<SyncState, bool> cond)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (cond(svc.Current)) return svc.Current;
            await Task.Delay(20);
        }
        return svc.Current;
    }

    [Fact]
    public async Task Online_and_synced_when_blocks_reach_headers_and_not_in_ibd()
    {
        var rpc = Substitute.For<IChainInfoRpc>();
        rpc.GetBlockchainInfoAsync().Returns(new BlockchainInfo
        {
            Chain = "main", Blocks = 100, Headers = 100, InitialBlockDownload = false
        });
        rpc.GetNetworkInfoAsync().Returns(new NetworkInfo { Connections = 3 });
        rpc.GetPeersAsync().Returns(new List<PeerSummary>());

        await using var svc = new SyncStateService(rpc);
        var s = await WaitForAsync(svc, x => x.State == DaemonState.Online);

        Assert.Equal(DaemonState.Online, s.State);
        Assert.True(s.Synced);
        Assert.Equal(0, s.Behind);
        Assert.Equal(100d, s.Progress);
        Assert.Equal(3, s.Peers);
        Assert.Equal("main", s.Chain);
    }

    [Fact]
    public async Task Not_synced_and_progress_from_verificationprogress_during_ibd()
    {
        var rpc = Substitute.For<IChainInfoRpc>();
        rpc.GetBlockchainInfoAsync().Returns(new BlockchainInfo
        {
            Chain = "main", Blocks = 50, Headers = 100,
            InitialBlockDownload = true, VerificationProgress = 0.25m
        });
        rpc.GetNetworkInfoAsync().Returns(new NetworkInfo { Connections = 1 });
        rpc.GetPeersAsync().Returns(new List<PeerSummary>());

        await using var svc = new SyncStateService(rpc);
        var s = await WaitForAsync(svc, x => x.State == DaemonState.Online);

        Assert.False(s.Synced);
        Assert.Equal(50, s.Behind);
        Assert.Equal(25d, s.Progress); // verificationprogress (0.25) × 100 during IBD
    }

    [Fact]
    public async Task Warmup_when_rpc_returns_code_minus_28()
    {
        var rpc = Substitute.For<IChainInfoRpc>();
        rpc.GetBlockchainInfoAsync().Throws(new RpcException(-28, "Loading block index..."));

        await using var svc = new SyncStateService(rpc);
        var s = await WaitForAsync(svc, x => x.State == DaemonState.Warmup);

        Assert.Equal(DaemonState.Warmup, s.State);
        Assert.NotNull(s.WarmupSince);
        Assert.False(s.Synced);
    }

    [Fact]
    public async Task Offline_on_any_other_failure()
    {
        var rpc = Substitute.For<IChainInfoRpc>();
        rpc.GetBlockchainInfoAsync().Throws(new HttpRequestException("connection refused"));

        await using var svc = new SyncStateService(rpc);
        var s = await WaitForAsync(svc, x => x.State == DaemonState.Offline);

        Assert.Equal(DaemonState.Offline, s.State);
        Assert.False(s.Synced);
    }
}

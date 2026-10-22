using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Mining;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Tests for <see cref="MiningService"/> that exercise the fail-fast validation paths and
/// the status/thread-cap accounting WITHOUT starting real scrypt worker threads. StartAsync validates
/// the payout address and fetches the first block template BEFORE spawning any worker, so an invalid
/// address or a null template throws with no thread ever created — which is exactly what these assert.</summary>
public class MiningServiceTests
{
    // A real, checksum-valid Blazecoin mainnet P2PKH address (hash160 = 20 zero bytes).
    private const string ValidAddress = "BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z";

    [Fact]
    public void Fresh_service_reports_not_running_and_zeroed_status()
    {
        var svc = new MiningService(Substitute.For<IMiningRpc>());
        var s = svc.GetStatus();

        Assert.False(s.IsRunning);
        Assert.Equal(0, s.ActiveThreads);
        Assert.Equal(0, s.TotalHashes);
        Assert.Equal(0, s.BlocksFound);
        Assert.Equal(TimeSpan.Zero, s.Elapsed);
    }

    [Fact]
    public void Thread_caps_are_derived_from_the_processor_count()
    {
        var svc = new MiningService(Substitute.For<IMiningRpc>());
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 4), svc.MaxAllowedThreads);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount / 8), svc.DefaultThreadCount);
        Assert.Equal(svc.MaxAllowedThreads, svc.GetStatus().MaxAllowedThreads);
    }

    [Fact]
    public async Task StartAsync_throws_on_an_invalid_payout_address_before_spawning_workers()
    {
        var rpc = Substitute.For<IMiningRpc>();
        var svc = new MiningService(rpc);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StartAsync(new MiningOptions { PayoutAddress = "not-a-valid-address", ThreadCount = 1 }));
        Assert.Contains("Invalid payout address", ex.Message);

        // No template was ever requested (we failed at address validation first).
        await rpc.DidNotReceive().GetBlockTemplateAsync();
        Assert.False(svc.GetStatus().IsRunning);
    }

    [Fact]
    public async Task StartAsync_throws_when_the_block_template_is_null()
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetBlockTemplateAsync().Returns((BlockTemplate?)null);
        var svc = new MiningService(rpc);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StartAsync(new MiningOptions { PayoutAddress = ValidAddress, ThreadCount = 1 }));
        Assert.Contains("getblocktemplate", ex.Message);
        Assert.False(svc.GetStatus().IsRunning);
    }

    [Fact]
    public async Task StartAsync_resolves_an_address_from_the_daemon_when_none_is_configured()
    {
        var rpc = Substitute.For<IMiningRpc>();
        rpc.GetNewAddressAsync().Returns((string?)null); // daemon gives nothing back
        var svc = new MiningService(rpc);

        // Empty PayoutAddress -> asks the daemon -> null -> fails fast, still no workers.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StartAsync(new MiningOptions { PayoutAddress = "", ThreadCount = 1 }));
        await rpc.Received().GetNewAddressAsync();
        Assert.False(svc.GetStatus().IsRunning);
    }
}

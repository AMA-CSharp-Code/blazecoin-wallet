using System.Text.Json;
using BlazecoinWallet.Core.Services;            // IWalletRpc, WalletBalances, BalanceMine
using BlazecoinWallet.Core.Services.AutoPayout;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Unit tests for the auto-payout engine — the highest-risk MAUI logic
/// (it auto-broadcasts real BLZ). Drives it with a mocked IWalletRpc + the
/// in-memory store; asserts the threshold/cooldown/cap/dry-run/one-shot rules.</summary>
public class AutoPayoutServiceTests
{
    private const string StateKey = "blz_autopayout_state";
    private const string InflightKey = "blz_autopayout_inflight";

    private static (AutoPayoutService svc, IWalletRpc rpc, FakeKeyValueStore store) Make(decimal spendable)
    {
        var rpc = Substitute.For<IWalletRpc>();
        rpc.GetBalancesAsync().Returns(Task.FromResult<WalletBalances?>(
            new WalletBalances { Mine = new BalanceMine { Trusted = spendable } }));
        var store = new FakeKeyValueStore();
        return (new AutoPayoutService(rpc, store), rpc, store);
    }

    [Fact]
    public async Task does_not_send_below_threshold()
    {
        var (svc, rpc, _) = Make(spendable: 50m);
        svc.Threshold = 100m; svc.Address = "Bvalid"; svc.Live = true;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        await rpc.DidNotReceive().SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>());
        Assert.Contains("Waiting", svc.Reason);
        Assert.True(svc.Enabled);   // still armed — nothing fired
    }

    [Fact]
    public async Task dry_run_fires_but_never_calls_rpc_and_disarms()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        svc.Threshold = 100m; svc.Address = "Bvalid"; svc.Live = false; // dry-run (default)
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        await rpc.DidNotReceive().SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>());
        Assert.Contains("DRY RUN", string.Join("\n", svc.Log));
        Assert.False(svc.Enabled);  // one-shot disarm after firing
    }

    [Fact]
    public async Task live_sends_amount_above_threshold_then_stops()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        rpc.SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>())
           .Returns(Task.FromResult<string?>("txid123"));
        svc.Threshold = 100m; svc.Address = "Bvalid"; svc.Live = true; svc.Mode = "above"; svc.Batch = "single";
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        await rpc.Received(1).SendToAddressAsync("Bvalid", 100m);  // spendable - threshold
        Assert.False(svc.Enabled);  // one-shot
    }

    [Fact]
    public async Task status_only_tick_never_sends()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        svc.Threshold = 100m; svc.Address = "Bvalid"; svc.Live = true;
        await svc.StartAsync();
        await svc.TickAsync(fire: false);

        await rpc.DidNotReceive().SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>());
        Assert.True(svc.Enabled);
    }

    [Fact]
    public async Task blz1_segwit_destination_is_blocked()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        svc.Threshold = 100m; svc.Address = "blz1qunsafe"; svc.Live = true;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        await rpc.DidNotReceive().SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>());
        Assert.Contains("SegWit", svc.Reason);
    }

    [Fact]
    public async Task respects_cooldown()
    {
        var (svc, rpc, store) = Make(spendable: 200m);
        store.Data[StateKey] = JsonSerializer.Serialize(new AutoPayoutState
        {
            LastPayoutUtc = DateTime.UtcNow, SentToday = 0m, SentTodayDate = DateTime.UtcNow,
        });
        await svc.LoadAsync();
        svc.Threshold = 100m; svc.Address = "Bvalid"; svc.Live = true; svc.CooldownMin = 60;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        await rpc.DidNotReceive().SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>());
        Assert.Contains("Cooldown", svc.Reason);
    }

    [Fact]
    public async Task respects_daily_cap()
    {
        var (svc, rpc, store) = Make(spendable: 200m);
        store.Data[StateKey] = JsonSerializer.Serialize(new AutoPayoutState
        {
            LastPayoutUtc = DateTime.MinValue, SentToday = 500m, SentTodayDate = DateTime.UtcNow,
        });
        await svc.LoadAsync();
        svc.Threshold = 100m; svc.Address = "Bvalid"; svc.Live = true; svc.DailyCap = 500m;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        await rpc.DidNotReceive().SendToAddressAsync(Arg.Any<string>(), Arg.Any<decimal>());
        Assert.Contains("Daily cap", svc.Reason);
    }

    [Fact]
    public async Task crash_recovery_marker_warns_and_clears()
    {
        var (svc, _, store) = Make(spendable: 0m);
        store.Data[InflightKey] = "42.0000 BLZ to Bxyz at 12:00:00";
        await svc.LoadAsync();

        Assert.Contains("interrupted", string.Join("\n", svc.Log));
        Assert.False(store.Data.ContainsKey(InflightKey));  // marker cleared so it can't re-warn
    }
}

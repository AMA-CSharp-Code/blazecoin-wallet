using System.Text.Json;
using BlazecoinWallet.Core.Services;            // IWalletRpc, WalletBalances, BalanceMine
using BlazecoinWallet.Core.Services.AutoPayout;
using BlazecoinWallet.Core.Services.Send;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Unit tests for the auto-payout engine — the highest-risk MAUI logic
/// (it auto-broadcasts real BLZ). Drives it with a mocked IWalletRpc + the
/// in-memory store; asserts the threshold/cooldown/cap/dry-run/one-shot rules.</summary>
public class AutoPayoutServiceTests
{
    private const string StateKey = "blz_autopayout_state";
    private const string InflightKey = "blz_autopayout_inflight";

    /// <summary>The wallet on screen in these tests - the one every payout must name.</summary>
    private const string Wallet = "Savings";

    /// <summary>
    /// The engine's daemon, as it sees it: balances (a mocked IWalletRpc) and the ONE send path
    /// (ISendRpc, recorded). Until the audit of 2026-11-09 the engine's wallet binding, sender and
    /// change policy were optional and these tests built it with none of them - so what they
    /// drove was the fallback path, a bare sendtoaddress on the active wallet. Every payout now
    /// goes through the send that names its wallet, and the assertions read that.
    /// </summary>
    private sealed class FakeDaemon : ISendRpc
    {
        public IWalletRpc Rpc { get; } = Substitute.For<IWalletRpc>();
        public List<SendRequest> Sent { get; } = new();
        /// <summary>What a send answers; null (the default) is "no txid returned".</summary>
        public string? Txid { get; set; }

        public Task<string?> SendAsync(SendRequest request, CancellationToken ct = default) { Sent.Add(request); return Task.FromResult(Txid); }
        public Task<decimal?> EstimateFeeAsync(SendRequest request, CancellationToken ct = default) => Task.FromResult<decimal?>(null);

        /// <summary>The one payout that went out: where, how much, and from which wallet by name.</summary>
        public (string Address, decimal Amount, string? Wallet) Paid()
        {
            var r = Assert.Single(Sent);
            return (r.Address, r.Amount, r.Wallet);
        }
    }

    private static (AutoPayoutService svc, FakeDaemon rpc, FakeKeyValueStore store) Make(decimal spendable)
    {
        var rpc = new FakeDaemon();
        rpc.Rpc.GetBalancesAsync().Returns(Task.FromResult<WalletBalances?>(
            new WalletBalances { Mine = new BalanceMine { Trusted = spendable } }));
        var store = new FakeKeyValueStore();
        var svc = new AutoPayoutService(rpc.Rpc, store,
            new WalletBinding(new TestWalletContext(Wallet), new WalletSwitchNotice()), rpc, new SendChangePolicy(new TestMint()));
        return (svc, rpc, store);
    }

    [Fact]
    public void the_engine_cannot_be_built_without_its_wallet_binding_its_sender_or_its_change_policy()
    {
        // They defaulted to null, and a null switched the safeguard off without a word (audit 2026-11-09).
        Assert.DoesNotContain(typeof(AutoPayoutService).GetConstructors().Single().GetParameters(), p => p.IsOptional);
        var rpc = Substitute.For<IWalletRpc>();
        var store = new FakeKeyValueStore();
        var binding = new WalletBinding(new TestWalletContext(Wallet), new WalletSwitchNotice());
        var policy = new SendChangePolicy(new TestMint());
        Assert.Throws<ArgumentNullException>(() => new AutoPayoutService(rpc, store, null!, new FakeDaemon(), policy));
        Assert.Throws<ArgumentNullException>(() => new AutoPayoutService(rpc, store, binding, null!, policy));
        Assert.Throws<ArgumentNullException>(() => new AutoPayoutService(rpc, store, binding, new FakeDaemon(), null!));
    }

    [Fact]
    public async Task does_not_send_below_threshold()
    {
        var (svc, rpc, _) = Make(spendable: 50m);
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Live = true;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Empty(rpc.Sent);
        Assert.Contains("Waiting", svc.Reason);
        Assert.True(svc.Enabled);   // still armed — nothing fired
    }

    [Fact]
    public async Task dry_run_fires_but_never_calls_rpc_and_disarms()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Live = false; // dry-run (default)
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Empty(rpc.Sent);
        Assert.Contains("DRY RUN", string.Join("\n", svc.Log));
        Assert.False(svc.Enabled);  // one-shot disarm after firing
    }

    [Fact]
    public async Task a_dry_run_uses_up_neither_the_daily_cap_nor_the_cooldown()
    {
        // Bug-bounty report 2026-11-06 (seventh round): the rehearsal charged the amount it "would send"
        // to the day's cap and started the cooldown, and both were saved — so the real payout right
        // after it was refused for the rest of the day although nothing had been sent.
        var (svc, rpc, store) = Make(spendable: 200m);
        rpc.Txid = "txid123";
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Mode = "above"; svc.Batch = "single";
        svc.DailyCap = 100m; svc.CooldownMin = 60;

        svc.Live = false;                                           // the rehearsal
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Empty(rpc.Sent);
        var saved = JsonSerializer.Deserialize<AutoPayoutState>(store.Data[StateKey])!;
        Assert.Equal(0m, saved.SentToday);                          // was 100: the whole cap, spent on nothing
        Assert.True(DateTime.UtcNow - saved.LastPayoutUtc > TimeSpan.FromHours(1));   // and no cooldown was started

        svc.Live = true;                                            // now for real, straight away
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Equal((TestAddr.Payout, 100m, Wallet), rpc.Paid());   // was refused: "Daily cap … reached for today"
    }

    [Fact]
    public async Task live_sends_amount_above_threshold_then_stops()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        rpc.Txid = "txid123";
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Live = true; svc.Mode = "above"; svc.Batch = "single";
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Equal((TestAddr.Payout, 100m, Wallet), rpc.Paid());  // spendable - threshold, from the armed wallet by name
        Assert.False(svc.Enabled);  // one-shot
    }

    [Fact]
    public async Task status_only_tick_never_sends()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Live = true;
        await svc.StartAsync();
        await svc.TickAsync(fire: false);

        Assert.Empty(rpc.Sent);
        Assert.True(svc.Enabled);
    }

    [Fact]
    public async Task blz1_segwit_destination_is_blocked()
    {
        var (svc, rpc, _) = Make(spendable: 200m);
        svc.Threshold = 100m; svc.Address = "blz1qunsafe"; svc.Live = true;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Empty(rpc.Sent);
        Assert.Contains("SegWit", svc.Reason);
    }

    [Fact]
    public async Task a_destination_that_is_not_a_B_or_BQ_address_is_never_paid()
    {
        // Audit 2026-11-09: the check was a deny-list of ONE prefix (blz1), so a mistyped address,
        // a Bitcoin address or a script-hash address went straight to the send.
        foreach (var bad in TestAddr.NotPayable)
        {
            var (svc, rpc, _) = Make(spendable: 200m);
            svc.Threshold = 100m; svc.Address = bad; svc.Live = true;
            await svc.StartAsync();
            await svc.TickAsync(fire: true);

            Assert.Empty(rpc.Sent);                                    // was: sent, for all but blz1
            Assert.Contains("Paused", svc.Reason);
            Assert.Contains("not a valid Blazecoin address", svc.Reason);
        }
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
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Live = true; svc.CooldownMin = 60;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Empty(rpc.Sent);
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
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Live = true; svc.DailyCap = 500m;
        await svc.StartAsync();
        await svc.TickAsync(fire: true);

        Assert.Empty(rpc.Sent);
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

    // ── the four typed amounts (audit 2026-11-09) ──
    // Text that is not an amount used to be remembered by the page, in a dictionary keyed by
    // strings, and "Start is off until it is fixed" was a disabled attribute on a button.

    [Fact]
    public async Task start_is_refused_while_an_amount_cannot_be_read()
    {
        var (svc, rpc, _) = Make(spendable: 500m);
        svc.Threshold = 100m; svc.Address = TestAddr.Payout; svc.Live = true;

        svc.SetAmountText(AutoPayoutField.Threshold, "1,000.50");   // two marks: not an amount
        await svc.StartAsync();                                      // was: armed on the old threshold of 100

        Assert.False(svc.Enabled);
        Assert.True(svc.ShowReason);
        Assert.Contains("could not be read", svc.Reason);
        Assert.Contains("NOT started", Assert.Single(svc.Log));
        await rpc.Rpc.DidNotReceive().GetBalancesAsync();

        svc.SetAmountText(AutoPayoutField.Threshold, "1000,5");      // fixed
        await svc.StartAsync();
        Assert.True(svc.Enabled);
        Assert.Equal(1000.5m, svc.Threshold);
    }

    [Fact]
    public void unreadable_text_is_shown_back_flagged_and_leaves_the_setting_alone()
    {
        var (svc, _, _) = Make(spendable: 0m);
        svc.Reserve = 250m;

        svc.SetAmountText(AutoPayoutField.Reserve, "25o");

        Assert.Equal(250m, svc.Reserve);                             // the setting keeps its value
        Assert.Equal("25o", svc.AmountText(AutoPayoutField.Reserve)); // as typed, so it can be fixed
        Assert.True(svc.IsUnreadable(AutoPayoutField.Reserve));
        Assert.False(svc.IsUnreadable(AutoPayoutField.Threshold));
        Assert.True(svc.HasUnreadableAmount);
    }

    [Theory]
    [InlineData(AutoPayoutField.Threshold)]
    [InlineData(AutoPayoutField.Reserve)]
    [InlineData(AutoPayoutField.BatchSize)]
    [InlineData(AutoPayoutField.DailyCap)]
    public void a_readable_amount_sets_its_setting_and_reads_back_in_the_wallets_form(AutoPayoutField field)
    {
        var (svc, _, _) = Make(spendable: 0m);

        svc.SetAmountText(field, "1,5");                             // a decimal comma is a decimal mark, never thousands

        Assert.Equal("1.5", svc.AmountText(field));
        Assert.False(svc.HasUnreadableAmount);
        Assert.Equal(1.5m, field switch
        {
            AutoPayoutField.Threshold => svc.Threshold,
            AutoPayoutField.Reserve => svc.Reserve,
            AutoPayoutField.BatchSize => svc.BatchSize,
            _ => svc.DailyCap,
        });

        svc.SetAmountText(field, "");                                // cleared: no value, and nothing to flag
        Assert.Equal("", svc.AmountText(field));
        Assert.False(svc.IsUnreadable(field));
    }

    [Fact]
    public async Task the_step_arrows_set_a_number_and_clear_text_that_could_not_be_read()
    {
        var (svc, _, _) = Make(spendable: 0m);
        svc.DailyCap = 2000m;
        svc.SetAmountText(AutoPayoutField.DailyCap, "lots");

        await svc.StepAsync(AutoPayoutField.DailyCap, 1);

        Assert.Equal(3000m, svc.DailyCap);
        Assert.Equal("3000", svc.AmountText(AutoPayoutField.DailyCap));
        Assert.False(svc.HasUnreadableAmount);
    }

    [Fact]
    public async Task cooldown_is_stepped_in_whole_minutes_and_is_not_an_amount_field()
    {
        var (svc, _, _) = Make(spendable: 0m);
        svc.CooldownMin = 1;

        await svc.StepAsync(AutoPayoutField.Cooldown, -1);
        await svc.StepAsync(AutoPayoutField.Cooldown, -1);

        Assert.Equal(0, svc.CooldownMin);                            // never below zero
        Assert.Throws<ArgumentOutOfRangeException>(() => svc.SetAmountText(AutoPayoutField.Cooldown, "5"));
        Assert.Throws<ArgumentOutOfRangeException>(() => svc.AmountText(AutoPayoutField.Cooldown));
    }
}

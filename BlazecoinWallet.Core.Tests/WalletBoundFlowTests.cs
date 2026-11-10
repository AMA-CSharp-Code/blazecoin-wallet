using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Quantum;
using BlazecoinWallet.Core.Services.Send;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The two raw-transaction flows - the Quantum sweep and the Provenance vintage send - are bound
/// to the wallet their plan was made on (audit 2026-11-09). Their change address, their signature
/// and the "is this destination mine" check used to follow whichever wallet was active at that
/// moment: a plan made on "Savings" was signed by "Primary" if the app had moved there, and where
/// the two wallets share keys (a migrated or restored copy) the sweep's coins or the send's change
/// landed in the wallet the app had moved to. Driven through the production RPC service against
/// a recording daemon (in which every wallet can sign everything - the case that hurts), so
/// "which wallet did this reach" is asserted on the wire.
/// </summary>
public class WalletBoundFlowTests
{
    private const string LegacyA = "BnKVYKojUkgynKnD43ZHzSfGTw3apinDMM";

    private static UnspentOutput Coin(string txid, int vout = 0) => new(txid, vout, 100_000_000, LegacyA, 10);
    private static SweepSelection Coins() => new(LegacyA, new[] { Coin("e") }, true);

    private sealed record Rig(BlazecoindRpcService Rpc, WalletDaemonStub Daemon, TestWalletContext Ctx, WalletSwitchNotice Notice, WalletBinding Binding)
    {
        public IEnumerable<string> WalletsReached => Daemon.Calls.Where(c => c.Wallet.Length > 0).Select(c => c.Wallet).Distinct();
    }

    private static Rig On(string active, params string[] loaded)
    {
        var ctx = new TestWalletContext(active);
        var notice = new WalletSwitchNotice();
        var daemon = WalletDaemonStub.With(loaded);
        foreach (var w in loaded) daemon.PostQuantum.Add(w);
        return new Rig(daemon.Rpc(ctx, notice), daemon, ctx, notice, new WalletBinding(ctx, notice));
    }

    private static QuantumSweepSession Sweep(Rig r) =>
        new(r.Rpc, new SweepBroadcaster(r.Rpc, r.Binding), new QuantumSweepTests.FakeCreator(), r.Binding);

    private static VintageSender Vintage(Rig r) => new(r.Binding, r.Rpc, new ChangeAddressSource(r.Rpc));

    /// <summary>"Savings" has been unloaded and an ordinary read has re-pointed the app at "Primary";
    /// the notice is up and nobody has pressed OK.</summary>
    private static async Task<Rig> HealedAsync()
    {
        var r = On("Savings", "Primary");
        await r.Rpc.GetBalancesAsync();
        Assert.Equal("Primary", r.Ctx.Active);
        Assert.NotNull(r.Notice.Current);
        r.Daemon.Calls.Clear();
        return r;
    }

    // ════════════════════════ the Quantum sweep ════════════════════════

    [Fact]
    public async Task A_sweep_reaches_no_wallet_but_the_one_it_was_opened_on()
    {
        var r = On("Savings", "Primary", "Savings");
        var session = Sweep(r);

        await session.SelectAsync(Coins());                    // the destination is minted here
        await session.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);   // "is it mine" is asked here
        var sent = await session.BroadcastAsync(true, true);   // and it is signed here

        Assert.True(sent!.Succeeded);
        Assert.Equal("Savings", session.Wallet);
        Assert.Equal(new[] { "Savings" }, r.WalletsReached);
        Assert.Equal(new[] { "getnewaddress", "getaddressinfo", "signrawtransactionwithwallet" },
            r.Daemon.Calls.Where(c => c.Wallet == "Savings").Select(c => c.Method));
    }

    [Fact]
    public async Task A_sweep_is_signed_by_the_wallet_it_was_planned_on_or_not_at_all()
    {
        var r = On("Savings", "Primary", "Savings");
        var session = Sweep(r);
        await session.SelectAsync(Coins());
        await session.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);
        Assert.NotNull(session.Plan);

        r.Ctx.SetActive("Primary");                            // the app is on another wallet when Broadcast is pressed
        var sent = await session.BroadcastAsync(true, true);

        Assert.Empty(r.Daemon.Of("signrawtransactionwithwallet"));   // was: signed by "Primary"
        Assert.Empty(r.Daemon.Of("sendrawtransaction"));              // was: sent
        Assert.Null(sent);
        Assert.Null(session.Plan);                                    // and that plan is gone
        Assert.Equal(WalletBinding.MovedMessage("Savings", "Primary"), session.Problem);   // the Send page's words
    }

    [Fact]
    public async Task Every_later_step_of_a_sweep_refuses_once_the_wallet_has_moved()
    {
        var r = On("Savings", "Primary", "Savings");
        var session = Sweep(r);
        await session.SelectAsync(Coins());
        r.Daemon.Calls.Clear();

        r.Ctx.SetActive("Primary");
        await session.NewPostQuantumDestinationAsync();        // was: a BQ… address of "Primary"
        Assert.Equal("", session.Destination);
        Assert.Equal(WalletBinding.MovedMessage("Savings", "Primary"), session.Problem);

        session.SetDestination(WalletDaemonStub.BqAddress);
        await session.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);   // was: "is it mine" asked of "Primary"
        Assert.Null(session.Plan);
        Assert.Equal(WalletBinding.MovedMessage("Savings", "Primary"), session.Problem);

        Assert.Empty(r.Daemon.Calls);                          // nothing reached either wallet

        await session.SelectAsync(Coins());                    // opening it again is a new sweep, on the wallet now on screen
        Assert.Equal("Primary", session.Wallet);
        Assert.Equal("Primary", r.Daemon.Of("getnewaddress").Single().Wallet);
    }

    [Fact]
    public async Task A_sweep_is_not_opened_on_a_wallet_the_user_has_not_acknowledged()
    {
        var r = await HealedAsync();
        var session = Sweep(r);

        await session.SelectAsync(Coins());

        Assert.Null(session.Wallet);
        Assert.Equal("", session.Destination);
        Assert.Contains("nothing was done", session.Problem);
        Assert.Contains("Savings", session.Problem);
        Assert.Empty(r.Daemon.Calls);

        r.Notice.Dismiss();                                    // the user pressed OK on the notice
        await session.NewPostQuantumDestinationAsync();
        Assert.Equal("Primary", session.Wallet);
        Assert.Equal(WalletDaemonStub.BqAddress, session.Destination);
    }

    [Fact]
    public async Task A_sweep_stops_between_batches_when_the_wallet_moves_and_says_what_went_out()
    {
        var r = On("Savings", "Primary", "Savings");
        var plan = QuantumSweepPlanner.Plan(
            Enumerable.Range(0, 5).Select(i => Coin($"u{i}", i)).ToList(), WalletDaemonStub.BqAddress, maxInputsPerTx: 2).Plan!;
        r.Daemon.Before = call => { if (call.Method == "sendrawtransaction") r.Ctx.SetActive("Primary"); };   // moved during batch 1

        var result = await new SweepBroadcaster(r.Rpc, r.Binding).BroadcastAsync(plan, "Savings");

        Assert.Equal(SweepSendStatus.WalletChanged, result.Status);
        Assert.Equal(2, result.FailedBatch);
        Assert.Single(result.SentTxIds);
        Assert.Equal("Savings", r.Daemon.Of("signrawtransactionwithwallet").Single().Wallet);   // batch 2 was never signed, by anyone
        Assert.Single(r.Daemon.Of("sendrawtransaction"));
        var said = result.Describe();
        Assert.Contains("Batch 2 of 3 was not sent", said);
        Assert.Contains(WalletBinding.MovedSentence("Savings", "Primary"), said);
        Assert.Contains("1 earlier batch was already sent", said);
        Assert.DoesNotContain("Nothing was sent", said);       // something was
    }

    [Fact]
    public async Task A_sweep_stopped_before_its_first_batch_says_it_in_the_send_pages_words()
    {
        var r = On("Savings", "Primary", "Savings");
        var plan = QuantumSweepPlanner.Plan(new[] { Coin("u") }, WalletDaemonStub.BqAddress).Plan!;
        r.Ctx.SetActive("Primary");

        var result = await new SweepBroadcaster(r.Rpc, r.Binding).BroadcastAsync(plan, "Savings");

        Assert.Equal(SweepSendStatus.WalletChanged, result.Status);
        Assert.Equal(WalletBinding.MovedMessage("Savings", "Primary"), result.Describe());
        Assert.Empty(r.Daemon.Calls);
    }

    // ════════════════════════ the Provenance vintage send ════════════════════════

    private static VintageSendPlan Plan(string change) => new(
        4_100_000, new HashSet<long> { 4_100_000 },
        new[] { new PlannedInput("in", 0, 1_000_000) },
        new[] { new PlannedOutput(LegacyA, 600_000, OutputRole.Vintage), new PlannedOutput(change, 399_000, OutputRole.Change) },
        1_000, Array.Empty<string>());

    [Fact]
    public async Task A_vintage_send_reaches_no_wallet_but_the_one_its_plan_was_made_on()
    {
        var r = On("Savings", "Primary", "Savings");
        var sender = Vintage(r);

        Assert.Null(sender.BeginPlan());
        var change = await sender.NewChangeAddressAsync();
        var sent = await sender.BroadcastAsync(Plan(change.Address!));

        Assert.Equal(WalletDaemonStub.TxId, sent.TxId);
        Assert.Equal("Savings", sender.Wallet);
        Assert.Equal(new[] { "Savings" }, r.WalletsReached);
        Assert.Equal("signed-by-Savings", r.Daemon.Of("sendrawtransaction").Single().Body.Split('"').First(p => p.StartsWith("signed-by-")));
    }

    [Fact]
    public async Task A_vintage_plans_change_comes_from_the_plans_wallet_whatever_is_active_by_then()
    {
        var r = On("Savings", "Primary", "Savings");
        var sender = Vintage(r);
        Assert.Null(sender.BeginPlan());

        r.Ctx.SetActive("Primary");                            // the app moved while the plan was being built
        var change = await sender.NewChangeAddressAsync();

        Assert.Equal(WalletDaemonStub.BqAddress, change.Address);
        Assert.Equal(new[] { "Savings" }, r.WalletsReached);   // was: listdescriptors and getnewaddress in "Primary"
    }

    [Fact]
    public async Task A_vintage_send_is_not_signed_or_sent_once_the_wallet_has_moved()
    {
        var r = On("Savings", "Primary", "Savings");
        var sender = Vintage(r);
        Assert.Null(sender.BeginPlan());
        var change = await sender.NewChangeAddressAsync();
        r.Daemon.Calls.Clear();

        r.Ctx.SetActive("Primary");
        var sent = await sender.BroadcastAsync(Plan(change.Address!));

        Assert.False(sent.Sent);
        Assert.Equal(WalletBinding.MovedMessage("Savings", "Primary"), sent.Problem);   // the Send page's words
        Assert.Empty(r.Daemon.Calls);                          // was: created, signed by "Primary", sent
    }

    [Fact]
    public async Task A_vintage_plan_is_not_started_on_a_wallet_the_user_has_not_acknowledged()
    {
        var r = await HealedAsync();
        var sender = Vintage(r);

        var refused = sender.BeginPlan();

        Assert.Contains("nothing was done", refused);
        Assert.Contains("Savings", refused);
        Assert.Null(sender.Wallet);
        Assert.Equal("Plan the send first.", (await sender.NewChangeAddressAsync()).Problem);
        Assert.Equal("Plan the send first.", (await sender.BroadcastAsync(Plan(LegacyA))).Problem);
        Assert.Empty(r.Daemon.Calls);

        r.Notice.Dismiss();
        Assert.Null(sender.BeginPlan());
        Assert.Equal("Primary", sender.Wallet);
    }

    [Fact]
    public async Task A_vintage_send_whose_wallet_has_gone_fails_and_touches_no_other()
    {
        var r = On("Savings", "Primary", "Savings");
        var sender = Vintage(r);
        Assert.Null(sender.BeginPlan());
        r.Daemon.Loaded.Remove("Savings");                     // unloaded; no read has noticed yet

        var change = await sender.NewChangeAddressAsync();
        var sent = await sender.BroadcastAsync(Plan(LegacyA));

        Assert.Null(change.Address);
        Assert.Contains("not loaded on the node", sent.Problem);
        Assert.DoesNotContain(r.Daemon.Calls, c => c.Wallet == "Primary");
        Assert.Empty(r.Daemon.Of("sendrawtransaction"));
        Assert.Equal("Savings", r.Ctx.Active);                 // and nothing here re-pointed the app
    }

    // ════════════════════════ one change rule (audit 2026-11-09) ════════════════════════

    [Theory]
    [InlineData(true, BlazecoinAddressKind.PostQuantum, BlazecoinAddressKind.PostQuantum)]
    [InlineData(false, null, BlazecoinAddressKind.Legacy)]
    [InlineData(null, null, BlazecoinAddressKind.PostQuantum)]   // "could not check": change stays off post-quantum, receive offers it
    public void Post_quantum_or_legacy_is_answered_in_one_place(bool? canMint, BlazecoinAddressKind? change, BlazecoinAddressKind receive)
    {
        Assert.Equal(change, PostQuantumPreference.ForChange(canMint));
        Assert.Equal(receive, PostQuantumPreference.ForReceive(canMint));
    }

    [Fact]
    public async Task A_change_address_follows_that_rule_in_the_wallet_it_is_asked_of()
    {
        var r = On("Primary", "Primary", "Savings", "Old");
        r.Daemon.PostQuantum.Remove("Old");                    // a wallet made before post-quantum keys
        var source = new ChangeAddressSource(r.Rpc);

        Assert.Equal(WalletDaemonStub.BqAddress, await source.NewAsync("Savings", "x"));
        Assert.Equal("Blegacy-of-Old", await source.NewAsync("Old", "x"));

        Assert.DoesNotContain(r.Daemon.Calls, c => c.Wallet == "Primary");   // the active wallet is never asked
        Assert.Equal(new[] { "\"pq\"", "\"legacy\"" },
            r.Daemon.Of("getnewaddress").Select(c => c.Body.Contains("\"pq\"") ? "\"pq\"" : "\"legacy\""));
    }

    [Fact]
    public async Task When_the_wallet_cannot_be_checked_change_is_legacy_never_a_guess_at_post_quantum()
    {
        var r = On("Savings", "Savings");
        r.Daemon.Before = call => { if (call.Method == "listdescriptors") throw new HttpRequestException("down"); };

        var change = await new ChangeAddressSource(r.Rpc).NewAsync("Savings", "x");

        Assert.Equal("Blegacy-of-Savings", change);
        Assert.Contains("\"legacy\"", r.Daemon.Of("getnewaddress").Single().Body);
    }
}

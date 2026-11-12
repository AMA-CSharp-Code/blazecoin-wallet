using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Consolidation;
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The Send page's Consolidate card (2026-11-12): merging a wallet's many small coins into a few.
/// Pins: batches are filled by bytes (26 post-quantum inputs, 400 legacy ones), a batch of one coin
/// is never made, post-quantum coins never go to a legacy address, the destination is always a new
/// address of the SAME wallet, and a plan goes out at most once through the sweep's broadcaster.
/// </summary>
public class ConsolidationTests
{
    private const string LegacyA = "BnKVYKojUkgynKnD43ZHzSfGTw3apinDMM";
    private const string LegacyB = "BTt1goArr9xPyiJmuCVqZ3DpTVMKkBT1dL";
    private const string LegacyDest = "BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z";
    private static readonly string BqHeld = Bq(1);
    private static readonly string BqDest = Bq(7);
    private const long Coin = 100_000_000;          // 1 BLZ
    private const string W = "Savings";

    private static string Bq(byte seed)
    {
        var payload = new byte[34]; payload[0] = 0x46; payload[1] = 0x50;
        for (int i = 0; i < 32; i++) payload[2 + i] = (byte)(seed + i);
        return BitcoinProtocol.Base58CheckEncode(payload);
    }

    private static UnspentOutput U(int n, long sats, string address, int conf = 10) => new($"tx{n:D5}", 0, sats, address, conf);

    private static List<UnspentOutput> Coins(int count, string address, long sats = 51 * Coin, int from = 0)
        => Enumerable.Range(from, count).Select(i => U(i, sats, address)).ToList();

    // ════════════════════════════════════════════════════════════ planner

    [Fact]
    public void Post_quantum_coins_are_merged_twenty_six_to_a_transaction()
    {
        // The wallet that hit "inputs size exceeds the maximum weight" on 2026-11-12: thousands of small BQ coins.
        var r = ConsolidationPlanner.Plan(Coins(60, BqHeld), BqDest);

        var plan = r.Plan!;
        Assert.Equal(new[] { 26, 26, 8 }, plan.Transactions.Select(t => t.Inputs.Count));
        Assert.All(plan.Transactions, t => Assert.True(t.EstimatedBytes <= TransactionSizes.MaxStandardTx));
        Assert.Equal(10 + 43 + 26 * 3_782, plan.Transactions[0].EstimatedBytes);
        Assert.True(plan.Balances);
        Assert.Equal(SweepDestinationKind.PostQuantum, plan.DestinationKind);
        Assert.Contains(plan.Warnings, w => w.Contains("3 transactions leave 3 coins"));
    }

    [Fact]
    public void A_twenty_seventh_post_quantum_input_would_not_be_relayed_so_the_budget_stops_at_twenty_six()
    {
        Assert.True(10 + 43 + 26 * TransactionSizes.PostQuantumInput <= ConsolidationPlanner.BatchByteBudget);
        Assert.True(10 + 43 + 27 * TransactionSizes.PostQuantumInput > TransactionSizes.MaxStandardTx);
    }

    [Fact]
    public void Legacy_coins_are_merged_four_hundred_to_a_transaction_and_the_fee_comes_out_of_the_total()
    {
        var r = ConsolidationPlanner.Plan(Coins(1_000, LegacyA), LegacyDest);

        var plan = r.Plan!;
        Assert.Equal(new[] { 400, 400, 200 }, plan.Transactions.Select(t => t.Inputs.Count));
        var first = plan.Transactions[0];
        Assert.Equal(10 + 34 + 400 * 148, first.EstimatedBytes);
        Assert.Equal(QuantumSweepPlanner.FeeFor(first.EstimatedBytes, ConsolidationPlanner.DefaultFeeRateSatPerKb), first.FeeSatoshis);
        Assert.Equal(400 * 51 * Coin - first.FeeSatoshis, first.OutputSatoshis);
        Assert.True(plan.Balances);
        Assert.Equal(1_000 * 51 * Coin, plan.OutputSatoshis + plan.FeeSatoshis);
    }

    [Fact]
    public void Legacy_and_post_quantum_coins_share_a_transaction_by_bytes_and_go_to_a_BQ_address()
    {
        var coins = Coins(10, LegacyA).Concat(Coins(30, BqHeld, from: 100)).ToList();

        var plan = ConsolidationPlanner.Plan(coins, BqDest).Plan!;

        // 10 legacy (1,480 bytes) leave room for 25 post-quantum inputs; the other 5 follow.
        Assert.Equal(new[] { 35, 5 }, plan.Transactions.Select(t => t.Inputs.Count));
        Assert.All(plan.Transactions, t => Assert.True(t.EstimatedBytes <= ConsolidationPlanner.BatchByteBudget));
        Assert.Equal(40, plan.InputCount);
    }

    [Fact]
    public void Post_quantum_coins_are_never_merged_into_a_legacy_address()
    {
        var r = ConsolidationPlanner.Plan(Coins(5, BqHeld), LegacyDest);
        Assert.Null(r.Plan);
        Assert.Contains("never a legacy one", r.Problem!);
    }

    [Fact]
    public void A_single_coin_left_after_the_full_transactions_is_left_alone()
    {
        // 27 post-quantum coins: one full transaction, and a 27th that would be a one-coin "merge".
        var plan = ConsolidationPlanner.Plan(Coins(27, BqHeld), BqDest).Plan!;

        Assert.Single(plan.Transactions);
        Assert.Equal(26, plan.InputCount);
        Assert.Contains(plan.Warnings, w => w.Contains("left alone"));
    }

    [Theory]
    [InlineData(0, "no coins to merge")]
    [InlineData(1, "only one coin")]
    public void Fewer_than_two_coins_is_nothing_to_consolidate(int count, string expected)
    {
        var r = ConsolidationPlanner.Plan(Coins(count, LegacyA), LegacyDest);
        Assert.Null(r.Plan);
        Assert.Contains(expected, r.Problem!);
    }

    [Fact]
    public void Only_coins_no_larger_than_the_limit_are_merged()
    {
        var coins = Coins(5, LegacyA, sats: 10 * Coin);
        coins.Add(U(900, 50_000 * Coin, LegacyA));       // the big one stays where it is
        coins.Add(U(901, 10 * Coin, LegacyA));           // exactly the limit: merged

        var plan = ConsolidationPlanner.Plan(coins, LegacyDest, maxCoinSatoshis: 10 * Coin).Plan!;

        Assert.Equal(6, plan.InputCount);
        Assert.DoesNotContain(plan.Transactions.SelectMany(t => t.Inputs), i => i.TxId == "tx00900");
    }

    [Fact]
    public void Unconfirmed_coins_and_coins_worth_less_than_their_own_fee_are_left_alone_and_said_so()
    {
        var coins = Coins(3, BqHeld);
        coins.Add(U(50, Coin, BqHeld, conf: 0));
        // A post-quantum input adds 3,782 bytes: 378,200 satoshis at the default rate.
        coins.Add(U(51, 378_200, BqHeld));
        coins.Add(U(52, 378_201, BqHeld));

        var plan = ConsolidationPlanner.Plan(coins, BqDest).Plan!;

        Assert.Equal(4, plan.InputCount);
        Assert.Contains(plan.Warnings, w => w.StartsWith("1 unconfirmed coin(s)"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("1 coin(s) left alone: each is worth no more than the fee"));
    }

    [Fact]
    public void The_same_wallet_always_plans_the_same_batches_smallest_coins_first_legacy_before_post_quantum()
    {
        var coins = new List<UnspentOutput> { U(3, 5 * Coin, BqHeld), U(1, 9 * Coin, LegacyA), U(2, 2 * Coin, LegacyB), U(4, 1 * Coin, BqHeld) };

        var a = ConsolidationPlanner.Plan(coins, BqDest).Plan!;
        var b = ConsolidationPlanner.Plan(coins.AsEnumerable().Reverse().ToList(), BqDest).Plan!;

        Assert.Equal(new[] { "tx00002", "tx00001", "tx00004", "tx00003" }, a.Transactions[0].Inputs.Select(i => i.TxId));
        Assert.Equal(a.Transactions[0].Inputs, b.Transactions[0].Inputs);
        Assert.Contains(a.Warnings, w => w.Contains("3 addresses are combined"));
    }

    [Theory]
    [InlineData("", "No destination")]
    [InlineData("blz1qxyz", "destination")]
    public void A_destination_that_cannot_be_paid_is_refused(string destination, string expected)
    {
        var r = ConsolidationPlanner.Plan(Coins(5, LegacyA), destination);
        Assert.Null(r.Plan);
        Assert.Contains(expected, r.Problem!);
    }

    [Fact]
    public void A_merge_whose_one_output_would_be_dust_is_refused()
    {
        // Two coins that only just beat their own fee leave less than a BQ output's dust floor (11,475).
        var r = ConsolidationPlanner.Plan(new[] { U(1, 15_000, LegacyA), U(2, 15_000, LegacyA) }, BqDest);
        Assert.Null(r.Plan);
        Assert.Contains("dust floor", r.Problem!);
    }

    [Fact]
    public void The_picture_says_how_many_transactions_spending_everything_would_take()
    {
        var p = CoinPicture.Of(Coins(6_511, BqHeld, sats: 267 * Coin));      // the wallet of 2026-11-12
        Assert.Equal(6_511, p.PostQuantumCoins);
        Assert.Equal(0, p.LegacyCoins);
        Assert.Equal(249, p.TransactionsToSpendEverything);
        Assert.Equal(1, CoinPicture.Of(Coins(300, LegacyA)).TransactionsToSpendEverything);
        Assert.Equal(0, CoinPicture.Of(new List<UnspentOutput>()).LargestSatoshis);
    }

    // ════════════════════════════════════════════════════════════ session

    private sealed class FakeCoins : IConsolidationRpc
    {
        public List<UnspentOutput> Coins { get; set; } = new();
        public List<string> Wallets { get; } = new();
        public Exception? Throws { get; set; }
        public Action? OnRead { get; set; }
        public Task<IReadOnlyList<UnspentOutput>> ListSpendableCoinsAsync(string wallet, CancellationToken ct = default)
        {
            Wallets.Add(wallet);
            OnRead?.Invoke();
            if (Throws is not null) throw Throws;
            return Task.FromResult<IReadOnlyList<UnspentOutput>>(Coins.ToList());
        }
    }

    private sealed class FakeAddresses : IBoundWalletRpc
    {
        public PqAddressResult Pq { get; set; } = PqAddressResult.Ok(BqDest);
        public string? Legacy { get; set; } = LegacyDest;
        public bool? Mine { get; set; } = true;
        public int PqMints, LegacyMints;
        public List<string> Wallets { get; } = new();
        public List<string> Labels { get; } = new();

        public Task<PqAddressResult> MintPostQuantumAddressAsync(string wallet, string label, CancellationToken ct = default)
        { Wallets.Add(wallet); Labels.Add(label); PqMints++; return Task.FromResult(Pq); }
        public Task<string?> MintLegacyAddressAsync(string wallet, string label, CancellationToken ct = default)
        { Wallets.Add(wallet); Labels.Add(label); LegacyMints++; return Task.FromResult(Legacy); }
        public Task<bool?> HasPostQuantumKeysAsync(string wallet, CancellationToken ct = default) => Task.FromResult<bool?>(true);
        public Task<bool?> OwnsAddressAsync(string wallet, string address, CancellationToken ct = default)
        { Wallets.Add(wallet); return Task.FromResult(Mine); }
    }

    private sealed class FakeBroadcaster : ISweepBroadcaster
    {
        public List<SweepPlan> Plans { get; } = new();
        public List<string> Wallets { get; } = new();
        public SweepBroadcastResult? Result { get; set; }
        public Task<SweepBroadcastResult> BroadcastAsync(SweepPlan plan, string wallet, CancellationToken ct = default)
        {
            Plans.Add(plan);
            Wallets.Add(wallet);
            return Task.FromResult(Result ?? new SweepBroadcastResult(SweepSendStatus.AllSent,
                plan.Transactions.Select((_, i) => $"txid{i + 1}").ToList(), plan.Transactions.Count, 0, null));
        }
    }

    private sealed class Rig
    {
        public TestWalletContext Context { get; } = new(W);
        public WalletSwitchNotice Notice { get; } = new();
        public FakeCoins Coins { get; } = new();
        public FakeAddresses Addresses { get; } = new();
        public FakeBroadcaster Broadcaster { get; } = new();
        public ConsolidationSession Session { get; }
        public Rig(List<UnspentOutput>? coins = null)
        {
            Coins.Coins = coins ?? ConsolidationTests.Coins(60, BqHeld);
            Session = new ConsolidationSession(Coins, Addresses, Broadcaster, new WalletBinding(Context, Notice));
        }
        public async Task<Rig> Planned()
        {
            await Session.CountAsync();
            await Session.PlanAsync(ConsolidationPlanner.DefaultFeeRateSatPerKb, null);
            return this;
        }
    }

    [Fact]
    public async Task Counting_binds_to_the_wallet_on_screen_and_mints_and_sends_nothing()
    {
        var rig = new Rig();

        await rig.Session.CountAsync();

        Assert.Equal(W, rig.Session.Wallet);
        Assert.Equal(60, rig.Session.Picture!.Coins);
        Assert.Equal(new[] { W }, rig.Coins.Wallets);
        Assert.Equal(0, rig.Addresses.PqMints + rig.Addresses.LegacyMints);
        Assert.Empty(rig.Broadcaster.Plans);
        Assert.Null(rig.Session.Plan);
    }

    [Fact]
    public async Task Coins_still_confirming_are_shown_at_the_count_and_left_out_of_the_plan()
    {
        // First live run 2026-11-12: a second round pressed while 70 of the first round's outputs were
        // still confirming merged only the other 171, and the card had not said why. The read was
        // "confirmed only", so the card never saw them.
        var coins = Coins(30, BqHeld);
        coins.AddRange(Enumerable.Range(500, 4).Select(i => U(i, 1_000 * Coin, BqHeld, conf: 0)));
        var rig = new Rig(coins);

        await rig.Session.CountAsync();

        var pic = rig.Session.Picture!;
        Assert.Equal(30, pic.Coins);
        Assert.Equal(30 * 51 * Coin, pic.Satoshis);
        Assert.Equal(4, pic.ConfirmingCoins);
        Assert.Equal(4_000 * Coin, pic.ConfirmingSatoshis);
        Assert.Equal(51 * Coin, pic.LargestSatoshis);                      // of the coins that can be merged
        Assert.Contains("4 coins are still confirming and will be left out", pic.ConfirmingNotice);

        await rig.Session.PlanAsync(ConsolidationPlanner.DefaultFeeRateSatPerKb, null);

        Assert.Equal((30, 2), rig.Session.Effect);
        Assert.Contains(rig.Session.Warnings, w => w.StartsWith("4 unconfirmed coin(s) left alone"));
    }

    [Fact]
    public void A_wallet_with_nothing_confirming_says_nothing_about_it()
    {
        Assert.Null(CoinPicture.Of(Coins(5, LegacyA)).ConfirmingNotice);
        Assert.Contains("1 coin is still confirming", CoinPicture.Of(new[] { U(1, Coin, LegacyA, conf: 0) }).ConfirmingNotice);
    }

    [Fact]
    public async Task Planning_before_counting_is_refused()
    {
        var rig = new Rig();
        await rig.Session.PlanAsync(ConsolidationPlanner.DefaultFeeRateSatPerKb, null);
        Assert.Null(rig.Session.Plan);
        Assert.Equal("Count the coins first.", rig.Session.Problem);
        Assert.Empty(rig.Coins.Wallets);
    }

    [Fact]
    public async Task The_destination_is_a_new_BQ_address_of_the_same_wallet_and_every_call_names_that_wallet()
    {
        var rig = await new Rig().Planned();

        var s = rig.Session;
        Assert.Null(s.Problem);
        Assert.Equal(BqDest, s.Destination);
        Assert.Equal((60, 3), s.Effect);
        Assert.True(s.RequiresBackupAck);
        Assert.Equal(new[] { ConsolidationSession.Label }, rig.Addresses.Labels);
        Assert.All(rig.Addresses.Wallets, w => Assert.Equal(W, w));
        Assert.All(rig.Coins.Wallets, w => Assert.Equal(W, w));
        Assert.Equal(2, rig.Coins.Wallets.Count);            // read at Count, and again at Plan
    }

    [Theory]
    [InlineData(PqAddressOutcome.WalletHasNoPqKeys)]
    [InlineData(PqAddressOutcome.LegacyWallet)]
    [InlineData(PqAddressOutcome.DaemonTooOld)]
    public async Task A_wallet_without_post_quantum_keys_merges_into_a_new_legacy_address(PqAddressOutcome outcome)
    {
        var rig = new Rig(Coins(500, LegacyA));
        rig.Addresses.Pq = PqAddressResult.Fail(outcome, "no pq");

        await rig.Planned();

        Assert.Null(rig.Session.Problem);
        Assert.Equal(LegacyDest, rig.Session.Destination);
        Assert.False(rig.Session.RequiresBackupAck);
        Assert.Equal((500, 2), rig.Session.Effect);
    }

    [Fact]
    public async Task Any_other_failure_to_mint_stops_the_plan()
    {
        var rig = new Rig();
        rig.Addresses.Pq = PqAddressResult.Fail(PqAddressOutcome.Failed, "node said no");

        await rig.Planned();

        Assert.Null(rig.Session.Plan);
        Assert.Contains("node said no", rig.Session.Problem!);
        Assert.Equal(0, rig.Addresses.LegacyMints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Nothing_is_planned_unless_the_wallet_confirms_the_new_address_is_its_own(bool? mine)
    {
        var rig = new Rig();
        rig.Addresses.Mine = mine;

        await rig.Planned();

        Assert.Null(rig.Session.Plan);
        Assert.Contains("Couldn't confirm that the new address belongs to this wallet", rig.Session.Problem!);
        Assert.Equal("", rig.Session.Destination);
    }

    [Fact]
    public async Task No_address_is_minted_when_there_is_nothing_to_merge()
    {
        var rig = new Rig(Coins(1, LegacyA));

        await rig.Planned();

        Assert.Null(rig.Session.Plan);
        Assert.Contains("only one coin", rig.Session.Problem!);
        Assert.Equal(0, rig.Addresses.PqMints + rig.Addresses.LegacyMints);
    }

    [Fact]
    public async Task Planning_again_reuses_the_address_and_reads_the_coins_afresh()
    {
        var rig = await new Rig().Planned();
        rig.Coins.Coins = Coins(30, BqHeld);

        await rig.Session.PlanAsync(ConsolidationPlanner.DefaultFeeRateSatPerKb, null);

        Assert.Equal(1, rig.Addresses.PqMints);
        Assert.Equal((30, 2), rig.Session.Effect);
        Assert.Equal(30, rig.Session.Picture!.Coins);
    }

    [Fact]
    public async Task Broadcast_needs_both_acknowledgements_for_a_BQ_destination()
    {
        var rig = await new Rig().Planned();
        var s = rig.Session;

        Assert.False(s.CanBroadcast(false, true));
        Assert.False(s.CanBroadcast(true, false));
        Assert.Null(await s.BroadcastAsync(true, false));
        Assert.Contains("backup", s.Problem!);
        Assert.Empty(rig.Broadcaster.Plans);
        Assert.NotNull(s.Plan);                              // a refused press keeps the plan

        Assert.True(s.CanBroadcast(true, true));
    }

    [Fact]
    public async Task A_plan_goes_out_once_in_the_bound_wallet_and_the_card_must_count_again()
    {
        var rig = await new Rig().Planned();
        var s = rig.Session;

        var r = await s.BroadcastAsync(true, true);

        Assert.True(r!.Succeeded);
        Assert.Equal(new[] { W }, rig.Broadcaster.Wallets);
        Assert.Equal(3, rig.Broadcaster.Plans.Single().Transactions.Count);
        Assert.Null(s.Plan);
        Assert.Null(s.Picture);
        Assert.Equal("", s.Destination);
        Assert.Contains("Sent 3 transactions. Count the coins again once they have confirmed.", s.Outcome!);

        // A second press has nothing to send.
        Assert.Null(await s.BroadcastAsync(true, true));
        Assert.Single(rig.Broadcaster.Plans);
    }

    [Fact]
    public async Task An_unknown_result_is_reported_as_unknown_and_tells_the_user_not_to_retry()
    {
        var rig = await new Rig().Planned();
        rig.Broadcaster.Result = new SweepBroadcastResult(SweepSendStatus.Unknown, new[] { "txid1" }, 3, 2, "timed out");

        await rig.Session.BroadcastAsync(true, true);

        var text = rig.Session.Outcome!;
        Assert.Contains("result UNKNOWN", text);
        Assert.Contains("Do NOT retry", text);
        Assert.Contains("Do NOT retry: count the coins again", text);
        Assert.DoesNotContain("escan", text);
        // The Quantum page's wording is untouched: same result, its own action.
        Assert.Contains("Do NOT retry: rescan", rig.Session.LastBroadcast!.Describe());
        Assert.Null(rig.Session.Plan);
    }

    [Fact]
    public async Task Once_the_app_is_on_another_wallet_nothing_is_planned_or_sent()
    {
        var rig = await new Rig().Planned();
        rig.Context.SetActive("Primary");

        Assert.Null(await rig.Session.BroadcastAsync(true, true));
        Assert.Empty(rig.Broadcaster.Plans);
        Assert.Contains("Savings", rig.Session.Problem!);
        Assert.Contains("Primary", rig.Session.Problem!);

        await rig.Session.PlanAsync(ConsolidationPlanner.DefaultFeeRateSatPerKb, null);
        Assert.Null(rig.Session.Plan);
        Assert.Equal(2, rig.Coins.Wallets.Count);            // the other wallet's coins were never read
    }

    [Fact]
    public async Task A_wallet_that_moves_while_its_coins_are_being_read_is_not_counted()
    {
        var rig = new Rig();
        rig.Coins.OnRead = () => rig.Context.SetActive("Primary");

        await rig.Session.CountAsync();

        Assert.Null(rig.Session.Picture);
        Assert.Null(rig.Session.Wallet);
        Assert.NotNull(rig.Session.Problem);
    }

    [Fact]
    public async Task A_failed_read_is_a_problem_not_a_crash()
    {
        var rig = new Rig();
        rig.Coins.Throws = new InvalidOperationException("node down");

        await rig.Session.CountAsync();

        Assert.Contains("node down", rig.Session.Problem!);
        Assert.Null(rig.Session.Picture);
        Assert.False(rig.Session.IsWorking);
    }

    [Fact]
    public async Task Changing_the_size_limit_discards_the_plan()
    {
        var rig = await new Rig().Planned();
        rig.Session.Discard();
        Assert.Null(rig.Session.Plan);
        Assert.False(rig.Session.CanBroadcast(true, true));
    }

    [Fact]
    public async Task The_real_broadcaster_builds_each_batch_exactly_as_planned_with_one_send_each()
    {
        var rpc = new RecordingSendRpc();
        var context = new TestWalletContext(W);
        var binding = new WalletBinding(context, new WalletSwitchNotice());
        var coins = new FakeCoins { Coins = Coins(30, BqHeld) };
        var session = new ConsolidationSession(coins, new FakeAddresses(), new SweepBroadcaster(rpc, binding), binding);
        await session.CountAsync();
        await session.PlanAsync(ConsolidationPlanner.DefaultFeeRateSatPerKb, null);
        var plan = session.Plan!;

        var r = await session.BroadcastAsync(true, true);

        Assert.True(r!.Succeeded);
        Assert.Equal(2, rpc.Sends);
        Assert.Equal(new[] { 26, 4 }, rpc.Created.Select(c => c.Inputs.Count));
        Assert.All(rpc.Created, c => Assert.Equal(BqDest, Assert.Single(c.Outputs).Address));
        Assert.Equal(plan.Transactions.Select(t => t.OutputSatoshis), rpc.Created.Select(c => c.Outputs[0].Satoshis));
        Assert.All(rpc.SignedIn, w => Assert.Equal(W, w));
    }

    private sealed class RecordingSendRpc : IVintageSendRpc
    {
        public List<(IReadOnlyList<(string TxId, int Vout)> Inputs, IReadOnlyList<(string Address, long Satoshis)> Outputs)> Created { get; } = new();
        public List<string> SignedIn { get; } = new();
        public int Sends;
        public Task<string?> CreateRawTransactionAsync(IReadOnlyList<(string TxId, int Vout)> inputs, IReadOnlyList<(string Address, long Satoshis)> outputs, CancellationToken ct = default)
        { Created.Add((inputs, outputs)); return Task.FromResult<string?>($"raw{Created.Count}"); }
        public Task<string?> SignRawTransactionAsync(string wallet, string rawHex, CancellationToken ct = default)
        { SignedIn.Add(wallet); return Task.FromResult<string?>("signed-" + rawHex); }
        public Task<string?> SendRawTransactionAsync(string signedHex, CancellationToken ct = default)
        { Sends++; return Task.FromResult<string?>($"txid{Sends}"); }
    }
}

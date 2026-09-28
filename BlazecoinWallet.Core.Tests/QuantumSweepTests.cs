using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// "Move to post-quantum" on the Quantum Exposure page (2026-09-28). Written after a large move went
/// from its vaults into a new wallet on LEGACY B… addresses — the GUI's only address type — and had
/// to be moved a second time to reach BQ…. Pins: the planner prices and accepts BQ… destinations;
/// selections never include post-quantum coins; the session defaults to a BQ… destination, offers a
/// legacy one only for exposed coins in a wallet without PQ keys, flags foreign destinations and
/// demands a backup acknowledgement; the broadcaster sends each batch exactly once and reports a
/// timed-out send as UNKNOWN, never retrying it (the 2026-08-15 duplicate-payout rule).
/// </summary>
public class QuantumSweepTests
{
    // Pinned legacy vectors (BitcoinProtocolTests / QuantumExposureTests).
    private const string LegacyA = "BnKVYKojUkgynKnD43ZHzSfGTw3apinDMM";
    private const string LegacyB = "BTt1goArr9xPyiJmuCVqZ3DpTVMKkBT1dL";
    private const string LegacyC = "BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z";
    // PQ_SIGNATURES.md §5 example (pqkh = 0x00…00).
    private const string BqSpec = "BQGeaQmsowAjL1ZG8q1AfVH4NgBvtJKJSsjnnKEf62BMjekdU82n";
    private static readonly string BqOther = Bq(7);

    private static string Bq(byte seed)
    {
        var payload = new byte[34]; payload[0] = 0x46; payload[1] = 0x50;
        for (int i = 0; i < 32; i++) payload[2 + i] = (byte)(seed + i);
        return BitcoinProtocol.Base58CheckEncode(payload);
    }

    private static UnspentOutput U(string txid, long sats, string address = LegacyA, int conf = 10, int vout = 0)
        => new(txid, vout, sats, address, conf);

    private static AddressExposure Row(string address, ExposureStatus status, params UnspentOutput[] outs)
        => new(address, outs.Sum(o => o.Satoshis), outs, status, status == ExposureStatus.Exposed ? "exposing-tx" : null);

    private static ExposureReport Report(params AddressExposure[] rows)
        => new(rows, rows.Sum(r => r.Satoshis), rows.Where(r => r.Status == ExposureStatus.Exposed).Sum(r => r.Satoshis), 1, 1, DateTime.UtcNow);

    // ════════════════════════════════════════════════════════════ planner: post-quantum destinations

    [Fact]
    public void KindOf_tells_a_BQ_address_from_a_legacy_one_and_rejects_everything_else()
    {
        Assert.Equal(SweepDestinationKind.PostQuantum, QuantumSweepPlanner.KindOf(BqSpec));
        Assert.Equal(SweepDestinationKind.PostQuantum, QuantumSweepPlanner.KindOf("  " + BqOther + " "));
        Assert.Equal(SweepDestinationKind.Legacy, QuantumSweepPlanner.KindOf(LegacyA));
        Assert.Null(QuantumSweepPlanner.KindOf(BqSpec[..^1] + "m"));                    // bad checksum
        Assert.Null(QuantumSweepPlanner.KindOf("blz1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"));
        Assert.Null(QuantumSweepPlanner.KindOf(""));
        Assert.Null(QuantumSweepPlanner.KindOf(null));
    }

    [Fact]
    public void A_BQ_output_is_priced_at_43_bytes_nine_more_than_a_legacy_one()
    {
        Assert.Equal(10 + 148 * 3 + 43, QuantumSweepPlanner.EstimateBytes(3, SweepDestinationKind.PostQuantum));
        Assert.Equal(QuantumSweepPlanner.EstimateBytes(3), QuantumSweepPlanner.EstimateBytes(3, SweepDestinationKind.Legacy));
        Assert.Equal(9, QuantumSweepPlanner.EstimateBytes(1, SweepDestinationKind.PostQuantum) - QuantumSweepPlanner.EstimateBytes(1));
    }

    [Fact]
    public void Planner_moves_legacy_coins_to_a_BQ_address_with_the_PQ_size_model_and_no_legacy_warning()
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 100_000_000), U("b", 50_000_000, vout: 1) }, BqSpec);

        Assert.Null(r.Problem);
        var plan = r.Plan!;
        Assert.Equal(SweepDestinationKind.PostQuantum, plan.DestinationKind);
        var tx = Assert.Single(plan.Transactions);
        Assert.Equal(BqSpec, tx.Destination);
        Assert.Equal(10 + 148 * 2 + 43, tx.EstimatedBytes);
        Assert.Equal(QuantumSweepPlanner.FeeFor(tx.EstimatedBytes, QuantumSweepPlanner.DefaultFeeRateSatPerKb), tx.FeeSatoshis);
        Assert.Equal(150_000_000 - tx.FeeSatoshis, tx.OutputSatoshis);
        Assert.True(plan.Balances);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Planner_trims_the_destination()
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 100_000_000) }, "  " + BqSpec + "  ");
        Assert.Equal(BqSpec, r.Plan!.Transactions[0].Destination);
    }

    [Fact]
    public void Planner_refuses_coins_that_are_already_post_quantum()
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 100_000_000), U("b", 5_000, address: BqOther) }, BqSpec);

        Assert.Null(r.Plan);
        Assert.Contains("already on a post-quantum address", r.Problem!);
    }

    [Fact]
    public void Planner_warns_that_combining_several_addresses_links_them()
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 100_000_000, LegacyA), U("b", 100_000_000, LegacyB), U("c", 100_000_000, LegacyC) }, BqSpec);

        Assert.Contains(r.Plan!.Warnings, w => w.Contains("3 addresses") && w.Contains("links"));
    }

    [Fact]
    public void Planner_says_BQ_reuse_across_batches_reveals_nothing_useful()
    {
        var utxos = Enumerable.Range(0, 5).Select(i => U($"u{i}", 10_000_000)).ToList();

        var r = QuantumSweepPlanner.Plan(utxos, BqSpec, maxInputsPerTx: 2);

        Assert.Equal(3, r.Plan!.Transactions.Count);
        Assert.All(r.Plan.Transactions, t => Assert.Equal(BqSpec, t.Destination));
        Assert.Contains(r.Plan.Warnings, w => w.Contains("3 transactions") && w.Contains("reveals nothing"));
        Assert.DoesNotContain(r.Plan.Warnings, w => w.Contains("NOT post-quantum"));
        Assert.Equal(r.Plan.InputSatoshis, r.Plan.OutputSatoshis + r.Plan.FeeSatoshis);
    }

    [Fact]
    public void Planner_error_for_a_bad_destination_names_both_address_kinds()
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 100_000_000) }, "not-an-address");
        Assert.Contains("legacy B…", r.Problem!);
        Assert.Contains("BQ…", r.Problem!);
    }

    [Fact]
    public void A_31_input_consolidation_to_one_BQ_output_balances_to_the_satoshi()
    {
        // 2026-09-28 shape: 31 legacy outputs, one BQ output, fee out of the total.
        var utxos = Enumerable.Range(0, 31).Select(i => U($"t{i}", 12_345_678_901L + i, i < 7 ? LegacyA : LegacyB, vout: i)).ToList();

        var r = QuantumSweepPlanner.Plan(utxos, BqSpec);

        var tx = Assert.Single(r.Plan!.Transactions);
        Assert.Equal(31, tx.Inputs.Count);
        Assert.True(tx.Balances);
        Assert.Equal(utxos.Sum(u => u.Satoshis), tx.OutputSatoshis + tx.FeeSatoshis);
    }

    // ════════════════════════════════════════════════════════════ selection

    [Fact]
    public void A_post_quantum_row_is_never_a_selection()
    {
        Assert.Null(SweepSelection.FromAddress(Row(BqOther, ExposureStatus.PostQuantum, U("p", 1_000, BqOther))));
        Assert.Null(SweepSelection.FromAddress(Row(LegacyA, ExposureStatus.Unexposed)));   // no outputs
    }

    [Theory]
    [InlineData(ExposureStatus.Exposed, true)]
    [InlineData(ExposureStatus.Unexposed, false)]
    public void An_address_selection_carries_its_outputs_and_whether_they_are_exposed(ExposureStatus status, bool exposed)
    {
        var s = SweepSelection.FromAddress(Row(LegacyA, status, U("a", 700), U("b", 300, vout: 1)))!;

        Assert.Equal(LegacyA, s.Title);
        Assert.Equal(2, s.OutputCount);
        Assert.Equal(1_000, s.Satoshis);
        Assert.Equal(exposed, s.ContainsExposed);
    }

    [Fact]
    public void All_legacy_takes_exposed_and_unexposed_coins_but_never_post_quantum_ones()
    {
        var report = Report(
            Row(LegacyA, ExposureStatus.Exposed, U("a", 100)),
            Row(LegacyB, ExposureStatus.Unexposed, U("b", 200), U("b2", 50, LegacyB, vout: 1)),
            Row(BqOther, ExposureStatus.PostQuantum, U("p", 9_999, BqOther)));

        var s = SweepSelection.AllLegacy(report)!;

        Assert.Equal(3, s.OutputCount);
        Assert.Equal(350, s.Satoshis);
        Assert.True(s.ContainsExposed);
        Assert.DoesNotContain(s.Outputs, o => o.Address == BqOther);
        Assert.Contains("2", s.Title);
    }

    [Fact]
    public void All_legacy_is_null_when_everything_is_already_post_quantum()
    {
        Assert.Null(SweepSelection.AllLegacy(Report(Row(BqOther, ExposureStatus.PostQuantum, U("p", 1, BqOther)))));
    }

    [Fact]
    public void All_legacy_with_only_unexposed_coins_is_not_marked_exposed()
    {
        var s = SweepSelection.AllLegacy(Report(Row(LegacyA, ExposureStatus.Unexposed, U("a", 1))))!;
        Assert.False(s.ContainsExposed);
    }

    // ════════════════════════════════════════════════════════════ table order

    [Fact]
    public void Rows_come_exposed_then_legacy_then_post_quantum_whatever_their_size()
    {
        var report = Report(
            Row(Bq(1), ExposureStatus.PostQuantum, U("p1", 9_000_000_000_000, Bq(1))),
            Row(LegacyB, ExposureStatus.Unexposed, U("l1", 10_000)),
            Row(LegacyA, ExposureStatus.Exposed, U("e1", 5)),
            Row(LegacyC, ExposureStatus.Unexposed, U("l2", 20_000)));

        var order = report.InActionOrder().Select(a => a.Address).ToList();

        Assert.Equal(new[] { LegacyA, LegacyC, LegacyB, Bq(1) }, order);
    }

    [Fact]
    public void Coinbase_inbox_shape_legacy_leftovers_are_never_buried_under_big_BQ_epoch_addresses()
    {
        // 2026-09-28: six BQ epoch addresses of ~74k BLZ each and two legacy addresses of ~11k — the old
        // size-only order showed four BQ rows and hid both legacy ones behind "show all".
        var rows = Enumerable.Range(1, 6)
            .Select(i => Row(Bq((byte)(i * 3)), ExposureStatus.PostQuantum, U($"p{i}", 7_400_000_000_000 + i, Bq((byte)(i * 3)))))
            .Append(Row(LegacyA, ExposureStatus.Unexposed, U("a", 1_177_093_407_428)))
            .Append(Row(LegacyB, ExposureStatus.Unexposed, U("b", 1_104_778_090_700)))
            .ToArray();

        var firstFour = Report(rows).InActionOrder().Take(4).ToList();

        Assert.Equal(new[] { LegacyA, LegacyB }, firstFour.Take(2).Select(a => a.Address));
        Assert.All(firstFour.Skip(2), a => Assert.Equal(ExposureStatus.PostQuantum, a.Status));
    }

    [Fact]
    public void Ties_break_by_address_so_the_order_is_stable()
    {
        var report = Report(Row(LegacyB, ExposureStatus.Unexposed, U("x", 100)), Row(LegacyA, ExposureStatus.Unexposed, U("y", 100)));
        Assert.Equal(new[] { LegacyA, LegacyB }.OrderBy(a => a, StringComparer.Ordinal), report.InActionOrder().Select(a => a.Address));
    }

    // ════════════════════════════════════════════════════════════ explanations

    [Theory]
    [InlineData(PqAddressOutcome.WalletHasNoPqKeys, "Create a new wallet")]
    [InlineData(PqAddressOutcome.LegacyWallet, "Create a new wallet")]
    [InlineData(PqAddressOutcome.DaemonTooOld, "2.1.0")]
    [InlineData(PqAddressOutcome.Failed, "boom")]
    public void Every_failure_to_mint_a_BQ_address_is_explained_in_plain_words(PqAddressOutcome outcome, string expected)
    {
        var text = PqAddressResult.Fail(outcome, "boom").Explain();
        Assert.Contains(expected, text);
    }

    [Fact]
    public void An_ok_result_has_nothing_to_explain()
    {
        var ok = PqAddressResult.Ok(BqSpec);
        Assert.True(ok.IsOk);
        Assert.Equal("", ok.Explain());
    }

    // ════════════════════════════════════════════════════════════ broadcaster

    private sealed class FakeSendRpc : IVintageSendRpc
    {
        public Func<int, string?> Create { get; set; } = n => $"raw{n}";
        public Func<int, string?> Sign { get; set; } = n => $"signed{n}";
        /// <summary>Per send call (1-based): return a txid, or throw.</summary>
        public Func<int, string?> Send { get; set; } = n => $"txid{n}";
        public int CreateCalls, SignCalls, SendCalls;
        public List<(IReadOnlyList<(string TxId, int Vout)> Inputs, IReadOnlyList<(string Address, long Satoshis)> Outputs)> Created { get; } = new();
        public List<string> SentHex { get; } = new();

        public Task<string?> GetChangeAddressAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> CreateRawTransactionAsync(IReadOnlyList<(string TxId, int Vout)> inputs, IReadOnlyList<(string Address, long Satoshis)> outputs, CancellationToken ct = default)
        { CreateCalls++; Created.Add((inputs, outputs)); return Task.FromResult(Create(CreateCalls)); }
        public Task<string?> SignRawTransactionAsync(string rawHex, CancellationToken ct = default)
        { SignCalls++; return Task.FromResult(Sign(SignCalls)); }
        public Task<string?> SendRawTransactionAsync(string signedHex, CancellationToken ct = default)
        { SendCalls++; SentHex.Add(signedHex); return Task.FromResult(Send(SendCalls)); }
    }

    private static SweepPlan ThreeBatchPlan()
        => QuantumSweepPlanner.Plan(Enumerable.Range(0, 5).Select(i => U($"u{i}", 10_000_000, vout: i)).ToList(), BqSpec, maxInputsPerTx: 2).Plan!;

    [Fact]
    public async Task Every_batch_is_built_exactly_as_planned_signed_and_sent_once()
    {
        var rpc = new FakeSendRpc();
        var plan = ThreeBatchPlan();

        var r = await new SweepBroadcaster(rpc).BroadcastAsync(plan);

        Assert.True(r.Succeeded);
        Assert.Equal(new[] { "txid1", "txid2", "txid3" }, r.SentTxIds);
        Assert.Equal(3, rpc.SendCalls);
        Assert.Equal(new[] { "signed1", "signed2", "signed3" }, rpc.SentHex);
        for (int i = 0; i < 3; i++)
        {
            var planned = plan.Transactions[i];
            Assert.Equal(planned.Inputs.Select(x => (x.TxId, x.Vout)), rpc.Created[i].Inputs);
            var output = Assert.Single(rpc.Created[i].Outputs);
            Assert.Equal((BqSpec, planned.OutputSatoshis), output);
        }
        Assert.Contains("Sent 3 transactions", r.Describe());
    }

    [Fact]
    public async Task A_batch_the_node_cannot_build_stops_the_run_before_signing()
    {
        var rpc = new FakeSendRpc { Create = n => n == 2 ? null : $"raw{n}" };

        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());

        Assert.Equal(SweepSendStatus.BuildFailed, r.Status);
        Assert.Equal(2, r.FailedBatch);
        Assert.Equal(new[] { "txid1" }, r.SentTxIds);
        Assert.Equal(1, rpc.SignCalls);
        Assert.Equal(1, rpc.SendCalls);
        Assert.Contains("1 earlier batch was already sent", r.Describe());
    }

    [Fact]
    public async Task A_build_exception_is_a_build_failure_not_a_crash()
    {
        var rpc = new FakeSendRpc { Create = _ => throw new RpcException(-8, "bad input") };
        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());
        Assert.Equal(SweepSendStatus.BuildFailed, r.Status);
        Assert.Equal(0, rpc.SendCalls);
    }

    [Fact]
    public async Task An_incomplete_signature_is_never_sent_and_hints_at_a_locked_wallet()
    {
        var rpc = new FakeSendRpc { Sign = _ => null };

        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());

        Assert.Equal(SweepSendStatus.SignFailed, r.Status);
        Assert.Equal(1, r.FailedBatch);
        Assert.Equal(0, rpc.SendCalls);
        Assert.Contains("unlocked", r.Describe());
        Assert.Contains("was not sent", r.Describe());
    }

    [Fact]
    public async Task A_signing_exception_is_a_sign_failure()
    {
        var rpc = new FakeSendRpc { Sign = _ => throw new RpcException(-13, "Please enter the wallet passphrase") };
        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());
        Assert.Equal(SweepSendStatus.SignFailed, r.Status);
        Assert.Contains("passphrase", r.Detail!);
        Assert.Equal(0, rpc.SendCalls);
    }

    [Fact]
    public async Task A_rejection_is_definite_and_later_batches_are_not_attempted()
    {
        var rpc = new FakeSendRpc { Send = n => n == 2 ? throw new RpcException(-26, "non-mandatory-script-verify-flag") : $"txid{n}" };

        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());

        Assert.Equal(SweepSendStatus.Rejected, r.Status);
        Assert.Equal(2, r.FailedBatch);
        Assert.Equal(2, rpc.SendCalls);
        Assert.Equal(2, rpc.CreateCalls);
        Assert.Contains("-26", r.Detail!);
        Assert.Contains("was not sent", r.Describe());
    }

    [Fact]
    public async Task A_null_txid_is_treated_as_a_rejection()
    {
        var rpc = new FakeSendRpc { Send = _ => null };
        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());
        Assert.Equal(SweepSendStatus.Rejected, r.Status);
        Assert.Equal(1, rpc.SendCalls);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("http")]
    [InlineData("cancelled")]
    public async Task A_send_that_times_out_is_UNKNOWN_and_is_never_retried(string failure)
    {
        Exception ex = failure switch
        {
            "timeout" => new TimeoutException("RPC call to sendrawtransaction timed out after 30 seconds"),
            "http" => new HttpRequestException("connection reset"),
            _ => new TaskCanceledException("gone"),
        };
        var rpc = new FakeSendRpc { Send = n => n == 2 ? throw ex : $"txid{n}" };

        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());

        Assert.Equal(SweepSendStatus.Unknown, r.Status);
        Assert.Equal(2, r.FailedBatch);
        Assert.Equal(2, rpc.SendCalls);          // batch 2 sent ONCE, batch 3 never touched
        Assert.Equal(2, rpc.CreateCalls);
        Assert.Equal(new[] { "txid1" }, r.SentTxIds);
        var text = r.Describe();
        Assert.Contains("UNKNOWN", text);
        Assert.Contains("Do NOT retry", text);
        Assert.Contains("1 earlier batch was already sent", text);
    }

    [Fact]
    public async Task A_busy_node_is_reported_as_not_sent_not_as_unknown()
    {
        // HTTP 503 is answered before the request reaches a worker: provably not executed.
        var rpc = new FakeSendRpc { Send = _ => throw new DaemonBusyException() };

        var r = await new SweepBroadcaster(rpc).BroadcastAsync(ThreeBatchPlan());

        Assert.Equal(SweepSendStatus.NotSentDaemonBusy, r.Status);
        Assert.Equal(1, rpc.SendCalls);
        Assert.Contains("was not sent", r.Describe());
    }

    [Fact]
    public async Task An_unbalanced_plan_touches_nothing()
    {
        var bad = new SweepPlan(new[] { new SweepTx(new[] { U("a", 1_000) }, BqSpec, 999, 5, 200) }, Array.Empty<string>(), SweepDestinationKind.PostQuantum);
        var rpc = new FakeSendRpc();

        var r = await new SweepBroadcaster(rpc).BroadcastAsync(bad);

        Assert.Equal(SweepSendStatus.BuildFailed, r.Status);
        Assert.Equal(0, rpc.CreateCalls + rpc.SignCalls + rpc.SendCalls);
    }

    [Fact]
    public void The_broadcaster_needs_an_rpc()
        => Assert.Throws<ArgumentNullException>(() => new SweepBroadcaster(null!));

    // ════════════════════════════════════════════════════════════ session

    private sealed class FakeDestinations : ISweepDestinationRpc
    {
        public Queue<PqAddressResult> PqResults { get; } = new();
        public Exception? PqThrows { get; set; }
        public string? Legacy { get; set; } = LegacyC;
        public Dictionary<string, bool?> Mine { get; } = new(StringComparer.Ordinal);
        public List<string> PqLabels { get; } = new();
        public List<string> LegacyLabels { get; } = new();
        public int IsMineCalls;

        public Task<PqAddressResult> GetNewPostQuantumAddressAsync(string label, CancellationToken ct = default)
        {
            PqLabels.Add(label);
            if (PqThrows is not null) throw PqThrows;
            return Task.FromResult(PqResults.Count > 0 ? PqResults.Dequeue() : PqAddressResult.Ok(BqSpec));
        }
        public Task<string?> GetNewLegacyAddressAsync(string label, CancellationToken ct = default)
        { LegacyLabels.Add(label); return Task.FromResult(Legacy); }
        public Task<bool?> IsMineAsync(string address, CancellationToken ct = default)
        { IsMineCalls++; return Task.FromResult(Mine.TryGetValue(address, out var m) ? m : true); }
        public Task<bool?> CanMintPostQuantumAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }

    private sealed class FakeBroadcaster : ISweepBroadcaster
    {
        public List<SweepPlan> Plans { get; } = new();
        public TaskCompletionSource<SweepBroadcastResult>? Gate { get; set; }
        public Task<SweepBroadcastResult> BroadcastAsync(SweepPlan plan, CancellationToken ct = default)
        {
            Plans.Add(plan);
            return Gate?.Task ?? Task.FromResult(new SweepBroadcastResult(SweepSendStatus.AllSent, new[] { "tx" }, plan.Transactions.Count, 0, null));
        }
    }

    private static SweepSelection Exposed() => new(LegacyA, new[] { U("e", 100_000_000) }, true);
    private static SweepSelection Unexposed() => new(LegacyB, new[] { U("n", 100_000_000, LegacyB) }, false);
    private static readonly PqAddressResult NoPqKeys = PqAddressResult.Fail(PqAddressOutcome.WalletHasNoPqKeys, "This wallet has no post-quantum (pq) key manager");

    /// <summary>Returns a scripted creation result; records every request.</summary>
    internal sealed class FakeCreator : IPostQuantumWalletCreator
    {
        public List<PqWalletRequest> Requests { get; } = new();
        public PqWalletCreationResult Result { get; set; } = PqWalletCreationResult.Ok(
            new PqWalletCreated("post-quantum", BqOther, @"C:\b\post-quantum.dat", 4_280_320, new string('A', 64), true));
        public TaskCompletionSource<PqWalletCreationResult>? Gate { get; set; }
        public Task<PqWalletCreationResult> CreateAsync(PqWalletRequest request, CancellationToken ct = default)
        { Requests.Add(request); return Gate?.Task ?? Task.FromResult(Result); }
    }

    private static (QuantumSweepSession s, FakeDestinations d, FakeBroadcaster b) NewSession() => NewSession(out _);

    private static (QuantumSweepSession s, FakeDestinations d, FakeBroadcaster b) NewSession(out FakeCreator creator)
    {
        var d = new FakeDestinations();
        var b = new FakeBroadcaster();
        creator = new FakeCreator();
        return (new QuantumSweepSession(d, b, creator), d, b);
    }

    [Fact]
    public async Task Opening_a_selection_mints_a_BQ_destination_straight_away()
    {
        var (s, d, _) = NewSession();

        await s.SelectAsync(Exposed());

        Assert.Equal(BqSpec, s.Destination);
        Assert.Equal(SweepDestinationKind.PostQuantum, s.DestinationKind);
        Assert.Null(s.DestinationNotice);
        Assert.False(s.CanUseLegacyFallback);
        Assert.Equal(new[] { QuantumSweepSession.PostQuantumLabel }, d.PqLabels);
        Assert.Empty(d.LegacyLabels);
    }

    [Fact]
    public async Task A_null_selection_does_nothing()
    {
        var (s, d, _) = NewSession();
        await s.SelectAsync(null);
        Assert.Null(s.Selection);
        Assert.Empty(d.PqLabels);
    }

    [Fact]
    public async Task A_wallet_without_PQ_keys_is_told_to_make_a_new_wallet_and_offered_legacy_only_for_exposed_coins()
    {
        var (s, d, _) = NewSession();
        d.PqResults.Enqueue(NoPqKeys);

        await s.SelectAsync(Exposed());

        Assert.Equal("", s.Destination);
        Assert.Contains("Create a new wallet", s.DestinationNotice!);
        Assert.True(s.CanUseLegacyFallback);

        await s.UseLegacyFallbackAsync();

        Assert.Equal(LegacyC, s.Destination);
        Assert.Equal(SweepDestinationKind.Legacy, s.DestinationKind);
        Assert.Equal(new[] { QuantumSweepSession.LegacyLabel }, d.LegacyLabels);
    }

    [Fact]
    public async Task Unexposed_coins_in_a_wallet_without_PQ_keys_get_no_legacy_fallback()
    {
        var (s, d, _) = NewSession();
        d.PqResults.Enqueue(NoPqKeys);
        await s.SelectAsync(Unexposed());

        Assert.False(s.CanUseLegacyFallback);
        await s.UseLegacyFallbackAsync();

        Assert.Equal("", s.Destination);
        Assert.Contains("gains nothing", s.Problem!);
        Assert.Empty(d.LegacyLabels);
    }

    [Fact]
    public async Task The_legacy_fallback_is_refused_while_BQ_minting_works()
    {
        var (s, d, _) = NewSession();
        await s.SelectAsync(Exposed());

        await s.UseLegacyFallbackAsync();

        Assert.Equal(BqSpec, s.Destination);
        Assert.NotNull(s.Problem);
        Assert.Empty(d.LegacyLabels);
    }

    [Fact]
    public async Task A_mint_that_throws_becomes_an_explained_notice()
    {
        var (s, d, _) = NewSession();
        d.PqThrows = new TimeoutException("slow");

        await s.SelectAsync(Exposed());

        Assert.Contains("slow", s.DestinationNotice!);
        Assert.True(s.CanUseLegacyFallback);
    }

    [Fact]
    public async Task Moving_unexposed_coins_to_a_typed_legacy_address_is_refused()
    {
        var (s, _, b) = NewSession();
        await s.SelectAsync(Unexposed());
        s.SetDestination(LegacyC);

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.Null(s.Plan);
        Assert.Contains("gains nothing", s.Problem!);
        Assert.Empty(b.Plans);
    }

    [Fact]
    public async Task A_BQ_plan_from_this_wallet_needs_both_acknowledgements()
    {
        var (s, d, _) = NewSession();
        await s.SelectAsync(Unexposed());

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.NotNull(s.Plan);
        Assert.Equal(1, d.IsMineCalls);
        Assert.True(s.DestinationIsMine);
        Assert.DoesNotContain(s.Warnings, w => w.Contains("NOT an address of this wallet"));
        Assert.True(s.RequiresBackupAck);
        Assert.False(s.CanBroadcast(false, false));
        Assert.False(s.CanBroadcast(true, false));
        Assert.False(s.CanBroadcast(false, true));
        Assert.True(s.CanBroadcast(true, true));
    }

    [Fact]
    public async Task A_legacy_fallback_plan_needs_only_the_irreversible_acknowledgement()
    {
        var (s, d, _) = NewSession();
        d.PqResults.Enqueue(NoPqKeys);
        await s.SelectAsync(Exposed());
        await s.UseLegacyFallbackAsync();

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.False(s.RequiresBackupAck);
        Assert.True(s.CanBroadcast(true, false));
        Assert.Contains(s.Warnings, w => w.Contains("NOT post-quantum"));
    }

    [Fact]
    public async Task A_destination_outside_this_wallet_is_flagged_first()
    {
        var (s, d, _) = NewSession();
        d.Mine[BqOther] = false;
        await s.SelectAsync(Exposed());
        s.SetDestination(BqOther);

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.NotNull(s.Plan);
        Assert.False(s.DestinationIsMine);
        Assert.StartsWith("This destination is NOT an address of this wallet", s.Warnings[0]);
    }

    [Fact]
    public async Task An_unconfirmable_owner_is_flagged_too()
    {
        var (s, d, _) = NewSession();
        d.Mine[BqSpec] = null;
        await s.SelectAsync(Exposed());

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.Null(s.DestinationIsMine);
        Assert.StartsWith("Couldn't confirm", s.Warnings[0]);
    }

    [Fact]
    public async Task A_planner_refusal_surfaces_as_the_problem()
    {
        var (s, _, _) = NewSession();
        await s.SelectAsync(Exposed());
        s.SetDestination("junk");

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.Null(s.Plan);
        Assert.Contains("not a valid Blazecoin address", s.Problem!);
    }

    [Fact]
    public async Task Changing_the_destination_throws_the_plan_away()
    {
        var (s, _, _) = NewSession();
        await s.SelectAsync(Exposed());
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);
        Assert.NotNull(s.Plan);

        s.SetDestination("  " + BqOther + " ");

        Assert.Null(s.Plan);
        Assert.Equal(BqOther, s.Destination);
    }

    [Fact]
    public async Task Broadcasting_without_the_acknowledgements_sends_nothing()
    {
        var (s, _, b) = NewSession();
        await s.SelectAsync(Exposed());
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.Null(await s.BroadcastAsync(true, false));
        Assert.Contains("backup", s.Problem!);
        Assert.Null(await s.BroadcastAsync(false, true));
        Assert.Contains("acknowledgement", s.Problem!);
        Assert.Empty(b.Plans);
        Assert.NotNull(s.Plan);                  // still there to be acknowledged
    }

    [Fact]
    public async Task Broadcasting_before_planning_sends_nothing()
    {
        var (s, _, b) = NewSession();
        await s.SelectAsync(Exposed());
        Assert.Null(await s.BroadcastAsync(true, true));
        Assert.Contains("Plan", s.Problem!);
        Assert.Empty(b.Plans);
    }

    [Fact]
    public async Task A_plan_goes_out_exactly_once()
    {
        var (s, _, b) = NewSession();
        await s.SelectAsync(Exposed());
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);
        var planned = s.Plan;

        var r = await s.BroadcastAsync(true, true);

        Assert.NotNull(r);
        Assert.Same(planned, Assert.Single(b.Plans));
        Assert.Null(s.Plan);
        Assert.Same(r, s.LastBroadcast);
        Assert.Null(await s.BroadcastAsync(true, true));   // second click: nothing
        Assert.Single(b.Plans);
    }

    [Fact]
    public async Task While_sending_every_other_action_is_ignored()
    {
        var (s, d, b) = NewSession();
        b.Gate = new TaskCompletionSource<SweepBroadcastResult>();
        await s.SelectAsync(Exposed());
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        var first = s.BroadcastAsync(true, true);
        Assert.True(s.IsSending);
        Assert.False(s.CanBroadcast(true, true));

        Assert.Null(await s.BroadcastAsync(true, true));
        s.SetDestination(BqOther);
        await s.NewPostQuantumDestinationAsync();
        await s.SelectAsync(Unexposed());
        s.Close();
        Assert.Equal(BqSpec, s.Destination);
        Assert.Equal(LegacyA, s.Selection!.Title);
        Assert.Single(d.PqLabels);
        Assert.Single(b.Plans);

        b.Gate.SetResult(new SweepBroadcastResult(SweepSendStatus.AllSent, new[] { "tx" }, 1, 0, null));
        await first;
        Assert.False(s.IsSending);
    }

    [Fact]
    public async Task A_failed_broadcast_is_kept_for_the_page_and_the_plan_is_still_gone()
    {
        var (s, _, b) = NewSession();
        b.Gate = new TaskCompletionSource<SweepBroadcastResult>();
        b.Gate.SetResult(new SweepBroadcastResult(SweepSendStatus.Unknown, Array.Empty<string>(), 1, 1, "timed out"));
        await s.SelectAsync(Exposed());
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        var r = await s.BroadcastAsync(true, true);

        Assert.Equal(SweepSendStatus.Unknown, r!.Status);
        Assert.Null(s.Plan);                     // no "try again" button on an unknown result
        Assert.Contains("Do NOT retry", s.LastBroadcast!.Describe());
    }

    [Fact]
    public async Task Close_forgets_everything()
    {
        var (s, _, _) = NewSession();
        await s.SelectAsync(Exposed());
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        s.Close();

        Assert.Null(s.Selection);
        Assert.Null(s.Plan);
        Assert.Equal("", s.Destination);
        Assert.Null(s.DestinationNotice);
    }

    [Fact]
    public async Task A_new_selection_starts_clean()
    {
        var (s, _, _) = NewSession();
        await s.SelectAsync(Exposed());
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);
        await s.BroadcastAsync(true, true);

        await s.SelectAsync(Unexposed());

        Assert.Null(s.LastBroadcast);
        Assert.Null(s.Plan);
        Assert.Equal(LegacyB, s.Selection!.Title);
    }

    [Fact]
    public void The_session_needs_both_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new QuantumSweepSession(null!, new FakeBroadcaster(), new FakeCreator()));
        Assert.Throws<ArgumentNullException>(() => new QuantumSweepSession(new FakeDestinations(), null!, new FakeCreator()));
        Assert.Throws<ArgumentNullException>(() => new QuantumSweepSession(new FakeDestinations(), new FakeBroadcaster(), null!));
    }

    // ════════════════════════════════════════════════════════════ session: create a post-quantum wallet

    private static PqWalletRequest Req() => new("post-quantum", "correct horse battery", "correct horse battery", @"C:\b\post-quantum.dat");

    [Theory]
    [InlineData(PqAddressOutcome.WalletHasNoPqKeys, true)]
    [InlineData(PqAddressOutcome.LegacyWallet, true)]
    [InlineData(PqAddressOutcome.DaemonTooOld, false)]     // a new wallet on an old node cannot hold BQ keys either
    [InlineData(PqAddressOutcome.Failed, false)]           // transient: try again, not a new wallet
    public async Task A_new_wallet_is_offered_only_when_this_wallet_cannot_hold_BQ_keys(PqAddressOutcome outcome, bool offered)
    {
        var (s, d, _) = NewSession();
        d.PqResults.Enqueue(PqAddressResult.Fail(outcome, "x"));

        await s.SelectAsync(Unexposed());

        Assert.Equal(offered, s.CanCreatePostQuantumWallet);
    }

    [Fact]
    public async Task A_wallet_that_can_mint_BQ_is_not_offered_a_new_one()
    {
        var (s, _, _) = NewSession(out var creator);
        await s.SelectAsync(Exposed());

        Assert.False(s.CanCreatePostQuantumWallet);
        Assert.Null(await s.CreatePostQuantumWalletAsync(Req()));
        Assert.Contains("already make BQ", s.Problem!);
        Assert.Empty(creator.Requests);
    }

    [Fact]
    public async Task Creating_the_wallet_makes_its_first_BQ_address_the_destination_and_sends_nothing()
    {
        var (s, d, b) = NewSession(out var creator);
        d.PqResults.Enqueue(NoPqKeys);
        await s.SelectAsync(Unexposed());

        var r = await s.CreatePostQuantumWalletAsync(Req());

        Assert.True(r!.Succeeded);
        Assert.Equal("correct horse battery", Assert.Single(creator.Requests).Passphrase);
        Assert.Equal(BqOther, s.Destination);
        Assert.Equal(SweepDestinationKind.PostQuantum, s.DestinationKind);
        Assert.Equal("post-quantum", s.CreatedWallet!.WalletName);
        Assert.False(s.CanCreatePostQuantumWallet);          // one per panel
        Assert.Empty(b.Plans);
        Assert.Null(s.Plan);
    }

    [Fact]
    public async Task A_plan_to_the_new_wallet_names_it_and_its_backup_instead_of_warning_it_is_foreign()
    {
        var (s, d, _) = NewSession();
        d.PqResults.Enqueue(NoPqKeys);
        d.Mine[BqOther] = false;                              // not the ACTIVE wallet's address — by design
        await s.SelectAsync(Unexposed());
        await s.CreatePostQuantumWalletAsync(Req());

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.NotNull(s.Plan);
        Assert.Equal(0, d.IsMineCalls);
        Assert.DoesNotContain(s.Warnings, w => w.Contains("NOT an address of this wallet"));
        Assert.Contains("new wallet 'post-quantum'", s.Warnings[0]);
        Assert.Contains(@"C:\b\post-quantum.dat", s.Warnings[0]);
        Assert.True(s.RequiresBackupAck);
    }

    [Fact]
    public async Task A_plan_to_an_unencrypted_new_wallet_says_so_in_capitals()
    {
        var (s, d, _) = NewSession(out var creator);
        creator.Result = PqWalletCreationResult.Ok(new PqWalletCreated("hot-pq", BqOther, @"C:\b\hot-pq.dat", 4_000_000, new string('B', 64), true, Encrypted: false));
        d.PqResults.Enqueue(NoPqKeys);
        await s.SelectAsync(Unexposed());
        await s.CreatePostQuantumWalletAsync(Req() with { Encrypt = false });

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.Contains("UNENCRYPTED", s.Warnings[0]);
        Assert.Contains("hot-pq", s.Warnings[0]);
        Assert.False(Assert.Single(creator.Requests).Encrypt);
    }

    [Fact]
    public async Task Typing_a_different_address_after_creating_brings_the_ownership_check_back()
    {
        var (s, d, _) = NewSession();
        d.PqResults.Enqueue(NoPqKeys);
        d.Mine[BqSpec] = false;
        await s.SelectAsync(Unexposed());
        await s.CreatePostQuantumWalletAsync(Req());
        s.SetDestination(BqSpec);

        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);

        Assert.Equal(1, d.IsMineCalls);
        Assert.StartsWith("This destination is NOT an address of this wallet", s.Warnings[0]);
    }

    [Fact]
    public async Task A_failed_creation_leaves_no_destination_and_says_why()
    {
        var (s, d, _) = NewSession(out var creator);
        creator.Result = PqWalletCreationResult.Fail(PqWalletStep.Validate, "The two passphrases do not match.");
        d.PqResults.Enqueue(NoPqKeys);
        await s.SelectAsync(Unexposed());

        var r = await s.CreatePostQuantumWalletAsync(Req());

        Assert.False(r!.Succeeded);
        Assert.Equal("", s.Destination);
        Assert.Equal("The two passphrases do not match.", s.Problem);
        Assert.True(s.CanCreatePostQuantumWallet);            // can try again
    }

    [Fact]
    public async Task While_the_wallet_is_being_created_nothing_else_runs()
    {
        var (s, d, b) = NewSession(out var creator);
        creator.Gate = new TaskCompletionSource<PqWalletCreationResult>();
        d.PqResults.Enqueue(NoPqKeys);
        await s.SelectAsync(Unexposed());

        var pending = s.CreatePostQuantumWalletAsync(Req());
        Assert.True(s.IsCreatingWallet);
        Assert.Null(await s.CreatePostQuantumWalletAsync(Req()));
        s.SetDestination(BqSpec);
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);
        s.Close();
        Assert.Single(creator.Requests);
        Assert.Null(s.Plan);
        Assert.NotNull(s.Selection);

        creator.Gate.SetResult(creator.Result);
        await pending;
        Assert.False(s.IsCreatingWallet);
        Assert.Equal(BqOther, s.Destination);
    }

    [Fact]
    public async Task Selecting_again_forgets_the_created_wallet()
    {
        var (s, d, _) = NewSession();
        d.PqResults.Enqueue(NoPqKeys);
        d.PqResults.Enqueue(NoPqKeys);
        await s.SelectAsync(Unexposed());
        await s.CreatePostQuantumWalletAsync(Req());

        await s.SelectAsync(Unexposed());

        Assert.Null(s.CreatedWallet);
        Assert.True(s.CanCreatePostQuantumWallet);
    }

    // ════════════════════════════════════════════════════════════ end to end (session + real broadcaster)

    [Fact]
    public async Task End_to_end_every_legacy_coin_lands_on_one_BQ_address()
    {
        var report = Report(
            Row(LegacyA, ExposureStatus.Exposed, U("a", 4_000_000_12345678L, LegacyA)),
            Row(LegacyB, ExposureStatus.Unexposed, U("b", 2_500_000_87654321L, LegacyB)),
            Row(BqOther, ExposureStatus.PostQuantum, U("p", 1_000, BqOther)));
        var send = new FakeSendRpc();
        var s = new QuantumSweepSession(new FakeDestinations(), new SweepBroadcaster(send), new FakeCreator());

        await s.SelectAsync(SweepSelection.AllLegacy(report));
        await s.PlanAsync(QuantumSweepPlanner.DefaultFeeRateSatPerKb);
        var plan = s.Plan!;
        var r = await s.BroadcastAsync(true, true);

        Assert.True(r!.Succeeded);
        var built = Assert.Single(send.Created);
        Assert.Equal(new[] { ("a", 0), ("b", 0) }, built.Inputs);
        Assert.Equal(BqSpec, Assert.Single(built.Outputs).Address);
        Assert.Equal(4_000_000_12345678L + 2_500_000_87654321L, plan.OutputSatoshis + plan.FeeSatoshis);
        Assert.Equal(1, send.SendCalls);
    }
}

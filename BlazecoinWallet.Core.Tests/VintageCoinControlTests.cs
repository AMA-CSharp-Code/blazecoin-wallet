using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Vintage coin control — choosing which satoshis a send actually spends. This plans
/// transactions that move real BLZ, so every case ends by re-running the FIFO stream
/// over the plan (VerifyVintageDelivered) to prove the recipient's output really lands
/// on the requested block's satoshis. Pins the two order-critical rules: outputs must
/// never be re-sorted, and the fee comes out of the TAIL.
/// </summary>
public class VintageCoinControlTests
{
    private const long Pure413 = 413;
    private const long Other = 4_000_000;

    /// <summary>Builds a report directly from segment lists — no chain walk needed.</summary>
    private static ProvenanceReport Report(params OutputProvenance[] outputs)
    {
        var origins = new Dictionary<long, MintPoint>
        {
            [Pure413] = new(Pure413, new DateTime(2014, 5, 24, 1, 51, 0, DateTimeKind.Utc),
                VintageClassifier.Classify(Pure413, new DateTime(2014, 5, 24, 1, 51, 0, DateTimeKind.Utc))),
            [Other] = new(Other, new DateTime(2026, 2, 16, 0, 0, 0, DateTimeKind.Utc),
                VintageClassifier.Classify(Other, new DateTime(2026, 2, 16, 0, 0, 0, DateTimeKind.Utc))),
        };
        var empty = new VintageBreakdown(Array.Empty<VintageRow>(), Array.Empty<VintageRow>(), 0, Array.Empty<VintageRow>());
        return new ProvenanceReport(outputs, origins, empty, empty, outputs.Sum(o => o.Satoshis), 0);
    }

    private static OutputProvenance Utxo(string txid, long sats, params (long Len, long? Height)[] runs)
    {
        var segments = new List<SatSegment>();
        long offset = 0;
        foreach (var (len, height) in runs)
        {
            segments.Add(new SatSegment(offset, len, height));
            offset += len;
        }
        return new OutputProvenance(txid, 0, sats, new Dictionary<long, long>(), segments);
    }

    // ── holdings ──

    [Fact]
    public void holdings_rank_pure_coins_above_blended_ones()
    {
        var report = Report(
            Utxo("blended", 1000, (400, Pure413), (600, Other)),
            Utxo("pure", 500, (500, Pure413)));

        var holdings = VintageCoinControl.Holdings(report, Pure413);

        Assert.Equal("pure", holdings[0].Output.TxId);
        Assert.True(holdings[0].IsPure);
        Assert.Equal("blended", holdings[1].Output.TxId);
        Assert.Equal(0.4, holdings[1].Purity, 3);
        Assert.Equal(900, VintageCoinControl.Available(report, Pure413));
    }

    [Fact]
    public void sendable_vintages_group_by_vintage_not_by_block()
    {
        // A mining wallet holds thousands of coinbases from the SAME year. The picker
        // must show "2026" once with the combined total — listing every block repeated
        // the same year and amount over and over (the 2026-08-01 dropdown bug).
        var origins = new Dictionary<long, MintPoint>();
        var outputs = new List<OutputProvenance>();
        for (long h = 4_000_001; h <= 4_000_005; h++)
        {
            var t = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            origins[h] = new MintPoint(h, t, VintageClassifier.Classify(h, t));
            outputs.Add(new OutputProvenance($"cb{h}", 0, 100, new Dictionary<long, long>(),
                new[] { new SatSegment(0, 100, h) }));
        }
        var empty = new VintageBreakdown(Array.Empty<VintageRow>(), Array.Empty<VintageRow>(), 0, Array.Empty<VintageRow>());
        var report = new ProvenanceReport(outputs, origins, empty, empty, 500, 0);

        var sendable = VintageCoinControl.SendableVintages(report);

        // Two entries for five blocks: the YEAR and the one month they all fall in —
        // never one per block.
        Assert.Collection(sendable,
            v => { Assert.Equal("2026", v.Key); Assert.False(v.IsMonth); Assert.Equal(500, v.Satoshis); Assert.Equal(5, v.Heights.Count); },
            v => { Assert.Equal("2026-06", v.Key); Assert.True(v.IsMonth); Assert.Equal("Jun 2026", v.Label); Assert.Equal(500, v.Satoshis); });

        Assert.Equal(500, VintageCoinControl.Available(report, sendable[0].Heights));
    }

    [Fact]
    public void months_are_offered_separately_so_a_single_month_can_be_sent()
    {
        // Two coins from the SAME year but different months: the year offers both,
        // each month offers only its own.
        var origins = new Dictionary<long, MintPoint>();
        var outputs = new List<OutputProvenance>();
        foreach (var (h, month) in new[] { (4_000_001L, 5), (4_000_002L, 6) })
        {
            var t = new DateTime(2026, month, 10, 0, 0, 0, DateTimeKind.Utc);
            origins[h] = new MintPoint(h, t, VintageClassifier.Classify(h, t));
            outputs.Add(new OutputProvenance($"cb{h}", 0, 100_000, new Dictionary<long, long>(),
                new[] { new SatSegment(0, 100_000, h) }));
        }
        var empty = new VintageBreakdown(Array.Empty<VintageRow>(), Array.Empty<VintageRow>(), 0, Array.Empty<VintageRow>());
        var report = new ProvenanceReport(outputs, origins, empty, empty, 200_000, 0);

        var sendable = VintageCoinControl.SendableVintages(report);
        var year = sendable.Single(v => !v.IsMonth);
        var may = sendable.Single(v => v.Key == "2026-05");

        Assert.Equal(200_000, year.Satoshis);      // the year holds both coins
        Assert.Equal(100_000, may.Satoshis);       // May holds only its own
        Assert.Equal("May 2026", may.Label);

        // Sending "May 2026" must spend the May coin, not the June one.
        var plan = VintageCoinControl.PlanSend(report, may.Heights, may.FirstHeight,
            100_000, "Brecipient", "Bchange", 0).Plan!;
        Assert.Equal("cb4000001", Assert.Single(plan.Inputs).TxId);
        Assert.Equal(100_000, VintageCoinControl.VerifyVintageDelivered(plan, report));
    }

    [Fact]
    public void a_whole_year_send_may_draw_on_any_of_its_blocks()
    {
        var origins = new Dictionary<long, MintPoint>();
        var outputs = new List<OutputProvenance>();
        foreach (var h in new long[] { 4_000_001, 4_000_002 })
        {
            var t = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            origins[h] = new MintPoint(h, t, VintageClassifier.Classify(h, t));
            outputs.Add(new OutputProvenance($"cb{h}", 0, 100, new Dictionary<long, long>(),
                new[] { new SatSegment(0, 100, h) }));
        }
        var empty = new VintageBreakdown(Array.Empty<VintageRow>(), Array.Empty<VintageRow>(), 0, Array.Empty<VintageRow>());
        var report = new ProvenanceReport(outputs, origins, empty, empty, 200, 0);
        var year = VintageCoinControl.SendableVintages(report).Single(v => !v.IsMonth);

        // 150 sats needs BOTH coins — neither block alone covers it.
        var plan = VintageCoinControl.PlanSend(report, year.Heights, year.FirstHeight,
            150, "Brecipient", "Bchange", 0, dustThresholdSatoshis: 0).Plan!;

        Assert.Equal(2, plan.Inputs.Count);
        Assert.Equal(150, VintageCoinControl.VerifyVintageDelivered(plan, report));  // both blocks count
    }

    [Fact]
    public void a_mixed_vintage_also_offers_a_pure_coins_only_entry()
    {
        // 500 sats of the vintage sit in a pure coin, another 400 inside a blended one.
        var report = Report(
            Utxo("pure", 500, (500, Pure413)),
            Utxo("blended", 1000, (400, Pure413), (600, Other)));

        var sendable = VintageCoinControl.SendableVintages(report);
        var plain = sendable.Single(v => v.Key == "block-413");
        var pureOnly = sendable.Single(v => v.Key == "block-413" + VintageCoinControl.PureSuffix);

        Assert.Equal(900, plain.Satoshis);       // everything of that vintage
        Assert.Equal(500, pureOnly.Satoshis);    // only what sits in whole pure coins
        Assert.True(pureOnly.PureOnly);
        Assert.Contains("unmixed outputs only", pureOnly.Label);
    }

    [Fact]
    public void an_all_pure_vintage_gets_no_duplicate_entry()
    {
        // Every coin is already pure, so a "pure only" twin would just repeat the row —
        // the planner prefers pure coins anyway.
        var report = Report(Utxo("pure", 500, (500, Pure413)));

        var sendable = VintageCoinControl.SendableVintages(report);

        Assert.Single(sendable);
        Assert.False(sendable[0].PureOnly);
    }

    // ── the dust floor ──

    [Fact]
    public void a_send_below_the_dust_limit_is_refused_before_it_reaches_the_network()
    {
        // Free relay does NOT mean any size relays: dustrelayfee is separate policy, and
        // the live daemon rejects 545 satoshis as "dust" while accepting 546 (verified
        // 2026-08-01). A 1-sat send failed on the network before this guard existed.
        var report = Report(Utxo("pure", 41_300_000_000, (41_300_000_000, Pure413)));
        var heights = new HashSet<long> { Pure413 };

        foreach (var tooSmall in new long[] { 1, 10, 100, 545 })
        {
            var refused = VintageCoinControl.PlanSend(report, heights, Pure413, tooSmall,
                "Brecipient", "Bchange", 0);
            Assert.Null(refused.Plan);
            Assert.Contains("dust limit", refused.Problem);
        }

        // 546 is the floor and goes through.
        var ok = VintageCoinControl.PlanSend(report, heights, Pure413,
            VintageCoinControl.DustThresholdSatoshis, "Brecipient", "Bchange", 0).Plan!;
        Assert.Equal(546, ok.SendAmount);
        Assert.Equal(546, VintageCoinControl.VerifyVintageDelivered(ok, report));
    }

    [Fact]
    public void dusty_CHANGE_is_caught_too_not_just_the_amount()
    {
        // The amount clears the floor but the change left behind would not — the whole
        // transaction would be rejected, so the plan is refused with the change named.
        var report = Report(Utxo("pure", 1_000, (1_000, Pure413)));

        var result = VintageCoinControl.PlanSend(report, new HashSet<long> { Pure413 }, Pure413,
            600, "Brecipient", "Bchange", 0);

        Assert.Null(result.Plan);
        Assert.Contains("change coming back to you", result.Problem);
        Assert.Contains("400 satoshis", result.Problem);
    }

    // ── whole coins (the collector's send) ──

    [Fact]
    public void a_whole_coin_is_sent_intact_with_no_change_output()
    {
        // Block 413's entire 413 BLZ coinbase, plus a bigger coin it must not touch.
        var report = Report(
            Utxo("cb413", 41_300_000_000, (41_300_000_000, Pure413)),
            Utxo("cb413b", 82_600_000_000, (82_600_000_000, Pure413)));

        var plan = VintageCoinControl.PlanWholeCoins(report, new HashSet<long> { Pure413 }, Pure413,
            1, "Bcollector", 0).Plan!;

        Assert.Single(plan.Inputs);
        Assert.Equal("cb413", plan.Inputs[0].TxId);              // smallest coin chosen
        var only = Assert.Single(plan.Outputs);                  // ONE output — no change
        Assert.Equal(OutputRole.Vintage, only.Role);
        Assert.Equal(41_300_000_000, only.Satoshis);             // the coin, entire
        Assert.True(plan.Balances);
        Assert.Equal(41_300_000_000, VintageCoinControl.VerifyVintageDelivered(plan, report));
        Assert.Contains(plan.Warnings, w => w.Contains("EXACTLY as it was minted"));
        Assert.Contains(plan.Warnings, w => w.Contains("entire coinbase of block 413"));
    }

    [Fact]
    public void a_named_coin_size_beats_the_smallest_first_default()
    {
        // The wallet holds a full coinbase AND a small pure coin of the same vintage.
        // Without naming a size the small one goes (which surprised a real send on
        // 2026-08-01: 0.02675 BLZ left instead of a 51.625 coinbase).
        var report = Report(
            Utxo("dust", 2_675_000, (2_675_000, Pure413)),
            Utxo("coinbase", 5_162_500_000, (5_162_500_000, Pure413)));
        var heights = new HashSet<long> { Pure413 };

        var defaulted = VintageCoinControl.PlanWholeCoins(report, heights, Pure413, 1, "B", 0).Plan!;
        Assert.Equal("dust", defaulted.Inputs[0].TxId);

        // Naming the size sends the coin the collector actually meant.
        var chosen = VintageCoinControl.PlanWholeCoins(report, heights, Pure413, 1, "B", 0,
            coinValueSatoshis: 5_162_500_000).Plan!;
        Assert.Equal("coinbase", Assert.Single(chosen.Inputs).TxId);
        Assert.Equal(5_162_500_000, Assert.Single(chosen.Outputs).Satoshis);

        // The sizes on offer, largest first, with counts.
        Assert.Collection(VintageCoinControl.WholeCoinSizes(report, heights),
            s => { Assert.Equal(5_162_500_000, s.Satoshis); Assert.Equal(1, s.Count); },
            s => { Assert.Equal(2_675_000, s.Satoshis); Assert.Equal(1, s.Count); });

        // A size the vintage doesn't hold is refused, not silently substituted.
        Assert.Contains("no whole output of exactly",
            VintageCoinControl.PlanWholeCoins(report, heights, Pure413, 1, "B", 0, 999).Problem);
    }

    [Fact]
    public void a_fee_breaks_the_whole_coin_and_says_so()
    {
        var report = Report(Utxo("cb", 41_300_000_000, (41_300_000_000, Pure413)));

        var plan = VintageCoinControl.PlanWholeCoins(report, new HashSet<long> { Pure413 }, Pure413,
            1, "Bcollector", 1_000).Plan!;

        Assert.Equal(41_299_999_000, Assert.Single(plan.Outputs).Satoshis);   // short by the fee
        Assert.True(plan.Balances);
        Assert.Contains(plan.Warnings, w => w.Contains("arrives short of its minted value"));
    }

    [Fact]
    public void whole_coin_sends_refuse_what_the_vintage_cannot_supply()
    {
        var report = Report(
            Utxo("pure", 500, (500, Pure413)),
            Utxo("blended", 1000, (400, Other), (600, Pure413)));

        var heights = new HashSet<long> { Pure413 };
        Assert.Contains("fewer than the 3",
            VintageCoinControl.PlanWholeCoins(report, heights, Pure413, 3, "B", 0).Problem);
        Assert.Contains("at least one output",
            VintageCoinControl.PlanWholeCoins(report, heights, Pure413, 0, "B", 0).Problem);
        Assert.Contains("consume the whole output",
            VintageCoinControl.PlanWholeCoins(report, heights, Pure413, 1, "B", 9_999).Problem);

        // A vintage held only inside blended coins has no whole coin to send.
        Assert.Contains("no whole (pure) outputs",
            VintageCoinControl.PlanWholeCoins(report, new HashSet<long> { Other }, Other, 1, "B", 0).Problem);
    }

    [Fact]
    public void a_whole_block_sends_every_coin_that_block_minted()
    {
        // Block 413's coinbase really paid THREE outputs — the whole block is all of
        // them together, which is a bigger collector unit than any single coin.
        var report = Report(
            Utxo("cb413-0", 40_845_700_000, (40_845_700_000, Pure413)),
            Utxo("cb413-1", 413_000_000, (413_000_000, Pure413)),
            Utxo("cb413-2", 41_300_000, (41_300_000, Pure413)),
            Utxo("elsewhere", 5_000_000_000, (5_000_000_000, Other)));

        var plan = VintageCoinControl.PlanWholeBlock(report, Pure413, "Bcollector", 0).Plan!;

        Assert.Equal(3, plan.Inputs.Count);                       // all three outputs
        var only = Assert.Single(plan.Outputs);                   // one output, no change
        Assert.Equal(41_300_000_000, only.Satoshis);              // the full 413 BLZ reward
        Assert.True(plan.Balances);
        Assert.Equal(41_300_000_000, VintageCoinControl.VerifyVintageDelivered(plan, report));
        Assert.Contains(plan.Warnings, w => w.Contains("every coin this wallet holds from block 413"));
        Assert.Contains(plan.Warnings, w => w.Contains("EXACTLY as it was minted"));
    }

    [Fact]
    public void blocks_held_whole_are_listed_so_a_height_never_has_to_be_guessed()
    {
        // Two outputs of block 413 (an early three-way reward), one modern block, and a
        // blended output that belongs to no single block.
        var report = Report(
            Utxo("cb413-a", 40_845_700_000, (40_845_700_000, Pure413)),
            Utxo("cb413-b", 413_000_000, (413_000_000, Pure413)),
            Utxo("modern", 5_162_500_000, (5_162_500_000, Other)),
            Utxo("blended", 1000, (400, Pure413), (600, Other)));

        var blocks = VintageCoinControl.BlocksHeld(report);

        // Richest first; the blended output contributes to neither block's row.
        Assert.Collection(blocks,
            b => { Assert.Equal(Pure413, b.Height); Assert.Equal(2, b.Outputs); Assert.Equal(41_258_700_000, b.Satoshis); },
            b => { Assert.Equal(Other, b.Height); Assert.Equal(1, b.Outputs); Assert.Equal(5_162_500_000, b.Satoshis); });

        // Narrowing to a vintage lists only that vintage's blocks.
        var only413 = VintageCoinControl.BlocksHeld(report, new HashSet<long> { Pure413 });
        Assert.Equal(Pure413, Assert.Single(only413).Height);

        // And the listed figure is what a whole-block send actually delivers.
        var plan = VintageCoinControl.PlanWholeBlock(report, Pure413, "Bcollector", 0).Plan!;
        Assert.Equal(41_258_700_000, Assert.Single(plan.Outputs).Satoshis);
    }

    [Fact]
    public void a_whole_block_refuses_when_the_wallet_holds_none_of_it()
    {
        var report = Report(Utxo("pure", 500, (500, Pure413)));

        var result = VintageCoinControl.PlanWholeBlock(report, 999_999, "Bcollector", 0);

        Assert.Null(result.Plan);
        Assert.Contains("no whole outputs minted by block 999,999", result.Problem);
    }

    [Fact]
    public void purity_is_measured_against_the_chosen_vintage_not_a_single_block()
    {
        // One coin built from TWO blocks of the same year. It is 100% "2026" — pure for
        // the YEAR — but not pure for either block on its own. The page's Pure Coins
        // table must count it the way the picker does, or the two quote different
        // totals (the 2026-08-01 report).
        var a = 4_000_001L; var b = 4_000_002L;
        var t = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var origins = new Dictionary<long, MintPoint>
        {
            [a] = new(a, t, VintageClassifier.Classify(a, t)),
            [b] = new(b, t, VintageClassifier.Classify(b, t)),
        };
        var mixed = new OutputProvenance("two-blocks", 0, 1000, new Dictionary<long, long>(),
            new[] { new SatSegment(0, 600, a), new SatSegment(600, 400, b) });
        var empty = new VintageBreakdown(Array.Empty<VintageRow>(), Array.Empty<VintageRow>(), 0, Array.Empty<VintageRow>());
        var report = new ProvenanceReport(new[] { mixed }, origins, empty, empty, 1000, 0);

        var year = VintageCoinControl.SendableVintages(report).Single(v => !v.IsMonth && !v.PureOnly);

        // Pure for the whole year …
        Assert.Equal(1000, VintageCoinControl.PureSatoshis(report, year.Heights));
        // … but not for either single block.
        Assert.Equal(0, VintageCoinControl.PureSatoshis(report, new HashSet<long> { a }));
        Assert.Equal(0, VintageCoinControl.PureSatoshis(report, new HashSet<long> { b }));

        // And because it is year-pure, the whole coin can be sent as that vintage.
        var plan = VintageCoinControl.PlanSend(report, year.Heights, year.FirstHeight, 1000,
            "Brecipient", "Bchange", 0, pureOnly: true).Plan!;
        Assert.Equal(1000, VintageCoinControl.VerifyVintageDelivered(plan, report));
    }

    [Fact]
    public void pure_only_refuses_to_slice_a_blended_coin()
    {
        // 300 in a pure coin, and a 400-sat run of the same vintage inside a blended one.
        // 350 is more than the pure coins hold but fits inside the blended run.
        var report = Report(
            Utxo("pure", 300, (300, Pure413)),
            Utxo("blended", 1000, (400, Pure413), (600, Other)));
        var heights = new HashSet<long> { Pure413 };

        // Pure-only must REFUSE rather than quietly slicing the blended coin.
        var refused = VintageCoinControl.PlanSend(report, heights, Pure413, 350,
            "Brecipient", "Bchange", 0, pureOnly: true, dustThresholdSatoshis: 0);
        Assert.Null(refused.Plan);
        Assert.Contains("Pure coins of that vintage total 300", refused.Problem);

        // The same request WITHOUT the restriction slices the blended coin instead.
        var sliced = VintageCoinControl.PlanSend(report, heights, Pure413, 350,
            "Brecipient", "Bchange", 0, dustThresholdSatoshis: 0).Plan!;
        Assert.Equal("blended", Assert.Single(sliced.Inputs).TxId);
        Assert.Equal(350, VintageCoinControl.VerifyVintageDelivered(sliced, report));
    }

    // ── pure coins ──

    [Fact]
    public void a_pure_coin_sends_whole_with_change_and_the_fee_from_the_tail()
    {
        var report = Report(Utxo("pure", 1000, (1000, Pure413)));

        var result = VintageCoinControl.PlanSend(report, new HashSet<long> { Pure413 }, Pure413, 600, "Brecipient", "Bchange", 10, dustThresholdSatoshis: 0);

        var plan = result.Plan!;
        Assert.Null(result.Problem);
        Assert.True(plan.Balances);                         // 1000 in = 990 out + 10 fee
        Assert.Collection(plan.Outputs,
            o => { Assert.Equal(OutputRole.Vintage, o.Role); Assert.Equal(600, o.Satoshis); Assert.Equal("Brecipient", o.Address); },
            o => { Assert.Equal(OutputRole.Change, o.Role); Assert.Equal(390, o.Satoshis); });
        Assert.Equal(600, VintageCoinControl.VerifyVintageDelivered(plan, report));
    }

    [Fact]
    public void the_gift_case_the_smallest_relayable_slice_of_a_badge_lineage()
    {
        // The on-brand use: post someone enough of a badge vintage that THEY can claim
        // it on the website (10 sats ever-held). Sends are FREE here, but the network
        // still refuses dust, so the smallest possible gift is the 546-satoshi floor —
        // comfortably above the site's 10-sat claim threshold. Asking for 10 is refused.
        var report = Report(Utxo("pure", 41_300_000_000, (41_300_000_000, Pure413)));
        var heights = new HashSet<long> { Pure413 };

        Assert.Contains("dust limit",
            VintageCoinControl.PlanSend(report, heights, Pure413, 10, "Bfriend", "Bchange", 0).Problem);

        var plan = VintageCoinControl.PlanSend(report, heights, Pure413,
            VintageCoinControl.DustThresholdSatoshis, "Bfriend", "Bchange", 0).Plan!;

        Assert.Equal(546, plan.SendAmount);
        Assert.Equal(546, VintageCoinControl.VerifyVintageDelivered(plan, report));
        Assert.True(plan.Balances);
        Assert.True(plan.SendAmount > 10);   // still clears the site's claim threshold
    }

    // ── exact slicing of a blended coin ──

    [Fact]
    public void a_blended_coin_is_sliced_with_leading_change_positioning_the_vintage()
    {
        // 1000-sat output: [0,600) other lineage, [600,1000) the vintage.
        var report = Report(Utxo("blended", 1000, (600, Other), (400, Pure413)));

        var result = VintageCoinControl.PlanSend(report, new HashSet<long> { Pure413 }, Pure413, 300, "Brecipient", "Bchange", 10, dustThresholdSatoshis: 0);

        var plan = result.Plan!;
        Assert.Collection(plan.Outputs,
            o => { Assert.Equal(OutputRole.LeadingChange, o.Role); Assert.Equal(600, o.Satoshis); },
            o => { Assert.Equal(OutputRole.Vintage, o.Role); Assert.Equal(300, o.Satoshis); },
            o => { Assert.Equal(OutputRole.Change, o.Role); Assert.Equal(90, o.Satoshis); });
        Assert.True(plan.Balances);

        // The proof: the recipient's slot really lands on the vintage run.
        Assert.Equal(300, VintageCoinControl.VerifyVintageDelivered(plan, report));
        Assert.Contains(plan.Warnings, w => w.Contains("ORDER"));
    }

    [Fact]
    public void slicing_refuses_when_the_fee_would_eat_into_the_vintage()
    {
        // The vintage run sits at the very END, so the tail the fee comes from IS the
        // vintage — the planner must refuse rather than quietly send fewer vintage sats.
        var report = Report(Utxo("blended", 1000, (600, Other), (400, Pure413)));

        var result = VintageCoinControl.PlanSend(report, new HashSet<long> { Pure413 }, Pure413, 400, "Brecipient", "Bchange", 10, dustThresholdSatoshis: 0);

        Assert.Null(result.Plan);
        Assert.Contains("fee would eat into the vintage", result.Problem);
    }

    [Fact]
    public void a_vintage_at_the_front_needs_no_leading_change()
    {
        var report = Report(Utxo("blended", 1000, (400, Pure413), (600, Other)));

        var plan = VintageCoinControl.PlanSend(report, new HashSet<long> { Pure413 }, Pure413, 400, "Brecipient", "Bchange", 10, dustThresholdSatoshis: 0).Plan!;

        Assert.Collection(plan.Outputs,
            o => { Assert.Equal(OutputRole.Vintage, o.Role); Assert.Equal(400, o.Satoshis); },
            o => { Assert.Equal(OutputRole.Change, o.Role); Assert.Equal(590, o.Satoshis); });
        Assert.Equal(400, VintageCoinControl.VerifyVintageDelivered(plan, report));
    }

    // ── refusals ──

    [Fact]
    public void it_refuses_what_the_wallet_does_not_hold()
    {
        var report = Report(Utxo("pure", 500, (500, Pure413)));

        Assert.Contains("Only 500", VintageCoinControl.PlanSend(report, Pure413, 900, "B", "B", 0).Problem);
        Assert.Contains("holds none", VintageCoinControl.PlanSend(report, 999_999, 10, "B", "B", 0).Problem);
        Assert.Contains("positive", VintageCoinControl.PlanSend(report, Pure413, 0, "B", "B", 0).Problem);
        Assert.Contains("recipient", VintageCoinControl.PlanSend(report, Pure413, 10, " ", "B", 0).Problem);
    }

    [Fact]
    public void it_refuses_when_the_vintage_is_fragmented_below_the_amount()
    {
        // 200 + 200 of the vintage, but in two separate blended outputs — no single run
        // is big enough, and combining them would need a consolidation that re-blends.
        var report = Report(
            Utxo("a", 1000, (200, Pure413), (800, Other)),
            Utxo("b", 1000, (200, Pure413), (800, Other)));

        var result = VintageCoinControl.PlanSend(report, Pure413, 400, "B", "Bchange", 0);

        Assert.Null(result.Plan);
        Assert.Contains("runs smaller than the amount", result.Problem);
    }

    // ── the warnings that protect a collection ──

    [Fact]
    public void merging_several_pure_coins_is_warned_about()
    {
        var report = Report(
            Utxo("p1", 300, (300, Pure413)),
            Utxo("p2", 300, (300, Pure413)),
            Utxo("p3", 300, (300, Pure413)));

        var plan = VintageCoinControl.PlanSend(report, new HashSet<long> { Pure413 }, Pure413, 800, "Brecipient", "Bchange", 0, dustThresholdSatoshis: 0).Plan!;

        Assert.Equal(3, plan.Inputs.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("merge into one lineage stream"));
        Assert.Equal(800, VintageCoinControl.VerifyVintageDelivered(plan, report));
    }

    [Fact]
    public void paying_the_fee_in_vintage_satoshis_is_stated_plainly()
    {
        var report = Report(Utxo("pure", 1000, (1000, Pure413)));

        var plan = VintageCoinControl.PlanSend(report, new HashSet<long> { Pure413 }, Pure413, 500, "Brecipient", "Bchange", 25, dustThresholdSatoshis: 0).Plan!;

        Assert.Contains(plan.Warnings, w => w.Contains("fee is taken from the tail"));
        Assert.Contains(plan.Warnings, w => w.Contains("return as change"));
    }

    [Fact]
    public void verification_reports_zero_when_the_inputs_are_not_in_the_report()
    {
        var report = Report(Utxo("known", 500, (500, Pure413)));
        var bogus = new VintageSendPlan(Pure413, new HashSet<long> { Pure413 },
            new[] { new PlannedInput("unknown-txid", 0, 500) },
            new[] { new PlannedOutput("B", 500, OutputRole.Vintage) }, 0, Array.Empty<string>());

        Assert.Equal(0, VintageCoinControl.VerifyVintageDelivered(bogus, report));
    }
}

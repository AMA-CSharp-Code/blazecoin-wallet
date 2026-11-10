using System.Text.Json;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Provenance.Explorer;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// "Send by explorer lineage" (2026-10-31): whole outputs picked by the explorer's stored
/// PROPORTIONAL lineage, for wallets whose own walk cannot reach the old lineage. Pins the
/// join against the wallet's own unspent list (the authority on what can be spent), the
/// catalogue and purity arithmetic, the planner's refusals, and that every plan carries the
/// model caveat and no pedigree heights (so the FIFO verifier has nothing to claim).
/// </summary>
public class ExplorerVintageSendTests
{
    private static UnspentOutput U(string tx, int vout, long sats) => new(tx, vout, sats, "BQtest", 100);
    private static ExplorerOutputVintage O(string tx, int vout, long sats, params (int Key, long Sats)[] rows)
        => new(tx, vout, sats, rows.Select(r => new ExplorerOutputVintageRow(r.Key, r.Sats)).ToList());

    // 2014 (key 2014), premine (key 0), 2026 (key 2026)
    private static readonly ExplorerAddressOutputs Answer = new("BQa", 4_382_915, new[]
    {
        O("t1", 0, 1_000, (2014, 1_000)),                       // pure 2014
        O("t2", 0, 1_000, (2014, 920), (2026, 80)),             // 92 % 2014
        O("t3", 1, 2_000, (2014, 600), (2026, 1_400)),          // 30 % 2014
        O("t4", 0, 500, (0, 500)),                              // pure premine
        O("t5", 0, 700),                                        // not reached by the worker yet
        O("gone", 0, 300, (2014, 300)),                         // spent locally — must be ignored
    });

    private static readonly IReadOnlyList<UnspentOutput> Local = new[]
    {
        U("t1", 0, 1_000), U("t2", 0, 1_000), U("t3", 1, 2_000), U("t4", 0, 500), U("t5", 0, 700),
        U("onlylocal", 0, 50),                                   // the explorer hasn't seen it
    };

    [Fact]
    public void Join_keeps_only_outputs_the_wallet_still_holds_and_maps_keys_the_wallets_way()
    {
        var joined = ExplorerVintageSend.Join(new[] { Answer }, Local);

        Assert.Equal(5, joined.Count);
        Assert.DoesNotContain(joined, j => j.Output.TxId == "gone");
        Assert.DoesNotContain(joined, j => j.Output.TxId == "onlylocal");
        var t2 = joined.Single(j => j.Output.TxId == "t2");
        Assert.Equal(920, t2.ByVintage["2014"]);
        Assert.Equal(80, t2.ByVintage["2026"]);
        Assert.Equal(500, joined.Single(j => j.Output.TxId == "t4").ByVintage["premine"]);
        Assert.Empty(joined.Single(j => j.Output.TxId == "t5").ByVintage);
    }

    [Fact]
    public void Join_ignores_duplicates_and_unknown_keys()
    {
        var dup = new ExplorerAddressOutputs("BQb", 1, new[] { O("t1", 0, 1_000, (2014, 1_000), (60, 5)) });
        var joined = ExplorerVintageSend.Join(new[] { Answer, dup }, Local);
        Assert.Single(joined, j => j.Output.TxId == "t1");
        Assert.False(joined.Single(j => j.Output.TxId == "t1").ByVintage.ContainsKey("leap-2012"));
    }

    [Fact]
    public void Catalogue_sums_lineage_counts_outputs_and_pure_ones_years_then_specials()
    {
        var joined = ExplorerVintageSend.Join(new[] { Answer }, Local);
        var cat = ExplorerVintageSend.Catalogue(joined, minPurity: 0.9);

        Assert.Equal(new[] { "2014", "2026", "premine" }, cat.Select(c => c.Key));
        var y2014 = cat[0];
        Assert.Equal(2_520, y2014.Satoshis);
        Assert.Equal(3, y2014.OutputCount);
        Assert.Equal(2, y2014.PureCount);           // t1 (100 %) and t2 (92 %); t3 is 30 %
        Assert.Equal("Premine", cat[2].Label);
        Assert.Equal(1, cat[2].Height);
        Assert.True(cat[2].IsSpecial);
    }

    [Fact]
    public void Holdings_are_purest_first_then_smallest()
    {
        var joined = ExplorerVintageSend.Join(new[] { Answer }, Local);
        var h = ExplorerVintageSend.Holdings(joined, "2014");
        Assert.Equal(new[] { "t1", "t2", "t3" }, h.Select(x => x.TxId));
        Assert.Equal(1.0, h[0].Purity);
        Assert.Equal(0.92, h[1].Purity, 3);
        Assert.Equal(new[] { (1_000L, 2) }, ExplorerVintageSend.Sizes(h, 0.9));
    }

    private static ExplorerSendableVintage V2014 => new("2014", "2014", VintageKind.Year, 0, 2_520, 3, 2);

    [Fact]
    public void Plan_sends_whole_outputs_as_one_output_with_the_model_caveat_and_no_pedigree_heights()
    {
        var h = ExplorerVintageSend.Holdings(ExplorerVintageSend.Join(new[] { Answer }, Local), "2014");
        var r = ExplorerVintageSend.PlanWholeCoins(h, V2014, coinCount: 2, minPurity: 0.9, recipient: TestAddr.Dest, feeSatoshis: 0);

        Assert.Null(r.Problem);
        var plan = r.Plan!;
        Assert.Equal(new[] { ("t1", 0), ("t2", 0) }, plan.Inputs.Select(i => (i.TxId, i.Vout)));
        var o = Assert.Single(plan.Outputs);
        Assert.Equal((TestAddr.Dest, 2_000L, OutputRole.Vintage), (o.Address, o.Satoshis, o.Role));
        Assert.True(plan.Balances);
        Assert.Empty(plan.TargetHeights);
        Assert.Contains(plan.Warnings, w => w.Contains("PROPORTIONAL") && w.Contains("1,920 of 2,000"));
        Assert.Contains(plan.Warnings, w => w.Contains("Zero fee"));
        Assert.Contains(plan.Warnings, w => w.Contains("merge on arrival"));
    }

    [Fact]
    public void Plan_refuses_when_nothing_is_pure_enough_or_too_few_qualify()
    {
        var h = ExplorerVintageSend.Holdings(ExplorerVintageSend.Join(new[] { Answer }, Local), "2014");
        Assert.Contains("No output is at least",
            ExplorerVintageSend.PlanWholeCoins(h.Where(x => x.TxId != "t1").ToList(), V2014, 1, 1.0, TestAddr.Dest, 0).Problem);
        Assert.Contains("fewer than the 3", ExplorerVintageSend.PlanWholeCoins(h, V2014, 3, 0.9, TestAddr.Dest, 0).Problem);
        Assert.Contains("exactly 999", ExplorerVintageSend.PlanWholeCoins(h, V2014, 1, 0.9, TestAddr.Dest, 0, coinValueSatoshis: 999).Problem);
    }

    [Fact]
    public void Plan_refuses_a_fee_that_eats_the_output_and_a_dusty_result()
    {
        var h = ExplorerVintageSend.Holdings(ExplorerVintageSend.Join(new[] { Answer }, Local), "2014");
        Assert.Contains("consume the whole output", ExplorerVintageSend.PlanWholeCoins(h, V2014, 1, 0.9, TestAddr.Dest, 1_000).Problem);
        Assert.Contains("dust limit", ExplorerVintageSend.PlanWholeCoins(h, V2014, 1, 0.9, TestAddr.Dest, 999).Problem);
        Assert.Contains("recipient", ExplorerVintageSend.PlanWholeCoins(h, V2014, 1, 0.9, " ", 0).Problem);
        // A blz1… (SegWit) recipient is refused too - the coins would be unspendable (bug-bounty report 2026-11-06, ninth round).
        Assert.Contains("SegWit", ExplorerVintageSend.PlanWholeCoins(h, V2014, 1, 0.9, "blz1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", 0).Problem);
        Assert.Contains("at least one", ExplorerVintageSend.PlanWholeCoins(h, V2014, 0, 0.9, TestAddr.Dest, 0).Problem);
        // The same 2,000 satoshis that reach a legacy address are dust for a post-quantum one: its limit
        // is 11,475 on node 2.1.2 (bug-bounty report 2026-11-06, seventh round).
        const string bq = "BQGeaQmsowAjL1ZG8q1AfVH4NgBvtJKJSsjnnKEf62BMjekdU82n";
        Assert.Null(ExplorerVintageSend.PlanWholeCoins(h, V2014, 2, 0.9, TestAddr.Dest, 0).Problem);
        Assert.Contains("11,475-satoshi dust limit", ExplorerVintageSend.PlanWholeCoins(h, V2014, 2, 0.9, bq, 0).Problem);
    }

    [Fact]
    public void A_named_size_picks_that_coin_and_a_fee_warns_about_the_shortfall()
    {
        var h = ExplorerVintageSend.Holdings(ExplorerVintageSend.Join(new[] { Answer }, Local), "2014");
        var r = ExplorerVintageSend.PlanWholeCoins(h, V2014, 1, 0.3, TestAddr.Dest, 10, coinValueSatoshis: 2_000);
        Assert.Null(r.Problem);
        Assert.Equal("t3", r.Plan!.Inputs.Single().TxId);
        Assert.Equal(1_990, r.Plan.SendAmount);
        Assert.Contains(r.Plan.Warnings, w => w.Contains("arrives short"));
    }

    [Fact]
    public void The_per_output_json_parses_and_tolerates_missing_rows()
    {
        using var doc = JsonDocument.Parse("""
            {"address":"BQa","computedToHeight":4382915,"outputs":[
              {"txId":"aa","vout":1,"satoshis":273431812400,"vintages":[{"year":2014,"satoshis":100},{"year":0,"satoshis":5}]},
              {"txId":"bb","vout":0,"satoshis":700,"vintages":[]},
              {"txId":"cc","vout":0,"satoshis":1}]}
            """);
        var parsed = ExplorerVintageClient.ParseOutputs(doc.RootElement, "fallback");
        Assert.Equal("BQa", parsed.Address);
        Assert.Equal(4_382_915, parsed.ComputedToHeight);
        Assert.Equal(3, parsed.Outputs.Count);
        Assert.Equal(2, parsed.Outputs[0].Vintages.Count);
        Assert.Empty(parsed.Outputs[1].Vintages);
        Assert.Empty(parsed.Outputs[2].Vintages);
        Assert.Equal("https://blazecoin.co.uk/indexer/api/address/BQa/outputs/vintage",
            ExplorerVintageClient.OutputsVintageUri("https://blazecoin.co.uk/", "BQa").ToString());
    }
}

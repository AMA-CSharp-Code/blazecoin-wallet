using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Provenance.Explorer;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// One "can this address be paid" rule and one dust floor (audit 2026-11-09). The rule was a check
/// of one prefix in two places and an allow-list in a third; the two dust figures were written
/// once for the desktop planners and again in the lite wallet's transaction builder.
/// </summary>
public class PayableAddressTests
{
    [Fact]
    public void Only_a_B_or_a_BQ_address_can_be_paid()
    {
        Assert.True(PayableAddress.CanBePaid(TestAddr.Recipient));
        Assert.True(PayableAddress.CanBePaid("  " + TestAddr.Recipient + " "));
        Assert.True(PayableAddress.CanBePaid(TestAddr.PostQuantum));
        Assert.Null(PayableAddress.Problem(TestAddr.Recipient, "recipient"));

        foreach (var bad in TestAddr.NotPayable)
        {
            Assert.False(PayableAddress.CanBePaid(bad));
            var why = PayableAddress.Problem(bad, "recipient");
            Assert.StartsWith("The recipient address is not a valid Blazecoin address: ", why);
            Assert.EndsWith(PayableAddress.Rule, why);
        }
        Assert.False(PayableAddress.CanBePaid(null));
        Assert.Equal("A change address is required.", PayableAddress.Problem("  ", "change"));
    }

    [Fact]
    public void The_sweep_planner_gives_that_rules_answer_in_that_rules_words()
    {
        // (The vintage planners and auto-payout are held to it in VintageCoinControlTests and AutoPayoutServiceTests.)
        const string bad = "3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy";
        var coin = new UnspentOutput("e", 0, 100_000_000, TestAddr.Recipient, 10);

        Assert.Equal(PayableAddress.Problem(bad, "destination"), QuantumSweepPlanner.Plan(new[] { coin }, bad).Problem);
        Assert.Null(QuantumSweepPlanner.KindOf(bad));          // the sweep's own kind test agrees
    }

    [Theory]
    [InlineData(false, 546)]
    [InlineData(true, 11_475)]
    public void The_dust_floor_is_one_figure_per_kind_of_output(bool postQuantum, long floor)
    {
        Assert.Equal(floor, DustPolicy.FloorFor(postQuantum));
        Assert.Equal(floor, DustPolicy.FloorFor(postQuantum ? TestAddr.PostQuantum : TestAddr.Recipient));
        Assert.Equal(postQuantum ? DustPolicy.PostQuantumFloorSatoshis : DustPolicy.LegacyFloorSatoshis, floor);
    }

    [Fact]
    public void The_planners_legacy_default_is_that_figure_and_a_BQ_output_is_never_held_to_less_than_its_own()
    {
        Assert.Equal(DustPolicy.LegacyFloorSatoshis, VintageCoinControl.DustThresholdSatoshis);
        Assert.Equal(DustPolicy.LegacyFloorSatoshis, QuantumSweepPlanner.DustThresholdSatoshis);

        Assert.Equal(1_000, DustPolicy.FloorFor(TestAddr.Recipient, legacyFloor: 1_000));
        Assert.Equal(11_475, DustPolicy.FloorFor(TestAddr.PostQuantum, legacyFloor: 1_000));
        Assert.Equal(20_000, DustPolicy.FloorFor(TestAddr.PostQuantum, legacyFloor: 20_000));
        Assert.Equal(0, DustPolicy.FloorFor(TestAddr.PostQuantum, legacyFloor: 0));      // 0 switches the check off
    }
}

using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>One transaction of a sweep: every listed input to one fresh address, fee from the total.</summary>
public sealed record SweepTx(
    IReadOnlyList<UnspentOutput> Inputs,
    string Destination,
    long OutputSatoshis,
    long FeeSatoshis,
    int EstimatedBytes)
{
    public long InputSatoshis => Inputs.Sum(i => i.Satoshis);
    public bool Balances => InputSatoshis == OutputSatoshis + FeeSatoshis;
}

/// <summary>A sweep plan: one or more transactions (batched by input count) plus warnings.</summary>
public sealed record SweepPlan(IReadOnlyList<SweepTx> Transactions, IReadOnlyList<string> Warnings)
{
    public long InputSatoshis => Transactions.Sum(t => t.InputSatoshis);
    public long OutputSatoshis => Transactions.Sum(t => t.OutputSatoshis);
    public long FeeSatoshis => Transactions.Sum(t => t.FeeSatoshis);
    public int InputCount => Transactions.Sum(t => t.Inputs.Count);
    public bool Balances => Transactions.All(t => t.Balances);
}

public sealed record SweepPlanResult(SweepPlan? Plan, string? Problem)
{
    public static SweepPlanResult Fail(string problem) => new(null, problem);
    public static SweepPlanResult Ok(SweepPlan plan) => new(plan, null);
}

/// <summary>
/// Plans the move of every output on an exposed address to ONE fresh, never-used legacy
/// address, so the coins sit behind a key the chain has never seen. Pure: no RPC, no
/// signing. Mirrors <see cref="VintageCoinControl"/>'s rules — explicit inputs, explicit
/// outputs, fee out of the total, nothing below the 546-sat dust floor.
/// </summary>
public static class QuantumSweepPlanner
{
    /// <summary>Legacy flat rate the chain's <c>fallbackfee</c> uses: 0.001 BLZ per kB.</summary>
    public const long DefaultFeeRateSatPerKb = 100_000;
    /// <summary>A whole-block-sized sweep is pointless; batches keep each tx comfortably relayable.</summary>
    public const int DefaultMaxInputsPerTx = 400;
    public const long DustThresholdSatoshis = VintageCoinControl.DustThresholdSatoshis;

    // Legacy P2PKH sizing: 148 bytes per input (compressed key), 34 per output, 10 overhead.
    private const int BytesPerInput = 148, BytesPerOutput = 34, BytesOverhead = 10;

    public static int EstimateBytes(int inputs, int outputs = 1) => BytesOverhead + BytesPerInput * inputs + BytesPerOutput * outputs;

    /// <summary>Fee for a tx of <paramref name="bytes"/> at <paramref name="satPerKb"/>, rounded UP to the satoshi.</summary>
    public static long FeeFor(int bytes, long satPerKb) => (bytes * satPerKb + 999) / 1000;

    public static SweepPlanResult Plan(
        IReadOnlyList<UnspentOutput> outputs,
        string destination,
        long feeRateSatPerKb = DefaultFeeRateSatPerKb,
        int maxInputsPerTx = DefaultMaxInputsPerTx,
        int minConfirmations = 1,
        long dustThresholdSatoshis = DustThresholdSatoshis)
    {
        if (string.IsNullOrWhiteSpace(destination)) return SweepPlanResult.Fail("No destination address.");
        try { BitcoinProtocol.AddressToP2PKH(destination.Trim()); }
        catch (FormatException) { return SweepPlanResult.Fail("The destination is not a valid Blazecoin legacy address."); }
        if (feeRateSatPerKb < 0) return SweepPlanResult.Fail("The fee rate cannot be negative.");
        if (maxInputsPerTx < 1) return SweepPlanResult.Fail("At least one input per transaction is needed.");

        var warnings = new List<string>();
        var eligible = outputs.Where(o => o.Confirmations >= minConfirmations).ToList();
        var skipped = outputs.Count - eligible.Count;
        if (skipped > 0) warnings.Add($"{skipped} unconfirmed output(s) left behind — sweep again once they confirm.");
        if (eligible.Count == 0) return SweepPlanResult.Fail("Nothing confirmed to sweep.");

        var txs = new List<SweepTx>();
        foreach (var batch in eligible.Chunk(maxInputsPerTx))
        {
            var inputs = batch.ToList();
            var bytes = EstimateBytes(inputs.Count);
            var fee = FeeFor(bytes, feeRateSatPerKb);
            var total = inputs.Sum(i => i.Satoshis);
            var output = total - fee;
            if (output < dustThresholdSatoshis)
            {
                warnings.Add($"A batch of {inputs.Count} output(s) worth {total:N0} sats would leave {output:N0} sats after the fee — below the {dustThresholdSatoshis} dust floor, left behind.");
                continue;
            }
            txs.Add(new SweepTx(inputs, destination.Trim(), output, fee, bytes));
        }
        if (txs.Count == 0) return SweepPlanResult.Fail("Every batch would fall below the dust floor after the fee — nothing to sweep.");
        if (txs.Count > 1) warnings.Add($"{txs.Count} transactions, all to the same fresh address. That address is reused between them but its key stays unexposed until it spends.");

        return SweepPlanResult.Ok(new SweepPlan(txs, warnings));
    }
}

using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>What a sweep's destination protects against.</summary>
public enum SweepDestinationKind
{
    /// <summary>A fresh legacy B… address: the key is only a hash again until it spends. Hides an
    /// exposed key; does NOT make the coins post-quantum.</summary>
    Legacy,
    /// <summary>A BQ… (P2PQH, ML-DSA-44) address: post-quantum whether it spends or not.</summary>
    PostQuantum,
}

/// <summary>One transaction of a sweep: every listed input to one destination, fee from the total.</summary>
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
public sealed record SweepPlan(IReadOnlyList<SweepTx> Transactions, IReadOnlyList<string> Warnings,
    SweepDestinationKind DestinationKind = SweepDestinationKind.Legacy)
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
/// Plans the move of a set of legacy outputs to ONE destination: a post-quantum BQ… address
/// (the goal since the 4,250,000 fork) or a fresh legacy address (the pre-fork mitigation that
/// only re-hides an exposed key). Pure: no RPC, no signing. Mirrors
/// <see cref="VintageCoinControl"/>'s rules — explicit inputs, explicit outputs, fee out of the
/// total, nothing below the 546-sat dust floor.
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
    /// <summary>A P2PQH output: 8 value + 1 length + 34-byte scriptPubKey (<c>0x20 &lt;pqkh:32&gt; OP_CHECKPQSIG</c>, PQ_SIGNATURES.md §5).</summary>
    public const int BytesPerPqOutput = 43;

    public static int EstimateBytes(int inputs, int outputs = 1) => BytesOverhead + BytesPerInput * inputs + BytesPerOutput * outputs;

    public static int EstimateBytes(int inputs, SweepDestinationKind kind) =>
        kind == SweepDestinationKind.PostQuantum
            ? BytesOverhead + BytesPerInput * inputs + BytesPerPqOutput
            : EstimateBytes(inputs);

    /// <summary>Fee for a tx of <paramref name="bytes"/> at <paramref name="satPerKb"/>, rounded UP to the satoshi.</summary>
    public static long FeeFor(int bytes, long satPerKb) => (bytes * satPerKb + 999) / 1000;

    /// <summary>Classifies a destination, or null when it is neither a legacy nor a BQ… address.</summary>
    public static SweepDestinationKind? KindOf(string? destination) =>
        BitcoinProtocol.KindOf(destination) switch
        {
            BlazecoinAddressKind.PostQuantum => SweepDestinationKind.PostQuantum,
            BlazecoinAddressKind.Legacy => SweepDestinationKind.Legacy,
            _ => null,
        };

    public static SweepPlanResult Plan(
        IReadOnlyList<UnspentOutput> outputs,
        string destination,
        long feeRateSatPerKb = DefaultFeeRateSatPerKb,
        int maxInputsPerTx = DefaultMaxInputsPerTx,
        int minConfirmations = 1,
        long dustThresholdSatoshis = DustThresholdSatoshis)
    {
        if (string.IsNullOrWhiteSpace(destination)) return SweepPlanResult.Fail("No destination address.");
        var kind = KindOf(destination);
        if (kind is null) return SweepPlanResult.Fail("The destination is not a valid Blazecoin address (a legacy B… or a post-quantum BQ… address).");
        if (feeRateSatPerKb < 0) return SweepPlanResult.Fail("The fee rate cannot be negative.");
        if (maxInputsPerTx < 1) return SweepPlanResult.Fail("At least one input per transaction is needed.");
        // The size model prices legacy inputs; a PQ input is ~3,782 vbytes and is already safe.
        if (outputs.Any(o => BitcoinProtocol.IsPostQuantumAddress(o.Address)))
            return SweepPlanResult.Fail("Some of these coins are already on a post-quantum address — there is nothing to move for those. Select only legacy coins.");

        var warnings = new List<string>();
        if (kind == SweepDestinationKind.Legacy)
            warnings.Add("The destination is a LEGACY address: this hides the key again but is NOT post-quantum. Use a BQ… address to protect the coins for good.");
        var eligible = outputs.Where(o => o.Confirmations >= minConfirmations).ToList();
        var skipped = outputs.Count - eligible.Count;
        if (skipped > 0) warnings.Add($"{skipped} unconfirmed output(s) left behind — sweep again once they confirm.");
        if (eligible.Count == 0) return SweepPlanResult.Fail("Nothing confirmed to sweep.");
        var sources = eligible.Select(o => o.Address).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).Count();
        if (sources > 1) warnings.Add($"Coins from {sources} addresses are combined, which links those addresses publicly.");

        var txs = new List<SweepTx>();
        foreach (var batch in eligible.Chunk(maxInputsPerTx))
        {
            var inputs = batch.ToList();
            var bytes = EstimateBytes(inputs.Count, kind.Value);
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
        if (txs.Count > 1)
            warnings.Add(kind == SweepDestinationKind.PostQuantum
                ? $"{txs.Count} transactions, all to the same BQ… address. Reusing a post-quantum address reveals nothing a quantum computer can use."
                : $"{txs.Count} transactions, all to the same fresh address. That address is reused between them but its key stays unexposed until it spends.");

        return SweepPlanResult.Ok(new SweepPlan(txs, warnings, kind.Value));
    }
}

using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Services.Consolidation;

/// <summary>
/// Plans the merge of a wallet's many small coins into a few large ones (2026-11-12, asked for by a
/// miner on Discord: a wallet fed by pool payouts ends up with thousands of small coins, and a
/// payment that needs too many of them fails with "The inputs size exceeds the maximum weight").
/// <para>Pure: no RPC, no signing. The plan is a <see cref="SweepPlan"/> — every listed input to ONE
/// destination, fee out of the total — so it goes out through the same one-attempt-per-batch
/// <see cref="SweepBroadcaster"/> the Quantum sweep uses.</para>
/// <para>What differs from <see cref="QuantumSweepPlanner"/>: post-quantum coins are welcome (they are
/// the ones that need it most: a BQ… input is 3,782 bytes, so a standard transaction carries 26 of
/// them against about 400 legacy ones), a batch is filled by BYTES and not by a fixed count, and a
/// batch of one coin is never made (it would merge nothing).</para>
/// </summary>
public static class ConsolidationPlanner
{
    public const long DefaultFeeRateSatPerKb = QuantumSweepPlanner.DefaultFeeRateSatPerKb;

    /// <summary>What a batch is filled to. The 1,000 bytes kept back cover the size model's slack
    /// (signature lengths vary by a byte or two, and a count above 252 takes two more bytes). It
    /// admits exactly 26 post-quantum inputs. The sizes themselves live in <see cref="TransactionSizes"/>.</summary>
    public const int BatchByteBudget = TransactionSizes.MaxStandardTx - 1_000;
    /// <summary>Never more inputs than this in one transaction, however small they are — the sweep's limit.</summary>
    public const int MaxInputsPerTx = QuantumSweepPlanner.DefaultMaxInputsPerTx;

    private const int BytesOverhead = TransactionSizes.Overhead;

    public static int InputBytes(UnspentOutput o) =>
        BitcoinProtocol.IsPostQuantumAddress(o.Address) ? TransactionSizes.PostQuantumInput : TransactionSizes.LegacyInput;

    private static int OutputBytes(SweepDestinationKind kind) =>
        kind == SweepDestinationKind.PostQuantum ? TransactionSizes.PostQuantumOutput : TransactionSizes.LegacyOutput;

    /// <summary>The "only coins smaller than" box, typed in BLZ, as the satoshi ceiling the planner takes.</summary>
    public static long? CeilingFromBlz(decimal? blz) =>
        blz is { } v ? (long)decimal.Round(v * 100_000_000m, 0, MidpointRounding.AwayFromZero) : null;

    /// <summary>The coins a consolidation would touch, and why the others are left.</summary>
    public sealed record Candidates(
        IReadOnlyList<UnspentOutput> Coins,
        int Unconfirmed,
        int AboveCeiling,
        /// <summary>Coins worth no more than the fee their own input adds.</summary>
        int NotWorthMoving);

    /// <summary>
    /// Which coins are in play: confirmed, no larger than <paramref name="maxCoinSatoshis"/> (the
    /// "only coins smaller than" box; null = every coin), and worth more than the fee their own
    /// input costs. Smallest first within a kind, legacy before post-quantum, so the batches are
    /// the same every time the same wallet is planned.
    /// </summary>
    public static Candidates Select(IReadOnlyList<UnspentOutput> outputs, long feeRateSatPerKb = DefaultFeeRateSatPerKb,
        long? maxCoinSatoshis = null, int minConfirmations = 1)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        int unconfirmed = 0, above = 0, notWorth = 0;
        var coins = new List<UnspentOutput>(outputs.Count);
        foreach (var o in outputs)
        {
            if (o.Confirmations < minConfirmations) { unconfirmed++; continue; }
            if (maxCoinSatoshis is { } ceiling && o.Satoshis > ceiling) { above++; continue; }
            if (o.Satoshis <= QuantumSweepPlanner.FeeFor(InputBytes(o), Math.Max(0, feeRateSatPerKb))) { notWorth++; continue; }
            coins.Add(o);
        }
        var ordered = coins
            .OrderBy(o => BitcoinProtocol.IsPostQuantumAddress(o.Address) ? 1 : 0)
            .ThenBy(o => o.Satoshis)
            .ThenBy(o => o.TxId, StringComparer.Ordinal)
            .ThenBy(o => o.Vout)
            .ToList();
        return new Candidates(ordered, unconfirmed, above, notWorth);
    }

    public static SweepPlanResult Plan(
        IReadOnlyList<UnspentOutput> outputs,
        string destination,
        long feeRateSatPerKb = DefaultFeeRateSatPerKb,
        long? maxCoinSatoshis = null,
        int minConfirmations = 1)
    {
        if (string.IsNullOrWhiteSpace(destination)) return SweepPlanResult.Fail("No destination address.");
        if (PayableAddress.Problem(destination, "destination") is { } notPayable) return SweepPlanResult.Fail(notPayable);
        var kind = QuantumSweepPlanner.KindOf(destination);
        if (kind is null) return SweepPlanResult.Fail("The destination cannot be paid.");
        if (feeRateSatPerKb < 0) return SweepPlanResult.Fail("The fee rate cannot be negative.");
        if (maxCoinSatoshis is <= 0) return SweepPlanResult.Fail("The coin size limit must be more than zero.");
        destination = destination.Trim();

        var picked = Select(outputs, feeRateSatPerKb, maxCoinSatoshis, minConfirmations);
        var coins = picked.Coins;
        // Merging must never take protection away: a BQ… coin only ever goes to a BQ… address.
        if (kind == SweepDestinationKind.Legacy && coins.Any(o => BitcoinProtocol.IsPostQuantumAddress(o.Address)))
            return SweepPlanResult.Fail("Some of these coins are on post-quantum (BQ…) addresses. They are only ever merged into a BQ… address, never a legacy one.");

        var warnings = new List<string>();
        if (picked.Unconfirmed > 0) warnings.Add($"{picked.Unconfirmed:N0} unconfirmed coin(s) left alone. Consolidate again once they confirm.");
        if (picked.NotWorthMoving > 0) warnings.Add($"{picked.NotWorthMoving:N0} coin(s) left alone: each is worth no more than the fee it would add.");
        if (coins.Count < 2)
            return SweepPlanResult.Fail(coins.Count == 0
                ? "There are no coins to merge."
                : "There is only one coin to merge, so there is nothing to consolidate.");

        var sources = coins.Select(o => o.Address).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal).Count();
        if (sources > 1) warnings.Add($"Coins from {sources:N0} addresses are combined, which links those addresses publicly.");

        var floor = DustPolicy.FloorFor(destination, DustPolicy.LegacyFloorSatoshis);
        var txs = new List<SweepTx>();
        var leftover = 0;
        foreach (var batch in Batches(coins, OutputBytes(kind.Value)))
        {
            if (batch.Inputs.Count < 2) { leftover += batch.Inputs.Count; continue; }   // one coin merges nothing
            var fee = QuantumSweepPlanner.FeeFor(batch.Bytes, feeRateSatPerKb);
            var total = batch.Inputs.Sum(i => i.Satoshis);
            var output = total - fee;
            if (output < floor)
            {
                warnings.Add($"A batch of {batch.Inputs.Count:N0} coins worth {total:N0} sats would leave {output:N0} sats after the fee, below the {floor:N0} dust floor. Left alone.");
                continue;
            }
            txs.Add(new SweepTx(batch.Inputs, destination, output, fee, batch.Bytes));
        }
        if (leftover > 0) warnings.Add("One coin did not fit the last full transaction and is left alone. It merges next time.");
        if (txs.Count == 0) return SweepPlanResult.Fail("Nothing can be merged: every batch would fall below the dust floor after the fee.");
        if (txs.Count > 1)
            warnings.Add($"{txs.Count:N0} transactions leave {txs.Count:N0} coins. Once they confirm, consolidate again to merge those into fewer.");

        return SweepPlanResult.Ok(new SweepPlan(txs, warnings, kind.Value));
    }

    private readonly record struct Batch(List<UnspentOutput> Inputs, int Bytes);

    /// <summary>Fills each transaction up to the byte budget (and the input-count limit), in order.</summary>
    private static IEnumerable<Batch> Batches(IReadOnlyList<UnspentOutput> coins, int outputBytes)
    {
        var inputs = new List<UnspentOutput>();
        var bytes = BytesOverhead + outputBytes;
        foreach (var coin in coins)
        {
            var size = InputBytes(coin);
            if (inputs.Count > 0 && (bytes + size > BatchByteBudget || inputs.Count >= MaxInputsPerTx))
            {
                yield return new Batch(inputs, bytes);
                inputs = new List<UnspentOutput>();
                bytes = BytesOverhead + outputBytes;
            }
            inputs.Add(coin);
            bytes += size;
        }
        if (inputs.Count > 0) yield return new Batch(inputs, bytes);
    }
}

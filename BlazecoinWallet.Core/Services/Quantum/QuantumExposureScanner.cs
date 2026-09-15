using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>
/// Classifies every funded address in the wallet as EXPOSED (its public key has already
/// appeared in a scriptSig this wallet signed) or UNEXPOSED (only its hash is on-chain).
/// Reads are wallet-only: <c>listunspent</c> for the funded set, <c>listtransactions</c>
/// for the wallet's own sends, <c>gettransaction … true true</c> for each send's inputs.
/// No tx index is needed and nothing is written.
/// </summary>
public sealed class QuantumExposureScanner
{
    private readonly IExposureRpc _rpc;

    public QuantumExposureScanner(IExposureRpc rpc) => _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));

    public async Task<ExposureReport> ScanAsync(
        int maxTransactions = 100_000,
        IProgress<ExposureProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Say so before the long history read, so a big wallet never looks frozen at zero.
        progress?.Report(new ExposureProgress(0, 0, 0, ExposurePhase.ReadingHistory));
        var utxos = await _rpc.ListUnspentAsync(ct);
        var txids = await _rpc.ListSpendingTxIdsAsync(maxTransactions, ct);

        // address → first txid that revealed its key
        var exposed = new Dictionary<string, string>(StringComparer.Ordinal);
        int done = 0;
        progress?.Report(new ExposureProgress(0, txids.Count, 0));
        foreach (var txid in txids)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> sigs;
            try { sigs = await _rpc.GetInputScriptSigHexAsync(txid, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sigs = Array.Empty<string>();   // an unreadable tx cannot prove exposure; it never hides it either
            }
            foreach (var hex in sigs)
            {
                var pk = ScriptSigParser.TryExtractPubKey(hex);
                if (pk is null) continue;
                exposed.TryAdd(ScriptSigParser.PubKeyToAddress(pk), txid);
            }
            done++;
            if ((done & 31) == 0 || done == txids.Count)
                progress?.Report(new ExposureProgress(done, txids.Count, exposed.Count));
        }

        var rows = utxos
            .Where(u => !string.IsNullOrEmpty(u.Address))
            .GroupBy(u => u.Address!, StringComparer.Ordinal)
            .Select(g =>
            {
                var outputs = g.OrderByDescending(u => u.Satoshis).ToList();
                // A BQ… address is post-quantum by construction: nothing a spend reveals helps
                // Shor, so it is never exposed (and never needs sweeping).
                if (Mining.BitcoinProtocol.IsPostQuantumAddress(g.Key))
                    return new AddressExposure(g.Key, outputs.Sum(u => u.Satoshis), outputs, ExposureStatus.PostQuantum, null);
                var isExposed = exposed.TryGetValue(g.Key, out var byTx);
                return new AddressExposure(
                    g.Key,
                    outputs.Sum(u => u.Satoshis),
                    outputs,
                    isExposed ? ExposureStatus.Exposed : ExposureStatus.Unexposed,
                    isExposed ? byTx : null);
            })
            .OrderByDescending(a => a.Status == ExposureStatus.Exposed)
            .ThenByDescending(a => a.Satoshis)
            .ToList();

        return new ExposureReport(
            rows,
            rows.Sum(a => a.Satoshis),
            rows.Where(a => a.Status == ExposureStatus.Exposed).Sum(a => a.Satoshis),
            exposed.Count,
            txids.Count,
            DateTime.UtcNow);
    }
}

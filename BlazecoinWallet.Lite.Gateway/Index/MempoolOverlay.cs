namespace BlazecoinWallet.Lite.Gateway.Index;

public sealed record MempoolUtxo(string TxId, int Vout, string Address, long Amount, string? ScriptPubKey = null);

/// <summary>
/// The unconfirmed layer on top of the SQLite index: outputs created by mempool
/// transactions (so an incoming payment shows at 0 confirmations — table stakes on a 30 s
/// chain) and confirmed outpoints those transactions spend (so a coin being spent right
/// now stops being offered). Rebuilt as an immutable snapshot each walker tick and swapped
/// atomically — readers never see a half-updated view.
/// </summary>
public sealed class MempoolOverlay
{
    private sealed record Snapshot(
        ILookup<string, MempoolUtxo> OutputsByAddress,
        HashSet<string> SpentOutpoints,
        int TxCount);

    private static readonly Snapshot Empty = new(
        Array.Empty<MempoolUtxo>().ToLookup(u => u.Address),
        [], 0);

    private volatile Snapshot _current = Empty;

    public int TxCount => _current.TxCount;

    public void Replace(IEnumerable<MempoolUtxo> outputs, IEnumerable<(string TxId, int Vout)> spends, int txCount)
    {
        var spent = spends.Select(s => Key(s.TxId, s.Vout)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _current = new Snapshot(outputs.ToLookup(u => u.Address, StringComparer.Ordinal), spent, txCount);
    }

    public void Clear() => _current = Empty;

    /// <summary>Unconfirmed outputs paying this address (skipping any already spent by
    /// ANOTHER mempool tx — chained unconfirmed spends).</summary>
    public IReadOnlyList<MempoolUtxo> GetForAddress(string address)
    {
        var snap = _current;
        return snap.OutputsByAddress[address]
            .Where(u => !snap.SpentOutpoints.Contains(Key(u.TxId, u.Vout)))
            .ToList();
    }

    /// <summary>True when a CONFIRMED outpoint is being spent by a mempool transaction.</summary>
    public bool IsSpent(string txId, int vout) => _current.SpentOutpoints.Contains(Key(txId, vout));

    private static string Key(string txId, int vout) => $"{txId}:{vout}";
}

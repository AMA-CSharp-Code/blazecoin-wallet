using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>An output of a decoded transaction (address may be null for non-standard scripts).</summary>
public sealed record LiteTxOutputView(int Index, string? Address, long Amount);

/// <summary>An input references the output it spends (prev txid : index).</summary>
public sealed record LiteTxInputView(string PrevTxId, uint PrevIndex);

/// <summary>A decoded transaction for the detail view — outputs with addresses/amounts and
/// the outpoints its inputs spend (input amounts aren't in the tx itself).</summary>
public sealed record LiteTxDetails(
    string TxId,
    IReadOnlyList<LiteTxInputView> Inputs,
    IReadOnlyList<LiteTxOutputView> Outputs,
    long TotalOut,
    bool IsCoinbase);

/// <summary>Decodes raw transaction hex (from the gateway's <c>/raw</c> passthrough) into a
/// view model — keeps NBitcoin out of the UI layer.</summary>
public static class LiteTxDecoder
{
    public static LiteTxDetails? Decode(string? hex, Network? network = null)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        network ??= BlazecoinNetwork.Instance;
        Transaction tx;
        try { tx = Transaction.Parse(hex, network); }
        catch { return null; }

        var inputs = tx.Inputs.Select(i => new LiteTxInputView(
            i.PrevOut.Hash.ToString(), i.PrevOut.N)).ToList();

        var outputs = tx.Outputs.Select((o, idx) =>
        {
            string? address = null;
            try { address = o.ScriptPubKey.GetDestinationAddress(network)?.ToString(); } catch { /* non-standard */ }
            // A post-quantum P2PQH output has no NBitcoin destination — render its BQ address.
            address ??= Pq.PqAddress.FromScriptPubKey(o.ScriptPubKey.ToBytes(), network);
            return new LiteTxOutputView(idx, address, o.Value.Satoshi);
        }).ToList();

        return new LiteTxDetails(
            tx.GetHash().ToString(), inputs, outputs,
            outputs.Sum(o => o.Amount),
            tx.IsCoinBase);
    }
}

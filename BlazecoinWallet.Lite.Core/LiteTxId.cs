using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>
/// A transaction's id, computed by the wallet from the bytes it holds — the double-SHA256 of
/// the serialized transaction. The wallet NEVER takes a txid from whoever it handed the
/// transaction to (bug-bounty report 2026-11-06: a relay's answer replaced the wallet's own
/// txid on the receipt and in the session's sent-set).
/// </summary>
public static class LiteTxId
{
    /// <summary>The id the network will know <paramref name="rawTxHex"/> by; null when the hex
    /// is not a transaction.</summary>
    public static string? OfHex(string? rawTxHex)
    {
        if (string.IsNullOrWhiteSpace(rawTxHex)) return null;
        try { return Transaction.Parse(rawTxHex.Trim(), BlazecoinNetwork.Instance).GetHash().ToString(); }
        catch { return null; }
    }
}

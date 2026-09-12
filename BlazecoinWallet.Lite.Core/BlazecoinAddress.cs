using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>Address utilities — pure functions the UI can call without coupling to the
/// stateful wallet facade (audit round-3 F2).</summary>
public static class BlazecoinAddress
{
    /// <summary>True when <paramref name="address"/> is a valid Blazecoin legacy P2PKH
    /// address — for the address book, watch list, and send validation.</summary>
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        try { _ = new BitcoinPubKeyAddress(address.Trim(), BlazecoinNetwork.Instance); return true; }
        catch (FormatException) { return false; }
    }
}

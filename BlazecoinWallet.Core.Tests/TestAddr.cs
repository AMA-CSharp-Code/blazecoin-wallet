using BlazecoinWallet.Core.Services.Mining;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Checksum-valid mainnet addresses for tests. The planners and auto-payout pay only a legacy B…
/// or a post-quantum BQ… address (PayableAddress, audit 2026-11-09), so the placeholders these
/// tests used to pass ("Brecipient", "Bchange", "Bvalid" …) are no longer addresses at all.
/// </summary>
internal static class TestAddr
{
    /// <summary>A legacy B… address for a made-up key hash.</summary>
    public static string Legacy(byte seed)
    {
        var payload = new byte[21];
        payload[0] = BitcoinProtocol.MainnetPubKeyVersion;
        for (var i = 0; i < 20; i++) payload[1 + i] = (byte)(seed + i);
        return BitcoinProtocol.Base58CheckEncode(payload);
    }

    public static readonly string Recipient = Legacy(1);
    public static readonly string Change = Legacy(2);
    public static readonly string Lead = Legacy(3);
    public static readonly string Collector = Legacy(4);
    public static readonly string Friend = Legacy(5);
    public static readonly string Same = Legacy(6);
    public static readonly string Dest = Legacy(7);
    public static readonly string Payout = Legacy(8);

    /// <summary>PQ_SIGNATURES.md §5 example.</summary>
    public const string PostQuantum = "BQGeaQmsowAjL1ZG8q1AfVH4NgBvtJKJSsjnnKEf62BMjekdU82n";

    /// <summary>Things that look like an address and that this chain's wallet must not pay.</summary>
    public static readonly string[] NotPayable =
    [
        Legacy(1)[..^1] + (Legacy(1)[^1] == 'x' ? "y" : "x"),   // one character off: the checksum fails
        "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa",            // a Bitcoin address
        "3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy",            // a script-hash address (which may hide a witness program)
        "Brecipient",                                    // not an address at all
        "blz1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4",   // SegWit: accepted on chain, spendable by nobody
    ];
}

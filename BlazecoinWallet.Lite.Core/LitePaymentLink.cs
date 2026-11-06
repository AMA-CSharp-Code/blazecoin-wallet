using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Lite;

/// <summary>What a scanned or pasted payment request asks for.</summary>
/// <param name="Destination">The address (the whole text when it wasn't a <c>blazecoin:</c> link).</param>
/// <param name="AmountSatoshis">The requested amount, when the link carried a valid one.</param>
/// <param name="AmountRejected">The link carried an <c>amount=</c> that is not a valid amount —
/// the page must say so and leave the field for the user, never guess (a mis-pay risk).</param>
public sealed record LitePaymentRequest(string Destination, long? AmountSatoshis, bool AmountRejected);

/// <summary>
/// Reads a bare address or a <c>blazecoin:</c> (BIP21) link. Lived inline in the Send page until
/// 2026-11-06, where it could not be tested and had drifted into the same lenient number parse
/// as the amount box ("amount=1,5" pre-filled 15 BLZ). BIP21 amounts are ALWAYS in whole coins.
/// </summary>
public static class LitePaymentLink
{
    private const string Scheme = "blazecoin:";

    public static LitePaymentRequest Parse(string scanned)
    {
        var text = (scanned ?? string.Empty).Trim();
        if (!text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return new LitePaymentRequest(text, null, false);

        var body = text[Scheme.Length..];
        var q = body.IndexOf('?');
        var destination = q < 0 ? body : body[..q];
        if (q < 0) return new LitePaymentRequest(destination, null, false);

        long? sats = null;
        var rejected = false;
        foreach (var part in body[(q + 1)..].Split('&'))
        {
            if (!part.StartsWith("amount=", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryCoinsToSats(part["amount=".Length..], out var parsed)) sats = parsed;
            else rejected = true;
        }
        // One bad amount= poisons the request: a link with two amounts, one of them malformed,
        // is not something to pick a winner from.
        return rejected
            ? new LitePaymentRequest(destination, null, true)
            : new LitePaymentRequest(destination, sats, false);
    }

    private static bool TryCoinsToSats(string text, out long satoshis)
    {
        satoshis = 0;
        if (!AmountText.TryParsePaymentLink(text, out var coins) || coins <= 0) return false;
        if (coins > BlazecoinChain.MaxMoney / (decimal)BlazecoinChain.Coin) return false;
        var sats = coins * BlazecoinChain.Coin;
        if (sats != Math.Floor(sats)) return false;   // finer than a satoshi — not a payable amount
        satoshis = (long)sats;
        return satoshis > 0;
    }
}

namespace BlazecoinWallet.Core.Services;

/// <summary>What a scanned or pasted payment request asks for.</summary>
/// <param name="Destination">The address (the whole text when it was not a payment link).</param>
/// <param name="AmountSatoshis">The requested amount, when the link carried a valid one.</param>
/// <param name="AmountRejected">The link carried an <c>amount=</c> that is not a valid amount — the
/// page must say so and leave the field for the user, never guess and never drop it in silence.</param>
/// <param name="Label">The link's <c>label=</c> (or <c>message=</c>), when it carried one.</param>
public sealed record PaymentRequest(string Destination, long? AmountSatoshis, bool AmountRejected, string? Label);

/// <summary>Where the two wallets' readers differ: which link schemes are theirs, and whether the
/// link's keys and values are percent-decoded.</summary>
/// <param name="Schemes">Link schemes read as a payment link, without the colon ("blazecoin").</param>
/// <param name="PercentDecode">Decode %xx in keys and values before reading them.</param>
/// <param name="MaxSatoshis">The largest amount a link may ask for — the money supply.</param>
public sealed record PaymentLinkOptions(IReadOnlyList<string> Schemes, bool PercentDecode, long MaxSatoshis)
{
    /// <summary>The desktop Send page: its three schemes, percent-decoded, bounded by the supply.</summary>
    public static readonly PaymentLinkOptions Desktop =
        new(new[] { "blazecoin", "blz", "bitcoin" }, PercentDecode: true, PaymentLink.MaxMoneySatoshis);
}

/// <summary>
/// Reads a bare address or a BIP21 payment link (<c>blazecoin:&lt;address&gt;?amount=…&amp;label=…</c>) —
/// the ONE reader for both wallets (audit 2026-11-09). The lite wallet's was extracted and tested on
/// 2026-11-06; the desktop Send page kept its own inline copy, which silently DROPPED an amount it
/// could not read (the user then typed or kept a different one, unaware the link had named one) and
/// had no upper bound. BIP21 amounts are ALWAYS in whole coins, digits with at most one dot.
/// </summary>
public static class PaymentLink
{
    public const long SatoshisPerCoin = 100_000_000;

    /// <summary>The money supply cap, 2,065,000,000 BLZ, in satoshis. (The lite wallet's
    /// <c>BlazecoinChain.MaxMoney</c> is the same figure and is what its reader passes in.)</summary>
    public const long MaxMoneySatoshis = 2_065_000_000L * SatoshisPerCoin;

    /// <summary>What a page says when <see cref="PaymentRequest.AmountRejected"/>.</summary>
    public const string AmountRejectedMessage =
        "That payment request's amount isn't valid, so it was left out — enter the amount by hand.";

    public static PaymentRequest Parse(string? scanned, PaymentLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = (scanned ?? string.Empty).Trim();

        string? body = null;
        foreach (var scheme in options.Schemes)
        {
            if (text.Length > scheme.Length && text[scheme.Length] == ':' &&
                text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                body = text[(scheme.Length + 1)..];
                break;
            }
        }
        if (body is null) return new PaymentRequest(text, null, false, null);   // not a link: the text is the address

        var q = body.IndexOf('?');
        var destination = (q < 0 ? body : body[..q]).Trim();
        if (q < 0) return new PaymentRequest(destination, null, false, null);

        long? sats = null;
        var rejected = false;
        string? label = null;
        foreach (var part in body[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (options.PercentDecode) { key = Uri.UnescapeDataString(key); value = Uri.UnescapeDataString(value); }

            if (key.Equals("amount", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCoinsToSatoshis(value, options.MaxSatoshis, out var parsed)) sats = parsed;
                else rejected = true;
            }
            else if (label is null && (key.Equals("label", StringComparison.OrdinalIgnoreCase)
                                       || key.Equals("message", StringComparison.OrdinalIgnoreCase)))
                label = value;
        }
        // One bad amount= poisons the request: a link with two amounts, one of them malformed,
        // is not something to pick a winner from.
        return rejected
            ? new PaymentRequest(destination, null, true, label)
            : new PaymentRequest(destination, sats, false, label);
    }

    private static bool TryCoinsToSatoshis(string text, long maxSatoshis, out long satoshis)
    {
        satoshis = 0;
        if (!AmountText.TryParsePaymentLink(text, out var coins) || coins <= 0) return false;
        if (coins > maxSatoshis / (decimal)SatoshisPerCoin) return false;
        var sats = coins * SatoshisPerCoin;
        if (sats != Math.Floor(sats)) return false;   // finer than a satoshi — not a payable amount
        satoshis = (long)sats;
        return satoshis > 0;
    }
}

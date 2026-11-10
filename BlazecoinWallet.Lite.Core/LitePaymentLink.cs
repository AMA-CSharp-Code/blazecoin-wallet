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
/// <para>The reading itself is <see cref="PaymentLink"/>'s since the audit of 2026-11-09 — one
/// reader for both wallets (the desktop page had kept its own, which dropped a bad amount in
/// silence). What is the lite wallet's own is said here: its one scheme, no percent-decoding,
/// and the money supply as the bound.</para>
/// </summary>
public static class LitePaymentLink
{
    private static readonly PaymentLinkOptions Options =
        new(new[] { "blazecoin" }, PercentDecode: false, MaxSatoshis: BlazecoinChain.MaxMoney);

    public static LitePaymentRequest Parse(string scanned)
    {
        var r = PaymentLink.Parse(scanned, Options);
        return new LitePaymentRequest(r.Destination, r.AmountSatoshis, r.AmountRejected);
    }
}

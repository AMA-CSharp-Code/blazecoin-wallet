using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Services.Send;

/// <summary>One manual send: who, how much, whether the fee comes out of the amount, and the change kind.</summary>
public sealed record SendRequest(string Address, decimal Amount, bool SubtractFeeFromAmount, BlazecoinAddressKind? Change)
{
    /// <summary>The daemon's <c>change_type</c> option, or null to leave the daemon's own default (-changetype).</summary>
    public string? ChangeType => Change switch
    {
        BlazecoinAddressKind.PostQuantum => "pq",
        BlazecoinAddressKind.Legacy => "legacy",
        _ => null,
    };
}

/// <summary>
/// The desktop Send page's send + fee-estimate calls, with the change type chosen by the caller.
/// <c>sendtoaddress</c> cannot choose one, so with a change type this goes through <c>send</c>.
/// </summary>
public interface ISendRpc
{
    /// <summary>Broadcasts; returns the txid, or null when the daemon built but could not complete the tx.</summary>
    Task<string?> SendAsync(SendRequest request, CancellationToken ct = default);
    /// <summary>Non-broadcasting fee estimate for the same request (fundrawtransaction); null when unknown.</summary>
    Task<decimal?> EstimateFeeAsync(SendRequest request, CancellationToken ct = default);
}

/// <summary>The change kind for a send, and the sentence the review step shows.</summary>
public sealed record ChangeDecision(BlazecoinAddressKind? Change, string Explanation)
{
    public bool IsPostQuantum => Change == BlazecoinAddressKind.PostQuantum;
}

/// <summary>
/// Change goes to a post-quantum address whenever the wallet can mint one. Written after a send
/// from an operational wallet on 2026-09-28 spent a BQ… coin and put its change on a
/// LEGACY address, because that node runs with <c>changetype=legacy</c>. A wallet without a
/// PQ key chain keeps the daemon's default — asking for "pq" there would fail the send.
/// </summary>
public sealed class SendChangePolicy
{
    private readonly IAddressMintRpc _mint;

    public SendChangePolicy(IAddressMintRpc mint) => _mint = mint ?? throw new ArgumentNullException(nameof(mint));

    public async Task<ChangeDecision> DecideAsync(CancellationToken ct = default)
    {
        bool? can;
        try { can = await _mint.CanMintPostQuantumAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { can = null; }
        return can switch
        {
            true => new ChangeDecision(BlazecoinAddressKind.PostQuantum, "Any change returns to a new post-quantum (BQ…) address in this wallet."),
            false => new ChangeDecision(null, "Any change returns to a legacy address: this wallet was created before post-quantum keys existed."),
            _ => new ChangeDecision(null, "Couldn't check this wallet for post-quantum keys, so any change uses the node's default (legacy)."),
        };
    }
}

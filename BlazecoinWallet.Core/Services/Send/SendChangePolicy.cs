using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Services.Send;

/// <summary>One manual send: who, how much, whether the fee comes out of the amount, and the change kind.</summary>
public sealed record SendRequest(string Address, decimal Amount, bool SubtractFeeFromAmount, BlazecoinAddressKind? Change)
{
    /// <summary>The wallet this send was REVIEWED against ("" = the node's default wallet
    /// endpoint, when no wallet had been chosen). When set, the send runs against exactly that
    /// wallet or fails — it can never come out of another one because the active wallet changed
    /// between review and confirm (bug-bounty report 2026-11-06). Null = the active wallet.</summary>
    public string? Wallet { get; init; }

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
    /// <summary>Non-broadcasting fee estimate for the same request (fundrawtransaction); null when unknown.
    /// Like the send, it runs against <see cref="SendRequest.Wallet"/> when that is set. A request
    /// that follows the active wallet throws <see cref="WalletSwitchPendingException"/> while a
    /// wallet switch is unacknowledged — that is a refusal to show, not an unknown fee (audit 2026-11-09).</summary>
    Task<decimal?> EstimateFeeAsync(SendRequest request, CancellationToken ct = default);
}

/// <summary>The change kind for a send, and the sentence the review step shows.</summary>
public sealed record ChangeDecision(BlazecoinAddressKind? Change, string Explanation)
{
    public bool IsPostQuantum => Change == BlazecoinAddressKind.PostQuantum;
}

/// <summary>
/// The one answer to "post-quantum or legacy?" from the one question "can this wallet mint a BQ…
/// address?" (yes / no / could not check). Written in three places until the audit of 2026-11-09 —
/// the Send page's change policy, the RPC class's vintage change address, and the Receive page's
/// default — each with its own reading of "could not check".
/// <para>The two uses answer "could not check" differently ON PURPOSE, and this is where that is
/// written down:</para>
/// <list type="bullet">
/// <item><b>Change</b> is taken silently, inside a payment that is already going out. Asking for a
///   post-quantum change address in a wallet that turns out to have no post-quantum keys FAILS the
///   payment, so change is post-quantum only when the wallet is KNOWN to be able; "could not
///   check" is treated like "cannot" — legacy (for a <c>send</c>, the node's own default).</item>
/// <item><b>Receive</b> only pre-selects a choice the user sees and can change, and the mint that
///   follows explains itself when the wallet cannot. So it is post-quantum unless the wallet is
///   KNOWN to be unable; "could not check" is treated like "can".</item>
/// </list>
/// </summary>
public static class PostQuantumPreference
{
    /// <summary>The kind of change address: post-quantum, or null — not post-quantum (legacy / the node's default).</summary>
    public static BlazecoinAddressKind? ForChange(bool? canMintPostQuantum) =>
        canMintPostQuantum == true ? BlazecoinAddressKind.PostQuantum : null;

    /// <summary>The kind the Receive page offers first.</summary>
    public static BlazecoinAddressKind ForReceive(bool? canMintPostQuantum) =>
        canMintPostQuantum == false ? BlazecoinAddressKind.Legacy : BlazecoinAddressKind.PostQuantum;
}

/// <summary>
/// Change goes to a post-quantum address whenever the wallet can mint one. Written after a send
/// from an operational wallet on 2026-09-28 spent a BQ… coin and put its change on a
/// LEGACY address, because that node runs with <c>changetype=legacy</c>. A wallet without a
/// PQ key chain keeps the daemon's default — asking for "pq" there would fail the send.
/// The rule itself is <see cref="PostQuantumPreference.ForChange"/>.
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
        return new ChangeDecision(PostQuantumPreference.ForChange(can), can switch
        {
            true => "Any change returns to a new post-quantum (BQ…) address in this wallet.",
            false => "Any change returns to a legacy address: this wallet was created before post-quantum keys existed.",
            _ => "Couldn't check this wallet for post-quantum keys, so any change uses the node's default (legacy).",
        });
    }
}

/// <summary>
/// A fresh change ADDRESS for a flow that builds its own transaction (the Provenance vintage
/// send), minted in exactly the wallet the flow is bound to and of the kind the one change rule
/// gives (<see cref="PostQuantumPreference.ForChange"/>). This lived in the RPC class, which
/// decided the kind itself and asked the ACTIVE wallet (audit 2026-11-09).
/// <para>A wallet that can make post-quantum addresses and then fails to gets NO address (the
/// page says so) rather than a quiet legacy one: spending part of a BQ… coin must not put the rest
/// back behind an ECDSA key (bug-bounty report 2026-11-06, fourth round).</para>
/// </summary>
public sealed class ChangeAddressSource
{
    private readonly IBoundWalletRpc _wallets;

    public ChangeAddressSource(IBoundWalletRpc wallets) => _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));

    public async Task<string?> NewAsync(string wallet, string label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        bool? can;
        try { can = await _wallets.HasPostQuantumKeysAsync(wallet, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { can = null; }
        if (PostQuantumPreference.ForChange(can) == BlazecoinAddressKind.PostQuantum)
        {
            var pq = await _wallets.MintPostQuantumAddressAsync(wallet, label, ct);
            return pq.IsOk ? pq.Address : null;
        }
        return await _wallets.MintLegacyAddressAsync(wallet, label, ct);
    }
}

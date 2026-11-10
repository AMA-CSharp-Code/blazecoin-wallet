namespace BlazecoinWallet.Core.Services.Provenance;

/// <summary>A change address for a vintage plan, or why there is none.</summary>
public sealed record VintageChangeAddress(string? Address, string? Problem);

/// <summary>How a vintage broadcast ended: the txid, or the sentence the page shows.</summary>
public sealed record VintageBroadcastResult(string? TxId, string? Problem)
{
    public bool Sent => TxId is not null;
}

/// <summary>
/// The wallet side of a Provenance vintage send, bound to ONE wallet: the wallet on screen when the
/// plan is built. Its change addresses are minted there, it is signed there, and it is not signed
/// or sent at all once that wallet is no longer the active one.
/// <para>Why (audit 2026-11-09): this sequence lived in Provenance.razor and every step followed
/// whichever wallet was active at that moment. A plan made from one wallet's coins could take its
/// change address from another wallet and be signed by it — and where the two share keys (a
/// migrated or restored copy) the change, which can be most of a large coin, landed in the wallet
/// the app had moved to. It also took its wallet with no look at the switch notice, like the Send
/// page did. One instance per page; it holds the binding of the plan in hand.</para>
/// </summary>
public sealed class VintageSender
{
    public const string ChangeLabel = "vintage change";

    private readonly IWalletBinding _binding;
    private readonly IVintageSendRpc _rpc;
    private readonly Send.ChangeAddressSource _change;

    public VintageSender(IWalletBinding binding, IVintageSendRpc rpc, Send.ChangeAddressSource change)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _change = change ?? throw new ArgumentNullException(nameof(change));
    }

    /// <summary>The wallet the plan in hand is bound to ("" = the node's default wallet endpoint);
    /// null when no plan has been started.</summary>
    public string? Wallet { get; private set; }

    /// <summary>
    /// Binds the plan about to be built to the wallet on screen. Null when it is bound; otherwise
    /// the reason to show — the app has moved to another wallet and the user has not acknowledged
    /// it, so nothing may be bound to it yet.
    /// </summary>
    public string? BeginPlan()
    {
        try { Wallet = _binding.BindActiveForWrite(); return null; }
        catch (WalletSwitchPendingException ex) { Wallet = null; return ex.Message; }
    }

    /// <summary>A fresh change address in the plan's wallet, of the kind the change rule gives.</summary>
    public async Task<VintageChangeAddress> NewChangeAddressAsync(CancellationToken ct = default)
    {
        if (Wallet is not string wallet) return new(null, "Plan the send first.");
        try
        {
            var address = await _change.NewAsync(wallet, ChangeLabel, ct);
            return string.IsNullOrWhiteSpace(address)
                ? new(null, "Couldn't get a change address from the wallet.")
                : new(address, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, $"Couldn't get a change address from the wallet: {ex.Message}");
        }
    }

    /// <summary>Builds, signs and broadcasts the planned transaction — inputs and outputs in the
    /// plan's exact order, signed by the plan's wallet.</summary>
    public async Task<VintageBroadcastResult> BroadcastAsync(VintageSendPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Wallet is not string wallet) return new(null, "Plan the send first.");
        // Planned on one wallet, and the app is on another now: nothing is signed, nothing is sent.
        if (!_binding.IsStillActive(wallet, out var activeNow))
            return new(null, WalletBinding.MovedMessage(wallet, activeNow));
        try
        {
            var raw = await _rpc.CreateRawTransactionAsync(
                plan.Inputs.Select(i => (i.TxId, i.Vout)).ToList(),
                plan.Outputs.Select(o => (o.Address, o.Satoshis)).ToList(), ct);
            if (raw is null) return new(null, "The daemon couldn't build the transaction.");

            var signed = await _rpc.SignRawTransactionAsync(wallet, raw, ct);
            if (signed is null) return new(null, "Signing failed or was incomplete (is the wallet unlocked?).");

            var txid = await _rpc.SendRawTransactionAsync(signed, ct);
            if (txid is null) return new(null, "The network rejected the transaction.");
            return new(txid, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, $"Send failed: {ex.Message}");
        }
    }
}

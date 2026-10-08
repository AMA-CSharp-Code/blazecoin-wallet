using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Services.Receive;

/// <summary>The outcome of asking for a new receive address.</summary>
public sealed record ReceiveAddressResult(
    string? Address,
    BlazecoinAddressKind Kind,
    string? Problem,
    /// <summary>True when the wallet (or its node) can never mint BQ… — the page then offers legacy
    /// and points at "Create a post-quantum wallet" instead of suggesting a retry.</summary>
    bool WalletCannotMintPostQuantum)
{
    public bool Succeeded => Address is not null;
}

/// <summary>The Receive page's rules for which address to hand out.</summary>
public interface IReceiveAddressService
{
    /// <summary>Post-quantum when the wallet can mint BQ… (or when that cannot be determined — the mint
    /// then explains); legacy only for a wallet that has no post-quantum key chain.</summary>
    Task<BlazecoinAddressKind> DefaultKindAsync(CancellationToken ct = default);

    /// <summary>A new address of exactly the kind asked for. Never substitutes the other kind.</summary>
    Task<ReceiveAddressResult> NewAsync(string label, BlazecoinAddressKind kind, CancellationToken ct = default);
}

/// <summary>
/// Receive addresses, post-quantum by default since a 2026-09-28 move landed on legacy
/// addresses because the desktop wallet could only mint legacy. A BQ… address needs a 2.0.5+ sender,
/// so legacy stays available — as a deliberate choice, never as a silent fallback.
/// </summary>
public sealed class ReceiveAddressService : IReceiveAddressService
{
    private readonly IAddressMintRpc _mint;

    public ReceiveAddressService(IAddressMintRpc mint) => _mint = mint ?? throw new ArgumentNullException(nameof(mint));

    /// <summary>Who can pay each kind — shown beside the choice and under every new address.</summary>
    public static string SenderNote(BlazecoinAddressKind kind) => kind == BlazecoinAddressKind.PostQuantum
        ? "The sender needs a wallet that knows post-quantum addresses: the desktop wallet 2.0.5 or later, the web wallet, or the Android lite wallet 1.2.0 or later. V1.5 and older wallets cannot pay a BQ… address."
        : "Any Blazecoin wallet can pay a legacy address. Its key becomes public the first time you spend from it — move those coins to a BQ… address later on the Quantum page.";

    public async Task<BlazecoinAddressKind> DefaultKindAsync(CancellationToken ct = default)
    {
        bool? can;
        try { can = await _mint.CanMintPostQuantumAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { can = null; }
        return can == false ? BlazecoinAddressKind.Legacy : BlazecoinAddressKind.PostQuantum;
    }

    public async Task<ReceiveAddressResult> NewAsync(string label, BlazecoinAddressKind kind, CancellationToken ct = default)
    {
        label = label?.Trim() ?? "";
        if (kind == BlazecoinAddressKind.PostQuantum)
        {
            PqAddressResult r;
            try { r = await _mint.GetNewPostQuantumAddressAsync(label, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { r = PqAddressResult.Fail(PqAddressOutcome.Failed, ex.Message); }
            if (r.IsOk && BitcoinProtocol.KindOf(r.Address) == BlazecoinAddressKind.PostQuantum)
                return new ReceiveAddressResult(r.Address, kind, null, false);
            var cannot = r.Outcome is PqAddressOutcome.WalletHasNoPqKeys or PqAddressOutcome.LegacyWallet or PqAddressOutcome.DaemonTooOld;
            return new ReceiveAddressResult(null, kind, r.IsOk ? "The node returned an address that is not post-quantum." : r.Explain(), cannot);
        }

        string? legacy;
        try { legacy = await _mint.GetNewLegacyAddressAsync(label, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new ReceiveAddressResult(null, kind, $"Failed to generate address: {ex.Message}", false); }
        return BitcoinProtocol.KindOf(legacy) == BlazecoinAddressKind.Legacy
            ? new ReceiveAddressResult(legacy!.Trim(), kind, null, false)
            : new ReceiveAddressResult(null, kind, $"The node returned '{legacy}', which is not a legacy B… address.", false);
    }
}

using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Services;

/// <summary>
/// The address calls of a flow that is BOUND to one wallet, against exactly that wallet
/// ("" = the node's default wallet endpoint, when none had been chosen) — never the active one.
/// <para>Why (audit 2026-11-09): the Quantum sweep and the Provenance vintage send minted their
/// destination and change, and asked "is this address mine", through whichever wallet was active
/// at that moment. A plan made on "Savings" could get its change address from "Primary". A flow
/// takes its wallet once, through <see cref="IWalletBinding.BindActiveForWrite"/>, and names it
/// on every call here; a wallet that is no longer loaded is an error
/// (<see cref="WalletNotLoadedException"/>), never a reason to ask another.</para>
/// </summary>
public interface IBoundWalletRpc
{
    /// <summary><c>getnewaddress &lt;label&gt; "pq"</c> in <paramref name="wallet"/>. The "this wallet /
    /// daemon cannot do post-quantum" cases come back as a typed outcome, never as an exception.</summary>
    Task<PqAddressResult> MintPostQuantumAddressAsync(string wallet, string label, CancellationToken ct = default);

    /// <summary><c>getnewaddress &lt;label&gt; "legacy"</c> in <paramref name="wallet"/>.</summary>
    Task<string?> MintLegacyAddressAsync(string wallet, string label, CancellationToken ct = default);

    /// <summary>Whether <paramref name="wallet"/> holds an ACTIVE post-quantum key chain
    /// (<c>listdescriptors</c>; nothing is minted). Null when the daemon could not say.</summary>
    Task<bool?> HasPostQuantumKeysAsync(string wallet, CancellationToken ct = default);

    /// <summary><c>getaddressinfo</c> → <c>ismine</c> in <paramref name="wallet"/>. Null when the daemon could not say.</summary>
    Task<bool?> OwnsAddressAsync(string wallet, string address, CancellationToken ct = default);
}

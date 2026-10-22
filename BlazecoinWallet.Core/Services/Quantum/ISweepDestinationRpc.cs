namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>
/// Minting addresses in the active wallet, by kind. Shared by the Receive page, the solo miner's
/// payout address and the Quantum page's sweep destination, so "post-quantum unless the wallet
/// cannot" is decided the same way everywhere.
/// </summary>
public interface IAddressMintRpc
{
    /// <summary><c>getnewaddress &lt;label&gt; "pq"</c>. Never throws for the "this wallet / daemon cannot
    /// do post-quantum" cases — those come back as a typed outcome the page explains.</summary>
    Task<PqAddressResult> GetNewPostQuantumAddressAsync(string label, CancellationToken ct = default);

    /// <summary><c>getnewaddress &lt;label&gt; "legacy"</c>.</summary>
    Task<string?> GetNewLegacyAddressAsync(string label, CancellationToken ct = default);

    /// <summary>Whether the active wallet holds an ACTIVE post-quantum key chain, read from
    /// <c>listdescriptors</c> (nothing is minted). Null when the daemon could not say.</summary>
    Task<bool?> CanMintPostQuantumAsync(CancellationToken ct = default);
}

/// <summary>
/// The wallet calls a quantum sweep needs to pick and check its DESTINATION: the minting calls, plus
/// whether a typed address belongs to the active wallet. Its own slice so
/// <see cref="QuantumSweepSession"/> is tested against a fake, like <see cref="IExposureRpc"/>.
/// </summary>
public interface ISweepDestinationRpc : IAddressMintRpc
{
    /// <summary><c>getaddressinfo</c> → <c>ismine</c>. Null when the daemon could not say.</summary>
    Task<bool?> IsMineAsync(string address, CancellationToken ct = default);
}

/// <summary>Why a BQ address could, or could not, be minted.</summary>
public enum PqAddressOutcome
{
    /// <summary>A well-formed mainnet BQ… address came back.</summary>
    Ok,
    /// <summary>The wallet was created before the daemon supported P2PQH (2.1.0), so it has no
    /// post-quantum key chain. The fix is a NEW wallet, which gets one at birth.</summary>
    WalletHasNoPqKeys,
    /// <summary>A legacy (BDB, non-descriptor) wallet — it can never hold a PQ key chain.</summary>
    LegacyWallet,
    /// <summary>The daemon does not know the "pq" address type: it predates the fork build.</summary>
    DaemonTooOld,
    /// <summary>Anything else (a reply that is not a BQ address, an RPC failure...). <see cref="PqAddressResult.Message"/> says what.</summary>
    Failed,
}

public sealed record PqAddressResult(PqAddressOutcome Outcome, string? Address, string? Message)
{
    public bool IsOk => Outcome == PqAddressOutcome.Ok && Address is not null;
    public static PqAddressResult Ok(string address) => new(PqAddressOutcome.Ok, address, null);
    public static PqAddressResult Fail(PqAddressOutcome outcome, string message) => new(outcome, null, message);

    /// <summary>What the page tells the user for each outcome — one place, so the wording is tested.</summary>
    public string Explain() => Outcome switch
    {
        PqAddressOutcome.Ok => "",
        PqAddressOutcome.WalletHasNoPqKeys =>
            "This wallet was created before post-quantum keys existed (daemon 2.1.0), so it cannot make a BQ… address. " +
            "Create a new wallet — it gets post-quantum keys at birth — back it up, and send the coins there.",
        PqAddressOutcome.LegacyWallet =>
            "This is a legacy (non-descriptor) wallet, which cannot hold post-quantum keys. Create a new wallet and send the coins there.",
        PqAddressOutcome.DaemonTooOld =>
            "The node this wallet talks to predates post-quantum addresses. Update the node to 2.1.0 or later.",
        _ => $"Couldn't get a post-quantum address: {Message}",
    };
}

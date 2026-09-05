namespace BlazecoinWallet.Core.Services;

/// <summary>Tracks which loaded wallet the UI is currently acting on.
///
/// Bitcoin Core requires every wallet RPC to be addressed through a
/// <c>/wallet/&lt;name&gt;</c> URI-path whenever more than one wallet is loaded
/// (it returns RPC error -19 otherwise, since it can't guess which wallet you
/// mean). This holds the chosen wallet so <see cref="BlazecoindRpcService"/>
/// can build the right URL, persists the choice across launches, and raises
/// <see cref="Changed"/> when it flips so the UI can react.
///
/// The implementation (which persists via MAUI Preferences) lives in the app;
/// this interface is in Core so the platform-independent services can depend on
/// it without a MAUI reference.</summary>
public interface IWalletContext
{
    /// <summary>The active wallet name, or null when none has been chosen yet
    /// (in which case wallet RPCs fall back to the base URL — correct when a
    /// single wallet is loaded).</summary>
    string? Active { get; }

    event Action? Changed;

    void SetActive(string? name);
}

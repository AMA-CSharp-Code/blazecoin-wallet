namespace BlazecoinWallet.Core.Services.Wallets;

/// <summary>Outcome of an Unload/Remove operation, so the caller (a UI page) can
/// decide whether to reload itself without the service needing to know about
/// navigation.</summary>
public enum WalletOpResult
{
    /// <summary>Completed; the active wallet did not change.</summary>
    Completed,
    /// <summary>Completed and the active wallet was reassigned — the caller
    /// should reload so every page refetches against the new active wallet.</summary>
    ActiveReassigned,
    /// <summary>The wallet was already not loaded; nothing was done.</summary>
    AlreadyUnloaded,
}

/// <summary>Wallet lifecycle, extracted from Dashboard.razor so the daemon calls,
/// the irreversible on-disk delete, the datadir resolution, and the active-wallet
/// reassignment live in a testable service rather than the UI.
///
/// Operations may throw (RpcException / IO / InvalidOperationException); the UI
/// catches and formats the message. The active wallet is tracked via
/// IWalletContext, which this service owns the writes to.</summary>
public interface IWalletManager
{
    /// <summary>Wallets currently loaded in the daemon.</summary>
    Task<IReadOnlyList<string>> ListLoadedAsync();

    /// <summary>Wallets present on disk (the picker's source of truth).</summary>
    Task<IReadOnlyList<string>> ListOnDiskAsync();

    /// <summary>True for a wallet that must not be removed (the auto-load default,
    /// whose deletion would break daemon startup).</summary>
    bool IsProtected(string name);

    /// <summary>Load <paramref name="name"/> if it isn't already loaded, then make
    /// it the active wallet. Caller should reload afterwards.</summary>
    Task ActivateAsync(string name);

    /// <summary>Unload <paramref name="name"/> from the daemon (files kept). If it
    /// was the active wallet, reassign active to another loaded wallet.</summary>
    Task<WalletOpResult> UnloadAsync(string name);

    /// <summary>Unload (if loaded) then permanently delete <paramref name="name"/>'s
    /// directory from disk. If it was active, reassign active.</summary>
    Task<WalletOpResult> RemoveAsync(string name);
}

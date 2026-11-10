namespace BlazecoinWallet.Core.Services;

// Segregated views over the daemon JSON-RPC surface (SOLID audit #3). The single
// BlazecoindRpcService implements all of them; consumers depend only on the slice
// they use, so e.g. the sync poller and the miner no longer take a dependency on
// ~24 wallet/security methods they never call. IBlazecoindRpcService remains as a
// composite for the UI pages that genuinely span several of these areas.

/// <summary>The Console page's one call. Its own interface (audit 2026-11-09): it sat on
/// <see cref="INodeRpc"/>, so the daemon manager and the sync poller depended on a wallet-scoped
/// method only the Console uses.</summary>
public interface IConsoleRpc
{
    /// <summary>A Console command, run against exactly <paramref name="wallet"/> — the wallet the
    /// Console showed beside its prompt ("" = none chosen). Never re-points the app. Refused with
    /// <see cref="WalletSwitchPendingException"/> while a wallet switch is unacknowledged and with
    /// <see cref="ConsoleWalletChangedException"/> when that wallet is no longer the active one.</summary>
    Task<string> ExecuteConsoleCommandAsync(string wallet, string method, object[] parameters);
}

/// <summary>Node-level calls not tied to chain, wallet or mining: a liveness
/// probe and node housekeeping.</summary>
public interface INodeRpc
{
    /// <summary>Returns a human-readable error string if the daemon is
    /// unreachable, or null when it's responding.</summary>
    Task<string?> CheckConnectionAsync();
    // (ExecuteCommandAsync — a raw passthrough on the ACTIVE wallet's path — was here until the
    // audit of 2026-11-09. The Console had stopped using it and nothing else did.)

    /// <summary>A node-level command (<c>stop</c>, <c>getnetworkinfo</c>) on the node's own
    /// endpoint; no wallet state can hold it up.</summary>
    Task<string> ExecuteNodeCommandAsync(string method, params object[] parameters);

    /// <summary>Offline address-validity check (the daemon's `validateaddress` —
    /// no wallet needed). True/false = valid/invalid encoding+checksum for this
    /// chain; null = the check couldn't run (daemon busy/unreachable), so callers
    /// should not block on it.</summary>
    Task<bool?> ValidateAddressAsync(string address);
}

/// <summary>Read-only chain/network state — what the sync poller and the
/// dashboard's blockchain/network cards need.</summary>
public interface IChainInfoRpc
{
    Task<NetworkInfo?> GetNetworkInfoAsync();
    Task<List<PeerSummary>> GetPeersAsync();
    Task<BlockchainInfo?> GetBlockchainInfoAsync();
}

/// <summary>Solo-mining calls: get a payout address, fetch a block template,
/// submit a solved block.</summary>
public interface IMiningRpc
{
    Task<string?> GetNewAddressAsync(string label = "");
    Task<Mining.BlockTemplate?> GetBlockTemplateAsync();
    Task<string?> SubmitBlockAsync(string hexBlock);
}

/// <summary>Wallet operations: balances, transactions, addresses, sending, and
/// wallet-file lifecycle (list/load/unload/backup/restore/migrate).</summary>
public interface IWalletRpc
{
    Task<WalletInfo?> GetWalletInfoAsync();
    Task<Balance?> GetBalanceAsync();
    Task<WalletBalances?> GetBalancesAsync();
    Task<List<Transaction>?> ListTransactionsAsync(int count = 10);
    Task<List<Transaction>?> ListAllTransactionsAsync(int batch = 1000, int maxTotal = 100_000);
    Task<string?> GetNewAddressAsync(string label = "");
    Task SetLabelAsync(string address, string label);

    /// <summary>Every RECEIVE-purpose address the wallet has a label for (<c>listlabels</c> →
    /// <c>getaddressesbylabel</c>), as address+label pairs. Addresses created outside the GUI
    /// (RPC, rebuild scripts) surface here; keypool addresses never handed out do not.</summary>
    Task<List<AddressBook.AddressBookEntry>> ListLabelledAddressesAsync();
    Task<string?> SendToAddressAsync(string address, decimal amount, bool subtractFeeFromAmount = false);

    // (SendFromWalletAsync — a send from exactly one named wallet — was here for Auto Payout's
    // fallback path. Every payout now goes through Send.ISendRpc.SendAsync with the wallet named
    // on the request, so nothing needs it on this interface — audit 2026-11-09.)
    /// <summary>Best-effort fee dry run on the ACTIVE wallet; null when unknown. Throws
    /// <see cref="WalletSwitchPendingException"/> while a wallet switch is unacknowledged (audit 2026-11-09).</summary>
    Task<decimal?> EstimateSendFeeAsync(string address, decimal amount, bool subtractFeeFromAmount = false);
    Task<List<string>> ListWalletsAsync();
    Task<List<string>> ListWalletDirAsync();
    Task LoadWalletAsync(string name);
    Task UnloadWalletAsync(string name);
    Task BackupWalletAsync(string destination, string? walletName = null);
    Task RestoreWalletAsync(string name, string backupFile);
    Task MigrateWalletAsync(string name, string? passphrase = null);
    /// <summary><c>createwallet</c> (descriptor wallet, loaded on return) on the NODE endpoint — a fresh
    /// install has no active wallet yet. The first-run card's Create button uses it.</summary>
    Task CreateWalletAsync(string name);
}

/// <summary>Wallet encryption / lock state.</summary>
public interface ISecurityRpc
{
    Task<string?> EncryptWalletAsync(string passphrase);
    Task ChangeWalletPassphraseAsync(string oldPassphrase, string newPassphrase);
    Task UnlockWalletAsync(string passphrase, int seconds);
    Task LockWalletAsync();
}

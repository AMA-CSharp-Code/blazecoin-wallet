namespace BlazecoinWallet.Core.Services.Wallets;

/// <summary>Default <see cref="IWalletManager"/>. Logic is a faithful extraction
/// of the wallet load/unload/remove code that used to live in Dashboard.razor.</summary>
public sealed class WalletManager : IWalletManager
{
    private readonly IWalletRpc _rpc;
    private readonly IWalletContext _ctx;
    private readonly IWalletSwitchNotice? _switchNotice;

    /// <param name="switchNotice">Told when unloading or removing the active wallet moves the window
    /// to another one (bug-bounty report 2026-11-20). Null only where nothing shows a notice.</param>
    public WalletManager(IWalletRpc rpc, IWalletContext ctx, IWalletSwitchNotice? switchNotice = null)
    {
        _rpc = rpc;
        _ctx = ctx;
        _switchNotice = switchNotice;
    }

    // The auto-loaded default wallet (conf `wallet=Primary`). Removing it would
    // break daemon startup, so Remove is refused for it.
    private const string ProtectedWallet = "Primary";

    public bool IsProtected(string name) =>
        string.Equals(name, ProtectedWallet, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> ListLoadedAsync() => await _rpc.ListWalletsAsync();

    public async Task<IReadOnlyList<string>> ListOnDiskAsync() => await _rpc.ListWalletDirAsync();

    public async Task ActivateAsync(string name)
    {
        try
        {
            var loaded = await _rpc.ListWalletsAsync();
            if (!loaded.Contains(name))
                await _rpc.LoadWalletAsync(name);
            _ctx.SetActive(name);
        }
        catch (RpcException ex) when (ex.Code == -4) // already loaded — just switch
        {
            _ctx.SetActive(name);
        }
    }

    public async Task<WalletOpResult> UnloadAsync(string name)
    {
        var wasActive = name == _ctx.Active;
        try
        {
            await _rpc.UnloadWalletAsync(name);
        }
        catch (RpcException ex) when (ex.Code == -18) // already not loaded
        {
            return WalletOpResult.AlreadyUnloaded;
        }

        if (wasActive)
        {
            await ReassignActiveAsync(name);
            return WalletOpResult.ActiveReassigned;
        }
        return WalletOpResult.Completed;
    }

    public async Task<WalletOpResult> RemoveAsync(string name)
    {
        if (string.IsNullOrEmpty(name) || IsProtected(name))
            throw new InvalidOperationException($"\"{name}\" cannot be removed.");

        var wasActive = name == _ctx.Active;

        // Unload first so the daemon releases its file locks; otherwise the
        // directory delete fails (files in use).
        var loaded = await _rpc.ListWalletsAsync();
        if (loaded.Contains(name))
        {
            try { await _rpc.UnloadWalletAsync(name); }
            catch (RpcException ex) when (ex.Code == -18) { /* already unloaded */ }
        }

        DeleteWalletDirectory(name);

        if (wasActive)
        {
            await ReassignActiveAsync(name);
            return WalletOpResult.ActiveReassigned;
        }
        return WalletOpResult.Completed;
    }

    /// <summary>After the active wallet goes away, point the context at another
    /// loaded wallet (or null if none remain).</summary>
    /// <remarks>The user asked for the wallet to go, not for the window to move to another one:
    /// which wallet it lands on is the app's choice, so it is announced like the switch a failed
    /// read makes, and nothing that changes a wallet runs until the notice is acknowledged
    /// (bug-bounty report 2026-11-20: this path raised no notice, so a new passphrase or a fresh
    /// address could go to the wallet the window had moved to). Raised BEFORE the context moves,
    /// as there: whatever sees the new wallet also sees the notice.</remarks>
    private async Task ReassignActiveAsync(string from)
    {
        var loaded = await _rpc.ListWalletsAsync();
        var next = loaded.FirstOrDefault();
        if (!string.IsNullOrEmpty(next)) _switchNotice?.Record(from, next);
        _ctx.SetActive(next);
    }

    // Resolve <V2 datadir>\<walletName> and delete it. The datadir is the daemon's own
    // default per platform (DaemonPaths: legacy %APPDATA% else %LOCALAPPDATA% on
    // Windows, ~/Library/Application Support on macOS). Guards against path-traversal
    // names and refuses any folder that doesn't actually hold a wallet.dat, so a stale
    // selection can never delete an unrelated directory.
    private static void DeleteWalletDirectory(string walletName)
    {
        if (string.IsNullOrWhiteSpace(walletName)
            || walletName.Contains('/') || walletName.Contains('\\') || walletName.Contains(".."))
            throw new InvalidOperationException("Unsafe wallet name.");

        var dataDir = DaemonPaths.DefaultDataDir();
        var walletDir = Path.Combine(dataDir, walletName);

        if (!Directory.Exists(walletDir))
            throw new DirectoryNotFoundException($"Wallet folder not found: {walletDir}");
        if (!File.Exists(Path.Combine(walletDir, "wallet.dat")))
            throw new InvalidOperationException(
                $"\"{walletName}\" doesn't look like a wallet folder (no wallet.dat); not deleting.");

        Directory.Delete(walletDir, recursive: true);
    }
}

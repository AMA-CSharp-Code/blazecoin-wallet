namespace BlazecoinWallet.Core.Services.Wallets;

/// <summary>Default <see cref="IWalletManager"/>. Logic is a faithful extraction
/// of the wallet load/unload/remove code that used to live in Dashboard.razor.</summary>
public sealed class WalletManager : IWalletManager
{
    private readonly IWalletRpc _rpc;
    private readonly IWalletContext _ctx;

    public WalletManager(IWalletRpc rpc, IWalletContext ctx)
    {
        _rpc = rpc;
        _ctx = ctx;
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
            await ReassignActiveAsync();
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
            await ReassignActiveAsync();
            return WalletOpResult.ActiveReassigned;
        }
        return WalletOpResult.Completed;
    }

    /// <summary>After the active wallet goes away, point the context at another
    /// loaded wallet (or null if none remain).</summary>
    private async Task ReassignActiveAsync()
    {
        var loaded = await _rpc.ListWalletsAsync();
        _ctx.SetActive(loaded.FirstOrDefault());
    }

    // Resolve <V2 datadir>\<walletName> and delete it. Mirrors the launch script's
    // GetDefaultDataDir() fallback: keep the legacy %APPDATA% dir if it exists,
    // else %LOCALAPPDATA%. Guards against path-traversal names and refuses any
    // folder that doesn't actually hold a wallet.dat, so a stale selection can
    // never delete an unrelated directory.
    private static void DeleteWalletDirectory(string walletName)
    {
        if (string.IsNullOrWhiteSpace(walletName)
            || walletName.Contains('/') || walletName.Contains('\\') || walletName.Contains(".."))
            throw new InvalidOperationException("Unsafe wallet name.");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacy = Path.Combine(appData, "BlazecoinV2.0");
        var modern = Path.Combine(localAppData, "BlazecoinV2.0");
        var dataDir = Directory.Exists(legacy) ? legacy : modern;
        var walletDir = Path.Combine(dataDir, walletName);

        if (!Directory.Exists(walletDir))
            throw new DirectoryNotFoundException($"Wallet folder not found: {walletDir}");
        if (!File.Exists(Path.Combine(walletDir, "wallet.dat")))
            throw new InvalidOperationException(
                $"\"{walletName}\" doesn't look like a wallet folder (no wallet.dat); not deleting.");

        Directory.Delete(walletDir, recursive: true);
    }
}

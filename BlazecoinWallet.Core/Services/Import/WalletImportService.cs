using System.Text;

namespace BlazecoinWallet.Core.Services.Import;

/// <summary>Default <see cref="IWalletImportService"/>. The detection, two-phase
/// restore/migrate flow, and the -4 error mapping are moved verbatim from
/// Import.razor's code-behind, now operating against <see cref="IWalletRpc"/> and
/// <see cref="IWalletContext"/> instead of the page's injected services.</summary>
public sealed class WalletImportService : IWalletImportService
{
    private readonly IWalletRpc _rpc;
    private readonly IWalletContext _walletContext;

    public WalletImportService(IWalletRpc rpc, IWalletContext walletContext)
    {
        _rpc = rpc;
        _walletContext = walletContext;
    }

    public async Task<WalletFormat> DetectFormatAsync(string path)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var header = new byte[16];
            var read = await fs.ReadAsync(header.AsMemory(0, 16));
            if (read < 16) return WalletFormat.Unknown;
            // SQLite files start with the literal magic "SQLite format 3"
            // followed by a NUL byte. Anything else from a Bitcoin Core
            // wallet directory is BDB.
            var marker = Encoding.ASCII.GetString(header, 0, 15);
            return marker == "SQLite format 3" && header[15] == 0
                ? WalletFormat.Sqlite
                : WalletFormat.LegacyBdb;
        }
        catch
        {
            return WalletFormat.Unknown;
        }
    }

    public string FormatLabel(WalletFormat f) => f switch
    {
        WalletFormat.Sqlite => "Descriptor wallet (SQLite) — V2-compatible",
        WalletFormat.LegacyBdb => "Legacy wallet (Berkeley DB) — V1.5 / original Blazecoin",
        _ => "Unknown — Core will reject this if it's not a valid wallet.dat",
    };

    public async Task<WalletImportResult> ImportAsync(
        string newName,
        string sourcePath,
        WalletFormat format,
        bool migrate,
        string? passphrase,
        IProgress<WalletImportPhase>? onPhase = null)
    {
        try
        {
            // Phase 1 — restorewallet copies the file in and loads it. A legacy
            // wallet triggers a full-chain rescan here (can run 30-60 min).
            // restorewallet's own -4 means "a wallet of this name already exists";
            // keep that distinct from migratewallet's -4 below.
            try
            {
                await _rpc.RestoreWalletAsync(newName, sourcePath);
            }
            catch (RpcException ex) when (ex.Code == -4)
            {
                // restorewallet returns -4 for several DISTINCT conditions — a name
                // collision, an unflushed BDB, OR an unreadable/corrupt wallet (e.g. an
                // older V1.5/0.8.x wallet whose key encoding V2 can't load). Pick the
                // message by content instead of assuming "already exists".
                return new WalletImportResult(false, false, MapRestoreMinus4(ex.Message, newName));
            }

            // Phase 2 — convert legacy BDB → descriptors (optional). Factored into
            // MigrateLoadedAsync so the encrypted-passphrase retry reuses the exact
            // same classification + active-wallet handoff without re-running restore.
            if (format == WalletFormat.LegacyBdb && migrate)
                return await MigrateLoadedAsync(newName, passphrase, onPhase);

            // Descriptor wallet (or a legacy wallet left un-migrated): nothing more to
            // do. Make it the active wallet so the rest of the app targets it via
            // /wallet/<name> instead of failing with -19 now that >1 are loaded.
            _walletContext.SetActive(newName);
            return new WalletImportResult(true, false, null);
        }
        catch (RpcException ex)
        {
            return new WalletImportResult(false, false, ex.Code switch
            {
                -18 => "Daemon couldn't load the wallet file — it may be the wrong format or corrupted.",
                _ => $"Daemon rejected the import: {ex.Message}",
            });
        }
        catch (TimeoutException)
        {
            // Even the 2h cap was exceeded (or the connection dropped). The daemon
            // may still be finishing the rescan in the background.
            return TimedOut();
        }
        catch (Exception ex)
        {
            return new WalletImportResult(false, false, $"Error: {ex.Message}");
        }
    }

    public async Task<WalletImportResult> RetryMigrateAsync(
        string newName, string passphrase, IProgress<WalletImportPhase>? onPhase = null)
    {
        try
        {
            return await MigrateLoadedAsync(newName, passphrase, onPhase);
        }
        catch (RpcException ex)
        {
            return new WalletImportResult(false, false, ex.Code == -18
                ? "The restored wallet is no longer loaded, so it can't be migrated. Import the file again."
                : $"Daemon rejected the migration: {ex.Message}");
        }
        catch (TimeoutException) { return TimedOut(); }
        catch (Exception ex) { return new WalletImportResult(false, false, $"Error: {ex.Message}"); }
    }

    // Runs migratewallet against an already-restored, still-loaded legacy wallet and
    // classifies the outcome. On success it becomes the active wallet. Shared by the
    // initial import (phase 2) and the encrypted-passphrase retry.
    private async Task<WalletImportResult> MigrateLoadedAsync(
        string newName, string? passphrase, IProgress<WalletImportPhase>? onPhase)
    {
        onPhase?.Report(new WalletImportPhase(
            "Migrating to descriptor format — scanning the chain...",
            "Wallet loaded. Starting migration..."));
        try
        {
            await _rpc.MigrateWalletAsync(newName, string.IsNullOrEmpty(passphrase) ? null : passphrase);
        }
        catch (RpcException ex) when (ex.Code == -4 && LooksLikeBadPassphrase(ex.Message))
        {
            // The wallet is encrypted and migratewallet couldn't unlock it. It's still
            // restored + loaded, so the page can retry the migration alone with a
            // corrected passphrase (re-restoring would -4 on the now-existing name).
            return new WalletImportResult(false, false,
                "This wallet is encrypted and the passphrase was missing or incorrect. It has "
                + "been restored and loaded — enter its passphrase below to finish converting it "
                + "to the descriptor format.",
                NeedsPassphrase: true);
        }
        catch (RpcException ex) when (ex.Code == -4 && LooksLikeUnflushedBdb(ex.Message))
        {
            // migratewallet ALSO returns -4 for a wallet.dat whose Berkeley DB log was
            // never cleanly flushed ("LSNs are not reset"). V2's Core has read-only BDB,
            // so it can't flush it — the source must be closed by a BDB-capable version.
            return new WalletImportResult(false, false,
                "This legacy wallet wasn't cleanly closed, so it can't be migrated "
                + "(its Berkeley DB log isn't flushed). Open the wallet in Blazecoin V1.5, "
                + "shut V1.5 down cleanly, then back up wallet.dat and import that fresh copy.");
        }

        // The imported wallet is now loaded alongside any existing ones. Make it the
        // active wallet so the rest of the app (Dashboard, balances, etc.) targets it
        // via /wallet/<name> instead of failing with -19 now that >1 are loaded.
        _walletContext.SetActive(newName);
        return new WalletImportResult(true, true, null);
    }

    private static WalletImportResult TimedOut()
        => new(false, false,
            "The import is still running but the connection timed out. The daemon may "
            + "be finishing in the background — check the Dashboard in a few minutes "
            + "before retrying (re-importing the same name will fail with \"already exists\").");

    // migratewallet on an encrypted wallet it couldn't unlock returns code -4 with
    // "Wallet decryption failed, the wallet passphrase was not provided or was
    // incorrect." (src/wallet/wallet.cpp), or asks to "provide the wallet's passphrase
    // if it is encrypted." Match either so the page can prompt for the passphrase.
    private static bool LooksLikeBadPassphrase(string? msg)
        => msg != null
        && (msg.Contains("decryption failed", StringComparison.OrdinalIgnoreCase)
         || (msg.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
             && (msg.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
              || msg.Contains("not provided", StringComparison.OrdinalIgnoreCase)
              || msg.Contains("if it is encrypted", StringComparison.OrdinalIgnoreCase))));

    // migratewallet rejects a not-cleanly-flushed BDB file with code -4 and a
    // message mentioning unreset LSNs; match on that so we don't show the
    // restore-path "already exists" text for it.
    private static bool LooksLikeUnflushedBdb(string? msg)
        => msg != null && (msg.Contains("LSN", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("not completely flushed", StringComparison.OrdinalIgnoreCase));

    // restorewallet can return -4 for a name collision, an unflushed BDB, or an
    // unreadable/corrupt wallet — map the daemon's message to the right guidance
    // rather than blanket-labelling every -4 as "already exists". The final branch
    // surfaces the raw message so an unanticipated -4 is never mislabelled.
    private static string MapRestoreMinus4(string? msg, string name)
    {
        msg ??= "";
        if (msg.Contains("already", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("in use", StringComparison.OrdinalIgnoreCase))
            return $"A wallet named \"{name}\" already exists. Pick a different name.";

        if (LooksLikeUnflushedBdb(msg))
            return "This legacy wallet wasn't cleanly closed, so it can't be read "
                 + "(its Berkeley DB log isn't flushed). Open it in Blazecoin V1.5, shut V1.5 "
                 + "down cleanly, then back up wallet.dat and import that fresh copy.";

        if (msg.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("CPrivKey", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("verification failed", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("loading failed", StringComparison.OrdinalIgnoreCase))
            return "V2 couldn't read this wallet file — the daemon reports it corrupt or in a "
                 + "key format it can't load. This is common with older V1.5 / 0.8.x wallets; the "
                 + "file may still be fine in V1.5. To migrate it, export the keys in V1.5 "
                 + "(dumpprivkey) and import them into V2.";

        return $"Daemon rejected the import: {msg}";
    }
}

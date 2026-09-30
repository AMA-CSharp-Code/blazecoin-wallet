namespace BlazecoinWallet.Core.Services.Import;

/// <summary>Wallet-file import: format detection + the two-phase restore/migrate
/// orchestration + daemon-error classification, lifted out of Import.razor (SOLID
/// audit #7). The page keeps the file picker, the progress UI, and the success
/// view; everything that talks to the daemon and decides what went wrong lives
/// here.</summary>
public interface IWalletImportService
{
    /// <summary>Sniffs the wallet.dat header: SQLite (descriptor, V2-native) vs
    /// Berkeley DB (legacy V1.5 / original) vs unknown.</summary>
    Task<WalletFormat> DetectFormatAsync(string path);

    /// <summary>Human-readable label for the detected format (shown under the
    /// chosen file).</summary>
    string FormatLabel(WalletFormat format);

    /// <summary>Restores the file under <paramref name="newName"/>, optionally
    /// migrates a legacy BDB wallet to descriptors, and makes it the active wallet.
    /// Reports the mid-phase message via <paramref name="onPhase"/>. Returns a
    /// classified result instead of throwing on expected daemon errors.</summary>
    Task<WalletImportResult> ImportAsync(
        string newName,
        string sourcePath,
        WalletFormat format,
        bool migrate,
        string? passphrase,
        IProgress<WalletImportPhase>? onPhase = null);

    /// <summary>Retries ONLY the legacy→descriptor migration on a wallet that was
    /// already restored and is still loaded — used after an import returned
    /// <see cref="WalletImportResult.NeedsPassphrase"/> because the wallet is encrypted
    /// and the passphrase was missing/incorrect. Skips restorewallet (the name now
    /// exists), so the user supplies the correct passphrase without a 30-60 min
    /// re-restore. Reuses the same classification, so a still-wrong passphrase comes
    /// back as <see cref="WalletImportResult.NeedsPassphrase"/> again.</summary>
    Task<WalletImportResult> RetryMigrateAsync(
        string newName,
        string passphrase,
        IProgress<WalletImportPhase>? onPhase = null);
}

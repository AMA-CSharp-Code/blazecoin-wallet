namespace BlazecoinWallet.Core.Services.Import;

/// <summary>On-disk format of a wallet.dat the importer was handed.</summary>
public enum WalletFormat { Unknown, Sqlite, LegacyBdb }

/// <summary>Progress report emitted between the restore and migrate phases so the
/// page can update its busy/status text. (The long rescan has no reliable % — the
/// page shows an elapsed-time ticker instead.)</summary>
public sealed record WalletImportPhase(string BusyMessage, string? Status);

/// <summary>Outcome of an import attempt. The service never throws for an expected
/// failure — it classifies the daemon error into a user-facing <see cref="Error"/>
/// message — so the page just shows success or the message.
/// <para><see cref="NeedsPassphrase"/> is the one failure the page must treat
/// specially: the wallet was restored and loaded but it's encrypted and migration
/// couldn't unlock it (missing/incorrect passphrase). The page offers a focused
/// "finish migration" retry (<see cref="IWalletImportService.RetryMigrateAsync"/>)
/// rather than a dead-end, since re-restoring would now collide on the name.</para></summary>
public sealed record WalletImportResult(bool Success, bool Migrated, string? Error, bool NeedsPassphrase = false);

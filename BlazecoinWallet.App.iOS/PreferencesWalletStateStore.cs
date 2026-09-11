using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.iOS;

/// <summary>
/// Android-persistent <see cref="IWalletStateStore"/> over MAUI Preferences — rotation
/// position only, never key material. Losing it is safe (restore gap-scans the chain);
/// keeping it means the current receive address survives an app restart.
/// </summary>
public sealed class PreferencesWalletStateStore : IWalletStateStore
{
    private const string RevealedKey = "revealed_address_count";
    private const string ChangeKey = "change_address_count";
    private const string CheckpointKey = "verified_header_checkpoint";
    private const string BackupVerifiedKey = "backup_verified";

    public Task<int> GetRevealedAddressCountAsync()
        => Task.FromResult(Preferences.Default.Get(RevealedKey, 0));

    public Task SetRevealedAddressCountAsync(int count)
    {
        Preferences.Default.Set(RevealedKey, count);
        return Task.CompletedTask;
    }

    public Task<int> GetChangeAddressCountAsync()
        => Task.FromResult(Preferences.Default.Get(ChangeKey, 0));

    public Task SetChangeAddressCountAsync(int count)
    {
        Preferences.Default.Set(ChangeKey, count);
        return Task.CompletedTask;
    }

    public Task<string?> GetVerifiedCheckpointAsync()
        => Task.FromResult(Preferences.Default.Get<string?>(CheckpointKey, null));

    public Task SetVerifiedCheckpointAsync(string value)
    {
        Preferences.Default.Set(CheckpointKey, value);
        return Task.CompletedTask;
    }

    public Task<bool> GetBackupVerifiedAsync()
        => Task.FromResult(Preferences.Default.Get(BackupVerifiedKey, true));

    public Task SetBackupVerifiedAsync(bool verified)
    {
        Preferences.Default.Set(BackupVerifiedKey, verified);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        Preferences.Default.Remove(RevealedKey);
        Preferences.Default.Remove(ChangeKey);
        Preferences.Default.Remove(CheckpointKey);
        Preferences.Default.Remove(BackupVerifiedKey);
        return Task.CompletedTask;
    }
}

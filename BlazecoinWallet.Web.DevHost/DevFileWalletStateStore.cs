using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Web.DevHost;

/// <summary>
/// DEV-ONLY <see cref="IWalletStateStore"/>: the rotation position as a plain file beside
/// the dev mnemonic, so a dev-host restart keeps showing the same current address (and
/// keeps watching coins on rotated slots). Non-secret by nature.
/// </summary>
public sealed class DevFileWalletStateStore : IWalletStateStore
{
    private static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazecoinLiteDevHost");
    private static readonly string RevealedPath = System.IO.Path.Combine(Dir, "dev-revealed-count.txt");
    private static readonly string ChangePath = System.IO.Path.Combine(Dir, "dev-change-count.txt");
    private static readonly string CheckpointPath = System.IO.Path.Combine(Dir, "dev-verified-checkpoint.txt");
    private static readonly string BackupVerifiedPath = System.IO.Path.Combine(Dir, "dev-backup-verified.txt");

    public Task<int> GetRevealedAddressCountAsync() => ReadCountAsync(RevealedPath);
    public Task SetRevealedAddressCountAsync(int count) => WriteCountAsync(RevealedPath, count);
    public Task<int> GetChangeAddressCountAsync() => ReadCountAsync(ChangePath);
    public Task SetChangeAddressCountAsync(int count) => WriteCountAsync(ChangePath, count);

    public async Task<string?> GetVerifiedCheckpointAsync()
        => File.Exists(CheckpointPath) ? (await File.ReadAllTextAsync(CheckpointPath)).Trim() : null;

    public async Task SetVerifiedCheckpointAsync(string value)
    {
        Directory.CreateDirectory(Dir);
        await File.WriteAllTextAsync(CheckpointPath, value);
    }

    public async Task<bool> GetBackupVerifiedAsync()
        => !File.Exists(BackupVerifiedPath) || (await File.ReadAllTextAsync(BackupVerifiedPath)).Trim() == "1";

    public async Task SetBackupVerifiedAsync(bool verified)
    {
        Directory.CreateDirectory(Dir);
        await File.WriteAllTextAsync(BackupVerifiedPath, verified ? "1" : "0");
    }

    public Task ClearAsync()
    {
        if (File.Exists(RevealedPath)) File.Delete(RevealedPath);
        if (File.Exists(ChangePath)) File.Delete(ChangePath);
        if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
        if (File.Exists(BackupVerifiedPath)) File.Delete(BackupVerifiedPath);
        return Task.CompletedTask;
    }

    private static async Task<int> ReadCountAsync(string path) =>
        File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var n) ? n : 0;

    private static async Task WriteCountAsync(string path, int count)
    {
        Directory.CreateDirectory(Dir);
        await File.WriteAllTextAsync(path, count.ToString());
    }
}

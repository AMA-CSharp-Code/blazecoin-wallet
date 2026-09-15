using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Web.DevHost;

/// <summary>
/// DEV-ONLY seed vault: stores the mnemonic as a plain file under LocalAppData so the
/// desktop dev host can exercise the full wallet flow. This is NOT secure storage — the
/// real heads use platform keystores (Android Keystore / iOS Keychain). Never point this
/// host at a wallet holding meaningful coins.
/// </summary>
public sealed class DevFileSeedVault : ISeedVault
{
    private static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazecoinLiteDevHost");
    private static readonly string PathOnDisk = System.IO.Path.Combine(Dir, "dev-mnemonic.txt");
    private static readonly string PassPath = System.IO.Path.Combine(Dir, "dev-passphrase.txt");

    public Task<bool> HasWalletAsync() => Task.FromResult(File.Exists(PathOnDisk));

    public async Task SaveMnemonicAsync(string mnemonicWords)
    {
        Directory.CreateDirectory(Dir);
        await File.WriteAllTextAsync(PathOnDisk, mnemonicWords);
    }

    public async Task<string?> LoadMnemonicAsync() =>
        File.Exists(PathOnDisk) ? await File.ReadAllTextAsync(PathOnDisk) : null;

    public Task ClearAsync()
    {
        if (File.Exists(PathOnDisk)) File.Delete(PathOnDisk);
        if (File.Exists(PassPath)) File.Delete(PassPath);
        return Task.CompletedTask;
    }

    public async Task SavePassphraseAsync(string? passphrase)
    {
        Directory.CreateDirectory(Dir);
        await File.WriteAllTextAsync(PassPath, passphrase ?? string.Empty);
    }

    public async Task<string> LoadPassphraseAsync() =>
        File.Exists(PassPath) ? await File.ReadAllTextAsync(PassPath) : string.Empty;
}

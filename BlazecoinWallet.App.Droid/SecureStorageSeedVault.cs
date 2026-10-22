using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.Droid;

/// <summary>
/// The Android head's <see cref="ISeedVault"/>: MAUI SecureStorage, which on Android wraps
/// EncryptedSharedPreferences keyed by the Android Keystore — the mnemonic never touches
/// disk in the clear and never leaves the device.
/// </summary>
public sealed class SecureStorageSeedVault : ISeedVault
{
    private const string Key = "blz_mnemonic";
    private const string PassKey = "blz_passphrase";

    public async Task<bool> HasWalletAsync() =>
        !string.IsNullOrEmpty(await SecureStorage.Default.GetAsync(Key));

    public Task SaveMnemonicAsync(string mnemonicWords) =>
        SecureStorage.Default.SetAsync(Key, mnemonicWords);

    public Task<string?> LoadMnemonicAsync() =>
        SecureStorage.Default.GetAsync(Key);

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(Key);
        SecureStorage.Default.Remove(PassKey);
        return Task.CompletedTask;
    }

    // The optional BIP39 passphrase rides in the same Keystore-backed secure store as the
    // mnemonic (both are needed to derive the wallet). Empty removes the entry rather than
    // storing a decoy "" secret (audit round-3 C5).
    public Task SavePassphraseAsync(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            SecureStorage.Default.Remove(PassKey);
            return Task.CompletedTask;
        }
        return SecureStorage.Default.SetAsync(PassKey, passphrase);
    }

    public async Task<string> LoadPassphraseAsync() =>
        await SecureStorage.Default.GetAsync(PassKey) ?? string.Empty;
}

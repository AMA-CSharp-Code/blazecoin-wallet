namespace BlazecoinWallet.Lite;

/// <summary>
/// Where the mnemonic lives at rest. Each app head supplies its platform's secure store —
/// Android Keystore (MAUI SecureStorage) on the Droid head, Keychain on iOS later. Lite.Core
/// itself never persists key material.
/// </summary>
public interface ISeedVault
{
    Task<bool> HasWalletAsync();
    Task SaveMnemonicAsync(string mnemonicWords);
    /// <summary>Null when no wallet has been created/restored on this device.</summary>
    Task<string?> LoadMnemonicAsync();
    /// <summary>Wipes the stored mnemonic (wallet reset — the mnemonic backup is the recovery).</summary>
    Task ClearAsync();

    /// <summary>Persist the optional BIP39 passphrase (the "25th word"). The default no-ops
    /// for empty (tests and passphrase-less wallets are unaffected) but THROWS for a real
    /// passphrase (audit round-3 F1): a vault that silently discarded it would accept the
    /// passphrase at create, then unlock the EMPTY-passphrase wallet next launch — funds
    /// invisible with no error. An incomplete head must fail loudly at create instead.
    /// Heads that store secrets override this alongside the mnemonic; <see cref="ClearAsync"/>
    /// clears it too. Empty = no passphrase.</summary>
    Task SavePassphraseAsync(string? passphrase) =>
        string.IsNullOrEmpty(passphrase)
            ? Task.CompletedTask
            : throw new NotSupportedException(
                "This seed vault does not store BIP39 passphrases — override SavePassphraseAsync/LoadPassphraseAsync to support them.");

    /// <summary>The stored passphrase, or "" when none (the common case).</summary>
    Task<string> LoadPassphraseAsync() => Task.FromResult(string.Empty);
}

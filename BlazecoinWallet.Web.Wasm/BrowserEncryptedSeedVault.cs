using System.Text.Json;
using BlazecoinWallet.Lite;
using Microsoft.JSInterop;

namespace BlazecoinWallet.Web.Wasm;

/// <summary>
/// The web head's persistent seed vault (step 3 — replaces the step-1 session-only
/// scaffold). At rest the mnemonic (+ optional BIP39 passphrase) lives in localStorage
/// encrypted under a USER PASSWORD via WebCrypto (vault-crypto.js: PBKDF2-SHA256 600k →
/// AES-256-GCM); in memory it lives here after an unlock, exactly like the Android
/// Keystore vault after a biometric. A password rather than the app-lock PIN, on
/// purpose: an exfiltrated blob is brute-forced OFFLINE, where a 4-digit PIN's 10,000
/// candidates evaporate — the password IS the at-rest security on a platform with no
/// hardware keystore.
///
/// The flow is owned by <see cref="Pages"/>' VaultGate at the app root: while a blob
/// exists and nothing is unlocked, the app doesn't render; after a create/restore puts
/// a seed in memory, the gate prompts to set the password (or, explicitly, to stay
/// session-only). ISeedVault callers (LiteWalletService) only ever see the in-memory
/// side — by the time any page runs, the gate has resolved the state.
/// </summary>
public sealed class BrowserEncryptedSeedVault(IJSRuntime js) : ISeedVault
{
    /// <summary>The localStorage key vault-crypto.js writes the blob under — exposed so the
    /// locker can probe "is there a re-lockable vault?" synchronously via BrowserKv.</summary>
    public const string StoreKey = "blz_vault_v1";

    /// <summary>True while a seed is decrypted in memory (unlocked, or freshly created/restored).</summary>
    public bool HasSeedInMemory => _mnemonic != null;

    /// <summary>Re-lock: drop the decrypted seed (and passphrase) from memory while the encrypted
    /// blob stays put — the idle/manual lock for the web head. No-op without a blob (a
    /// session-only wallet has nowhere to come back from, so it is never re-locked this way).
    /// The gate's StateChanged hook turns this into the password screen.</summary>
    public void Relock()
    {
        if (_blobCached != true) return;   // only meaningful when the encrypted blob exists
        _mnemonic = null;
        _passphrase = null;
        StateChanged?.Invoke();
    }

    /// <summary>What the vault holds inside the encrypted blob.</summary>
    private sealed record Persisted(string M, string? P);

    private IJSObjectReference? _module;
    private string? _mnemonic;
    private string? _passphrase;
    private bool _sessionOnly;     // the user explicitly declined persistence
    private bool? _blobCached;     // localStorage probe, cached (it can't change under us)

    /// <summary>Raised whenever the gate should re-evaluate (save/unlock/protect/clear).</summary>
    public event Action? StateChanged;

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/vault-crypto.js");

    public async Task<bool> HasEncryptedBlobAsync() =>
        _blobCached ??= await (await ModuleAsync()).InvokeAsync<bool>("hasBlob");

    /// <summary>A seed is in memory but not persisted, and the user hasn't opted out —
    /// the gate should offer to set a wallet password.</summary>
    public async Task<bool> NeedsProtectionAsync() =>
        _mnemonic != null && !_sessionOnly && !await HasEncryptedBlobAsync();

    /// <summary>A blob exists but nothing is unlocked — the gate must block the app.</summary>
    public async Task<bool> IsLockedAsync() =>
        _mnemonic == null && await HasEncryptedBlobAsync();

    /// <summary>Decrypts the blob into memory. False = wrong password (GCM auth failure).</summary>
    public async Task<bool> UnlockWithPasswordAsync(string password)
    {
        var plaintext = await (await ModuleAsync()).InvokeAsync<string?>("unlock", password);
        if (plaintext is null) return false;
        var doc = JsonSerializer.Deserialize<Persisted>(plaintext);
        if (doc is null) return false;
        _mnemonic = doc.M;
        _passphrase = doc.P;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Encrypts the in-memory seed under the password and persists it.</summary>
    public async Task ProtectWithPasswordAsync(string password)
    {
        if (_mnemonic is null) throw new InvalidOperationException("No wallet in memory to protect.");
        var plaintext = JsonSerializer.Serialize(new Persisted(_mnemonic, _passphrase));
        await (await ModuleAsync()).InvokeVoidAsync("protect", password, plaintext);
        _blobCached = true;
        StateChanged?.Invoke();
    }

    /// <summary>Asks the browser to exempt this origin from storage eviction (which
    /// would delete the encrypted blob). False = declined/unsupported — survivable,
    /// because the recovery phrase is the designed backup, but the gate warns.</summary>
    public async Task<bool> RequestPersistenceAsync() =>
        await (await ModuleAsync()).InvokeAsync<bool>("requestPersistence");

    /// <summary>The explicit opt-out: keep this session only (a refresh forgets — the
    /// recovery phrase is then the only way back, which the gate's copy says plainly).</summary>
    public void KeepSessionOnly()
    {
        _sessionOnly = true;
        StateChanged?.Invoke();
    }

    // ── ISeedVault (what LiteWalletService sees) ────────────────────────────────────────
    public async Task<bool> HasWalletAsync() => _mnemonic != null || await HasEncryptedBlobAsync();

    public Task<string?> LoadMnemonicAsync() => Task.FromResult(_mnemonic);

    public Task SaveMnemonicAsync(string mnemonic)
    {
        _mnemonic = mnemonic;
        _sessionOnly = false;   // a NEW wallet gets a fresh protection offer
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task ClearAsync()
    {
        _mnemonic = null;
        _passphrase = null;
        _sessionOnly = false;
        if (await HasEncryptedBlobAsync()) await (await ModuleAsync()).InvokeVoidAsync("wipe");
        _blobCached = false;
        StateChanged?.Invoke();
    }

    // Explicit passphrase support (the 2026-07-25 F1 lesson: the default interface
    // method throws for a real passphrase). Persisted inside the blob at Protect time —
    // setup saves the passphrase before the gate ever offers protection.
    public Task SavePassphraseAsync(string? passphrase)
    {
        _passphrase = passphrase;
        return Task.CompletedTask;
    }

    public Task<string> LoadPassphraseAsync() => Task.FromResult(_passphrase ?? string.Empty);
}

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Web.Wasm;

// Browser-persistent versions of the local convenience stores + rotation state + app
// lock — the web twins of the Droid Preferences* classes, over localStorage (step 4).
// NON-SECRET data only: the seed/passphrase live in BrowserEncryptedSeedVault
// (AES-GCM under the wallet password), and the PIN is stored as a salted PBKDF2 hash
// exactly like Android. Deliberately NOT implemented here: IPersonalNodeSettings
// (carries an RPC password, and the feature is impractical from a browser —
// CORS/mixed-content to a loopback daemon) and IP2PNodeSettings (browsers have no raw
// sockets), so both keep their in-memory defaults and their features stay hidden.

/// <summary>Synchronous localStorage access via JSImport — usable from Program.cs
/// BEFORE the host is built (the gateway failover list must be read at composition
/// time), which IJSRuntime cannot do.</summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserKv
{
    private const string Prefix = "blz_";

    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);
    [JSImport("globalThis.localStorage.setItem")]
    private static partial void SetItem(string key, string value);
    [JSImport("globalThis.localStorage.removeItem")]
    private static partial void RemoveItem(string key);

    public static string Get(string key, string fallback) => GetItem(Prefix + key) ?? fallback;
    public static string? GetOrNull(string key) => GetItem(Prefix + key);
    public static bool Contains(string key) => GetItem(Prefix + key) != null;
    public static void Set(string key, string value) => SetItem(Prefix + key, value);
    public static void Remove(string key) => RemoveItem(Prefix + key);
    /// <summary>Un-prefixed probe, for keys owned by other modules (the vault blob).</summary>
    public static bool RawContains(string fullKey) => GetItem(fullKey) != null;
}

/// <summary>Rotation position + backup flag + verified header checkpoint — the web twin
/// of PreferencesWalletStateStore. This one is CORRECTNESS, not convenience: without it
/// a refresh forgets how many addresses were revealed and coins on later slots vanish
/// from view until a restore rescan.</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserWalletStateStore : IWalletStateStore
{
    public Task<int> GetRevealedAddressCountAsync()
        => Task.FromResult(int.TryParse(BrowserKv.GetOrNull("revealed_address_count"), out var n) ? n : 0);
    public Task SetRevealedAddressCountAsync(int count)
    { BrowserKv.Set("revealed_address_count", count.ToString()); return Task.CompletedTask; }

    public Task<int> GetChangeAddressCountAsync()
        => Task.FromResult(int.TryParse(BrowserKv.GetOrNull("change_address_count"), out var n) ? n : 0);
    public Task SetChangeAddressCountAsync(int count)
    { BrowserKv.Set("change_address_count", count.ToString()); return Task.CompletedTask; }

    public Task<string?> GetVerifiedCheckpointAsync()
        => Task.FromResult(BrowserKv.GetOrNull("verified_header_checkpoint"));
    public Task SetVerifiedCheckpointAsync(string value)
    { BrowserKv.Set("verified_header_checkpoint", value); return Task.CompletedTask; }

    public Task<bool> GetBackupVerifiedAsync()
        => Task.FromResult(!bool.TryParse(BrowserKv.GetOrNull("backup_verified"), out var v) || v);
    public Task SetBackupVerifiedAsync(bool verified)
    { BrowserKv.Set("backup_verified", verified.ToString()); return Task.CompletedTask; }

    public Task ClearAsync()
    {
        BrowserKv.Remove("revealed_address_count");
        BrowserKv.Remove("change_address_count");
        BrowserKv.Remove("verified_header_checkpoint");
        BrowserKv.Remove("backup_verified");
        return Task.CompletedTask;
    }
}

/// <summary>Labels as a JSON map — cached with write-through under a lock, the exact
/// PreferencesLabelStore recipe (Get runs per history row per render).</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserLabelStore : ILabelStore
{
    private const string Key = "labels_json";
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    private Dictionary<string, string> Map()
    {
        lock (_gate)
        {
            return _cache ??=
                JsonSerializer.Deserialize<Dictionary<string, string>>(BrowserKv.Get(Key, "{}")) ?? new();
        }
    }

    public string? Get(string key) => Map().TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? label)
    {
        lock (_gate)
        {
            var d = Map();
            if (string.IsNullOrWhiteSpace(label)) d.Remove(key); else d[key] = label.Trim();
            BrowserKv.Set(Key, JsonSerializer.Serialize(d));
        }
    }

    public IReadOnlyDictionary<string, string> All => Map();
}

/// <summary>Address book as a JSON list — the PreferencesAddressBook semantics.</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserAddressBook : IAddressBook
{
    private const string Key = "addressbook_json";
    private static List<AddressBookEntry> Load() =>
        JsonSerializer.Deserialize<List<AddressBookEntry>>(BrowserKv.Get(Key, "[]")) ?? new();
    private static void Persist(List<AddressBookEntry> e) => BrowserKv.Set(Key, JsonSerializer.Serialize(e));

    public IReadOnlyList<AddressBookEntry> Entries => Load();
    public void Save(string name, string address)
    {
        var addr = address.Trim();
        var list = Load();
        list.RemoveAll(x => x.Address == addr);
        list.Add(new AddressBookEntry(name.Trim(), addr));
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Persist(list);
    }
    public void Remove(string address)
    {
        var list = Load();
        list.RemoveAll(x => x.Address == address.Trim());
        Persist(list);
    }
}

/// <summary>Watch-only list as a JSON list — the PreferencesWatchList semantics.</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserWatchList : IWatchList
{
    private const string Key = "watchlist_json";
    private static List<WatchEntry> Load() =>
        JsonSerializer.Deserialize<List<WatchEntry>>(BrowserKv.Get(Key, "[]")) ?? new();
    private static void Persist(List<WatchEntry> e) => BrowserKv.Set(Key, JsonSerializer.Serialize(e));

    public IReadOnlyList<WatchEntry> Entries => Load();
    public void Add(string label, string address, string kind = "address")
    {
        var addr = address.Trim();
        var list = Load();
        list.RemoveAll(x => x.Address == addr);
        list.Add(new WatchEntry(label.Trim(), addr, kind));
        Persist(list);
    }
    public void Remove(string address)
    {
        var list = Load();
        list.RemoveAll(x => x.Address == address.Trim());
        Persist(list);
    }
}

/// <summary>App-lock PIN — salt + PBKDF2 hash (a hash, not the PIN). On web this is an
/// in-session gate; the at-rest security is the vault password.</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserPinLock : IPinLock
{
    private const string SaltKey = "pin_salt", HashKey = "pin_hash";
    public bool IsSet => BrowserKv.Contains(HashKey);
    public void Set(string pin)
    {
        var (salt, hash) = PinHasher.Hash(pin);
        BrowserKv.Set(SaltKey, salt);
        BrowserKv.Set(HashKey, hash);
    }
    public bool Verify(string pin) =>
        IsSet && PinHasher.Verify(pin, BrowserKv.Get(SaltKey, ""), BrowserKv.Get(HashKey, ""));
    public void Clear()
    {
        BrowserKv.Remove(SaltKey);
        BrowserKv.Remove(HashKey);
    }
}

/// <summary>Display units (display-only).</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserUnitSettings : IUnitSettings
{
    private const string Key = "display_unit";
    public LiteUnit Unit => Enum.TryParse<LiteUnit>(BrowserKv.Get(Key, nameof(LiteUnit.Blz)), out var u)
        ? u : LiteUnit.Blz;
    public void Set(LiteUnit unit) => BrowserKv.Set(Key, unit.ToString());
}

/// <summary>Explorer base URL (display-only; defaults to the production explorer, a saved "" disables links).</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserExplorerSettings : IExplorerSettings
{
    private const string Key = "explorer_url";
    public string BaseUrl => BrowserKv.Get(Key, BlazecoinWallet.Lite.Data.LiteExplorer.DefaultBaseUrl);
    public void Save(string baseUrl) => BrowserKv.Set(Key, baseUrl);
}

/// <summary>Skin choice (cosmetic). No legacy-bool migration here — the web head never
/// shipped the single-skin era, so a fresh profile just gets the shared default.</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserSkinSettings : ISkinSettings
{
    private const string Key = "skin_choice";
    public LiteSkin Skin => Enum.TryParse<LiteSkin>(BrowserKv.Get(Key, ""), out var s) ? s : LiteSkinResolver.Default;
    public void SetSkin(LiteSkin skin) => BrowserKv.Set(Key, skin.ToString());
}

/// <summary>Gateway failover list — the PreferencesGatewaySettings semantics, and the
/// reason BrowserKv is JSImport-synchronous: Program.cs reads Current at composition
/// time, before the host (and any IJSRuntime) exists.</summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserGatewaySettings(string defaultCsv) : IGatewaySettings
{
    private const string Key = "gateway_urls";

    public IReadOnlyList<string> Default => Split(defaultCsv);
    public IReadOnlyList<string> Current => Split(BrowserKv.Get(Key, defaultCsv));
    public bool IsCustom => !Current.SequenceEqual(Default);

    public void Save(IReadOnlyList<string> urls) => BrowserKv.Set(Key, string.Join(';', urls));
    public void ResetToDefault() => BrowserKv.Remove(Key);

    private static string[] Split(string csv) =>
        csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

using System.Text.Json;
using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.Droid;

// Android-persistent versions of the local convenience stores + app-lock, over MAUI
// Preferences (non-secret data as JSON blobs; the PIN as a salted PBKDF2 hash). The seed
// and passphrase live in SecureStorageSeedVault, never here.

/// <summary>Persistent labels (txid/address → note) as a JSON map in Preferences.
/// The map is cached in memory with write-through (audit round-3 C1: Get runs per history
/// row per render — re-parsing the whole blob 25× per refresh is waste), guarded by a lock
/// so concurrent Sets can't lose updates to a load-modify-persist race.</summary>
public sealed class PreferencesLabelStore : ILabelStore
{
    private const string Key = "labels_json";
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    private Dictionary<string, string> Map()
    {
        lock (_gate)
        {
            return _cache ??=
                JsonSerializer.Deserialize<Dictionary<string, string>>(Preferences.Default.Get(Key, "{}")) ?? new();
        }
    }

    public string? Get(string key) => Map().TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? label)
    {
        lock (_gate)
        {
            var d = Map();
            if (string.IsNullOrWhiteSpace(label)) d.Remove(key); else d[key] = label.Trim();
            Preferences.Default.Set(Key, JsonSerializer.Serialize(d));
        }
    }

    public IReadOnlyDictionary<string, string> All => Map();
}

/// <summary>Persistent address book as a JSON list in Preferences.</summary>
public sealed class PreferencesAddressBook : IAddressBook
{
    private const string Key = "addressbook_json";
    private static List<AddressBookEntry> Load() =>
        JsonSerializer.Deserialize<List<AddressBookEntry>>(Preferences.Default.Get(Key, "[]")) ?? new();
    private static void Persist(List<AddressBookEntry> e) => Preferences.Default.Set(Key, JsonSerializer.Serialize(e));

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

/// <summary>Persistent watch-only list as a JSON list in Preferences.</summary>
public sealed class PreferencesWatchList : IWatchList
{
    private const string Key = "watchlist_json";
    private static List<WatchEntry> Load() =>
        JsonSerializer.Deserialize<List<WatchEntry>>(Preferences.Default.Get(Key, "[]")) ?? new();
    private static void Persist(List<WatchEntry> e) => Preferences.Default.Set(Key, JsonSerializer.Serialize(e));

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

/// <summary>Persistent app-lock PIN — salt + PBKDF2 hash in Preferences (a hash, not the PIN).</summary>
public sealed class PreferencesPinLock : IPinLock
{
    private const string SaltKey = "pin_salt", HashKey = "pin_hash";
    public bool IsSet => Preferences.Default.ContainsKey(HashKey);
    public void Set(string pin)
    {
        var (salt, hash) = PinHasher.Hash(pin);
        Preferences.Default.Set(SaltKey, salt);
        Preferences.Default.Set(HashKey, hash);
    }
    public bool Verify(string pin) =>
        IsSet && PinHasher.Verify(pin, Preferences.Default.Get(SaltKey, ""), Preferences.Default.Get(HashKey, ""));
    public void Clear()
    {
        Preferences.Default.Remove(SaltKey);
        Preferences.Default.Remove(HashKey);
    }
}

using System.Text.Json;
using BlazecoinWallet.Core.Services.Storage;

namespace BlazecoinWallet.Core.Services.AddressBook;

/// <summary>Default <see cref="IAddressBookStore"/> — JSON lists over the key/value
/// store, using the same keys and serialization the pages used inline so existing
/// saved data is read/written unchanged.</summary>
public sealed class AddressBookStore : IAddressBookStore
{
    private readonly IKeyValueStore _store;

    public AddressBookStore(IKeyValueStore store) => _store = store;

    private const string AddressBookKey = "blz_addressbook";
    private const string ReceiveKey = "receive_addresses";

    private async Task<List<AddressBookEntry>> LoadAsync(string key)
    {
        try
        {
            var json = await _store.GetAsync(key);
            if (!string.IsNullOrEmpty(json))
                return JsonSerializer.Deserialize<List<AddressBookEntry>>(json) ?? new();
        }
        catch { /* corrupt/missing — start empty */ }
        return new();
    }

    private Task SaveAsync(string key, IEnumerable<AddressBookEntry> entries)
        => _store.SetAsync(key, JsonSerializer.Serialize(entries.ToList()));

    public Task<List<AddressBookEntry>> GetEntriesAsync() => LoadAsync(AddressBookKey);
    public Task SaveEntriesAsync(IEnumerable<AddressBookEntry> entries) => SaveAsync(AddressBookKey, entries);

    public async Task<bool> AddEntryIfNewAsync(AddressBookEntry entry)
    {
        var entries = await GetEntriesAsync();
        if (entries.Any(e => e.Address == entry.Address)) return false;
        entries.Add(entry);
        await SaveEntriesAsync(entries);
        return true;
    }

    public Task<List<AddressBookEntry>> GetReceiveAddressesAsync() => LoadAsync(ReceiveKey);
    public Task SaveReceiveAddressesAsync(IEnumerable<AddressBookEntry> entries) => SaveAsync(ReceiveKey, entries);

    public async Task<int> MergeReceiveAddressesAsync(IEnumerable<AddressBookEntry> fromWallet)
    {
        var existing = await GetReceiveAddressesAsync();
        var known = new HashSet<string>(existing.Select(e => e.Address), StringComparer.Ordinal);
        var added = 0;
        foreach (var e in fromWallet)
        {
            var addr = (e.Address ?? "").Trim();
            if (addr.Length == 0 || !known.Add(addr)) continue;
            existing.Add(new AddressBookEntry { Address = addr, Label = (e.Label ?? "").Trim() });
            added++;
        }
        if (added > 0) await SaveReceiveAddressesAsync(existing);
        return added;
    }
}

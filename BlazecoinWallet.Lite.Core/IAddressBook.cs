namespace BlazecoinWallet.Lite;

/// <summary>A saved payee: a friendly name for a destination address.</summary>
public sealed record AddressBookEntry(string Name, string Address);

/// <summary>
/// Saved payees for the Send picker — name ↔ destination address, persisted per platform.
/// No secrets (destination addresses are public), so a preference blob suffices.
/// </summary>
public interface IAddressBook
{
    IReadOnlyList<AddressBookEntry> Entries { get; }
    /// <summary>Add or update the entry for <paramref name="address"/> (keyed by address).</summary>
    void Save(string name, string address);
    void Remove(string address);
}

/// <summary>Non-persistent default (dev/test); real heads register a persistent one.</summary>
public sealed class InMemoryAddressBook : IAddressBook
{
    private readonly List<AddressBookEntry> _entries = [];
    public IReadOnlyList<AddressBookEntry> Entries => _entries;

    public void Save(string name, string address)
    {
        var addr = address.Trim();
        _entries.RemoveAll(e => e.Address == addr);
        _entries.Add(new AddressBookEntry(name.Trim(), addr));
        _entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public void Remove(string address) => _entries.RemoveAll(e => e.Address == address.Trim());
}

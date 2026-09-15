namespace BlazecoinWallet.Lite;

/// <summary>A watched entry: a label + a Blazecoin address OR an account xpub, monitored
/// read-only. <see cref="Kind"/> is "address" or "xpub".</summary>
public sealed record WatchEntry(string Label, string Address, string Kind = "address")
{
    public bool IsXpub => Kind == "xpub";
}

/// <summary>
/// Watch-only entries — monitor a balance (a cold-storage address, or a whole account via
/// its xpub) with NO keys on the device. Distinct from personal-node mode (which is about
/// the data source): this is just a list of things the wallet shows balances for. Persisted
/// per platform; addresses/xpubs are public, so no secret store needed.
/// </summary>
public interface IWatchList
{
    IReadOnlyList<WatchEntry> Entries { get; }
    /// <summary>Add or update a watched entry (keyed by its address/xpub value).</summary>
    void Add(string label, string address, string kind = "address");
    void Remove(string address);
}

/// <summary>Non-persistent default (dev/test); real heads register a persistent one.</summary>
public sealed class InMemoryWatchList : IWatchList
{
    private readonly List<WatchEntry> _entries = [];
    public IReadOnlyList<WatchEntry> Entries => _entries;

    public void Add(string label, string address, string kind = "address")
    {
        var addr = address.Trim();
        _entries.RemoveAll(e => e.Address == addr);
        _entries.Add(new WatchEntry(label.Trim(), addr, kind));
    }

    public void Remove(string address) => _entries.RemoveAll(e => e.Address == address.Trim());
}

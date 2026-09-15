namespace BlazecoinWallet.Core.Services.AddressBook;

/// <summary>Persistence for the two address+label lists the wallet keeps in
/// localStorage (SOLID audit #5): the shared Address Book (<c>blz_addressbook</c>,
/// used by Address Book / Send / Transactions) and the generated receive addresses
/// (<c>receive_addresses</c>, used by Receive). Both are lists of the same
/// <see cref="AddressBookEntry"/> shape, so they share one store.
///
/// All methods are best-effort: a missing/corrupt store reads back as an empty
/// list and a failed write is swallowed — matching how the pages always treated
/// localStorage directly. Backed by <see cref="Storage.IKeyValueStore"/>.</summary>
public interface IAddressBookStore
{
    /// <summary>The shared Address Book list (<c>blz_addressbook</c>).</summary>
    Task<List<AddressBookEntry>> GetEntriesAsync();
    Task SaveEntriesAsync(IEnumerable<AddressBookEntry> entries);

    /// <summary>Add an entry to the Address Book unless its address is already
    /// saved (regardless of label). Returns true if it was added. Mirrors Send's
    /// dedup-on-save behaviour.</summary>
    Task<bool> AddEntryIfNewAsync(AddressBookEntry entry);

    /// <summary>The generated receive-address list (<c>receive_addresses</c>).</summary>
    Task<List<AddressBookEntry>> GetReceiveAddressesAsync();
    Task SaveReceiveAddressesAsync(IEnumerable<AddressBookEntry> entries);

    /// <summary>"Sync from wallet" (2026-09-04): merge the wallet's own receive addresses into
    /// the local receive list. Addresses not yet listed are added with the wallet's label;
    /// addresses already listed keep their local label (the user may have renamed them here).
    /// Blank/duplicate incoming addresses are ignored. Returns how many were added.</summary>
    Task<int> MergeReceiveAddressesAsync(IEnumerable<AddressBookEntry> fromWallet);
}

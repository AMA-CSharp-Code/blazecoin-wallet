using BlazecoinWallet.Core.Services.AddressBook;

namespace BlazecoinWallet.Core.Tests;

/// <summary>IAddressBookStore over the in-memory store: round-trip, dedup-on-add,
/// and that the address book and receive-address lists use separate keys.</summary>
public class AddressBookStoreTests
{
    private static (AddressBookStore s, FakeKeyValueStore store) Make()
    {
        var store = new FakeKeyValueStore();
        return (new AddressBookStore(store), store);
    }

    [Fact]
    public async Task empty_when_unset()
    {
        var (s, _) = Make();
        Assert.Empty(await s.GetEntriesAsync());
        Assert.Empty(await s.GetReceiveAddressesAsync());
    }

    [Fact]
    public async Task save_and_get_roundtrip()
    {
        var (s, _) = Make();
        await s.SaveEntriesAsync(new[] { new AddressBookEntry { Label = "Alice", Address = "Baaa" } });
        var e = await s.GetEntriesAsync();
        Assert.Single(e);
        Assert.Equal("Alice", e[0].Label);
        Assert.Equal("Baaa", e[0].Address);
    }

    [Fact]
    public async Task add_if_new_dedups_by_address()
    {
        var (s, _) = Make();
        Assert.True(await s.AddEntryIfNewAsync(new AddressBookEntry { Label = "A", Address = "Bx" }));
        Assert.False(await s.AddEntryIfNewAsync(new AddressBookEntry { Label = "different", Address = "Bx" }));
        var e = await s.GetEntriesAsync();
        Assert.Single(e);
        Assert.Equal("A", e[0].Label);  // original kept, duplicate ignored
    }

    [Fact]
    public async Task address_book_and_receive_use_separate_keys()
    {
        var (s, store) = Make();
        await s.SaveEntriesAsync(new[] { new AddressBookEntry { Address = "Bab" } });
        await s.SaveReceiveAddressesAsync(new[] { new AddressBookEntry { Address = "Brecv" } });
        Assert.True(store.Data.ContainsKey("blz_addressbook"));
        Assert.True(store.Data.ContainsKey("receive_addresses"));
        Assert.Equal("Bab", (await s.GetEntriesAsync())[0].Address);
        Assert.Equal("Brecv", (await s.GetReceiveAddressesAsync())[0].Address);
    }
}

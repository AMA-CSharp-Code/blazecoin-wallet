using BlazecoinWallet.Core.Services.AddressBook;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The Receive page lists a LOCAL list of addresses (localStorage), not the wallet — so an address the
/// wallet owns but that was created outside the GUI (RPC, a rebuild script, another machine) was
/// invisible on the page even though it is perfectly good to receive on. Found 2026-09-04 when the
/// payout hot wallet's fixed pay-in address, created by the 08-17 rebuild, could not be seen on the
/// Payout window's Receive page. "Sync from wallet" merges the wallet's labelled receive addresses
/// into the local list: unknown addresses are added with the wallet's label, known ones keep the
/// local label (the user may have renamed it here), and the count added is reported.
/// </summary>
public class ReceiveAddressSyncTests
{
    [Fact]
    public async Task Unknown_wallet_addresses_are_added_with_their_wallet_label()
    {
        var store = new AddressBookStore(new FakeKeyValueStore());
        await store.SaveReceiveAddressesAsync(new[] { new AddressBookEntry { Address = "B-gui-1", Label = "made here" } });

        var added = await store.MergeReceiveAddressesAsync(new[]
        {
            new AddressBookEntry { Address = "Bs5A1PkZqhUWg3VWS57bmtxucYjy2oYGkE", Label = "payout-hot-main" },
            new AddressBookEntry { Address = "BrHvf9oBBGYzWXgqFNALfqMJrNxYQbWMBV", Label = "faucet-return" },
        });

        Assert.Equal(2, added);
        var list = await store.GetReceiveAddressesAsync();
        Assert.Equal(3, list.Count);
        Assert.Equal("made here", list.Single(e => e.Address == "B-gui-1").Label);
        Assert.Equal("payout-hot-main", list.Single(e => e.Address == "Bs5A1PkZqhUWg3VWS57bmtxucYjy2oYGkE").Label);
    }

    [Fact]
    public async Task Known_addresses_keep_their_local_label_and_are_not_duplicated()
    {
        var store = new AddressBookStore(new FakeKeyValueStore());
        await store.SaveReceiveAddressesAsync(new[] { new AddressBookEntry { Address = "B-1", Label = "my rename" } });

        var added = await store.MergeReceiveAddressesAsync(new[] { new AddressBookEntry { Address = "B-1", Label = "wallet label" } });

        Assert.Equal(0, added);
        var list = await store.GetReceiveAddressesAsync();
        Assert.Single(list);
        Assert.Equal("my rename", list[0].Label);
    }

    [Fact]
    public async Task Blank_and_duplicate_incoming_addresses_are_ignored()
    {
        var store = new AddressBookStore(new FakeKeyValueStore());
        var added = await store.MergeReceiveAddressesAsync(new[]
        {
            new AddressBookEntry { Address = "", Label = "x" },
            new AddressBookEntry { Address = "  ", Label = "y" },
            new AddressBookEntry { Address = "B-2", Label = "a" },
            new AddressBookEntry { Address = "B-2", Label = "b" },
        });
        Assert.Equal(1, added);
        Assert.Single(await store.GetReceiveAddressesAsync());
    }

    [Fact]
    public async Task Merging_nothing_leaves_the_store_untouched()
    {
        var kv = new FakeKeyValueStore();
        var store = new AddressBookStore(kv);
        var added = await store.MergeReceiveAddressesAsync(Array.Empty<AddressBookEntry>());
        Assert.Equal(0, added);
        Assert.Empty(await store.GetReceiveAddressesAsync());
    }
}

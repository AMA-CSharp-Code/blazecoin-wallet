namespace BlazecoinWallet.Core.Services.AddressBook;

/// <summary>A saved address + its label. Single shared model replacing the four
/// near-identical private classes that Send/Receive/AddressBook/Transactions each
/// declared (SOLID audit #5). The JSON property names (Label/Address) match what
/// those pages serialized, so existing localStorage data round-trips unchanged.</summary>
public sealed class AddressBookEntry
{
    public string Label { get; set; } = "";
    public string Address { get; set; } = "";
}

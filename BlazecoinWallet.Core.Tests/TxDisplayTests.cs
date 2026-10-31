using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Tests for <see cref="TxDisplay"/> — the shared category label + colour-class helper
/// used by both the Dashboard "Recent Transactions" table and the full Transactions page, so they
/// label/colour every tx category identically.</summary>
public class TxDisplayTests
{
    [Theory]
    [InlineData("generate", "Mined")]
    [InlineData("GENERATE", "Mined")]   // case-insensitive
    [InlineData("send", "send")]
    [InlineData("receive", "receive")]
    [InlineData(null, "")]
    public void TypeLabel_maps_generate_to_Mined_and_passes_others_through(string? category, string expected)
        => Assert.Equal(expected, TxDisplay.TypeLabel(category));

    [Theory]
    [InlineData("send", "send-cell")]
    [InlineData("receive", "receive-cell")]
    [InlineData("generate", "mined-cell")]
    [InlineData("immature", "immature-cell")]
    [InlineData("orphan", "orphan-cell")]
    [InlineData("Send", "send-cell")]       // case-insensitive
    [InlineData("weird", "")]               // unknown -> default
    [InlineData(null, "")]
    public void ColourClass_maps_each_category(string? category, string expected)
        => Assert.Equal(expected, TxDisplay.ColourClass(category));
}

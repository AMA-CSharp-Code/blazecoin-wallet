using BlazecoinWallet.Core.Services.Settings;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The Provenance page's "ask the explorer automatically" preference (2026-10-30). It must
/// default OFF — asking sends the wallet's addresses to the explorer — round-trip, and be
/// cleared by the Preferences reset like every other key.
/// </summary>
public class ProvenanceAutoExplorerSettingTests
{
    [Fact]
    public async Task Defaults_off_round_trips_and_resets()
    {
        var store = new FakeKeyValueStore();
        var s = new SettingsService(store);

        Assert.False(await s.GetProvenanceAutoExplorerAsync());

        await s.SetProvenanceAutoExplorerAsync(true);
        Assert.True(await s.GetProvenanceAutoExplorerAsync());
        Assert.Equal("true", store.Data["provenance_auto_explorer"]);

        await s.SetProvenanceAutoExplorerAsync(false);
        Assert.False(await s.GetProvenanceAutoExplorerAsync());

        await s.SetProvenanceAutoExplorerAsync(true);
        await s.ResetAsync();
        Assert.False(await s.GetProvenanceAutoExplorerAsync());
        Assert.False(store.Data.ContainsKey("provenance_auto_explorer"));
    }

    [Fact]
    public async Task Garbage_in_the_store_reads_as_off()
    {
        var store = new FakeKeyValueStore();
        store.Data["provenance_auto_explorer"] = "yes please";
        Assert.False(await new SettingsService(store).GetProvenanceAutoExplorerAsync());
    }
}

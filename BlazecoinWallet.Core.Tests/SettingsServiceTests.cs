using BlazecoinWallet.Core.Services.Settings;

namespace BlazecoinWallet.Core.Tests;

/// <summary>ISettingsService over the in-memory store: validated getters return
/// null on missing/invalid, setters round-trip, reset clears, defaults hold.</summary>
public class SettingsServiceTests
{
    private static (SettingsService s, FakeKeyValueStore store) Make()
    {
        var store = new FakeKeyValueStore();
        return (new SettingsService(store), store);
    }

    [Fact]
    public async Task getters_return_null_when_unset()
    {
        var (s, _) = Make();
        Assert.Null(await s.GetConfThresholdAsync());
        Assert.Null(await s.GetAmountUnitAsync());
        Assert.Null(await s.GetMiningThreadsAsync());
    }

    [Fact]
    public async Task set_then_get_roundtrips()
    {
        var (s, _) = Make();
        await s.SetConfThresholdAsync(12);
        await s.SetAmountUnitAsync("mBLZ");
        Assert.Equal(12, await s.GetConfThresholdAsync());
        Assert.Equal("mBLZ", await s.GetAmountUnitAsync());
    }

    [Fact]
    public async Task invalid_or_out_of_range_values_return_null()
    {
        var (s, store) = Make();
        store.Data["conf_threshold"] = "notanumber";
        Assert.Null(await s.GetConfThresholdAsync());
        store.Data["conf_threshold"] = "0";        // must be > 0
        Assert.Null(await s.GetConfThresholdAsync());
        store.Data["amount_unit"] = "BTC";          // only BLZ / mBLZ are valid
        Assert.Null(await s.GetAmountUnitAsync());
    }

    [Fact]
    public async Task always_resolvable_defaults()
    {
        var (s, _) = Make();
        Assert.True(await s.GetConfirmSendAsync());      // default true
        Assert.Equal(SettingsService.DefaultExplorerUrl, await s.GetExplorerUrlAsync()); // fresh profile → the apex explorer (2026-09-05)
        Assert.StartsWith("https://blazecoin.co.uk", SettingsService.DefaultExplorerUrl);
    }

    [Fact]
    public async Task dashboard_skin_migrates_legacy_typo()
    {
        var (s, store) = Make();
        store.Data["dashboard_skin"] = "pheonix";
        Assert.Equal("phoenix", await s.GetDashboardSkinAsync());
    }

    [Fact]
    public async Task reset_clears_all_keys()
    {
        var (s, _) = Make();
        await s.SetConfThresholdAsync(5);
        await s.SetAmountUnitAsync("BLZ");
        await s.ResetAsync();
        Assert.Null(await s.GetConfThresholdAsync());
        Assert.Null(await s.GetAmountUnitAsync());
    }

    [Fact]
    public async Task a_saved_empty_explorer_url_means_no_links_not_the_default()
    {
        var (s, _) = Make();
        await s.SetExplorerUrlAsync("");
        Assert.Equal("", await s.GetExplorerUrlAsync());
    }
}

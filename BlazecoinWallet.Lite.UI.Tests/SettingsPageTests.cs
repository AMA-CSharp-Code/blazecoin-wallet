using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The advanced Settings page: the gateway is shown read-only by default, the editor is
/// hidden behind "Advanced…", a save validates + persists (an insecure URL is refused), and
/// reset restores the official default.
/// </summary>
public class SettingsPageTests : TestContext
{
    private InMemoryPersonalNodeSettings _node = new();
    private InMemoryP2PNodeSettings _p2p = new(["85.15.179.171:55414"]);
    private InMemoryUnitSettings _units = new();
    private InMemoryExplorerSettings _explorer = new();

    private InMemorySkinSettings _skin = new();

    private readonly InMemoryAutoLockSettings _autoLock = new();

    private InMemoryGatewaySettings Wire()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; // the coin-background toggle imports a JS module
        var settings = new InMemoryGatewaySettings(["https://blazecoin.co.uk/"]);
        Services.AddSingleton<IGatewaySettings>(settings);
        Services.AddSingleton<IPersonalNodeSettings>(_node);
        Services.AddSingleton<IP2PNodeSettings>(_p2p);
        Services.AddSingleton<IUnitSettings>(_units);
        Services.AddSingleton<IExplorerSettings>(_explorer);
        Services.AddSingleton<ISkinSettings>(_skin);
        Services.AddSingleton<IPinLock, InMemoryPinLock>();
        Services.AddSingleton<IAutoLockSettings>(_autoLock);
        // No-feed header sync (Available == false) — the Chain Verification card stays hidden.
        Services.AddSingleton(new BlazecoinWallet.Lite.Data.HeaderChainSync());
        return settings;
    }

    /// <summary>An IHeaderReader that serves nothing — enough to make HeaderChainSync.Available
    /// true so the Chain Verification card renders at the shipped checkpoint height.</summary>
    private sealed class EmptyHeaderReader : BlazecoinWallet.Lite.Data.IHeaderReader
    {
        public Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public void Chain_verification_card_shows_the_verified_height_when_a_feed_is_present()
    {
        Wire();
        // Last registration wins — replace the no-feed default with an available (if idle) sync.
        Services.AddSingleton(new BlazecoinWallet.Lite.Data.HeaderChainSync(new EmptyHeaderReader()));
        var cut = RenderComponent<Settings>();

        Assert.Contains("Chain Verification", cut.Markup);
        Assert.Contains("Verified to block", cut.Markup);
        // The shipped checkpoint (no headers to advance past it) — read from the constant rather
        // than pinned, so the per-release checkpoint refresh can't silently break this test (it
        // did on 2026-08-04: the anchor moved 4,149,840 → 4,164,000 and the pin went stale).
        Assert.Contains(
            BlazecoinWallet.Lite.Data.HeaderChainSync.MainnetCheckpoint.Height.ToString("N0"),
            cut.Markup);
    }

    [Fact]
    public void The_editor_is_hidden_until_advanced_is_revealed()
    {
        Wire();
        var cut = RenderComponent<Settings>();

        Assert.Contains("https://blazecoin.co.uk/", cut.Markup); // shown read-only
        Assert.DoesNotContain("Chain Verification", cut.Markup); // no feed → card hidden
        Assert.Empty(cut.FindAll("textarea"));                   // no editor yet

        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();
        Assert.NotEmpty(cut.FindAll("textarea")); // gateway + P2P editors now visible
        Assert.Contains("Only change this if you run your own node", cut.Markup);
    }

    [Fact]
    public void Skin_choice_select_reflects_and_persists_the_setting()
    {
        _skin.SetSkin(LiteSkin.PixelatedCoins);
        Wire();
        var cut = RenderComponent<Settings>();

        var select = cut.Find("#skin-select");
        Assert.Equal(nameof(LiteSkin.PixelatedCoins), select.GetAttribute("value")); // starts on the default

        select.Change(nameof(LiteSkin.AxePattern));
        Assert.Equal(LiteSkin.AxePattern, _skin.Skin);   // switching skins persists

        select.Change(nameof(LiteSkin.DigitalFireFighters));
        Assert.Equal(LiteSkin.DigitalFireFighters, _skin.Skin);

        select.Change(nameof(LiteSkin.Random));
        Assert.Equal(LiteSkin.Random, _skin.Skin);       // Random persists as Random (re-rolls per launch)

        select.Change(nameof(LiteSkin.None));
        Assert.Equal(LiteSkin.None, _skin.Skin);         // and so does turning it off
    }

    [Fact]
    public void Saving_a_valid_gateway_persists_it_and_prompts_a_restart()
    {
        var settings = Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();

        cut.Find("textarea").Change("https://my.gateway/");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        Assert.Equal(["https://my.gateway/"], settings.Current);
        Assert.Contains("Restart the wallet", cut.Markup);
    }

    [Fact]
    public void Saving_an_insecure_gateway_is_refused_and_nothing_persists()
    {
        var settings = Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();

        cut.Find("textarea").Change("http://evil.gateway/");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        Assert.Contains("must use https", cut.Markup);
        Assert.False(settings.IsCustom); // unchanged
    }

    [Fact]
    public void Reset_restores_the_official_default()
    {
        var settings = Wire();
        settings.Save(["https://my.gateway/"]);
        var cut = RenderComponent<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();

        cut.FindAll("button").First(b => b.TextContent.Contains("Reset")).Click();

        Assert.False(settings.IsCustom);
        Assert.Equal(["https://blazecoin.co.uk/"], settings.Current);
    }

    [Fact]
    public void Enabling_personal_node_saves_a_valid_rpc_connection()
    {
        Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();

        // Tick "Connect via my own node" → the RPC fields appear.
        cut.FindAll("input[type=checkbox]")[0].Change(true); // personal node (the skin choice is a <select> now)
        cut.FindAll("input").First(i => i.GetAttribute("placeholder")?.Contains("55413") == true).Change("http://127.0.0.1:55413/");
        cut.FindAll("input").First(i => i.GetAttribute("placeholder") == "rpcauth user").Change("me");
        cut.FindAll("input").First(i => i.GetAttribute("type") == "password").Change("secret");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save node")).Click();

        Assert.True(_node.Enabled);
        Assert.Equal("http://127.0.0.1:55413/", _node.Config!.RpcUrl);
        Assert.Equal("me", _node.Config.RpcUser);
        Assert.Contains("connect through your node", cut.Markup);
    }

    [Fact]
    public void A_cleartext_off_box_node_rpc_is_refused()
    {
        Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();
        cut.FindAll("input[type=checkbox]")[0].Change(true); // personal node (the skin choice is a <select> now)
        cut.FindAll("input").First(i => i.GetAttribute("placeholder")?.Contains("55413") == true).Change("http://192.168.1.5:55413/");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save node")).Click();

        Assert.Contains("must use https", cut.Markup);
        Assert.False(_node.Enabled);
    }

    [Fact]
    public void Saving_valid_p2p_peers_persists_them()
    {
        Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();

        cut.FindAll("textarea").First(t => t.GetAttribute("placeholder")?.Contains("55414") == true)
            .Change("1.2.3.4:55414\n5.6.7.8:55414");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save peers").Click();

        Assert.Equal(["1.2.3.4:55414", "5.6.7.8:55414"], _p2p.Current);
        Assert.Contains("use the new peers", cut.Markup);
    }

    [Fact]
    public void Changing_the_display_unit_persists_immediately()
    {
        Wire();
        var cut = RenderComponent<Settings>();

        cut.Find("select").Change(nameof(LiteUnit.Sat));

        Assert.Equal(LiteUnit.Sat, _units.Unit); // applies at once, no restart
    }

    [Fact]
    public void Saving_a_valid_explorer_url_persists_it()
    {
        Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("input").First(i => i.GetAttribute("placeholder")?.Contains("explorer") == true)
            .Change("https://explorer.blazecoin.co.uk/");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save explorer")).Click();

        Assert.Equal("https://explorer.blazecoin.co.uk/", _explorer.BaseUrl);
    }

    [Fact]
    public void An_insecure_explorer_url_is_refused()
    {
        Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("input").First(i => i.GetAttribute("placeholder")?.Contains("explorer") == true)
            .Change("http://evil.explorer/");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save explorer")).Click();

        Assert.Contains("must use https", cut.Markup);
        Assert.Equal(LiteExplorer.DefaultBaseUrl, _explorer.BaseUrl);   // untouched — still the default (2026-09-05)
    }

    [Fact]
    public void The_about_card_shows_the_network()
    {
        Wire();
        var cut = RenderComponent<Settings>();
        Assert.Contains("About", cut.Markup);
        Assert.Contains("Mainnet", cut.Markup);
        Assert.Contains("Legacy P2PKH", cut.Markup);
    }

    [Fact]
    public void A_malformed_p2p_endpoint_is_refused()
    {
        Wire();
        var cut = RenderComponent<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Advanced")).Click();

        cut.FindAll("textarea").First(t => t.GetAttribute("placeholder")?.Contains("55414") == true)
            .Change("not-an-endpoint");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save peers").Click();

        Assert.Contains("host:port", cut.Markup);
        Assert.False(_p2p.IsCustom);
    }
}

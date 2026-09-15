using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>The gateway URL validator + the in-memory settings store (save / reset / custom).</summary>
public class GatewaySettingsTests
{
    [Theory]
    [InlineData("https://gateway.example/")]
    [InlineData("https://gateway.example")]          // trailing slash added
    [InlineData("http://localhost:7002/")]           // loopback http allowed
    [InlineData("http://127.0.0.1:7002")]
    public void Valid_urls_normalise(string url)
    {
        var uri = LiteGatewayUrls.Normalize(url);
        Assert.True(uri.AbsoluteUri.EndsWith('/'));
    }

    [Theory]
    [InlineData("http://gateway.example/")]           // cleartext off-box — MITM risk
    [InlineData("ftp://gateway.example/")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Invalid_or_insecure_urls_throw(string url)
        => Assert.Throws<ArgumentException>(() => LiteGatewayUrls.Normalize(url));

    [Fact]
    public void An_empty_list_is_rejected()
        => Assert.Throws<ArgumentException>(() => LiteGatewayUrls.NormalizeList([" ", ""]));

    [Theory]
    [InlineData("http://127.0.0.1:55413/")]   // loopback http — the normal case
    [InlineData("https://mynode.example/")]   // https off-box
    public void Valid_node_rpc_urls_normalise(string url)
        => Assert.NotNull(LitePersonalNode.NormalizeRpcUrl(url));

    [Fact]
    public void Off_box_cleartext_node_rpc_is_rejected()
        => Assert.Throws<ArgumentException>(() => LitePersonalNode.NormalizeRpcUrl("http://192.168.1.5:55413/"));

    [Fact]
    public void Personal_node_settings_enable_and_disable()
    {
        var s = new InMemoryPersonalNodeSettings();
        Assert.False(s.Enabled);
        Assert.Null(s.Config);

        s.Enable(new PersonalNodeOptions("http://127.0.0.1:55413/", RpcUser: "u", RpcPassword: "p"));
        Assert.True(s.Enabled);
        Assert.Equal("http://127.0.0.1:55413/", s.Config!.RpcUrl);

        s.Disable();
        Assert.False(s.Enabled);
        Assert.Null(s.Config);
    }

    [Theory]
    [InlineData("85.15.179.171:55414", "85.15.179.171:55414")]
    [InlineData(" seed.example.com:55414 ", "seed.example.com:55414")]  // trimmed
    public void Valid_p2p_endpoints_normalise(string input, string expected)
        => Assert.Equal(expected, LiteP2PNodes.Normalize(input));

    [Theory]
    [InlineData("85.15.179.171")]        // no port
    [InlineData("host:")]                // empty port
    [InlineData("host:99999")]           // port out of range
    [InlineData("host:abc")]             // non-numeric port
    [InlineData("")]
    public void Malformed_p2p_endpoints_throw(string endpoint)
        => Assert.Throws<ArgumentException>(() => LiteP2PNodes.Normalize(endpoint));

    [Fact]
    public void Explorer_base_url_normalises_and_builds_links()
    {
        Assert.Equal("", LiteExplorer.NormalizeBaseUrl(null));        // empty = disabled
        Assert.Equal("", LiteExplorer.NormalizeBaseUrl("  "));
        var b = LiteExplorer.NormalizeBaseUrl("https://ex.blazecoin/"); // trailing slash kept
        Assert.Equal("https://ex.blazecoin/tx/abc", LiteExplorer.TxUrl(b, "abc"));
        Assert.Equal("https://ex.blazecoin/address/Bxyz", LiteExplorer.AddressUrl(b, "Bxyz"));
        Assert.Null(LiteExplorer.TxUrl("", "abc"));                    // no explorer → no link
    }

    [Fact]
    public void An_insecure_explorer_url_is_rejected()
        => Assert.Throws<ArgumentException>(() => LiteExplorer.NormalizeBaseUrl("http://evil.explorer/"));

    [Fact]
    public void P2p_settings_track_custom_and_reset()
    {
        var s = new InMemoryP2PNodeSettings(["85.15.179.171:55414"]);
        Assert.False(s.IsCustom);

        s.Save(["1.2.3.4:55414"]);
        Assert.True(s.IsCustom);
        Assert.Equal(["1.2.3.4:55414"], s.Current);

        s.ResetToDefault();
        Assert.False(s.IsCustom);
        Assert.Equal(["85.15.179.171:55414"], s.Current);
    }

    [Fact]
    public void Settings_start_at_default_and_track_custom_vs_reset()
    {
        var s = new InMemoryGatewaySettings(["https://blazecoin.co.uk/"]);
        Assert.False(s.IsCustom);
        Assert.Equal(["https://blazecoin.co.uk/"], s.Current);

        s.Save(["https://my.gateway/"]);
        Assert.True(s.IsCustom);
        Assert.Equal(["https://my.gateway/"], s.Current);

        s.ResetToDefault();
        Assert.False(s.IsCustom);
        Assert.Equal(["https://blazecoin.co.uk/"], s.Current);
    }

    [Fact]
    public void Default_explorer_is_the_apex_permalink_root_and_builds_the_site_routes()
    {
        // 2026-09-05: fresh profiles link to the production explorer; the MVC site's permalinks are /tx, /address.
        Assert.Equal("https://blazecoin.co.uk/", LiteExplorer.DefaultBaseUrl);
        Assert.Equal(LiteExplorer.DefaultBaseUrl, LiteExplorer.NormalizeBaseUrl(LiteExplorer.DefaultBaseUrl)); // already normal
        Assert.Equal("https://blazecoin.co.uk/tx/abc", LiteExplorer.TxUrl(LiteExplorer.DefaultBaseUrl, "abc"));
        Assert.Equal("https://blazecoin.co.uk/address/Bxyz", LiteExplorer.AddressUrl(LiteExplorer.DefaultBaseUrl, "Bxyz"));
        Assert.Equal(LiteExplorer.DefaultBaseUrl, new InMemoryExplorerSettings().BaseUrl);
    }
}

using BlazecoinWallet.Lite.Gateway.Index;
using BlazecoinWallet.Lite.Gateway.Rpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.Gateway.Tests;

/// <summary>
/// The CORS contract for the WASM web head: the failover partner's origin (and any
/// localhost dev head) may read this anonymous API from a browser; arbitrary foreign
/// pages may not. The API is cookieless, so this is purely a read-permission gate.
/// </summary>
public class CorsContractTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-cors-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public CorsContractTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Index:DbPath", _dbPath);
            builder.ConfigureServices(services =>
            {
                var walker = services.Single(d => d.ImplementationType == typeof(ChainWalkerService));
                services.Remove(walker);
                var rpc = services.Single(d => d.ServiceType == typeof(DaemonRpcClient));
                services.Remove(rpc);
                services.AddSingleton<DaemonRpcClient>(new FakeDaemonRpc());
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<HttpResponseMessage> GetStatusWithOriginAsync(string origin)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task The_failover_partners_origin_gets_cors_headers()
    {
        var response = await GetStatusWithOriginAsync("https://54-39-23-245.sslip.io");

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal("https://54-39-23-245.sslip.io", Assert.Single(values!));
    }

    [Fact]
    public async Task Localhost_dev_heads_get_cors_headers_on_any_port()
    {
        var response = await GetStatusWithOriginAsync("https://localhost:7258");

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal("https://localhost:7258", Assert.Single(values!));
    }

    [Fact]
    public async Task Arbitrary_foreign_origins_get_no_cors_headers()
    {
        var response = await GetStatusWithOriginAsync("https://evil.example");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Broadcast_preflight_is_approved_for_an_allowed_origin()
    {
        // The browser preflights POST+json before the wallet's broadcast can fire.
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/tx/broadcast");
        request.Headers.Add("Origin", "https://51-210-47-141.sslip.io");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Equal("https://51-210-47-141.sslip.io", Assert.Single(origins!));
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }
}

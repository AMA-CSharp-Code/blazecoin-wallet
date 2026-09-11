using System.Net;
using System.Text;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The client-side gateway failover contract (Electrum model): dead gateways are skipped,
/// the last good one is remembered (sticky), request bodies survive the retry intact, an
/// HTTP error response is an ANSWER (never a failover trigger), and when every gateway is
/// down the delivery failure propagates.
/// </summary>
public class GatewayFailoverHandlerTests
{
    /// <summary>Scripted inner transport: per-host behavior + call/body capture.</summary>
    private sealed class ScriptedTransport : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Hosts = new();
        public readonly List<string> Calls = [];
        public string? LastBodySeen;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.Host);
            if (request.Content != null)
                LastBodySeen = await request.Content.ReadAsStringAsync(ct);
            return Hosts.TryGetValue(request.RequestUri.Host, out var f)
                ? f()
                : throw new HttpRequestException($"no route to {request.RequestUri.Host}");
        }
    }

    private static HttpClient Client(ScriptedTransport transport, params string[] gateways)
    {
        var failover = new GatewayFailoverHandler(gateways, transport);
        return new HttpClient(failover) { BaseAddress = failover.Primary };
    }

    [Fact]
    public async Task Dead_primary_fails_over_and_the_survivor_becomes_sticky()
    {
        var transport = new ScriptedTransport();
        transport.Hosts["b.test"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("from-b") };
        var client = Client(transport, "https://a.test/", "https://b.test/");

        var first = await client.GetStringAsync("indexer/api/x");
        Assert.Equal("from-b", first);
        Assert.Equal(new[] { "a.test", "b.test" }, transport.Calls);

        transport.Calls.Clear();
        await client.GetStringAsync("indexer/api/y");
        Assert.Equal(new[] { "b.test" }, transport.Calls); // sticky — no re-probe of the dead one
    }

    [Fact]
    public async Task Post_body_survives_the_failover_retry()
    {
        var transport = new ScriptedTransport();
        transport.Hosts["b.test"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{}") };
        var client = Client(transport, "https://a.test/", "https://b.test/");

        var res = await client.PostAsync("api/tx/broadcast",
            new StringContent("{\"hex\":\"cafe\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("{\"hex\":\"cafe\"}", transport.LastBodySeen);
    }

    [Fact]
    public async Task An_http_error_is_an_answer_not_an_outage()
    {
        var transport = new ScriptedTransport();
        transport.Hosts["a.test"] = () => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        transport.Hosts["b.test"] = () => new HttpResponseMessage(HttpStatusCode.OK);
        var client = Client(transport, "https://a.test/", "https://b.test/");

        var res = await client.GetAsync("api/tx/broadcast");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode); // a's verdict stands
        Assert.Equal(new[] { "a.test" }, transport.Calls);                // b never consulted
    }

    [Theory]
    [InlineData("http://gateway.example.com/")]       // cleartext off-box — MITM risk
    [InlineData("ftp://gateway.example.com/")]         // wrong scheme entirely
    public void A_non_https_off_box_gateway_is_rejected(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            new GatewayFailoverHandler([url], new HttpClientHandler()));
    }

    [Theory]
    [InlineData("https://gateway.example.com/")]       // https anywhere
    [InlineData("http://localhost:7002/")]             // loopback http (dev / emulator)
    [InlineData("http://127.0.0.1:7002/")]
    public void Https_or_loopback_gateways_are_accepted(string url)
    {
        var ex = Record.Exception(() => new GatewayFailoverHandler([url], new HttpClientHandler()));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Every_gateway_down_propagates_the_delivery_failure()
    {
        var transport = new ScriptedTransport();
        var client = Client(transport, "https://a.test/", "https://b.test/");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("indexer/api/x"));
        Assert.Equal(new[] { "a.test", "b.test" }, transport.Calls); // both were tried
    }
}

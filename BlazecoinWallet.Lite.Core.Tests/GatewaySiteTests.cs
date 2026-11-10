using System.Net;
using System.Text;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Audit 2026-11-09: "another gateway" must mean another SERVER. The shipped list names each of
/// the project's two gateways twice (a vanity domain and an sslip.io fallback), and every second
/// opinion in the wallet excluded only the one URL that had answered — so with the other server
/// down, the same server was asked again under its other name and its answer counted as
/// independent: a broadcast read as backed, a header run as confirmed.
/// </summary>
public class GatewaySiteTests
{
    private const string Strasbourg = "https://gw.blazecoin.co.uk/";
    private const string StrasbourgOtherName = "https://51-210-47-141.sslip.io/";
    private const string Beauharnois = "https://gw2.blazecoin.co.uk/";
    private const string BeauharnoisOtherName = "https://54-39-23-245.sslip.io/";
    private static readonly string[] Shipped = [Strasbourg, Beauharnois, StrasbourgOtherName, BeauharnoisOtherName];

    /// <summary>The wire: a host that is not scripted is unreachable.</summary>
    private sealed class Wire : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Hosts = new();
        public readonly List<string> Calls = [];

        public void Up(string url, Func<HttpRequestMessage, HttpResponseMessage> answer) => Hosts[new Uri(url).Host] = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Calls) Calls.Add(request.RequestUri!.Host);
            return Hosts.TryGetValue(request.RequestUri.Host, out var answer)
                ? Task.FromResult(answer(request))
                : throw new HttpRequestException($"no route to {request.RequestUri.Host}");
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Wire wire, params string[] gateways)
    {
        var failover = new GatewayFailoverHandler(gateways, wire);
        return new HttpClient(failover) { BaseAddress = failover.Primary };
    }

    private static IndexerDataService Indexer(Wire wire, params string[] gateways) =>
        new(Client(wire, gateways), NullLogger<IndexerDataService>.Instance);

    /// <summary>Strasbourg answers under both of its names; Beauharnois is down.</summary>
    private static Wire OnlyStrasbourgUp(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var wire = new Wire();
        wire.Up(Strasbourg, answer);
        wire.Up(StrasbourgOtherName, answer);
        return wire;
    }

    // ── the notion itself ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Strasbourg, StrasbourgOtherName, true)]
    [InlineData(Beauharnois, BeauharnoisOtherName, true)]
    [InlineData(Strasbourg, Beauharnois, false)]
    [InlineData(StrasbourgOtherName, BeauharnoisOtherName, false)]
    [InlineData("https://GW.blazecoin.co.uk:8443/", StrasbourgOtherName, true)]     // a port or capitals do not make another server
    [InlineData("https://my-gateway.example/", "https://my-gateway.example/api/", true)]
    [InlineData("https://my-gateway.example/", "https://other.example/", false)]    // a gateway the user added is its own site
    [InlineData("https://my-gateway.example/", Strasbourg, false)]
    [InlineData("http://localhost:7002/", "http://localhost:7003/", false)]         // two dev gateways on one box are two gateways
    public void Two_urls_are_one_site_only_when_they_reach_the_same_server(string a, string b, bool same)
    {
        Assert.Equal(same, GatewaySite.Same(new Uri(a), new Uri(b)));
        Assert.Equal(same, GatewaySite.Same(new Uri(b), new Uri(a)));
    }

    [Fact]
    public void A_gateway_nobody_named_is_the_same_as_nothing()
    {
        Assert.False(GatewaySite.Same(null, new Uri(Strasbourg)));
        Assert.False(GatewaySite.Same(null, null));
        Assert.False(GatewaySite.SameAsAny(new Uri(Strasbourg), null));
    }

    [Fact]
    public void The_shipped_list_is_two_servers_and_every_name_on_it_is_known()
    {
        var shipped = new LiteWalletOptions().GatewayUrls.Select(u => new Uri(u)).ToList();

        Assert.All(shipped, u => Assert.True(GatewaySite.IsShippedName(u), $"{u} is shipped but GatewaySite does not know whose it is"));
        Assert.Equal(2, shipped.Select(GatewaySite.Of).Distinct().Count());
    }

    /// <summary>The heads keep their own copy of the default list in projects this suite cannot
    /// load, so it is pinned at the source: a name added there must be added to <see cref="GatewaySite"/>.</summary>
    [Theory]
    [InlineData("BlazecoinWallet.App.Droid/MauiProgram.cs")]
    [InlineData("BlazecoinWallet.App.iOS/MauiProgram.cs")]
    [InlineData("BlazecoinWallet.Web.Wasm/Program.cs")]
    public void Every_gateway_name_a_head_ships_is_known(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "BlazecoinWallet.Lite.Core"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!.FullName, relativePath));

        var lists = System.Text.RegularExpressions.Regex.Matches(source, "\"(https://[^\"]*blazecoin\\.co\\.uk[^\"]*)\"");
        Assert.NotEmpty(lists);
        foreach (System.Text.RegularExpressions.Match list in lists)
        {
            var urls = list.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(u => new Uri(u)).ToList();
            Assert.All(urls, u => Assert.True(GatewaySite.IsShippedName(u), $"{u} in {relativePath} is not known to GatewaySite"));
            Assert.Equal(2, urls.Select(GatewaySite.Of).Distinct().Count());
        }
    }

    // ── the failover handler ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_opinion_is_never_taken_from_the_same_server_under_its_other_name()
    {
        var wire = OnlyStrasbourgUp(_ => Json("{}"));
        var client = Client(wire, Shipped);

        using var plain = new HttpRequestMessage(HttpMethod.Get, "indexer/api/x");
        await client.SendAsync(plain);
        Assert.True(plain.Options.TryGetValue(GatewayRouting.AnsweredBy, out var who));
        Assert.Equal(new Uri(Strasbourg), who);

        wire.Calls.Clear();
        using var steered = new HttpRequestMessage(HttpMethod.Get, "indexer/api/x");
        steered.Options.Set(GatewayRouting.Exclude, [who!]);

        // The other server did not answer: that is a delivery failure, not "nobody else is configured"…
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(steered));
        Assert.IsNotType<NoOtherGatewayException>(failure);
        // …and Strasbourg's sslip.io name was not asked (it answered, and was believed, before).
        Assert.Equal(["gw2.blazecoin.co.uk", "54-39-23-245.sslip.io"], wire.Calls);
    }

    [Fact]
    public async Task One_server_configured_under_two_names_has_nobody_else_to_ask()
    {
        var wire = OnlyStrasbourgUp(_ => Json("{}"));
        var client = Client(wire, Strasbourg, StrasbourgOtherName);

        using var steered = new HttpRequestMessage(HttpMethod.Get, "indexer/api/x");
        steered.Options.Set(GatewayRouting.Exclude, [new Uri(Strasbourg)]);

        await Assert.ThrowsAsync<NoOtherGatewayException>(() => client.SendAsync(steered));
        Assert.Empty(wire.Calls);
    }

    [Fact]
    public async Task Ordinary_failover_still_uses_a_servers_other_name()
    {
        // No exclusion: the vanity name is unreachable (a DNS problem, say) and the same server's
        // sslip.io name is what the fallback list is for.
        var wire = new Wire();
        wire.Up(StrasbourgOtherName, _ => Json("{}"));
        var client = Client(wire, Shipped);

        using var plain = new HttpRequestMessage(HttpMethod.Get, "indexer/api/x");
        var res = await client.SendAsync(plain);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(["gw.blazecoin.co.uk", "gw2.blazecoin.co.uk", "51-210-47-141.sslip.io"], wire.Calls);
        Assert.True(plain.Options.TryGetValue(GatewayRouting.AnsweredBy, out var who));
        Assert.Equal(new Uri(StrasbourgOtherName), who);
    }

    // ── the callers: with the other server down there is no second opinion ────────────────

    [Fact]
    public async Task A_broadcast_one_server_accepted_twice_is_not_backed()
    {
        var wire = OnlyStrasbourgUp(_ => Json("""{"txId":"cafe"}"""));
        var indexer = Indexer(wire, Shipped);

        var result = await indexer.BroadcastAsync("00");

        Assert.True(result.Success);
        Assert.True(result.Uncorroborated);                               // was false: "backed" by the same server
        Assert.DoesNotContain("51-210-47-141.sslip.io", wire.Calls);
    }

    [Fact]
    public async Task A_spend_question_is_not_put_to_the_listing_server_under_its_other_name()
    {
        var wire = OnlyStrasbourgUp(_ => Json("""{"height":1,"items":[]}"""));
        var indexer = Indexer(wire, Shipped);
        var coin = new LiteOutpoint(new string('a', 64), 0, new Uri(Strasbourg));

        var claims = await indexer.FindSpendsFromAnotherGatewayAsync([coin]);

        Assert.Null(claims);                                              // was []: "asked, none spent" — by the lister itself
        Assert.DoesNotContain("51-210-47-141.sslip.io", wire.Calls);
    }

    [Fact]
    public async Task An_address_usage_question_is_not_repeated_to_the_same_server()
    {
        var wire = OnlyStrasbourgUp(_ => Json("{}", HttpStatusCode.NotFound));
        var indexer = Indexer(wire, Shipped);

        Assert.False(await indexer.IsAddressUsedByAnyGatewayAsync("BAddress"));
        Assert.DoesNotContain("51-210-47-141.sslip.io", wire.Calls);
    }

    [Fact]
    public async Task A_header_run_one_server_served_is_not_confirmed_under_its_other_name()
    {
        var wire = OnlyStrasbourgUp(_ => Json("""{"headers":["00"]}"""));
        var indexer = Indexer(wire, Shipped);
        var batch = await indexer.GetHeaderBatchAsync(100, 1);
        Assert.Equal(new Uri(Strasbourg), batch.Source);

        var opinion = await indexer.HeaderFromAnotherGatewayAsync(100, [batch.Source!]);

        Assert.Equal(SecondGateway.Unavailable, opinion.Status);          // was Answered — by the server that served the run
        Assert.Null(opinion.RawHeader);
        Assert.DoesNotContain("51-210-47-141.sslip.io", wire.Calls);
    }

    [Fact]
    public async Task Headers_steered_away_from_a_server_do_not_come_from_its_other_name()
    {
        var wire = OnlyStrasbourgUp(_ => Json("""{"headers":["00"]}"""));
        var indexer = Indexer(wire, Shipped);

        var batch = await indexer.GetHeaderBatchAsync(100, 1, notFrom: [new Uri(Strasbourg)]);

        Assert.Empty(batch.Headers);
        Assert.DoesNotContain("51-210-47-141.sslip.io", wire.Calls);
    }

    // ── who answered is compared by server too ────────────────────────────────────────────

    /// <summary>A transport that ignores the exclusion and names who answered: Strasbourg's
    /// vanity name first, then its sslip.io name for everything after.</summary>
    private sealed class SameServerTwice(Wire wire) : DelegatingHandler(wire)
    {
        private int _answers;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var res = await base.SendAsync(request, ct);
            request.Options.Set(GatewayRouting.AnsweredBy,
                new Uri(Interlocked.Increment(ref _answers) == 1 ? Strasbourg : StrasbourgOtherName));
            return res;
        }
    }

    private static IndexerDataService DeafIndexer(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new(new HttpClient(new SameServerTwice(OnlyStrasbourgUp(answer))) { BaseAddress = new Uri(Strasbourg) },
            NullLogger<IndexerDataService>.Instance);

    [Fact]
    public async Task A_broadcast_answered_twice_by_one_server_is_recognised_whatever_name_answered()
    {
        var indexer = DeafIndexer(_ => Json("""{"txId":"cafe"}"""));

        var result = await indexer.BroadcastAsync("00");

        Assert.True(result.Success);
        Assert.True(result.Uncorroborated);                               // was false: the names differ, the server does not
    }

    [Fact]
    public async Task A_spend_answer_from_the_listing_server_under_its_other_name_is_no_second_opinion()
    {
        var indexer = DeafIndexer(_ => Json("""{"height":1,"items":[]}"""));
        await indexer.GetAddressAsync("BAddress");                        // the first answer: the vanity name
        var coin = new LiteOutpoint(new string('a', 64), 0, new Uri(Strasbourg));

        Assert.Null(await indexer.FindSpendsFromAnotherGatewayAsync([coin]));   // was []: "asked, none spent"
    }

    [Fact]
    public async Task A_header_from_the_same_server_under_its_other_name_is_no_second_opinion()
    {
        var indexer = DeafIndexer(_ => Json("""{"headers":["00"]}"""));
        var batch = await indexer.GetHeaderBatchAsync(100, 1);            // the first answer: the vanity name

        var opinion = await indexer.HeaderFromAnotherGatewayAsync(100, [batch.Source!]);

        Assert.Equal(SecondGateway.None, opinion.Status);
        Assert.Null(opinion.RawHeader);
    }
}

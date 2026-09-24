using System.Net;
using System.Text;
using BlazecoinWallet.Core.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Tests for <see cref="BlazecoindRpcService"/> — the JSON-RPC client that backs every
/// daemon call. A stub <see cref="HttpMessageHandler"/> feeds canned envelopes so we can pin the
/// behaviour that matters: JSON-RPC error objects surface as <see cref="RpcException"/> carrying the
/// numeric code; a wallet-scoped call is routed through <c>/wallet/&lt;active&gt;</c>; a best-effort
/// call swallows failure; peer rows are filtered/mapped; and a non-2xx with no JSON body becomes a
/// transport error (the "daemon still starting" signal).</summary>
public class BlazecoindRpcServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public Uri? LastRequestUri { get; private set; }

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (BlazecoindRpcService svc, StubHandler handler) Make(
        HttpStatusCode status, string body, string? activeWallet = null,
        string rpcUrl = "http://127.0.0.1:55413")
    {
        var handler = new StubHandler(status, body);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns(rpcUrl);

        var ctx = Substitute.For<IWalletContext>();
        ctx.Active.Returns(activeWallet);

        return (new BlazecoindRpcService(config, ctx, factory), handler);
    }

    [Fact]
    public async Task Parses_a_successful_result()
    {
        var (svc, _) = Make(HttpStatusCode.OK, """{"result":1.25,"error":null,"id":1}""");
        var balance = await svc.GetBalanceAsync();
        Assert.NotNull(balance);
        Assert.Equal(1.25m, balance!.Total);
    }

    [Fact]
    public async Task A_json_rpc_error_object_becomes_an_RpcException_with_the_code()
    {
        // Core returns HTTP 500 for most RPC errors, with the error envelope in the body.
        var (svc, _) = Make(HttpStatusCode.InternalServerError,
            """{"result":null,"error":{"code":-18,"message":"Requested wallet does not exist"},"id":1}""");

        var ex = await Assert.ThrowsAsync<RpcException>(() => svc.GetBalancesAsync());
        Assert.Equal(-18, ex.Code);
    }

    [Fact]
    public async Task Wallet_scoped_call_is_routed_through_the_active_wallet_path()
    {
        var (svc, handler) = Make(HttpStatusCode.OK, """{"result":0,"error":null,"id":1}""",
            activeWallet: "my wallet"); // space -> must be URL-escaped
        await svc.GetBalanceAsync();
        // AbsoluteUri keeps the escaped form (ToString() would decode %20 back to a space).
        Assert.Equal("http://127.0.0.1:55413/wallet/my%20wallet", handler.LastRequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Wallet_scoped_call_falls_back_to_the_base_url_when_no_wallet_is_active()
    {
        var (svc, handler) = Make(HttpStatusCode.OK, """{"result":0,"error":null,"id":1}""",
            activeWallet: null);
        await svc.GetBalanceAsync();
        Assert.Equal("http://127.0.0.1:55413/", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task ValidateAddress_returns_the_daemon_verdict()
    {
        var (svc, _) = Make(HttpStatusCode.OK, """{"result":{"isvalid":true,"address":"Bxxx"},"error":null,"id":1}""");
        Assert.Equal(true, await svc.ValidateAddressAsync("Bxxx"));
    }

    [Fact]
    public async Task ValidateAddress_swallows_failure_and_returns_null()
    {
        // Best-effort precheck: a daemon error must never block the send path.
        var (svc, _) = Make(HttpStatusCode.InternalServerError,
            """{"result":null,"error":{"code":-1,"message":"boom"},"id":1}""");
        Assert.Null(await svc.ValidateAddressAsync("Bxxx"));
    }

    [Fact]
    public async Task GetPeers_filters_empty_addresses_and_defaults_blank_subver()
    {
        var (svc, _) = Make(HttpStatusCode.OK,
            """{"result":[{"addr":"1.2.3.4:55414","subver":"/Blaze:2.0/"},{"addr":"","subver":"x"},{"addr":"5.6.7.8"}],"error":null,"id":1}""");

        var peers = await svc.GetPeersAsync();

        Assert.Equal(2, peers.Count); // the empty-addr row is dropped
        Assert.Equal("1.2.3.4:55414", peers[0].Address);
        Assert.Equal("/Blaze:2.0/", peers[0].SubVersion);
        Assert.Equal("5.6.7.8", peers[1].Address);
        Assert.Equal("unknown", peers[1].SubVersion); // blank subver -> "unknown"
    }

    [Fact]
    public async Task Non_success_status_with_no_json_body_is_a_transport_error()
    {
        // The earliest startup window: HTTP server up, RPC dispatcher not registered -> 500 empty body.
        var (svc, _) = Make(HttpStatusCode.InternalServerError, "");
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.GetBalanceAsync());
    }

    // ── Quantum Exposure history read (2026-09-15: a 65,702-transaction mining wallet sat at
    //    "listing" for about five minutes, because count/skip paging walked every newer row
    //    again on each page) ──

    /// <summary>Records each request body and answers every request with the same canned envelope.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public List<string> RequestBodies { get; } = new();
        public RecordingHandler(string body) => _body = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Spending_txids_are_read_in_one_history_call_and_deduplicated()
    {
        // 1,500 rows is more than the old 1,000-row page, so the old code would have paged; the new
        // code must read the history in one call. Sends sit at rows 0, 500 and 1000, and row 1000
        // repeats row 0's txid, so the result is exactly two distinct txids.
        var rows = new StringBuilder("[");
        for (int i = 0; i < 1500; i++)
        {
            if (i > 0) rows.Append(',');
            var category = i % 500 == 0 ? "send" : "receive";
            var txid = i == 1000 ? "tx0" : "tx" + i;
            rows.Append("{\"category\":\"").Append(category).Append("\",\"txid\":\"").Append(txid).Append("\"}");
        }
        rows.Append(']');
        var handler = new RecordingHandler("{\"result\":" + rows + ",\"error\":null,\"id\":1}");

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns("http://127.0.0.1:55417");
        var ctx = Substitute.For<IWalletContext>();
        ctx.Active.Returns("coinbase-inbox");
        var svc = new BlazecoindRpcService(config, ctx, factory);

        var ids = await svc.ListSpendingTxIdsAsync(100_000);

        Assert.Single(handler.RequestBodies);
        Assert.Contains("listtransactions", handler.RequestBodies[0]);
        Assert.Contains("100000", handler.RequestBodies[0]);
        Assert.Equal(new[] { "tx0", "tx500" }, ids);
    }

    // ── HTTP 503 "work queue depth exceeded" (2026-08-15 incident: the payout daemon spent ~20
    //    minutes after a restart catching up a 176k-tx wallet; every RPC worker was busy, so the
    //    8s sync poll got 503s and all three wallet windows reported the daemon as OFFLINE while
    //    it was up and at the chain tip). 503 is written before the request reaches a worker, so
    //    nothing ran — retry is always safe. ──

    [Fact]
    public async Task A_persistently_busy_daemon_is_reported_as_busy_not_offline()
    {
        // The real reply: 503 with a plain-text body, not a JSON-RPC envelope.
        var (svc, _) = Make(HttpStatusCode.ServiceUnavailable, "Work queue depth exceeded");

        var message = await svc.CheckConnectionAsync();

        Assert.NotNull(message);
        Assert.Contains("busy", message!, StringComparison.OrdinalIgnoreCase);
        // The whole point of the fix: a saturated daemon must never be called offline.
        Assert.DoesNotContain("offline", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_busy_daemon_is_retried_and_the_call_then_succeeds()
    {
        // A short 503 burst must be ridden out in the client rather than surfacing to the UI.
        var handler = new SequencedHandler(
            (HttpStatusCode.ServiceUnavailable, "Work queue depth exceeded"),
            (HttpStatusCode.OK, """{"result":1.25,"error":null,"id":1}"""));

        var svc = MakeSequenced(handler, Substitute.For<IWalletContext>()); // no active wallet -> base URL

        var balance = await svc.GetBalanceAsync();

        Assert.NotNull(balance);
        Assert.Equal(1.25m, balance!.Total);
        Assert.Equal(2, handler.RequestUris.Count); // the 503 was retried, not surfaced
    }

    // ── -18 self-heal (2026-08-10 incident: a daemon restart dropped an RPC-loaded active
    //    wallet; every wallet page in the already-open window dead-ended on the error) ──

    /// <summary>Sequenced stub: each request pops the next (status, body) pair, so one test can
    /// script the -18 failure, the <c>listwallets</c> recovery answer, and the retried call.</summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public List<Uri> RequestUris { get; } = new();

        public SequencedHandler(params (HttpStatusCode, string)[] responses)
            => _responses = new Queue<(HttpStatusCode, string)>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUris.Add(request.RequestUri!);
            var (status, body) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>The NSubstitute context returns a fixed Active, but the self-heal needs the
    /// post-SetActive value to be observable on the retried call's URL — a real mutable fake.</summary>
    private sealed class FakeWalletContext : IWalletContext
    {
        public string? Active { get; private set; }
        public int SetActiveCalls { get; private set; }
        public event Action? Changed { add { } remove { } }
        public FakeWalletContext(string? active) => Active = active;
        public void SetActive(string? name) { Active = name; SetActiveCalls++; }
    }

    private static BlazecoindRpcService MakeSequenced(SequencedHandler handler, IWalletContext ctx,
        string rpcUrl = "http://127.0.0.1:55413")
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns(rpcUrl);
        return new BlazecoindRpcService(config, ctx, factory);
    }

    private const string Minus18 =
        """{"result":null,"error":{"code":-18,"message":"Requested wallet does not exist or is not loaded"},"id":1}""";

    // NOTE: these use GetBalancesAsync (reference-type result) like the -18 test above —
    // a value-type result (GetBalanceAsync's decimal) can't deserialize an error envelope's
    // "result":null, so RPC errors on that method degrade to transport errors (pre-existing).

    [Fact]
    public async Task Wallet_call_self_heals_to_Primary_when_the_active_wallet_vanished()
    {
        var ctx = new FakeWalletContext("Ghost");
        var handler = new SequencedHandler(
            (HttpStatusCode.InternalServerError, Minus18),                                        // /wallet/Ghost -> -18
            (HttpStatusCode.OK, """{"result":["Primary","Other"],"error":null,"id":2}"""),        // listwallets
            (HttpStatusCode.OK, """{"result":{"mine":{"trusted":2.5}},"error":null,"id":3}""")); // retried getbalances

        var svc = MakeSequenced(handler, ctx);
        var balances = await svc.GetBalancesAsync();

        Assert.Equal(2.5m, balances!.Mine!.Trusted);
        Assert.Equal("Primary", ctx.Active);
        Assert.Equal(1, ctx.SetActiveCalls);
        Assert.Equal("http://127.0.0.1:55413/wallet/Ghost", handler.RequestUris[0].AbsoluteUri);
        Assert.Equal("http://127.0.0.1:55413/", handler.RequestUris[1].AbsoluteUri);              // recovery probe on base URL
        Assert.Equal("http://127.0.0.1:55413/wallet/Primary", handler.RequestUris[2].AbsoluteUri);
    }

    [Fact]
    public async Task Self_heal_prefers_first_loaded_wallet_when_Primary_is_absent()
    {
        var ctx = new FakeWalletContext("Ghost");
        var handler = new SequencedHandler(
            (HttpStatusCode.InternalServerError, Minus18),
            (HttpStatusCode.OK, """{"result":["payout-legacy"],"error":null,"id":2}"""),
            (HttpStatusCode.OK, """{"result":{"mine":{"trusted":1.0}},"error":null,"id":3}"""));

        var svc = MakeSequenced(handler, ctx);
        await svc.GetBalancesAsync();

        Assert.Equal("payout-legacy", ctx.Active);
    }

    [Fact]
    public async Task Minus18_rethrows_when_no_wallet_is_loaded_at_all()
    {
        var ctx = new FakeWalletContext("Ghost");
        var handler = new SequencedHandler(
            (HttpStatusCode.InternalServerError, Minus18),
            (HttpStatusCode.OK, """{"result":[],"error":null,"id":2}"""));                        // nothing to fall back to

        var svc = MakeSequenced(handler, ctx);
        var ex = await Assert.ThrowsAsync<RpcException>(() => svc.GetBalancesAsync());

        Assert.Equal(-18, ex.Code);
        Assert.Equal(0, ctx.SetActiveCalls); // never switched
    }

    [Fact]
    public async Task Minus18_rethrows_when_the_active_wallet_is_actually_loaded()
    {
        // A -18 that is NOT about the active wallet (some other cause) must not trigger a switch.
        var ctx = new FakeWalletContext("Primary");
        var handler = new SequencedHandler(
            (HttpStatusCode.InternalServerError, Minus18),
            (HttpStatusCode.OK, """{"result":["Primary"],"error":null,"id":2}"""));

        var svc = MakeSequenced(handler, ctx);
        var ex = await Assert.ThrowsAsync<RpcException>(() => svc.GetBalancesAsync());

        Assert.Equal(-18, ex.Code);
        Assert.Equal("Primary", ctx.Active);
        Assert.Equal(0, ctx.SetActiveCalls);
    }

    [Fact]
    public async Task createwallet_goes_to_the_node_endpoint_even_with_an_active_wallet()
    {
        var (svc, handler) = Make(HttpStatusCode.OK,
            "{\"result\":{\"name\":\"Primary\",\"warning\":\"\"},\"error\":null,\"id\":1}", activeWallet: "Other");

        await svc.CreateWalletAsync("Primary");

        Assert.Equal("/", handler.LastRequestUri!.AbsolutePath);   // not /wallet/Other
    }
}

using System.Net;
using System.Text;
using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Quantum;
using BlazecoinWallet.Core.Services.Send;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Post-quantum change on the desktop Send page (2026-09-28): a manual send from an operational wallet spent
/// a BQ… coin and returned its change to a LEGACY address, because that node runs
/// with changetype=legacy and sendtoaddress cannot override it. Pins the policy (post-quantum change
/// whenever the wallet can mint BQ…), and the exact RPC shapes: `send` with change_type for a chosen
/// kind, the unchanged sendtoaddress path when none is chosen, and the matching fee estimate.
/// </summary>
public class SendChangeTests
{
    private const string To = "BQGeaQmsowAjL1ZG8q1AfVH4NgBvtJKJSsjnnKEf62BMjekdU82n";

    // ════════════════════════════════════════════════════════════ policy

    private sealed class FakeMint : IAddressMintRpc
    {
        public bool? Can { get; set; }
        public bool Throw { get; set; }
        public Task<PqAddressResult> GetNewPostQuantumAddressAsync(string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetNewLegacyAddressAsync(string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> CanMintPostQuantumAsync(CancellationToken ct = default) => Throw ? throw new TimeoutException() : Task.FromResult(Can);
    }

    [Fact]
    public async Task A_wallet_with_BQ_keys_gets_post_quantum_change()
    {
        var d = await new SendChangePolicy(new FakeMint { Can = true }).DecideAsync();
        Assert.Equal(BlazecoinAddressKind.PostQuantum, d.Change);
        Assert.True(d.IsPostQuantum);
        Assert.Contains("post-quantum", d.Explanation);
    }

    [Fact]
    public async Task A_wallet_without_BQ_keys_keeps_the_daemon_default_and_says_why()
    {
        var d = await new SendChangePolicy(new FakeMint { Can = false }).DecideAsync();
        Assert.Null(d.Change);                        // asking for "pq" there would fail the send
        Assert.Contains("created before", d.Explanation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unknown_answer_keeps_the_daemon_default(bool throws)
    {
        var d = await new SendChangePolicy(new FakeMint { Can = null, Throw = throws }).DecideAsync();
        Assert.Null(d.Change);
        Assert.Contains("Couldn't check", d.Explanation);
    }

    [Fact]
    public void The_policy_needs_a_minter() => Assert.Throws<ArgumentNullException>(() => new SendChangePolicy(null!));

    [Theory]
    [InlineData(BlazecoinAddressKind.PostQuantum, "pq")]
    [InlineData(BlazecoinAddressKind.Legacy, "legacy")]
    [InlineData(null, null)]
    public void A_request_maps_its_change_kind_to_the_daemon_option(BlazecoinAddressKind? kind, string? expected)
        => Assert.Equal(expected, new SendRequest(To, 1m, false, kind).ChangeType);

    // ════════════════════════════════════════════════════════════ RPC shapes

    /// <summary>Answers each request with the next queued body and records every request.</summary>
    private sealed class Script : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _replies;
        public List<(string Url, string Body)> Requests { get; } = new();
        public Script(params (HttpStatusCode, string)[] replies) => _replies = new(replies);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add((r.RequestUri!.AbsoluteUri, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            var (s, b) = _replies.Dequeue();
            return new HttpResponseMessage(s) { Content = new StringContent(b, Encoding.UTF8, "application/json") };
        }
    }

    private static BlazecoindRpcService Svc(Script h)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(h));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns("http://127.0.0.1:55417");
        var ctx = Substitute.For<IWalletContext>();
        ctx.Active.Returns("hot-wallet");
        return new BlazecoindRpcService(config, ctx, factory);
    }

    private const string SendOk = """{"result":{"complete":true,"txid":"1f1e1d1c1b1a19181716151413121110f0e0d0c0b0a090807060504030201000"},"error":null,"id":1}""";

    [Fact]
    public async Task A_post_quantum_change_send_uses_send_with_change_type_pq_in_the_active_wallet()
    {
        var h = new Script((HttpStatusCode.OK, SendOk));

        var txid = await Svc(h).SendAsync(new SendRequest(To, 1000m, false, BlazecoinAddressKind.PostQuantum));

        Assert.Equal("1f1e1d1c1b1a19181716151413121110f0e0d0c0b0a090807060504030201000", txid);
        var (url, body) = Assert.Single(h.Requests);
        Assert.Equal("http://127.0.0.1:55417/wallet/hot-wallet", url);
        Assert.Contains("\"method\":\"send\"", body);
        Assert.Contains("\"params\":[[{\"" + To + "\":1000}],null,\"unset\",null,{\"change_type\":\"pq\"}]", body);
    }

    [Fact]
    public async Task A_send_max_with_a_change_type_takes_the_fee_from_the_output()
    {
        var h = new Script((HttpStatusCode.OK, SendOk));
        await Svc(h).SendAsync(new SendRequest(To, 5m, true, BlazecoinAddressKind.PostQuantum));
        Assert.Contains("\"subtract_fee_from_outputs\":[0]", h.Requests[0].Body);
    }

    [Fact]
    public async Task An_incomplete_send_returns_no_txid()
    {
        var h = new Script((HttpStatusCode.OK, """{"result":{"complete":false,"psbt":"cHNidP8="},"error":null,"id":1}"""));
        Assert.Null(await Svc(h).SendAsync(new SendRequest(To, 1m, false, BlazecoinAddressKind.PostQuantum)));
    }

    [Fact]
    public async Task A_daemon_refusal_surfaces_as_an_RpcException()
    {
        var h = new Script((HttpStatusCode.InternalServerError, """{"result":null,"error":{"code":-4,"message":"Insufficient funds"},"id":1}"""));
        var ex = await Assert.ThrowsAsync<RpcException>(() => Svc(h).SendAsync(new SendRequest(To, 1m, false, BlazecoinAddressKind.PostQuantum)));
        Assert.Equal(-4, ex.Code);
    }

    [Theory]
    [InlineData(false, "\"params\":[\"" + To + "\",1]")]
    [InlineData(true, "\"params\":[\"" + To + "\",1,\"\",\"\",true]")]
    public async Task Without_a_change_type_the_send_is_the_unchanged_sendtoaddress(bool subtract, string expected)
    {
        var h = new Script((HttpStatusCode.OK, "{\"result\":\"abc\",\"error\":null,\"id\":1}"));

        var txid = await Svc(h).SendAsync(new SendRequest(To, 1m, subtract, null));

        Assert.Equal("abc", txid);
        Assert.Contains("\"method\":\"sendtoaddress\"", h.Requests[0].Body);
        Assert.Contains(expected, h.Requests[0].Body);
    }

    [Fact]
    public async Task The_fee_estimate_funds_with_the_same_change_type()
    {
        var h = new Script(
            (HttpStatusCode.OK, "{\"result\":\"0200000000\",\"error\":null,\"id\":1}"),
            (HttpStatusCode.OK, """{"result":{"hex":"02","fee":0.00046500,"changepos":1},"error":null,"id":2}"""));

        var fee = await Svc(h).EstimateFeeAsync(new SendRequest(To, 1000m, false, BlazecoinAddressKind.PostQuantum));

        Assert.Equal(0.000465m, fee);
        Assert.Contains("\"method\":\"createrawtransaction\"", h.Requests[0].Body);
        Assert.Contains("\"method\":\"fundrawtransaction\"", h.Requests[1].Body);
        Assert.Contains("{\"change_type\":\"pq\"}", h.Requests[1].Body);
        Assert.EndsWith("/wallet/hot-wallet", h.Requests[1].Url);
    }

    [Fact]
    public async Task A_max_fee_estimate_with_a_change_type_subtracts_from_the_output()
    {
        var h = new Script(
            (HttpStatusCode.OK, "{\"result\":\"0200000000\",\"error\":null,\"id\":1}"),
            (HttpStatusCode.OK, """{"result":{"hex":"02","fee":0.0002,"changepos":-1},"error":null,"id":2}"""));
        await Svc(h).EstimateFeeAsync(new SendRequest(To, 5m, true, BlazecoinAddressKind.PostQuantum));
        Assert.Contains("\"subtractFeeFromOutputs\":[0]", h.Requests[1].Body);
    }

    [Fact]
    public async Task A_fee_estimate_that_cannot_fund_is_unknown_not_an_error()
    {
        var h = new Script(
            (HttpStatusCode.OK, "{\"result\":\"0200000000\",\"error\":null,\"id\":1}"),
            (HttpStatusCode.InternalServerError, """{"result":null,"error":{"code":-4,"message":"Insufficient funds"},"id":2}"""));
        Assert.Null(await Svc(h).EstimateFeeAsync(new SendRequest(To, 1m, false, BlazecoinAddressKind.PostQuantum)));
    }
}

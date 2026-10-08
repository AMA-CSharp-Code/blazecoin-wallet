using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Core.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The Sign Message page's plumbing (2026-10-08): the paste parser takes the website's console line or a bare
/// challenge apart; the RPC service finds the wallet that owns an address across every loaded wallet, signs
/// through THAT wallet's path (never the window's active one), verifies node-level, and reports a lock state;
/// and the Console page's passthrough now rides the active-wallet path (it used the root URL, which the daemon
/// refuses with -19 once two wallets are loaded).
/// </summary>
public class MessageSigningTests
{
    private const string Addr = "BQHVEUe4uG9FaoB2c9SWtnapMXuiHuFCtzqsRV2GFCNLJqyefPPm";
    private const string Challenge = "blazecoin-verify:621ec0bf94d66ec8cbaf1b852150002c";

    // ---- the paste parser -------------------------------------------------------------------

    [Fact]
    public void Parses_the_websites_console_line_into_address_and_message()
    {
        var r = SigningPasteParser.Parse($"signmessage \"{Addr}\" \"{Challenge}\"");
        Assert.Equal(Addr, r.Address);
        Assert.Equal(Challenge, r.Message);
    }

    [Fact]
    public void Tolerates_whitespace_and_single_quotes_around_the_console_line()
    {
        var r = SigningPasteParser.Parse($"   signmessage '{Addr}' '{Challenge}'  \r\n");
        Assert.Equal(Addr, r.Address);
        Assert.Equal(Challenge, r.Message);
    }

    [Fact]
    public void A_bare_challenge_is_the_message_with_no_address()
    {
        var r = SigningPasteParser.Parse($"  {Challenge}  ");
        Assert.Null(r.Address);
        Assert.Equal(Challenge, r.Message);
    }

    [Fact]
    public void Empty_paste_is_empty()
    {
        Assert.Equal(string.Empty, SigningPasteParser.Parse(null).Message);
        Assert.Equal(string.Empty, SigningPasteParser.Parse("   ").Message);
    }

    [Fact]
    public void Address_shape_check_accepts_B_and_BQ_and_rejects_the_rest()
    {
        Assert.True(SigningPasteParser.LooksLikeAddress(Addr));
        Assert.True(SigningPasteParser.LooksLikeAddress("B5Legacy1111111111111111111111111"));   // base58: no 0, O, I, l
        Assert.False(SigningPasteParser.LooksLikeAddress("blz1qexample"));
        Assert.False(SigningPasteParser.LooksLikeAddress(""));
        Assert.False(SigningPasteParser.LooksLikeAddress("B0OIl"));   // not base58, too short
    }

    // ---- the RPC service, driven by a scripted handler (answers by path + method) --------------

    private sealed record RpcCall(string Path, string Method, JsonElement Params);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<string, string, (HttpStatusCode, string)> _answer;
        public List<RpcCall> Calls { get; } = new();

        public ScriptedHandler(Func<string, string, (HttpStatusCode, string)> answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            var method = body.GetProperty("method").GetString()!;
            var prms = body.TryGetProperty("params", out var p) ? p.Clone() : default;
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(new RpcCall(path, method, prms));
            var (status, json) = _answer(path, method);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (BlazecoindRpcService svc, ScriptedHandler handler) Make(
        Func<string, string, (HttpStatusCode, string)> answer, string? activeWallet = "Primary")
    {
        var handler = new ScriptedHandler(answer);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns("http://127.0.0.1:55415");
        var ctx = Substitute.For<IWalletContext>();
        ctx.Active.Returns(activeWallet);
        return (new BlazecoindRpcService(config, ctx, factory), handler);
    }

    private static (HttpStatusCode, string) Ok(string resultJson)
        => (HttpStatusCode.OK, "{\"result\":" + resultJson + ",\"error\":null,\"id\":1}");

    private static (HttpStatusCode, string) Err(int code, string msg)
        => (HttpStatusCode.InternalServerError, "{\"result\":null,\"error\":{\"code\":" + code + ",\"message\":\"" + msg + "\"},\"id\":1}");

    [Fact]
    public async Task Finds_the_wallet_that_owns_the_address_checking_the_active_one_first()
    {
        var (svc, handler) = Make((path, method) => method switch
        {
            "listwallets" => Ok("[\"Primary\",\"post-quantum\"]"),
            "getaddressinfo" => Ok(path == "/wallet/post-quantum" ? "{\"ismine\":true}" : "{\"ismine\":false}"),
            _ => Err(-32601, "no such method")
        });

        Assert.Equal("post-quantum", await svc.FindWalletOwningAsync(Addr));

        Assert.Equal(("/", "listwallets"), (handler.Calls[0].Path, handler.Calls[0].Method));
        Assert.Equal("/wallet/Primary", handler.Calls[1].Path);        // active wallet asked first
        Assert.Equal("/wallet/post-quantum", handler.Calls[2].Path);
        Assert.Equal(Addr, handler.Calls[2].Params[0].GetString());
    }

    [Fact]
    public async Task Owner_lookup_returns_null_when_no_loaded_wallet_holds_the_key()
    {
        var (svc, _) = Make((_, method) => method == "listwallets" ? Ok("[\"Primary\"]") : Ok("{\"ismine\":false}"));
        Assert.Null(await svc.FindWalletOwningAsync(Addr));
    }

    [Fact]
    public async Task Owner_lookup_survives_a_wallet_that_errors_and_keeps_looking()
    {
        var (svc, _) = Make((path, method) => method switch
        {
            "listwallets" => Ok("[\"broken\",\"post-quantum\"]"),
            "getaddressinfo" when path == "/wallet/broken" => Err(-18, "Requested wallet does not exist or is not loaded"),
            "getaddressinfo" => Ok("{\"ismine\":true}"),
            _ => Err(-1, "unexpected")
        }, activeWallet: null);
        Assert.Equal("post-quantum", await svc.FindWalletOwningAsync(Addr));
    }

    [Fact]
    public async Task Signs_through_the_named_wallet_path_not_the_active_one()
    {
        var (svc, handler) = Make((_, method) => method == "signmessage" ? Ok("\"SIGNATURE==\"") : Err(-1, "unexpected"), activeWallet: "Primary");
        var sig = await svc.SignMessageInAsync("post-quantum", Addr, Challenge);
        Assert.Equal("SIGNATURE==", sig);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("/wallet/post-quantum", call.Path);
        Assert.Equal(Addr, call.Params[0].GetString());
        Assert.Equal(Challenge, call.Params[1].GetString());
    }

    [Fact]
    public async Task Verify_is_node_level_and_maps_an_rpc_error_to_null_not_false()
    {
        var (ok, okHandler) = Make((_, _) => Ok("true"));
        Assert.True(await ok.VerifyMessageAsync(Addr, "SIG", Challenge));
        Assert.Equal("/", Assert.Single(okHandler.Calls).Path);

        var (no, _) = Make((_, _) => Ok("false"));
        Assert.False(await no.VerifyMessageAsync(Addr, "SIG", Challenge));

        var (bad, _) = Make((_, _) => Err(-3, "Malformed base64 encoding"));
        Assert.Null(await bad.VerifyMessageAsync(Addr, "garbage", Challenge));
    }

    [Fact]
    public async Task Lock_state_reads_unlocked_until_from_the_named_wallet()
    {
        var (locked, h) = Make((_, _) => Ok("{\"walletname\":\"w\",\"unlocked_until\":0}"));
        Assert.True(await locked.IsLockedInAsync("w"));
        Assert.Equal("/wallet/w", Assert.Single(h.Calls).Path);

        var (open, _) = Make((_, _) => Ok("{\"walletname\":\"w\",\"unlocked_until\":1800000000}"));
        Assert.False(await open.IsLockedInAsync("w"));

        var (plain, _) = Make((_, _) => Ok("{\"walletname\":\"w\"}"));
        Assert.False(await plain.IsLockedInAsync("w"));
    }

    [Fact]
    public async Task Unlock_and_lock_go_to_the_named_wallet_with_the_passphrase_and_timeout()
    {
        var (svc, handler) = Make((_, _) => Ok("null"), activeWallet: "Primary");
        await svc.UnlockInAsync("post-quantum", "pass phrase", 60);
        await svc.LockInAsync("post-quantum");
        Assert.Equal(2, handler.Calls.Count);
        Assert.All(handler.Calls, c => Assert.Equal("/wallet/post-quantum", c.Path));
        Assert.Equal("walletpassphrase", handler.Calls[0].Method);
        Assert.Equal("pass phrase", handler.Calls[0].Params[0].GetString());
        Assert.Equal(60, handler.Calls[0].Params[1].GetInt32());
        Assert.Equal("walletlock", handler.Calls[1].Method);
    }

    [Fact]
    public async Task Console_passthrough_rides_the_active_wallet_path()
    {
        var (svc, handler) = Make((_, _) => Ok("\"ok\""), activeWallet: "post-quantum");
        await svc.ExecuteCommandAsync("signmessage", Addr, Challenge);
        Assert.Equal("/wallet/post-quantum", Assert.Single(handler.Calls).Path);
    }
}

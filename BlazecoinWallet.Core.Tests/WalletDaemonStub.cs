using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Core.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// A daemon with a set of loaded wallets, for tests that must show on the wire WHICH wallet a
/// call reached. A wallet RPC addressed to a wallet that is not loaded answers -18, exactly like
/// the real one; every request is recorded with the wallet its URL named ("" = the node endpoint).
/// </summary>
internal sealed class WalletDaemonStub : HttpMessageHandler
{
    public const string RpcUrl = "http://127.0.0.1:55413";
    /// <summary>PQ_SIGNATURES.md §5 example - what <c>getnewaddress … "pq"</c> answers.</summary>
    public const string BqAddress = "BQGeaQmsowAjL1ZG8q1AfVH4NgBvtJKJSsjnnKEf62BMjekdU82n";
    public static readonly string TxId = new('a', 64);
    private const string Minus18 =
        """{"result":null,"error":{"code":-18,"message":"Requested wallet does not exist or is not loaded"},"id":1}""";

    public sealed record Call(string Method, string Wallet, string Body);

    public HashSet<string> Loaded { get; } = new(StringComparer.Ordinal);
    /// <summary>Wallets that hold an active post-quantum key chain (<c>listdescriptors</c>).</summary>
    public HashSet<string> PostQuantum { get; } = new(StringComparer.Ordinal);
    public List<Call> Calls { get; } = [];
    /// <summary>Runs just before a call is answered - a test uses it to change something mid-flow.</summary>
    public Action<Call>? Before;

    public IEnumerable<Call> Of(string method) => Calls.Where(c => c.Method == method);

    public static WalletDaemonStub With(params string[] loaded)
    {
        var d = new WalletDaemonStub();
        foreach (var w in loaded) d.Loaded.Add(w);
        return d;
    }

    public BlazecoindRpcService Rpc(IWalletContext ctx, IWalletSwitchNotice notice)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(this));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns(RpcUrl);
        return new BlazecoindRpcService(config, ctx, factory, notice);
    }

    private static string Ok(string resultJson) => "{\"result\":" + resultJson + ",\"error\":null,\"id\":9}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var method = doc.RootElement.GetProperty("method").GetString() ?? "";
        var path = request.RequestUri!.AbsolutePath;
        var wallet = path.StartsWith("/wallet/") ? Uri.UnescapeDataString(path["/wallet/".Length..]) : "";
        var call = new Call(method, wallet, body);
        Before?.Invoke(call);
        Calls.Add(call);

        if (method == "listwallets")
            return Reply(HttpStatusCode.OK, Ok(JsonSerializer.Serialize(Loaded.ToArray())));
        if (wallet.Length > 0 && !Loaded.Contains(wallet))
            return Reply(HttpStatusCode.InternalServerError, Minus18);

        return method switch
        {
            "getbalances" => Reply(HttpStatusCode.OK, Ok("{\"mine\":{\"trusted\":200}}")),
            "getnewaddress" => Reply(HttpStatusCode.OK, Ok(body.Contains("\"pq\"") ? $"\"{BqAddress}\"" : $"\"Blegacy-of-{wallet}\"")),
            "listdescriptors" => Reply(HttpStatusCode.OK, Ok(PostQuantum.Contains(wallet)
                ? """{"descriptors":[{"desc":"pqkh(xpub/0/*)#abc","active":true,"internal":false}]}"""
                : """{"descriptors":[{"desc":"pkh(xpub/0/*)#abc","active":true,"internal":false}]}""")),
            "getaddressinfo" => Reply(HttpStatusCode.OK, Ok("{\"ismine\":true}")),
            "createrawtransaction" => Reply(HttpStatusCode.OK, Ok("\"0200000000\"")),
            "signrawtransactionwithwallet" => Reply(HttpStatusCode.OK, Ok($$"""{"hex":"signed-by-{{wallet}}","complete":true}""")),
            "sendrawtransaction" => Reply(HttpStatusCode.OK, Ok($"\"{TxId}\"")),
            _ => Reply(HttpStatusCode.OK, Ok("null")),
        };
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>The active wallet's minting calls for a wallet that can (or cannot, the default) make BQ… addresses.</summary>
internal sealed class TestMint(bool? canMintPostQuantum = false) : BlazecoinWallet.Core.Services.Quantum.IAddressMintRpc
{
    public Task<BlazecoinWallet.Core.Services.Quantum.PqAddressResult> GetNewPostQuantumAddressAsync(string label, CancellationToken ct = default) =>
        Task.FromResult(canMintPostQuantum == true
            ? BlazecoinWallet.Core.Services.Quantum.PqAddressResult.Ok(WalletDaemonStub.BqAddress)
            : BlazecoinWallet.Core.Services.Quantum.PqAddressResult.Fail(
                BlazecoinWallet.Core.Services.Quantum.PqAddressOutcome.WalletHasNoPqKeys, "This wallet has no post-quantum (pq) key manager"));
    public Task<string?> GetNewLegacyAddressAsync(string label, CancellationToken ct = default) => Task.FromResult<string?>(TestAddr.Recipient);
    public Task<bool?> CanMintPostQuantumAsync(CancellationToken ct = default) => Task.FromResult(canMintPostQuantum);
}

/// <summary>An active-wallet context a test can move by hand.</summary>
internal sealed class TestWalletContext(string? active) : IWalletContext
{
    public string? Active { get; private set; } = active;
    public event Action? Changed;
    public void SetActive(string? name) { Active = name; Changed?.Invoke(); }
}

using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Personal-node mode against a scripted daemon RPC: scantxoutset → wallet-grade UTXOs
/// (satoshi conversion exact, confirmations vs the scan tip, coinbase flag honored),
/// broadcast via sendrawtransaction (RPC error = rejection, transport failure =
/// unreachable), history honestly empty, and the FULL send pipeline works over it.
/// </summary>
public class PersonalNodeDataServiceTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>Scripted JSON-RPC daemon keyed by method name.</summary>
    private sealed class ScriptedRpc : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Results = new();   // method → result JSON
        public string? ErrorFor;                                       // method that errors
        public bool Dead;                                              // transport failure
        public string? LastSendHex;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Dead) throw new HttpRequestException("connection refused");
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = body.RootElement.GetProperty("method").GetString()!;
            if (method == "sendrawtransaction")
                LastSendHex = body.RootElement.GetProperty("params")[0].GetString();

            string json = method == ErrorFor
                ? "{\"result\":null,\"error\":{\"code\":-26,\"message\":\"bad-txns-inputs-missingorspent\"}}"
                : $"{{\"result\":{Results.GetValueOrDefault(method, "null")},\"error\":null}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (PersonalNodeDataService svc, ScriptedRpc rpc) Build()
    {
        var rpc = new ScriptedRpc();
        var svc = new PersonalNodeDataService(
            new HttpClient(rpc),
            new PersonalNodeOptions("http://127.0.0.1:55413/", RpcUser: "u", RpcPassword: "p"),
            NullLogger<PersonalNodeDataService>.Instance);
        return (svc, rpc);
    }

    [Fact]
    public void Off_box_cleartext_rpc_is_rejected_so_creds_cant_leak()
    {
        // Basic-auth credentials must never cross a LAN in cleartext (audit M2).
        Assert.Throws<ArgumentException>(() => new PersonalNodeDataService(
            new HttpClient(new ScriptedRpc()),
            new PersonalNodeOptions("http://192.168.1.50:55413/", RpcUser: "u", RpcPassword: "p"),
            NullLogger<PersonalNodeDataService>.Instance));

        // Loopback http (the normal case — daemon on the same box) is fine.
        var ok = Record.Exception(() => new PersonalNodeDataService(
            new HttpClient(new ScriptedRpc()),
            new PersonalNodeOptions("http://127.0.0.1:55413/", RpcUser: "u", RpcPassword: "p"),
            NullLogger<PersonalNodeDataService>.Instance));
        Assert.Null(ok);
    }

    [Fact]
    public async Task Scantxoutset_maps_to_wallet_grade_utxos()
    {
        var (svc, rpc) = Build();
        rpc.Results["scantxoutset"] = """
            {"success":true,"height":1000,"unspents":[
              {"txid":"aa","vout":1,"amount":0.10000000,"height":900},
              {"txid":"bb","vout":0,"amount":4.13000000,"height":990,"coinbase":true}
            ],"total_amount":4.23}
            """;

        var utxos = await svc.GetUtxosAsync("Bxyz");

        Assert.Equal(2, utxos.Count);
        var plain = utxos.Single(u => u.TxId == "aa");
        Assert.Equal(10_000_000, plain.Amount);        // decimal BLZ → satoshis, exact
        Assert.Equal(101, plain.Confirmations);        // 1000 − 900 + 1
        Assert.False(plain.IsCoinbase);
        var cb = utxos.Single(u => u.TxId == "bb");
        Assert.Equal(413_000_000, cb.Amount);
        Assert.Equal(11, cb.Confirmations);
        Assert.True(cb.IsCoinbase);                     // maturity filter can do its job

        var summary = await svc.GetAddressAsync("Bxyz");
        Assert.Equal(423_000_000, summary!.Balance);
        Assert.Empty(await svc.GetHistoryAsync("Bxyz")); // honestly empty — no address index
    }

    [Fact]
    public async Task Broadcast_maps_success_rejection_and_unreachable()
    {
        var (svc, rpc) = Build();

        rpc.Results["sendrawtransaction"] = "\"feedface\"";
        var ok = await svc.BroadcastAsync("00ff");
        Assert.True(ok.Success);
        Assert.Equal("feedface", ok.TxId);
        Assert.Equal("00ff", rpc.LastSendHex);

        rpc.ErrorFor = "sendrawtransaction";
        var rejected = await svc.BroadcastAsync("00ff");
        Assert.False(rejected.Success);
        Assert.Equal(BroadcastFailureKind.Rejected, rejected.Failure);   // the node JUDGED it
        Assert.Equal("bad-txns-inputs-missingorspent", rejected.Error);

        rpc.Dead = true;
        var down = await svc.BroadcastAsync("00ff");
        Assert.False(down.Success);
        Assert.Equal(BroadcastFailureKind.Unreachable, down.Failure);    // fallback-eligible
    }

    [Fact]
    public async Task The_full_send_pipeline_runs_over_a_personal_node()
    {
        var (data, rpc) = Build();
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var own = wallet.GetReceiveAddress(0);
        rpc.Results["scantxoutset"] =
            $$"""{"success":true,"height":500,"unspents":[{"txid":"{{new string('8', 64)}}","vout":0,"amount":0.10000000,"height":499}],"total_amount":0.1}""";
        rpc.Results["sendrawtransaction"] = "\"acceptedbymynode\"";

        var vault = new InlineVault(TestMnemonic);
        var service = new LiteWalletService(vault, data);
        await service.UnlockAsync();

        var result = await service.SendAsync(wallet.GetReceiveAddress(5), 5_000_000);

        Assert.True(result.Success, result.Error);
        Assert.Equal("acceptedbymynode", result.TxId);
        Assert.NotNull(rpc.LastSendHex);               // a real signed tx reached the node
    }

    private sealed class InlineVault(string words) : ISeedVault
    {
        public Task<bool> HasWalletAsync() => Task.FromResult(true);
        public Task SaveMnemonicAsync(string m) => Task.CompletedTask;
        public Task<string?> LoadMnemonicAsync() => Task.FromResult<string?>(words);
        public Task ClearAsync() => Task.CompletedTask;
    }
}

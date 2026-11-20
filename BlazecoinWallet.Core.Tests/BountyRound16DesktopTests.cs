using System.Net;
using System.Text;
using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Send;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Bug-bounty round sixteen (2026-11-18), the desktop wallet's two findings.
/// <list type="bullet">
/// <item>The Provenance vintage send minted a LEGACY change address, without a word, when the
/// wallet could not be asked whether it has post-quantum keys (a busy daemon, a failed call). The
/// round-four fix covered a mint that fails; this is the check that fails.</item>
/// <item>The Quantum Exposure Scanner read the newest 100,000 history rows and stopped, so in a
/// larger wallet the older sends were never examined and their addresses read UNEXPOSED.</item>
/// </list>
/// </summary>
public class BountyRound16DesktopTests
{
    private const string LegacyA = "BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z";

    private static string Ok(string resultJson) => "{\"result\":" + resultJson + ",\"error\":null,\"id\":1}";

    /// <summary>Answers each request with the next queued body and records every request.</summary>
    private sealed class Script(params (HttpStatusCode, string)[] replies) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _replies = new(replies);
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Bodies.Add(r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct));
            var (status, body) = _replies.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static BlazecoindRpcService Rpc(Script daemon)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(daemon));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns("http://127.0.0.1:55417");
        var ctx = Substitute.For<IWalletContext>();
        ctx.Active.Returns("Savings");
        return new BlazecoindRpcService(config, ctx, factory, new WalletSwitchNotice());
    }

    // ════════════════════════ vintage-send change ════════════════════════

    [Fact]
    public async Task A_wallet_that_could_not_be_checked_gives_no_change_address_not_a_quiet_legacy_one()
    {
        // listdescriptors fails (the daemon is still loading: not the -4 that means "a legacy
        // wallet", which is an answer and still gives legacy change); a legacy address is queued
        // behind it, which is what the old code went on to ask for.
        var daemon = new Script(
            (HttpStatusCode.InternalServerError, """{"result":null,"error":{"code":-28,"message":"Loading wallet"},"id":1}"""),
            (HttpStatusCode.OK, Ok($"\"{LegacyA}\"")));

        var change = await new ChangeAddressSource(Rpc(daemon)).NewAsync("Savings", "vintage change");

        Assert.Null(change);                    // was LegacyA
        Assert.Single(daemon.Bodies);           // and no second call for a legacy address
        Assert.Contains("\"method\":\"listdescriptors\"", daemon.Bodies[0]);
    }

    // ════════════════════════ exposure scanner history ════════════════════════

    private static string Rows(params (string Category, string TxId)[] rows) =>
        "[" + string.Join(",", rows.Select(r => $"{{\"category\":\"{r.Category}\",\"txid\":\"{r.TxId}\"}}")) + "]";

    [Fact]
    public async Task Spending_txids_are_read_past_one_full_page_of_history()
    {
        // Page size 3. The newest three rows fill the first read; the wallet's oldest send sits in
        // the second. The old code stopped after the first read and never saw "old-send".
        var daemon = new Script(
            (HttpStatusCode.OK, Ok(Rows(("receive", "r1"), ("send", "new-send"), ("receive", "r2")))),
            (HttpStatusCode.OK, Ok(Rows(("receive", "r3"), ("send", "old-send")))));

        var ids = await Rpc(daemon).ListSpendingTxIdsAsync(3);

        Assert.Equal(new[] { "new-send", "old-send" }, ids);
        Assert.Equal(2, daemon.Bodies.Count);
        Assert.Contains("\"params\":[\"*\",3,0]", daemon.Bodies[0]);
        Assert.Contains("\"params\":[\"*\",3,3]", daemon.Bodies[1]);
    }

    [Fact]
    public async Task A_history_that_ends_exactly_on_a_page_boundary_is_read_once_more_and_then_stops()
    {
        var daemon = new Script(
            (HttpStatusCode.OK, Ok(Rows(("send", "a"), ("send", "b")))),
            (HttpStatusCode.OK, Ok("[]")));

        var ids = await Rpc(daemon).ListSpendingTxIdsAsync(2);

        Assert.Equal(new[] { "a", "b" }, ids);
        Assert.Equal(2, daemon.Bodies.Count);
    }
}

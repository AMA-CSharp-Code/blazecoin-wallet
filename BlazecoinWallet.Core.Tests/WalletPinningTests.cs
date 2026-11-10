using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.AutoPayout;
using BlazecoinWallet.Core.Services.Send;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// A call meant for one wallet never runs against another (bug-bounty report 2026-11-06).
/// When the selected wallet is no longer loaded on the node the daemon answers RPC error -18;
/// the wallet layer used to answer EVERY -18 by switching to another loaded wallet and
/// repeating the call — so a <c>sendtoaddress</c> the user confirmed on "Savings" was re-sent,
/// unchanged, from "Primary" and reported as a normal success. Now only reads are repeated
/// (and the user is told the wallet changed); a send names its wallet and fails if that wallet
/// is gone; an armed auto-payout stops the moment the active wallet is not the one it was
/// armed on.
/// <para>Drives the production <see cref="BlazecoindRpcService"/> and
/// <see cref="AutoPayoutService"/> against a recording stub daemon — every request URI and body
/// is on record, so "nothing reached the other wallet" is asserted on the wire.</para>
/// </summary>
public class WalletPinningTests
{
    private const string RpcUrl = "http://127.0.0.1:55413";
    private const string Minus18 =
        """{"result":null,"error":{"code":-18,"message":"Requested wallet does not exist or is not loaded"},"id":1}""";
    private static readonly string TxId = new('a', 64);

    private static string Ok(string resultJson) => "{\"result\":" + resultJson + ",\"error\":null,\"id\":9}";

    private sealed class FakeWalletContext(string? active) : IWalletContext
    {
        public string? Active { get; private set; } = active;
        public int SetActiveCalls { get; private set; }
        public event Action? Changed;
        public void SetActive(string? name) { Active = name; SetActiveCalls++; Changed?.Invoke(); }
    }

    /// <summary>A daemon with a set of loaded wallets. A wallet RPC addressed to a wallet that is
    /// not loaded answers -18, exactly like the real one; everything is recorded.</summary>
    private sealed class StubDaemon : HttpMessageHandler
    {
        public sealed record Call(string Method, string Wallet, string Body);
        public HashSet<string> Loaded { get; } = new(StringComparer.Ordinal);
        public List<Call> Calls { get; } = [];
        public decimal Balance = 200m;
        /// <summary>Runs just before a call is answered — a test uses it to unload a wallet mid-flow.</summary>
        public Action<Call>? Before;
        /// <summary>Awaited before a call is answered — a test uses it to keep several requests in
        /// flight at once, the way a page fires its reads together.</summary>
        public Func<Call, Task>? Hold;

        public IEnumerable<Call> Of(string method) => Calls.Where(c => c.Method == method);

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
            if (Hold != null) await Hold(call);

            if (method == "listwallets")
                return Reply(HttpStatusCode.OK, Ok(JsonSerializer.Serialize(Loaded.ToArray())));
            if (wallet.Length > 0 && !Loaded.Contains(wallet))
                return Reply(HttpStatusCode.InternalServerError, Minus18);

            return method switch
            {
                "getbalances" => Reply(HttpStatusCode.OK, Ok("{\"mine\":{\"trusted\":" + Balance.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}")),
                "sendtoaddress" => Reply(HttpStatusCode.OK, Ok($"\"{TxId}\"")),
                "send" => Reply(HttpStatusCode.OK, Ok($$"""{"complete":true,"txid":"{{TxId}}"}""")),
                "getnewaddress" => Reply(HttpStatusCode.OK, Ok("\"Bnewaddress\"")),
                "getwalletinfo" => Reply(HttpStatusCode.OK, Ok("{\"walletname\":\"" + wallet + "\"}")),
                "createrawtransaction" => Reply(HttpStatusCode.OK, Ok("\"0200000000\"")),
                "fundrawtransaction" => Reply(HttpStatusCode.OK, Ok("{\"hex\":\"0200000000\",\"fee\":0.00025,\"changepos\":1}")),
                _ => Reply(HttpStatusCode.OK, Ok("null")),
            };
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static BlazecoindRpcService Rpc(StubDaemon daemon, IWalletContext ctx, IWalletSwitchNotice? notice = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(daemon));
        var config = Substitute.For<IConfiguration>();
        config["Blazecoind:RpcUrl"].Returns(RpcUrl);
        return new BlazecoindRpcService(config, ctx, factory, notice ?? new WalletSwitchNotice());
    }

    private static StubDaemon Daemon(params string[] loaded)
    {
        var d = new StubDaemon();
        foreach (var w in loaded) d.Loaded.Add(w);
        return d;
    }

    // ════════════════════════ the report's scenario ════════════════════════

    [Theory]
    [InlineData(false)]   // sendtoaddress
    [InlineData(true)]    // sendtoaddress with the fee taken from the amount (Send Max)
    public async Task A_send_is_never_repeated_against_another_wallet(bool subtractFee)
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary");                        // "Savings" was unloaded mid-session
        var svc = Rpc(daemon, ctx);

        var ex = await Assert.ThrowsAsync<WalletNotLoadedException>(
            () => svc.SendToAddressAsync("BReviewedDestinationAddress", 2.5m, subtractFee));

        Assert.Equal(-18, ex.Code);
        Assert.Equal("Savings", ex.Wallet);
        Assert.Contains("nothing was done", ex.Message);
        var only = Assert.Single(daemon.Calls);                // was: send, listwallets, the same send to Primary
        Assert.Equal(("sendtoaddress", "Savings"), (only.Method, only.Wallet));
        Assert.Equal("Savings", ctx.Active);                   // a failed send does not move the app either
        Assert.Equal(0, ctx.SetActiveCalls);
    }

    [Fact]
    public async Task A_send_with_a_chosen_change_type_is_not_repeated_either()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, ctx);
        var request = new SendRequest("BDest", 1m, false, BlazecoinWallet.Core.Services.Mining.BlazecoinAddressKind.PostQuantum);

        await Assert.ThrowsAsync<WalletNotLoadedException>(() => svc.SendAsync(request));

        Assert.Equal("send", Assert.Single(daemon.Calls).Method);
    }

    [Theory]
    [InlineData("getnewaddress")]                 // would hand out another wallet's address
    [InlineData("encryptwallet")]                 // would put the passphrase on another wallet
    [InlineData("walletpassphrase")]
    [InlineData("walletpassphrasechange")]
    [InlineData("walletlock")]
    [InlineData("setlabel")]
    [InlineData("signrawtransactionwithwallet")]
    [InlineData("fundrawtransaction")]
    [InlineData("sendtoaddress")]
    [InlineData("send")]
    [InlineData("sendmany")]                      // not used by the app — and so not on the list
    [InlineData("importdescriptors")]
    public async Task Nothing_but_a_read_is_repeated_against_another_wallet(string method)
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, ctx);

        // Through the Console's call - the raw passthrough on the active wallet's path that this
        // used to go through was deleted in the audit of 2026-11-09 (nothing in the app called it).
        await Assert.ThrowsAsync<WalletNotLoadedException>(() => svc.ExecuteConsoleCommandAsync("Savings", method, []));

        Assert.Single(daemon.Calls);
        Assert.Equal(0, ctx.SetActiveCalls);
        Assert.DoesNotContain(method, BlazecoindRpcService.HealableWalletReads);
    }

    [Fact]
    public void The_calls_that_may_be_repeated_are_reads_and_only_reads()
    {
        // Pinned so that adding a wallet RPC here is a decision someone has to make on purpose.
        Assert.Equal(
            new[] { "getaddressesbylabel", "getaddressinfo", "getbalance", "getbalances", "gettransaction",
                    "getwalletinfo", "listdescriptors", "listlabels", "listtransactions", "listunspent" },
            BlazecoindRpcService.HealableWalletReads.OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }

    // ════════════════════════ reads still heal — and now say so ════════════════════════

    [Fact]
    public async Task A_read_still_heals_to_a_loaded_wallet_and_the_user_is_told()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Other");
        var notice = new WalletSwitchNotice(() => new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
        var changes = 0;
        notice.Changed += () => changes++;
        var svc = Rpc(daemon, ctx, notice);

        var balances = await svc.GetBalancesAsync();

        Assert.Equal(200m, balances!.Mine!.Trusted);
        Assert.Equal("Primary", ctx.Active);
        Assert.Equal(new[] { "getbalances", "listwallets", "getbalances" }, daemon.Calls.Select(c => c.Method));
        var sw = Assert.IsType<WalletSwitch>(notice.Current);
        Assert.Equal("Savings", sw.From);
        Assert.Equal("Primary", sw.To);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Reads_fired_together_all_follow_the_first_one_that_heals()
    {
        // The Dashboard asks for balances and transactions at the same moment. Both hit -18; the
        // first to recover re-points the app, and the second used to find the app already on a
        // loaded wallet, decide its error "wasn't about the active wallet", and fail the page.
        var ctx = new FakeWalletContext("throwaway");
        var daemon = Daemon("Primary", "Rich Man");
        var notice = new WalletSwitchNotice();
        var bothInFlight = new TaskCompletionSource();
        var switched = new TaskCompletionSource();
        ctx.Changed += () => switched.TrySetResult();
        int arrived = 0, probes = 0;
        daemon.Hold = async call =>
        {
            if (call.Wallet == "throwaway")
            {
                // Neither read is answered until both are waiting — they fail together.
                if (Interlocked.Increment(ref arrived) == 2) bothInFlight.SetResult();
                await bothInFlight.Task;
            }
            else if (call.Method == "listwallets" && Interlocked.Increment(ref probes) == 2)
            {
                // The second read learns which wallets are loaded only AFTER the first has
                // switched — the order the app's single UI thread always produces.
                await switched.Task;
            }
        };
        var svc = Rpc(daemon, ctx, notice);

        var balances = svc.GetBalancesAsync();
        var transactions = svc.ListTransactionsAsync(25);
        await Task.WhenAll(balances, transactions);        // was: the second threw RpcException -18

        Assert.Equal(200m, (await balances)!.Mine!.Trusted);
        Assert.Equal("Primary", ctx.Active);
        Assert.Equal(1, ctx.SetActiveCalls);               // switched once, not once per read
        Assert.Equal("throwaway", notice.Current!.From);
        Assert.Equal(2, daemon.Calls.Count(c => c.Wallet == "Primary" && c.Method != "listwallets"));
    }

    [Fact]
    public async Task A_read_that_fails_for_another_reason_is_still_not_treated_as_a_missing_wallet()
    {
        // -18 while the active wallet IS loaded (and nobody switched): not ours to heal.
        var ctx = new FakeWalletContext("Primary");
        var daemon = Daemon("Primary");
        daemon.Before = call => { if (call.Method == "getbalances") daemon.Loaded.Remove("Primary"); else daemon.Loaded.Add("Primary"); };
        var svc = Rpc(daemon, ctx);

        var ex = await Assert.ThrowsAsync<RpcException>(() => svc.GetBalancesAsync());

        Assert.Equal(-18, ex.Code);
        Assert.Equal(0, ctx.SetActiveCalls);
    }

    [Fact]
    public async Task No_notice_when_nothing_was_switched()
    {
        var notice = new WalletSwitchNotice();
        var svc = Rpc(Daemon("Savings"), new FakeWalletContext("Savings"), notice);

        await svc.GetBalancesAsync();

        Assert.Null(notice.Current);
    }

    [Fact]
    public void The_notice_keeps_the_wallet_the_user_was_on_until_it_is_acknowledged()
    {
        var notice = new WalletSwitchNotice();
        notice.Record("Savings", "Primary");
        notice.Record("Primary", "Other");                     // a second switch before the user looked

        Assert.Equal("Savings", notice.Current!.From);
        Assert.Equal("Other", notice.Current.To);

        notice.Dismiss();
        Assert.Null(notice.Current);
        notice.Dismiss();                                      // idempotent
    }

    // ════════════════════════ a send names its wallet ════════════════════════

    [Fact]
    public async Task A_pinned_send_goes_to_its_wallet_whatever_the_active_wallet_is()
    {
        // The app has moved to "Primary" (a read healed) since the payment was reviewed on "Savings".
        var ctx = new FakeWalletContext("Primary");
        var daemon = Daemon("Primary", "Savings");
        var svc = Rpc(daemon, ctx);

        var txid = await svc.SendFromWalletAsync("Savings", "BDest", 2.5m);

        Assert.Equal(TxId, txid);
        Assert.Equal(("sendtoaddress", "Savings"), (daemon.Calls.Single().Method, daemon.Calls.Single().Wallet));
    }

    [Fact]
    public async Task A_pinned_send_fails_when_its_wallet_is_gone_and_touches_no_other()
    {
        var ctx = new FakeWalletContext("Primary");
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, ctx);

        var ex = await Assert.ThrowsAsync<WalletNotLoadedException>(() => svc.SendFromWalletAsync("Savings", "BDest", 2.5m));

        Assert.Equal("Savings", ex.Wallet);
        Assert.DoesNotContain(daemon.Calls, c => c.Wallet == "Primary");
        Assert.Single(daemon.Calls);
    }

    [Theory]
    [InlineData(null)]                                                                    // sendtoaddress path
    [InlineData(BlazecoinWallet.Core.Services.Mining.BlazecoinAddressKind.PostQuantum)]  // `send` path
    public async Task A_send_request_carries_the_wallet_it_was_reviewed_on(BlazecoinWallet.Core.Services.Mining.BlazecoinAddressKind? change)
    {
        var ctx = new FakeWalletContext("Primary");             // the active wallet moved after review
        var daemon = Daemon("Primary", "Savings");
        var svc = Rpc(daemon, ctx);

        var txid = await svc.SendAsync(new SendRequest("BDest", 1m, false, change) { Wallet = "Savings" });

        Assert.Equal(TxId, txid);
        Assert.Equal("Savings", daemon.Calls.Single().Wallet);

        daemon.Loaded.Remove("Savings");
        await Assert.ThrowsAsync<WalletNotLoadedException>(
            () => svc.SendAsync(new SendRequest("BDest", 1m, false, change) { Wallet = "Savings" }));
        Assert.DoesNotContain(daemon.Calls, c => c.Wallet == "Primary");
    }

    [Fact]
    public async Task The_empty_wallet_name_means_the_nodes_default_wallet_endpoint()
    {
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, new FakeWalletContext("Primary"));

        await svc.SendFromWalletAsync("", "BDest", 1m);

        Assert.Equal("", daemon.Calls.Single().Wallet);        // the base URL, not /wallet/Primary
    }

    [Fact]
    public async Task An_unpinned_send_request_still_goes_to_the_active_wallet()
    {
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, new FakeWalletContext("Primary"));

        await svc.SendAsync(new SendRequest("BDest", 1m, false, null));

        Assert.Equal("Primary", daemon.Calls.Single().Wallet);
    }

    // ════════════════════════ auto-payout ════════════════════════

    private sealed class Store : BlazecoinWallet.Core.Services.Storage.IKeyValueStore
    {
        private readonly Dictionary<string, string> _data = new();
        public Task<string?> GetAsync(string key) => Task.FromResult(_data.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value) { _data[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key) { _data.Remove(key); return Task.CompletedTask; }
    }

    private static AutoPayoutService Payout(BlazecoindRpcService rpc, IWalletContext ctx, IWalletSwitchNotice? notice = null) =>
        new(rpc, new Store(), new WalletBinding(ctx, notice ?? new WalletSwitchNotice()), rpc, new SendChangePolicy(rpc))
        {
            Threshold = 100m, Address = TestAddr.Payout, Live = true, Mode = "above", Batch = "single",
        };

    [Fact]
    public async Task An_armed_payout_stops_when_the_active_wallet_is_no_longer_the_one_it_was_armed_on()
    {
        // The realistic sequence, which excluding sends from the retry would NOT have stopped:
        // the tick's own balance read heals to "Primary", so the send would never see -18 — it
        // would simply be calculated on Primary's balance and sent from Primary.
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Savings");
        var svc = Rpc(daemon, ctx);
        var payout = Payout(svc, ctx);
        await payout.StartAsync();
        Assert.Equal("Savings", payout.ArmedWallet);

        daemon.Loaded.Remove("Savings");                       // the daemon restarted without it
        await payout.TickAsync(fire: true);

        Assert.Empty(daemon.Of("sendtoaddress"));              // was: 100 BLZ sent from Primary, logged as SENT
        Assert.False(payout.Enabled);
        Assert.Null(payout.ArmedWallet);
        Assert.Contains("armed on wallet \"Savings\"", payout.Reason);
        Assert.Contains("\"Primary\"", payout.Reason);
        Assert.Contains("STOPPED", string.Join("\n", payout.Log));
        Assert.DoesNotContain("SENT", string.Join("\n", payout.Log));
    }

    [Fact]
    public async Task A_payout_whose_wallet_vanishes_between_the_balance_read_and_the_send_sends_nothing()
    {
        // The reporter's exact timing: the balance read succeeds on "Savings", then it is unloaded.
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Savings");
        var svc = Rpc(daemon, ctx);
        var payout = Payout(svc, ctx);
        await payout.StartAsync();
        daemon.Before = call => { if (call.Method == "sendtoaddress") daemon.Loaded.Remove("Savings"); };

        await payout.TickAsync(fire: true);

        var send = Assert.Single(daemon.Of("sendtoaddress"));
        Assert.Equal("Savings", send.Wallet);                  // addressed to the armed wallet, by name
        Assert.DoesNotContain(daemon.Calls, c => c.Method == "sendtoaddress" && c.Wallet == "Primary");
        Assert.DoesNotContain("SENT", string.Join("\n", payout.Log));
        Assert.Contains("FAILED", string.Join("\n", payout.Log));
        Assert.Contains("not loaded on the node", string.Join("\n", payout.Log));
        Assert.Equal("Savings", ctx.Active);                   // and the failed send moved nothing
    }

    [Fact]
    public async Task A_payout_on_its_own_wallet_still_pays_and_names_that_wallet()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Savings");
        var payout = Payout(Rpc(daemon, ctx), ctx);
        await payout.StartAsync();

        await payout.TickAsync(fire: true);

        var send = Assert.Single(daemon.Of("sendtoaddress"));
        Assert.Equal("Savings", send.Wallet);
        Assert.Contains("SENT 100.00000000 BLZ", string.Join("\n", payout.Log));
        Assert.False(payout.Enabled);                          // one-shot, as before
        Assert.Null(payout.ArmedWallet);
    }

    [Fact]
    public async Task A_split_payout_stops_between_batches_when_the_user_switches_wallet()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Savings");
        var payout = Payout(Rpc(daemon, ctx), ctx);
        payout.Batch = "split";
        payout.BatchSize = 40m;                                // 100 BLZ → 40 + 40 + 20
        await payout.StartAsync();
        var sends = 0;
        daemon.Before = call =>
        {
            if (call.Method == "sendtoaddress" && ++sends == 2) ctx.SetActive("Primary");   // switched during batch 2
        };

        await payout.TickAsync(fire: true);

        Assert.Equal(2, daemon.Of("sendtoaddress").Count());                       // the third never left
        Assert.All(daemon.Of("sendtoaddress"), c => Assert.Equal("Savings", c.Wallet));
        Assert.False(payout.Enabled);
        Assert.Contains("after 2 of 3 tx", payout.Reason);
    }

    [Fact]
    public async Task Switching_wallet_by_hand_disarms_a_waiting_payout_before_it_ever_fires()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Savings");
        daemon.Balance = 50m;                                  // below the threshold: armed and waiting
        var payout = Payout(Rpc(daemon, ctx), ctx);
        await payout.StartAsync();
        await payout.TickAsync(fire: true);
        Assert.True(payout.Enabled);

        ctx.SetActive("Primary");                              // the user picks another wallet
        daemon.Balance = 500m;                                 // which is well over the threshold
        await payout.TickAsync(fire: true);

        Assert.Empty(daemon.Of("sendtoaddress"));
        Assert.False(payout.Enabled);
    }

    [Fact]
    public async Task Armed_with_no_wallet_chosen_it_pins_the_default_endpoint_and_stops_if_one_appears()
    {
        var ctx = new FakeWalletContext(null);
        var daemon = Daemon("Primary");
        var payout = Payout(Rpc(daemon, ctx), ctx);
        await payout.StartAsync();
        Assert.Equal("", payout.ArmedWallet);

        ctx.SetActive("Primary");
        await payout.TickAsync(fire: true);

        Assert.Empty(daemon.Of("sendtoaddress"));
        Assert.False(payout.Enabled);
        Assert.Contains("(default)", payout.Reason);
    }

    [Fact]
    public async Task Re_arming_pins_the_wallet_that_is_active_then()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Savings");
        var payout = Payout(Rpc(daemon, ctx), ctx);
        await payout.StartAsync();
        await payout.StopAsync();
        Assert.Null(payout.ArmedWallet);

        ctx.SetActive("Primary");
        await payout.StartAsync();
        await payout.TickAsync(fire: true);

        Assert.Equal("Primary", Assert.Single(daemon.Of("sendtoaddress")).Wallet);
    }

    // ════════ a switch the user has not acknowledged stops every write (bug-bounty report 2026-11-09) ════════
    // Only the Send page named its wallet. Everything else that changes a wallet — the Console,
    // a new address, a label, a passphrase, signing — followed the app's active wallet, so in the
    // moment after a read re-pointed the app, the next write ran against a wallet the user had
    // not been shown yet. Now nothing but a read runs until the notice has been acknowledged.

    private static async Task<(BlazecoindRpcService Svc, StubDaemon Daemon, FakeWalletContext Ctx, WalletSwitchNotice Notice)> HealedAsync()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary");                        // "Savings" was unloaded mid-session
        var notice = new WalletSwitchNotice();
        var svc = Rpc(daemon, ctx, notice);
        await svc.GetBalancesAsync();                          // an ordinary read re-points the app
        Assert.Equal("Primary", ctx.Active);
        Assert.NotNull(notice.Current);
        daemon.Calls.Clear();
        return (svc, daemon, ctx, notice);
    }

    [Theory]
    [InlineData("sendtoaddress")]
    [InlineData("send")]
    [InlineData("getnewaddress")]
    [InlineData("setlabel")]
    [InlineData("encryptwallet")]
    [InlineData("walletpassphrase")]
    [InlineData("walletpassphrasechange")]
    [InlineData("signrawtransactionwithwallet")]
    [InlineData("fundrawtransaction")]
    [InlineData("importdescriptors")]
    // (walletlock was on this list until the audit of 2026-11-09: locking is always safe, and is
    // now let through - Locking_the_wallet_is_never_held_up_by_the_notice.)
    public async Task A_write_does_not_run_until_the_wallet_switch_is_acknowledged(string method)
    {
        var (svc, daemon, _, notice) = await HealedAsync();

        var ex = await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.ExecuteConsoleCommandAsync("Primary", method, []));

        Assert.Empty(daemon.Calls);                            // was: the call ran against "Primary"
        Assert.Contains("Savings", ex.Message);
        Assert.Contains("Primary", ex.Message);
        Assert.Contains("nothing was done", ex.Message);

        notice.Dismiss();                                      // the user pressed OK on the notice
        await svc.ExecuteConsoleCommandAsync("Primary", method, []);
        Assert.Equal((method, "Primary"), (daemon.Calls.Single().Method, daemon.Calls.Single().Wallet));
    }

    [Fact]
    public async Task The_typed_writes_wait_for_the_acknowledgement_too()
    {
        var (svc, daemon, _, _) = await HealedAsync();

        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.SendToAddressAsync("BDest", 1m));
        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.SendAsync(new SendRequest("BDest", 1m, false, null)));
        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.SetLabelAsync("BAddr", "rent"));
        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.EncryptWalletAsync("pass phrase"));
        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.UnlockWalletAsync("pass phrase", 60));

        Assert.Empty(daemon.Calls);
    }

    [Fact]
    public async Task Reads_carry_on_while_the_switch_is_unacknowledged()
    {
        var (svc, daemon, _, _) = await HealedAsync();

        var balances = await svc.GetBalancesAsync();

        Assert.Equal(200m, balances!.Mine!.Trusted);
        Assert.Equal("Primary", daemon.Calls.Single().Wallet);
    }

    [Fact]
    public async Task A_send_that_names_its_wallet_is_not_held_up_by_the_notice()
    {
        // The Send page reviews and sends against a named wallet and has its own check that the
        // active wallet has not moved. The notice must not block a payment reviewed on "Primary".
        var (svc, daemon, _, _) = await HealedAsync();

        var txid = await svc.SendFromWalletAsync("Primary", "BDest", 2.5m);

        Assert.Equal(TxId, txid);
        Assert.Equal("Primary", daemon.Calls.Single().Wallet);
    }

    [Fact]
    public async Task The_notice_is_up_before_the_app_points_at_the_other_wallet()
    {
        // Closes the gap: a write starting between "context moved" and "notice raised" would
        // have seen the new wallet and no notice.
        var ctx = new FakeWalletContext("Savings");
        var notice = new WalletSwitchNotice();
        WalletSwitch? seenWhenContextMoved = null;
        ctx.Changed += () => seenWhenContextMoved = notice.Current;
        var svc = Rpc(Daemon("Primary"), ctx, notice);

        await svc.GetBalancesAsync();

        Assert.NotNull(seenWhenContextMoved);
        Assert.Equal("Primary", seenWhenContextMoved!.To);
    }

    // ════════════════════════ the Console names its wallet ════════════════════════

    [Fact]
    public async Task A_console_command_runs_against_the_wallet_the_console_showed()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary", "Savings");
        var svc = Rpc(daemon, ctx, new WalletSwitchNotice());

        await svc.ExecuteConsoleCommandAsync("Savings", "sendtoaddress", ["BDest", 1.0m]);

        Assert.Equal(("sendtoaddress", "Savings"), (daemon.Calls.Single().Method, daemon.Calls.Single().Wallet));
    }

    [Fact]
    public async Task A_console_command_for_an_unloaded_wallet_fails_and_goes_nowhere_else()
    {
        // The report's scenario: the user is on "Savings", it is unloaded, and a Console read
        // used to re-point the app at "Primary" — the next sendtoaddress then paid from there.
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary");
        var notice = new WalletSwitchNotice();
        var svc = Rpc(daemon, ctx, notice);

        var read = await Assert.ThrowsAsync<WalletNotLoadedException>(() => svc.ExecuteConsoleCommandAsync("Savings", "getbalance", []));
        var send = await Assert.ThrowsAsync<WalletNotLoadedException>(() => svc.ExecuteConsoleCommandAsync("Savings", "sendtoaddress", ["BDest", 1.0m]));

        Assert.Equal("Savings", read.Wallet);
        Assert.Equal("Savings", send.Wallet);
        Assert.All(daemon.Calls, c => Assert.Equal("Savings", c.Wallet));   // nothing reached "Primary"
        Assert.Equal("Savings", ctx.Active);                                // and the Console moved nothing
        Assert.Equal(0, ctx.SetActiveCalls);
        Assert.Null(notice.Current);
    }

    [Fact]
    public async Task A_console_command_waits_for_the_wallet_switch_to_be_acknowledged()
    {
        var (svc, daemon, _, notice) = await HealedAsync();

        // Even one addressed to the wallet now active: the Console's label may have changed
        // under the user's hands a moment before they pressed Enter.
        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.ExecuteConsoleCommandAsync("Primary", "sendtoaddress", ["BDest", 1.0m]));
        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.ExecuteConsoleCommandAsync("Primary", "getblockchaininfo", []));
        Assert.Empty(daemon.Calls);

        notice.Dismiss();
        await svc.ExecuteConsoleCommandAsync("Primary", "sendtoaddress", ["BDest", 1.0m]);
        Assert.Equal("Primary", daemon.Calls.Single().Wallet);
    }

    [Fact]
    public async Task A_console_command_is_refused_when_the_console_showed_a_wallet_that_is_no_longer_active()
    {
        var ctx = new FakeWalletContext("Primary");            // the user chose another wallet elsewhere
        var daemon = Daemon("Primary", "Savings");
        var svc = Rpc(daemon, ctx, new WalletSwitchNotice());

        var ex = await Assert.ThrowsAsync<ConsoleWalletChangedException>(() => svc.ExecuteConsoleCommandAsync("Savings", "sendtoaddress", ["BDest", 1.0m]));

        Assert.Contains("nothing was run", ex.Message);
        Assert.Equal(("Savings", "Primary", -4), (ex.Shown, ex.ActiveNow, ex.Code));
        Assert.Empty(daemon.Calls);
    }

    [Fact]
    public async Task A_console_with_no_wallet_chosen_uses_the_node_endpoint()
    {
        var ctx = new FakeWalletContext(null);
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, ctx, new WalletSwitchNotice());

        await svc.ExecuteConsoleCommandAsync("", "getblockchaininfo", []);

        Assert.Equal("", daemon.Calls.Single().Wallet);
    }

    // ════════════════════════ node housekeeping is not a wallet call ════════════════════════

    [Fact]
    public async Task Stopping_the_node_is_never_held_up_by_a_wallet_notice()
    {
        // The app stops its own daemon on exit; that must work whatever the wallet state.
        var (svc, daemon, _, _) = await HealedAsync();

        await svc.ExecuteNodeCommandAsync("stop");

        Assert.Equal(("stop", ""), (daemon.Calls.Single().Method, daemon.Calls.Single().Wallet));
    }

    // ════════ the hold covers every way a write gets its wallet (audit 2026-11-09) ════════
    // The 2026-11-09 fix held back the calls that FOLLOW the active wallet. The Send page and
    // auto-payout Start take the active wallet as a pin instead, and a pin was taken with no look
    // at the notice — so in the moment after a read re-pointed the app, Review, Send Max, a
    // straight send or Start bound themselves to a wallet the user had not acknowledged yet.

    [Fact]
    public async Task Auto_payout_does_not_arm_on_a_wallet_the_user_has_not_acknowledged()
    {
        var (svc, daemon, ctx, notice) = await HealedAsync();
        var payout = Payout(svc, ctx, notice);

        await payout.StartAsync();                             // was: armed on "Primary", the wallet the app moved to

        Assert.False(payout.Enabled);
        Assert.Null(payout.ArmedWallet);
        Assert.Contains("Savings", payout.Reason);
        Assert.Contains("Primary", payout.Reason);
        Assert.Contains("nothing was done", payout.Reason);
        // The page draws the reason line only while the engine is on; what it always draws is the
        // log - which is where the "STOPPED" message of a wallet change is read too.
        Assert.True(payout.ShowReason);                        // was: drawn only while the engine is on, so never for a refused Start
        var said = Assert.Single(payout.Log);
        Assert.Contains("NOT started", said);
        Assert.Contains("Savings", said);
        Assert.Contains("Primary", said);
        Assert.Empty(daemon.Calls);                            // not even the status read

        notice.Dismiss();                                      // the user pressed OK on the notice
        await payout.StartAsync();
        Assert.True(payout.Enabled);
        Assert.Equal("Primary", payout.ArmedWallet);
        Assert.DoesNotContain("nothing was done", payout.Reason);   // the refusal does not outlive the Start that worked

        await payout.StopAsync();
        Assert.False(payout.ShowReason);                       // stopped by hand: nothing to explain
    }

    // ════════ the fee shown at review belongs to the wallet the send will leave (audit 2026-11-09) ════════

    [Theory]
    [InlineData(null)]                                                                    // the sendtoaddress shape
    [InlineData(BlazecoinWallet.Core.Services.Mining.BlazecoinAddressKind.PostQuantum)]  // the `send` shape, with a change type
    public async Task A_fee_estimate_runs_against_the_wallet_the_request_names(BlazecoinWallet.Core.Services.Mining.BlazecoinAddressKind? change)
    {
        // Reviewed on "Savings"; the app is on "Primary" by the time the estimate runs.
        var ctx = new FakeWalletContext("Primary");
        var daemon = Daemon("Primary", "Savings");
        var svc = Rpc(daemon, ctx);

        var fee = await svc.EstimateFeeAsync(new SendRequest("BDest", 1m, false, change) { Wallet = "Savings" });

        Assert.Equal(0.00025m, fee);
        Assert.Equal("Savings", Assert.Single(daemon.Of("fundrawtransaction")).Wallet);   // was: "Primary", the active one
        Assert.DoesNotContain(daemon.Calls, c => c.Wallet == "Primary");
    }

    [Fact]
    public async Task A_fee_estimate_for_a_named_wallet_that_is_gone_is_unknown_and_touches_no_other()
    {
        var ctx = new FakeWalletContext("Primary");
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, ctx);

        var fee = await svc.EstimateFeeAsync(new SendRequest("BDest", 1m, false, null) { Wallet = "Savings" });

        Assert.Null(fee);                                      // best effort, as before
        Assert.DoesNotContain(daemon.Calls, c => c.Wallet == "Primary");
        Assert.Equal(0, ctx.SetActiveCalls);
    }

    [Fact]
    public async Task An_unpinned_fee_estimate_still_uses_the_active_wallet()
    {
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, new FakeWalletContext("Primary"));

        Assert.Equal(0.00025m, await svc.EstimateFeeAsync(new SendRequest("BDest", 1m, false, null)));
        Assert.Equal(0.00025m, await svc.EstimateSendFeeAsync("BDest", 1m));

        Assert.All(daemon.Of("fundrawtransaction"), c => Assert.Equal("Primary", c.Wallet));
    }

    // ════════ what the hold stops, and what it must not (audit 2026-11-09) ════════

    [Fact]
    public async Task Locking_the_wallet_is_never_held_up_by_the_notice()
    {
        // Locking cannot lose anything on any wallet; refusing it left a wallet unlocked.
        var (svc, daemon, _, _) = await HealedAsync();

        await svc.LockWalletAsync();                           // was: WalletSwitchPendingException

        Assert.Equal(("walletlock", "Primary"), (daemon.Calls.Single().Method, daemon.Calls.Single().Wallet));
    }

    [Fact]
    public async Task Locking_is_still_never_repeated_against_another_wallet()
    {
        // Allowed while a switch is unacknowledged is one question; "may be re-sent to a different
        // wallet after -18" is another, and for walletlock the answer to that one stays no.
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary");
        var svc = Rpc(daemon, ctx);

        await Assert.ThrowsAsync<WalletNotLoadedException>(() => svc.LockWalletAsync());

        Assert.Equal(("walletlock", "Savings"), (daemon.Calls.Single().Method, daemon.Calls.Single().Wallet));
        Assert.Equal(0, ctx.SetActiveCalls);
    }

    [Fact]
    public void What_may_run_while_a_switch_is_unacknowledged_is_a_list_of_its_own()
    {
        // Pinned, like the healable reads: letting a call through the hold is a decision someone
        // has to make on purpose - and it no longer changes what may be repeated on another wallet.
        Assert.Equal(
            new[] { "getaddressesbylabel", "getaddressinfo", "getbalance", "getbalances", "gettransaction",
                    "getwalletinfo", "listdescriptors", "listlabels", "listtransactions", "listunspent", "walletlock" },
            BlazecoindRpcService.AllowedWhileSwitchUnacknowledged.OrderBy(m => m, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("walletlock", BlazecoindRpcService.HealableWalletReads);
        // A heal is what raises the notice; the read that healed, and the page's other reads, must go on.
        Assert.Subset(
            new HashSet<string>(BlazecoindRpcService.AllowedWhileSwitchUnacknowledged),
            new HashSet<string>(BlazecoindRpcService.HealableWalletReads));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(BlazecoinWallet.Core.Services.Mining.BlazecoinAddressKind.PostQuantum)]
    public async Task A_fee_estimate_that_follows_the_active_wallet_says_why_it_was_held(BlazecoinWallet.Core.Services.Mining.BlazecoinAddressKind? change)
    {
        // The dry run is still held (it would price another wallet's coins) - but the refusal
        // used to be swallowed with every other failure, so the review showed no fee and no reason.
        var (svc, daemon, _, _) = await HealedAsync();

        var ex = await Assert.ThrowsAsync<WalletSwitchPendingException>(
            () => svc.EstimateFeeAsync(new SendRequest("BDest", 1m, false, change)));

        Assert.Contains("nothing was done", ex.Message);
        Assert.Empty(daemon.Of("fundrawtransaction"));
    }

    [Fact]
    public async Task The_plain_fee_estimate_says_why_it_was_held_too()
    {
        var (svc, daemon, _, _) = await HealedAsync();

        await Assert.ThrowsAsync<WalletSwitchPendingException>(() => svc.EstimateSendFeeAsync("BDest", 1m));

        Assert.Empty(daemon.Of("fundrawtransaction"));
    }

    [Fact]
    public async Task A_fee_estimate_that_fails_for_any_other_reason_is_still_just_unknown()
    {
        var ctx = new FakeWalletContext("Savings");
        var daemon = Daemon("Primary");                        // -18 on the dry run: not a read, so no heal
        var svc = Rpc(daemon, ctx);

        Assert.Null(await svc.EstimateFeeAsync(new SendRequest("BDest", 1m, false, null)));
        Assert.Null(await svc.EstimateSendFeeAsync("BDest", 1m));
        Assert.Equal(0, ctx.SetActiveCalls);
    }

    // ════════ the notice is not optional (audit 2026-11-09) ════════

    [Fact]
    public void The_rpc_service_cannot_be_built_without_a_switch_notice()
    {
        // It used to default to null, and a null notice switched the hold off without a word.
        var notice = typeof(BlazecoindRpcService).GetConstructors().Single().GetParameters()
            .Single(p => p.ParameterType == typeof(IWalletSwitchNotice));

        Assert.False(notice.IsOptional);
        Assert.False(notice.HasDefaultValue);
    }
}

using System.Net;
using System.Text;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.DataEncoders;
using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Bug-bounty round three (2026-11-06): three places the 1.3.3 read-path work had not reached.
/// <list type="bullet">
/// <item>"Recover an Old Wallet" summed whatever coins the gateway listed for the pasted key, and
/// one unprovable row then failed the whole sweep.</item>
/// <item>The transaction page showed the outputs of whatever transaction came back, under a badge
/// earned by the id that was asked for.</item>
/// <item>Restore discovery believed a single gateway's "unused", and the shortened address count
/// was saved — so the coins stayed hidden after switching gateway.</item>
/// </list>
/// Same production wiring as the rest of this class: the real <see cref="IndexerDataService"/>
/// over a scripted gateway, the real verifier, coins from <see cref="MinedBlockFixture"/>.
/// </summary>
public partial class ReadPathVerificationTests
{
    // The fixture block really pays this key 50 BLZ (transaction 11) — an "old wallet" to recover.
    private static readonly Key OldKey = new(Encoders.Hex.DecodeData("11".PadLeft(64, '1')));
    private static readonly string OldWif = OldKey.GetWif(BlazecoinNetwork.Instance).ToString();
    private const long OldKeyRealSats = 5_000_000_000;
    private static readonly string OtherFakeTxId = new('d', 64);

    private static void ScriptReportOne(Gateway gw)
    {
        gw.Coin(F.Foreign, F.TxId(11), 0, OldKeyRealSats);                  // real, confirmed: 50 BLZ
        gw.Coin(F.Foreign, FakeTxId, 0, 7_000_000_000);                     // made up, "confirmed": 70 BLZ
        gw.Coin(F.Foreign, OtherFakeTxId, 1, 5_000_000_000, confs: 0);      // made up, unconfirmed: 50 BLZ
    }

    // ════════════════════════ report 1: the old-key check and sweep ════════════════════════

    [Fact]
    public async Task The_old_key_check_counts_only_coins_proven_on_the_chain()
    {
        var w = await OpenAsync(ScriptReportOne);
        Assert.Equal(F.Foreign, OldKey.GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString());

        var check = await w.Service.CheckWifAsync(OldWif);

        Assert.True(check.Valid);
        Assert.Equal(OldKeyRealSats, check.SpendableSatoshis);              // was 170 BLZ: every row as served
        Assert.Equal(7_000_000_000, check.UnverifiedSatoshis);
        Assert.Equal(5_000_000_000, check.UnconfirmedSatoshis);
        Assert.Equal(0, check.ImmatureSatoshis);
        Assert.Equal(3, check.UtxoCount);
        Assert.False(string.IsNullOrWhiteSpace(check.UnverifiedReason));
    }

    [Fact]
    public async Task The_old_key_check_shows_the_chains_value_not_the_gateways()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Foreign, F.TxId(11), 0, 9_999_000_000_000));   // a real coin, inflated

        var check = await w.Service.CheckWifAsync(OldWif);

        Assert.Equal(OldKeyRealSats, check.SpendableSatoshis);
        Assert.Equal(0, check.UnverifiedSatoshis);
    }

    [Fact]
    public async Task A_made_up_coin_no_longer_blocks_the_sweep_of_the_real_ones()
    {
        var w = await OpenAsync(ScriptReportOne);

        var result = await w.Service.SweepWifAsync(OldWif);

        Assert.True(result.Success, result.Error);                          // was: "Could not verify your coins…", nothing sent
        var tx = Transaction.Parse(w.Gateway.BroadcastHex!, BlazecoinNetwork.Instance);
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(F.TxId(11), input.PrevOut.Hash.ToString());
        Assert.Equal(0u, input.PrevOut.N);
        Assert.InRange(tx.Outputs.Sum(o => o.Value.Satoshi), 1, OldKeyRealSats);
    }

    [Fact]
    public async Task A_sweep_with_nothing_proven_sends_nothing_and_says_why()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Foreign, FakeTxId, 0, 7_000_000_000));

        var result = await w.Service.SweepWifAsync(OldWif);

        Assert.False(result.Success);
        Assert.Contains("Could not verify this key's coins against the chain", result.Error);
        Assert.Null(w.Gateway.BroadcastHex);
    }

    [Fact]
    public async Task A_sweep_of_only_unconfirmed_coins_waits_for_a_confirmation()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Foreign, F.TxId(11), 0, OldKeyRealSats, confs: 0));

        var check = await w.Service.CheckWifAsync(OldWif);
        var result = await w.Service.SweepWifAsync(OldWif);

        Assert.Equal(0, check.SpendableSatoshis);
        Assert.Equal(OldKeyRealSats, check.UnconfirmedSatoshis);
        Assert.False(result.Success);
        Assert.Contains("not confirmed yet", result.Error);
        Assert.Null(w.Gateway.BroadcastHex);
    }

    // ════════════════════════ report 2: the transaction page's data ════════════════════════

    [Fact]
    public async Task A_transaction_is_returned_when_it_hashes_to_the_id_asked_for()
    {
        var w = await OpenAsync();

        var lookup = await w.Service.GetTransactionAsync(F.TxId(0));

        Assert.False(lookup.Mismatch);
        Assert.Equal(F.TxId(0), lookup.Details!.TxId);
        Assert.Equal(1_000_000_000, lookup.Details.TotalOut);
    }

    [Fact]
    public async Task A_substituted_transaction_is_refused_not_shown()
    {
        var w = await OpenAsync(gw => gw.Substitute[F.TxId(0)] = F.TxId(11));   // asked for 10 BLZ to us, served 50 BLZ to someone

        var lookup = await w.Service.GetTransactionAsync(F.TxId(0));

        Assert.True(lookup.Mismatch);
        Assert.Null(lookup.Details);                                            // was: the 50 BLZ outputs, under the real id
    }

    [Fact]
    public async Task A_transaction_the_gateway_does_not_have_is_simply_missing()
    {
        var w = await OpenAsync();

        var lookup = await w.Service.GetTransactionAsync(FakeTxId);

        Assert.False(lookup.Mismatch);
        Assert.Null(lookup.Details);
    }

    // ════════════════════════ report 3: restore discovery ════════════════════════

    /// <summary>Two gateways behind the wallet's failover list. Each answers the address-summary
    /// question from its own set of "used" addresses; everything else is a 404.</summary>
    private sealed class TwoGateways : HttpMessageHandler
    {
        public readonly HashSet<string> UsedOnA = new(StringComparer.Ordinal);
        public readonly HashSet<string> UsedOnB = new(StringComparer.Ordinal);
        public bool BIsDown;
        public int CallsToA, CallsToB;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var isA = request.RequestUri!.Host == "a.test";
            if (isA) Interlocked.Increment(ref CallsToA); else Interlocked.Increment(ref CallsToB);
            if (!isA && BIsDown) throw new HttpRequestException("connection refused");

            var path = request.RequestUri.AbsolutePath;
            var parts = path.TrimEnd('/').Split('/');
            if (path.EndsWith("/utxos")) return Task.FromResult(Reply(HttpStatusCode.OK, "[]"));
            if (path.Contains("/address/") && (isA ? UsedOnA : UsedOnB).Contains(parts[^1]))
                return Task.FromResult(Reply(HttpStatusCode.OK, """{"balance":0,"totalReceived":1,"totalSent":1,"txCount":2}"""));
            return Task.FromResult(Reply(HttpStatusCode.NotFound, """{"error":"not found"}"""));
        }

        private static HttpResponseMessage Reply(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static (LiteWalletService Service, IndexerDataService Indexer) Over(TwoGateways fleet, params string[] gateways)
    {
        var failover = new GatewayFailoverHandler(gateways, fleet);
        var indexer = new IndexerDataService(new HttpClient(failover) { BaseAddress = failover.Primary }, NullLogger<IndexerDataService>.Instance);
        return (new LiteWalletService(new MemoryVault(), indexer, verifier: new LiteTxVerifier(indexer)), indexer);
    }

    /// <summary>The report's layout: receive slots 0, 5, 15 and 30 have been used. Gateway A admits
    /// to the first three and says "unused" from slot 20 on; gateway B tells the truth.</summary>
    private static TwoGateways ReportThreeFleet()
    {
        var hd = LiteHdWallet.Restore(F.Mnemonic);
        var fleet = new TwoGateways();
        foreach (var slot in new[] { 0, 5, 15 }) { fleet.UsedOnA.Add(hd.GetReceiveAddress(slot)); fleet.UsedOnB.Add(hd.GetReceiveAddress(slot)); }
        fleet.UsedOnB.Add(hd.GetReceiveAddress(30));
        return fleet;
    }

    [Fact]
    public async Task Restore_asks_a_second_gateway_before_it_believes_unused()
    {
        var fleet = ReportThreeFleet();
        var (service, _) = Over(fleet, "https://a.test/", "https://b.test/");

        await service.RestoreWalletAsync(F.Mnemonic);

        Assert.Equal(31, service.RevealedCount);                // was 16: gateway A's word ended the scan
        Assert.True(fleet.CallsToB > 0);
    }

    [Fact]
    public async Task With_one_gateway_there_is_nobody_else_to_ask()
    {
        var fleet = ReportThreeFleet();
        var (service, _) = Over(fleet, "https://a.test/");

        await service.RestoreWalletAsync(F.Mnemonic);

        Assert.Equal(16, service.RevealedCount);                // the limit the look-ahead below exists for
        Assert.Equal(0, fleet.CallsToB);
    }

    [Fact]
    public async Task A_second_gateway_that_is_down_does_not_break_the_restore()
    {
        var fleet = ReportThreeFleet();
        fleet.BIsDown = true;
        var (service, _) = Over(fleet, "https://a.test/", "https://b.test/");

        await service.RestoreWalletAsync(F.Mnemonic);

        Assert.Equal(16, service.RevealedCount);
    }

    [Fact]
    public async Task Used_on_either_gateway_is_used_and_the_first_yes_is_enough()
    {
        var hd = LiteHdWallet.Restore(F.Mnemonic);
        var fleet = new TwoGateways();
        fleet.UsedOnA.Add(hd.GetReceiveAddress(1));
        fleet.UsedOnB.Add(hd.GetReceiveAddress(2));
        var (_, indexer) = Over(fleet, "https://a.test/", "https://b.test/");

        Assert.True(await indexer.IsAddressUsedByAnyGatewayAsync(hd.GetReceiveAddress(1)));
        Assert.Equal(0, fleet.CallsToB);                                         // A said yes: no second question
        Assert.True(await indexer.IsAddressUsedByAnyGatewayAsync(hd.GetReceiveAddress(2)));
        Assert.False(await indexer.IsAddressUsedByAnyGatewayAsync(hd.GetReceiveAddress(3)));
        Assert.Equal(2, fleet.CallsToB);
    }

    /// <summary>A new session on the same device: keys dropped, vault and saved counts kept —
    /// with the look-ahead on, as the composition root wires it for every head.</summary>
    private static async Task NewSessionAsync(Wallet w)
    {
        w.Service.LookAheadOnFirstRead = true;
        w.Service.Lock();
        Assert.True(await w.Service.UnlockAsync());
    }

    [Fact]
    public async Task A_later_session_finds_the_coins_a_restore_was_told_did_not_exist()
    {
        var w = await OpenAsync();                                              // the gateway admits to slot 0 only
        Assert.Equal(1, w.Service.RevealedCount);

        w.Gateway.Coin(F.Receive1, F.TxId(10), 0, 250_000_000);                 // an honest gateway now: 2.5 BLZ on slot 1
        await NewSessionAsync(w);
        await w.Service.GetBalanceBreakdownAsync();
        await w.Service.WhenLookAheadIdleAsync();

        Assert.Equal(2, w.Service.RevealedCount);                               // was 1 for ever, until another restore
        Assert.Equal(2, await w.Store.GetRevealedAddressCountAsync());
        await w.Service.WhenVerificationIdleAsync();
        Assert.Equal(250_000_000, (await w.Service.GetBalanceBreakdownAsync()).Spendable);
    }

    [Fact]
    public async Task The_look_ahead_tells_the_pages_when_it_found_more_addresses()
    {
        var w = await OpenAsync();
        w.Gateway.Coin(F.Receive1, F.TxId(10), 0, 250_000_000);
        await NewSessionAsync(w);
        var raised = 0;
        w.Service.VerificationChanged += () => Interlocked.Increment(ref raised);

        await w.Service.GetBalanceBreakdownAsync();
        await w.Service.WhenLookAheadIdleAsync();

        Assert.True(raised > 0);
    }

    [Fact]
    public async Task A_made_up_coin_further_out_extends_nothing()
    {
        var w = await OpenAsync();
        w.Gateway.Coin(F.Receive1, FakeTxId, 0, FakeSats);                      // listed, unprovable

        await NewSessionAsync(w);
        await w.Service.GetBalanceBreakdownAsync();
        await w.Service.WhenLookAheadIdleAsync();

        Assert.Equal(1, w.Service.RevealedCount);
    }

    [Fact]
    public async Task The_address_count_never_moves_back()
    {
        var w = await OpenAsync(gw => gw.Coin(F.Receive1, F.TxId(10), 0, 250_000_000));   // found at restore
        Assert.Equal(2, w.Service.RevealedCount);

        w.Gateway.Coins.Clear();                                                // a gateway that now hides everything
        w.Gateway.Summaries.Clear();
        await NewSessionAsync(w);
        await w.Service.GetBalanceBreakdownAsync();
        await w.Service.WhenLookAheadIdleAsync();

        Assert.Equal(2, w.Service.RevealedCount);
    }

    [Fact]
    public async Task The_look_ahead_runs_once_a_session_and_not_after_a_restore()
    {
        var w = await OpenAsync();
        w.Service.LookAheadOnFirstRead = true;
        await w.Service.GetBalanceBreakdownAsync();
        Assert.True(w.Service.WhenLookAheadIdleAsync().IsCompleted);            // the restore itself just scanned

        await NewSessionAsync(w);
        await w.Service.GetBalanceBreakdownAsync();
        var first = w.Service.WhenLookAheadIdleAsync();
        await first;
        await w.Service.GetBalanceBreakdownAsync();

        Assert.Same(first, w.Service.WhenLookAheadIdleAsync());
    }
}

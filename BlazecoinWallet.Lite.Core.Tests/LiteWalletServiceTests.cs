using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// End-to-end tests of the lite send pipeline against a scripted gateway: the service reads
/// UTXOs from the (stubbed) Indexer routes, signs client-side, and the test DECODES the hex
/// it posted to the broadcast route to prove the network would receive exactly the intended
/// payment. Also pins coinbase-maturity filtering, vault round-trips, and error surfaces.
/// </summary>
public class LiteWalletServiceTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>In-memory vault (the app heads bring Keystore/Keychain).</summary>
    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    /// <summary>Scripted gateway: canned JSON per path prefix + capture of the broadcast body.</summary>
    private sealed class ScriptedGateway : HttpMessageHandler
    {
        public string UtxosJson = "[]";
        public string? AddressJson;
        public HttpStatusCode BroadcastStatus = HttpStatusCode.OK;
        public string BroadcastBody = "{\"txId\":\"cafe\"}";
        public string? CapturedBroadcastHex;

        /// <summary>Per-address ledger pages (the /transactions endpoint); addresses not
        /// listed get an empty page. Keyed by address so history tests can serve DIFFERENT
        /// rows to the receive and change addresses.</summary>
        public Dictionary<string, string> HistoryJsonByAddress { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/utxos"))
                return Json(HttpStatusCode.OK, UtxosJson);
            if (path.EndsWith("/transactions"))
            {
                var segments = path.TrimEnd('/').Split('/');
                var addr = segments[^2]; // .../address/{addr}/transactions
                return Json(HttpStatusCode.OK, HistoryJsonByAddress.TryGetValue(addr, out var j)
                    ? j : "{\"page\":1,\"pageSize\":25,\"total\":0,\"items\":[]}");
            }
            if (path.Contains("/api/tx/broadcast"))
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                CapturedBroadcastHex = JsonDocument.Parse(body).RootElement.GetProperty("hex").GetString();
                return Json(BroadcastStatus, BroadcastBody);
            }
            if (path.Contains("/api/address/"))
                return AddressJson == null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(HttpStatusCode.OK, AddressJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static (LiteWalletService service, ScriptedGateway gateway, MemoryVault vault) Build()
    {
        var gateway = new ScriptedGateway();
        var http = new HttpClient(gateway) { BaseAddress = new Uri("https://gateway.test/") };
        var data = new IndexerDataService(http, NullLogger<IndexerDataService>.Instance);
        var vault = new MemoryVault();
        return (new LiteWalletService(vault, data), gateway, vault);
    }

    private static string UtxoJson(string txId, int vout, long sats, int confs, bool coinbase = false) =>
        $"{{\"txId\":\"{txId}\",\"outputIndex\":{vout},\"amount\":{sats},\"confirmations\":{confs},\"blockHeight\":100,\"isCoinbase\":{coinbase.ToString().ToLower()}}}";

    [Fact]
    public async Task Create_store_lock_unlock_roundtrip()
    {
        var (service, _, vault) = Build();
        Assert.False(await service.HasWalletAsync());

        var words = await service.CreateNewWalletAsync();
        Assert.Equal(words, vault.Stored);
        var address = service.Address;

        // A fresh service over the same stored words unlocks to the same address.
        var (service2, _, vault2) = Build();
        vault2.Stored = words;
        Assert.True(await service2.UnlockAsync());
        Assert.Equal(address, service2.Address);

        await service2.ResetAsync();
        Assert.Null(vault2.Stored);
        Assert.False(service2.IsUnlocked);
    }

    [Fact]
    public async Task Immature_coinbase_is_not_spendable_but_matured_is()
    {
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);

        gateway.UtxosJson = "[" + string.Join(",",
            UtxoJson(new string('1', 64), 0, 5_000_000, confs: 10, coinbase: true),   // immature
            UtxoJson(new string('2', 64), 0, 3_000_000, confs: 30, coinbase: true),   // matured
            UtxoJson(new string('3', 64), 0, 1_000_000, confs: 1)) + "]";             // normal

        Assert.Equal(4_000_000, await service.GetSpendableAsync());
    }

    [Fact]
    public async Task Send_signs_and_relays_a_transaction_paying_the_destination()
    {
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);

        gateway.UtxosJson = "[" + UtxoJson(new string('4', 64), 1, 10_000_000, confs: 3) + "]";
        var result = await service.SendAsync(dest, 6_000_000);

        Assert.True(result.Success, result.Error);
        Assert.Equal("cafe", result.TxId);

        // Decode what actually went to the relay: pays the destination exactly, change to a
        // fresh INTERNAL-chain change address (BIP44 change chain), NOT a receive address.
        Assert.NotNull(gateway.CapturedBroadcastHex);
        var tx = Transaction.Parse(gateway.CapturedBroadcastHex, BlazecoinNetwork.Instance);
        var destScript = new BitcoinPubKeyAddress(dest, BlazecoinNetwork.Instance).ScriptPubKey;
        var changeScript = new BitcoinPubKeyAddress(
            LiteHdWallet.Restore(TestMnemonic).GetChangeAddress(0), BlazecoinNetwork.Instance).ScriptPubKey;
        var receiveScript = new BitcoinPubKeyAddress(service.Address, BlazecoinNetwork.Instance).ScriptPubKey;
        Assert.Equal(6_000_000, tx.Outputs.Single(o => o.ScriptPubKey == destScript).Value.Satoshi);
        Assert.Equal(4_000_000, tx.Outputs.Single(o => o.ScriptPubKey == changeScript).Value.Satoshi);
        Assert.DoesNotContain(tx.Outputs, o => o.ScriptPubKey == receiveScript); // never reuses the receive address
    }

    [Fact]
    public async Task Send_max_sweeps_every_coin_into_a_single_output_no_change()
    {
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);

        // Two spendable coins totalling 15 BLZ — a max send should consume BOTH and pay the
        // whole 15 to the destination with no change back to self.
        gateway.UtxosJson = "[" + string.Join(",",
            UtxoJson(new string('a', 64), 0, 10_000_000, confs: 5),
            UtxoJson(new string('b', 64), 1, 5_000_000, confs: 5)) + "]";

        var result = await service.SendMaxAsync(dest);

        Assert.True(result.Success, result.Error);
        var tx = Transaction.Parse(gateway.CapturedBroadcastHex, BlazecoinNetwork.Instance);
        Assert.Equal(2, tx.Inputs.Count);                    // both coins spent
        var o = Assert.Single(tx.Outputs);                    // ONE output — no change
        Assert.Equal(15_000_000, o.Value.Satoshi);            // the whole balance, zero fee
        var destScript = new BitcoinPubKeyAddress(dest, BlazecoinNetwork.Instance).ScriptPubKey;
        Assert.Equal(destScript, o.ScriptPubKey);
    }

    [Fact]
    public async Task History_nets_a_send_to_the_amount_actually_sent_not_the_inputs_consumed()
    {
        // A send's change lands on an INTERNAL-chain address. History must read the owned set
        // (receive + used change) so the +change row nets against the spend rows: consuming a
        // 10 BLZ coin to pay 6 BLZ is a −6 entry, NOT −10 (the 2026-08-17 web-wallet bug —
        // receive-only history showed the aggregate of whatever coins the selection grabbed).
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);
        var receiveAddr = service.Address;
        var changeAddr = LiteHdWallet.Restore(TestMnemonic).GetChangeAddress(0);

        // The send is what marks change address 0 as USED (and therefore owned).
        gateway.UtxosJson = "[" + UtxoJson(new string('4', 64), 1, 10_000_000, confs: 3) + "]";
        Assert.True((await service.SendAsync(dest, 6_000_000)).Success);

        // The gateway's per-address ledgers after the spend confirms (F = funding tx, S = spend):
        var f = new string('f', 64);
        var s = new string('5', 64);
        static string Row(string txId, long height, long amount, string type) =>
            $"{{\"txId\":\"{txId}\",\"blockHeight\":{height},\"timestamp\":\"2026-01-01T00:00:00Z\",\"amount\":{amount},\"type\":\"{type}\"}}";
        gateway.HistoryJsonByAddress[receiveAddr] =
            $"{{\"page\":1,\"pageSize\":25,\"total\":2,\"items\":[{Row(s, 101, -10_000_000, "sent")},{Row(f, 100, 10_000_000, "received")}]}}";
        gateway.HistoryJsonByAddress[changeAddr] =
            $"{{\"page\":1,\"pageSize\":25,\"total\":1,\"items\":[{Row(s, 101, 4_000_000, "received")}]}}";

        var history = await service.GetHistoryAsync();

        Assert.Equal(2, history.Count);
        var sent = history.Single(h => h.TxId == s);
        Assert.Equal(-6_000_000, sent.Amount);   // the amount actually sent — inputs net of change
        Assert.Equal("sent", sent.Type);
        var received = history.Single(h => h.TxId == f);
        Assert.Equal(10_000_000, received.Amount);
        Assert.Equal("received", received.Type);
    }

    // ── Pending-spent overlay ────────────────────────────────────────────────────────────
    // The gateway folds a broadcast tx into its UTXO answers on a ~2 s poll, so right after a
    // send it still returns the consumed coins. These pin the overlay that bridges the gap:
    // the Send page's "spendable" label updates immediately, and a rapid second send cannot
    // re-select the just-spent inputs (which would be a guaranteed broadcast conflict).

    [Fact]
    public async Task Spendable_drops_immediately_after_a_send_even_while_the_gateway_is_stale()
    {
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);

        gateway.UtxosJson = "[" + UtxoJson(new string('4', 64), 1, 10_000_000, confs: 3) + "]";
        Assert.Equal(10_000_000, await service.GetSpendableAsync());

        Assert.True((await service.SendAsync(dest, 6_000_000)).Success);

        // The gateway STILL returns the consumed coin (poll hasn't run) — the wallet must not.
        // Change is 0-conf and 0-conf is never spendable, so the honest number here is 0.
        Assert.Equal(0, await service.GetSpendableAsync());

        // And a rapid second send must not re-select the just-spent input: with the only coin
        // pending-spent, it reports insufficient balance instead of building a conflicting tx.
        var second = await service.SendAsync(dest, 1_000_000);
        Assert.False(second.Success);
        Assert.Contains("Insufficient", second.Error);
    }

    [Fact]
    public async Task Pending_spent_overlay_self_cleans_once_the_gateway_catches_up()
    {
        // Send-max (no change output) keeps the owned-address set at ONE entry — the scripted
        // gateway serves the same canned list for every address, so extra addresses would
        // multiply the totals below.
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);

        gateway.UtxosJson = "[" + UtxoJson(new string('4', 64), 1, 10_000_000, confs: 3) + "]";
        Assert.True((await service.SendMaxAsync(dest)).Success);
        Assert.Equal(0, await service.GetSpendableAsync()); // stale gateway view, overlay filters

        // Gateway catches up: the spent outpoint is gone; a different confirmed coin arrives.
        // The overlay entry is dropped on this read, and the new coin counts normally.
        gateway.UtxosJson = "[" + UtxoJson(new string('c', 64), 1, 4_000_000, confs: 1) + "]";
        Assert.Equal(4_000_000, await service.GetSpendableAsync());

        // If the SAME outpoint key ever reappears later (deep reorg), it is no longer
        // filtered — the overlay entry was removed, not retained forever.
        gateway.UtxosJson = "[" + UtxoJson(new string('4', 64), 1, 10_000_000, confs: 3) + "]";
        Assert.Equal(10_000_000, await service.GetSpendableAsync());
    }

    [Fact]
    public async Task Send_max_with_an_empty_wallet_fails_gracefully()
    {
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        gateway.UtxosJson = "[]";

        var result = await service.SendMaxAsync(LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5));
        Assert.False(result.Success);
        Assert.Contains("no spendable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Send_surfaces_insufficient_funds_and_relay_rejections()
    {
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);

        // Nothing spendable.
        gateway.UtxosJson = "[]";
        var broke = await service.SendAsync(dest, 1_000_000);
        Assert.False(broke.Success);
        Assert.Contains("Insufficient", broke.Error);

        // Relay says no (mempool policy) — the daemon's reason reaches the caller.
        gateway.UtxosJson = "[" + UtxoJson(new string('5', 64), 0, 10_000_000, confs: 3) + "]";
        gateway.BroadcastStatus = HttpStatusCode.UnprocessableEntity;
        gateway.BroadcastBody = "{\"error\":\"bad-txns-inputs-missingorspent\"}";
        var rejected = await service.SendAsync(dest, 1_000_000);
        Assert.False(rejected.Success);
        Assert.Equal("bad-txns-inputs-missingorspent", rejected.Error);
    }

    [Fact]
    public async Task Duplicate_rejection_after_failover_counts_as_success_with_our_own_txid()
    {
        // If broadcast attempt #1 delivered but its response was lost, the failover retry is
        // rejected as a duplicate — the send actually WORKED, and the wallet knows its txid.
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);
        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5);

        gateway.UtxosJson = "[" + UtxoJson(new string('6', 64), 0, 10_000_000, confs: 3) + "]";
        gateway.BroadcastStatus = HttpStatusCode.UnprocessableEntity;
        gateway.BroadcastBody = "{\"error\":\"txn-already-in-mempool\"}";

        var result = await service.SendAsync(dest, 1_000_000);
        Assert.True(result.Success);
        Assert.Equal(64, result.TxId!.Length); // our locally-computed txid
    }

    [Fact]
    public async Task Summary_maps_the_address_endpoint_and_null_for_unknown()
    {
        var (service, gateway, _) = Build();
        await service.RestoreWalletAsync(TestMnemonic);

        Assert.Null(await service.GetSummaryAsync()); // 404 → never seen on-chain

        gateway.AddressJson = "{\"balance\":123,\"totalReceived\":200,\"totalSent\":77,\"txCount\":9}";
        var summary = await service.GetSummaryAsync();
        Assert.NotNull(summary);
        Assert.Equal(123, summary!.Balance);
        Assert.Equal(9, summary.TxCount);
    }
}

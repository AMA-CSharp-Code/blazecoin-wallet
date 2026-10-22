using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.Gateway.Index;
using BlazecoinWallet.Lite.Gateway.Rpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace BlazecoinWallet.Lite.Gateway.Tests;

/// <summary>
/// THE consistency proof: the REAL Lite.Core client (IndexerDataService — the exact code
/// the Android wallet ships) talks to this gateway hosted in a TestServer, and everything
/// round-trips: UTXOs, address summaries (rotation's 404 semantics), raw/proof
/// passthroughs, broadcast outcome classification, and a full LiteWalletService
/// sign-and-send. If these pass, the appliance is drop-in URL/shape compatible with the
/// full Indexer stack — a wallet can't tell the two apart.
/// </summary>
public class GatewayContractTests : IDisposable
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-contract-{Guid.NewGuid():N}.db");
    private readonly FakeDaemonRpc _rpc = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IndexerDataService _client;

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored = TestMnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    public GatewayContractTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Index:DbPath", _dbPath);
            builder.UseSetting("Broadcast:PerMinute", "1000"); // rate limiting has its own test
            builder.ConfigureServices(services =>
            {
                // No background walker in contract tests — state is seeded directly.
                var walker = services.Single(d => d.ImplementationType == typeof(ChainWalkerService));
                services.Remove(walker);
                // The daemon behind the gateway is scripted.
                var rpc = services.Single(d => d.ServiceType == typeof(DaemonRpcClient));
                services.Remove(rpc);
                services.AddSingleton<DaemonRpcClient>(_rpc);
            });
        });
        // The genuine shipping client, pointed at the gateway exactly as a phone would be.
        _client = new IndexerDataService(_factory.CreateClient(), NullLogger<IndexerDataService>.Instance);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private UtxoIndexDb Db => _factory.Services.GetRequiredService<UtxoIndexDb>();
    private WalkerStatus Status => _factory.Services.GetRequiredService<WalkerStatus>();
    private MempoolOverlay Mempool => _factory.Services.GetRequiredService<MempoolOverlay>();

    private static string WalletAddress(int slot = 0) =>
        LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(slot);

    private void SeedConfirmedUtxo(string address, string txId, long sats, long height = 90, long tip = 100)
    {
        _rpc.AddBlock(height, $"h{height}", FakeDaemonRpc.Spend(txId,
            (FakeDaemonRpc.TxId("funding"), 0), FakeDaemonRpc.Out(0, address, sats)));
        Db.ApplyBlock(_rpc.Blocks[height]);
        Status.IndexedHeight = tip;
        Status.DaemonHeight = tip;
        Status.DaemonReachable = true;
    }

    [Fact]
    public async Task Utxos_round_trip_through_the_shipping_client_with_real_confirmations()
    {
        var address = WalletAddress();
        SeedConfirmedUtxo(address, FakeDaemonRpc.TxId("coin"), 5_000_000, height: 90, tip: 100);

        var utxos = await _client.GetUtxosAsync(address);

        var u = Assert.Single(utxos);
        Assert.Equal(FakeDaemonRpc.TxId("coin"), u.TxId);
        Assert.Equal(5_000_000, u.Amount);
        Assert.Equal(11, u.Confirmations); // tip 100 − height 90 + 1
        Assert.False(u.IsCoinbase);
    }

    [Fact]
    public async Task Address_summary_serves_rotations_used_check_and_404_for_fresh_addresses()
    {
        var used = WalletAddress(0);
        SeedConfirmedUtxo(used, FakeDaemonRpc.TxId("coin"), 5_000_000);

        var summary = await _client.GetAddressAsync(used);
        Assert.NotNull(summary);
        Assert.Equal(5_000_000, summary!.Balance);
        Assert.True(summary.TxCount > 0);       // "used" — rotation counts on this

        Assert.Null(await _client.GetAddressAsync(WalletAddress(7))); // fresh → 404 → null
    }

    [Fact]
    public async Task History_round_trips_through_the_shipping_client()
    {
        var address = WalletAddress();
        // Received at height 90 (SeedConfirmedUtxo pays `address` and sets Synced).
        SeedConfirmedUtxo(address, FakeDaemonRpc.TxId("incoming"), 5_000_000, height: 90, tip: 100);

        var history = await _client.GetHistoryAsync(address);

        var e = Assert.Single(history);
        Assert.Equal(FakeDaemonRpc.TxId("incoming"), e.TxId);
        Assert.Equal(5_000_000, e.Amount);
        Assert.Equal("received", e.Type);
        Assert.Equal(90, e.BlockHeight);
        // Timestamp resolved from the daemon header (GenesisUnixTime + height).
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(FakeDaemonRpc.GenesisUnixTime + 90).UtcDateTime, e.Timestamp);
    }

    [Fact]
    public async Task Mempool_payments_appear_at_zero_confirmations()
    {
        var address = WalletAddress();
        Status.DaemonReachable = true;
        Status.DaemonHeight = 100;
        Status.IndexedHeight = 100;
        Mempool.Replace([new MempoolUtxo(FakeDaemonRpc.TxId("incoming"), 0, address, 750_000)], [], 1);

        var utxos = await _client.GetUtxosAsync(address);

        var u = Assert.Single(utxos);
        Assert.Equal(0, u.Confirmations);
        Assert.Equal(750_000, u.Amount);
    }

    [Fact]
    public async Task Raw_and_proof_passthroughs_feed_the_wallets_trustless_verifier()
    {
        var txId = FakeDaemonRpc.TxId("verified");
        _rpc.RawHex[txId] = "beefcafe01";
        _rpc.Proofs[txId] = "deadbeef02";

        Assert.Equal("beefcafe01", await _client.GetRawTransactionHexAsync(txId));
        Assert.Equal("deadbeef02", await _client.GetTxOutProofAsync(txId));
        Assert.Null(await _client.GetRawTransactionHexAsync(FakeDaemonRpc.TxId("unknown")));
    }

    [Fact]
    public async Task Broadcast_outcomes_classify_exactly_like_the_full_indexer()
    {
        var validHex = new string('a', 200);

        // Accepted → the daemon's txid comes back.
        var ok = await _client.BroadcastAsync(validHex);
        Assert.True(ok.Success);
        Assert.Equal(_rpc.SendResultTxId, ok.TxId);

        // Policy rejection → 422, daemon's reason verbatim, Rejected kind (never fallback).
        _rpc.AcceptAllowed = false;
        _rpc.RejectReason = "bad-txns-inputs-missingorspent";
        var rejected = await _client.BroadcastAsync(validHex);
        Assert.False(rejected.Success);
        Assert.Equal("bad-txns-inputs-missingorspent", rejected.Error);
        Assert.Equal(BroadcastFailureKind.Rejected, rejected.Failure);

        // Daemon down → 503 → Unreachable kind (the wallet's P2P fallback trigger).
        _rpc.Unreachable = true;
        var down = await _client.BroadcastAsync(validHex);
        Assert.False(down.Success);
        Assert.Equal(BroadcastFailureKind.Unreachable, down.Failure);
        _rpc.Unreachable = false;
        _rpc.AcceptAllowed = true;

        // Garbage shape → 400 before the daemon is ever consulted.
        var invalid = await _client.BroadcastAsync("zz");
        Assert.False(invalid.Success);
        Assert.Equal(BroadcastFailureKind.Rejected, invalid.Failure);
    }

    [Fact]
    public async Task The_whole_wallet_send_pipeline_works_through_this_gateway()
    {
        var wallet = new LiteWalletService(new MemoryVault(), _client);
        Assert.True(await wallet.UnlockAsync());
        SeedConfirmedUtxo(wallet.Address, FakeDaemonRpc.TxId("spendme"), 10_000_000);

        var dest = LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(9);
        var result = await wallet.SendMaxAsync(dest);

        Assert.True(result.Success, result.Error);
        // What the daemon actually received is a real signed tx paying the destination.
        Assert.NotNull(_rpc.LastSentHex);
        var tx = Transaction.Parse(_rpc.LastSentHex, BlazecoinNetwork.Instance);
        var o = Assert.Single(tx.Outputs);
        Assert.Equal(10_000_000, o.Value.Satoshi);
        Assert.Equal(new BitcoinPubKeyAddress(dest, BlazecoinNetwork.Instance).ScriptPubKey, o.ScriptPubKey);
    }

    [Fact]
    public async Task Wallet_reads_reject_a_malformed_address_with_400()
    {
        Status.DaemonReachable = true; Status.DaemonHeight = 100; Status.IndexedHeight = 100;
        var http = _factory.CreateClient();
        foreach (var bad in new[] { "not-an-address!", "0OIl-forbidden-base58", new string('B', 65) })
        {
            var res = await http.GetAsync($"/api/address/{Uri.EscapeDataString(bad)}");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
            var utxo = await http.GetAsync($"/api/address/{Uri.EscapeDataString(bad)}/utxos");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, utxo.StatusCode);
        }
    }

    [Fact]
    public async Task Index_backed_reads_503_until_synced_so_restore_never_under_discovers()
    {
        // A freshly deployed appliance (walker still far from tip) must not answer "unused"
        // for a real address — that would make a restore's gap-scan stop early (audit S3).
        Status.DaemonReachable = true;
        Status.DaemonHeight = 4_150_000;
        Status.IndexedHeight = 1_000; // way behind
        var http = _factory.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
            (await http.GetAsync($"/api/address/{WalletAddress()}")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
            (await http.GetAsync($"/api/address/{WalletAddress()}/utxos")).StatusCode);

        // Raw/proof read the daemon directly, so they stay available during sync.
        _rpc.RawHex[FakeDaemonRpc.TxId("t")] = "aa";
        Assert.True((await http.GetAsync($"/api/tx/{FakeDaemonRpc.TxId("t")}/raw")).IsSuccessStatusCode);
    }

    // Real mainnet checkpoint 4,149,840 + its next five headers (all off a retarget boundary,
    // so difficulty is constant) — enough to prove the gateway /headers passthrough feeds the
    // wallet's trustless header-chain sync end to end. Full validation (boundaries, tampering)
    // lives in the Core suite's HeaderChainTests against the 327-header run.
    private const string CheckpointHash = "a3a4f833afa47e2cc603b1856ba207b3a6c4fc52da368167df5d555b1dc7b816";
    private static readonly Dictionary<long, string> RealHeaders = new()
    {
        [4149841] = "0000002016b8c71d5b555ddf678136da52fcc4a6b307a26b85b103c62c7ea4af33f8a4a358d199634e75e7834606742904691fcb3357f60b1e5f5e1b446ad32799c039dcec81616acdad331cdc935a03",
        [4149842] = "0000002008e80703b25b964a1d2109673fa41ddc0656fa38da94bd3101d66af79e49dd01c4d330aea4c5543b5553bf07601a64bee18f6b47edcde29cb71cf1ba89752e50a882616acdad331c92733d8f",
        [4149843] = "00000020ef6c7fbafadfaaeac425def8710287c9cdc7956db4bdfae8764d66c166aa0941cbf32941102126ba4609fb9343d0d5772ce0ae8aa5897ef4b3b0d61ff91f8b1da882616acdad331c567d6fbd",
        [4149844] = "00000020deb729a82392448611549e095af4e41cb740cdd028278320b90b31d329e03414a56d5c0d60dd551b974b23a3964f7503c74911f0c7fb94f0241d537edee6664cbf82616acdad331c6e0e2fd6",
        [4149845] = "000000205947fac356a7af84aa86505b62ea55718670e5ec242a26585a41e59c86fc51c0c53894bd8aba1fbd236e96c9461557c5628256b509c8ca39151e64a52a4eb00bc582616acdad331c98df56bd",
    };

    [Fact]
    public async Task Header_chain_sync_verifies_the_tip_through_the_shipping_client()
    {
        foreach (var (h, hex) in RealHeaders) _rpc.HeaderHex[h] = hex;
        _rpc.AddBlock(4_149_845, "h4149845"); // just to set DaemonHeight/BlockCount to the tip
        Status.DaemonHeight = 4_149_845;

        // The genuine wallet component, anchored at the shipped checkpoint, pulling headers over
        // HTTP from THIS gateway and verifying scrypt-PoW + linkage + difficulty on each.
        var sync = new HeaderChainSync(
            _client, // IndexerDataService IS an IHeaderReader
            new InMemoryWalletStateStore(),
            BlazecoinNetwork.Instance,
            new HeaderCheckpoint(4_149_840, CheckpointHash, 0x1c33adcd));

        await sync.SyncAsync();

        Assert.Equal(4_149_845, sync.VerifiedTipHeight); // all five headers verified through the wire
        // The verified tip now caps confirmations: a coin 3 blocks down can't read as 9999-deep.
        Assert.Equal(3, sync.EffectiveConfirmations(9_999, 4_149_843));
    }

    [Fact]
    public async Task Headers_endpoint_returns_a_short_batch_at_the_tip()
    {
        foreach (var (h, hex) in RealHeaders) _rpc.HeaderHex[h] = hex;
        Status.DaemonHeight = 4_149_845;
        var http = _factory.CreateClient();

        // Ask for 250 from 4,149,841; only five exist up to the tip → a short batch, which the
        // client reads as "caught up".
        var headers = await _client.GetHeadersAsync(4_149_841, 250);
        Assert.Equal(5, headers.Count);
        Assert.Equal(RealHeaders[4149841], headers[0]);

        // A height entirely past the tip → empty (bounded, never an error).
        Assert.Empty(await _client.GetHeadersAsync(5_000_000, 250));
    }

    [Fact]
    public async Task Status_endpoint_reports_sync_health_for_ops()
    {
        Status.DaemonReachable = true;
        Status.DaemonHeight = 100;
        Status.IndexedHeight = 100;

        var body = await _factory.CreateClient().GetStringAsync("/api/status");
        Assert.Contains("\"synced\":true", body);
        // PQ_SIGNATURES §7.1: the fork announcement rides on the same response — null until
        // the operator sets Fork:* (this factory sets none), and the wallet-side "indexer/api"
        // spelling answers identically.
        Assert.Contains("\"forkName\":null", body);
        Assert.Contains("\"forkHeight\":null", body);
        Assert.Contains("\"minClientVersion\":null", body);
        Assert.Equal(body, await _factory.CreateClient().GetStringAsync("/indexer/api/status"));
    }

    [Fact]
    public async Task Status_fork_fields_are_null_by_default_and_the_real_client_reads_no_announcement()
    {
        Status.DaemonReachable = true;
        Status.DaemonHeight = 4_300_000;
        Status.IndexedHeight = 4_300_000;

        // The genuine shipping poller (what every lite head runs every 30 s) against this gateway.
        var svc = new BlazecoinWallet.Lite.Fork.GatewayForkStatusService(_factory.CreateClient());
        await svc.RefreshAsync();

        var current = Assert.IsType<BlazecoinWallet.Lite.Fork.ForkStatus>(svc.Current);
        Assert.False(current.IsAnnounced);
        Assert.Null(current.ForkName);
        Assert.Null(current.ForkHeight);
        Assert.Null(current.MinClientVersion);
        Assert.Equal(4_300_000, current.Tip);
        Assert.Equal(BlazecoinWallet.Lite.Fork.ForkBannerState.None, svc.State);
    }
}

/// <summary>§7.1 with the three Fork:* keys SET (its own factory — the announcement is
/// process-wide configuration, so it must not bleed into the null-by-default contract).</summary>
public class StatusForkAnnouncementTests
{
    [Fact]
    public async Task Status_carries_the_fork_announcement_from_configuration_on_both_spellings()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-fork-{Guid.NewGuid():N}.db");
        var rpc = new FakeDaemonRpc();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Index:DbPath", dbPath);
            builder.UseSetting("Fork:Name", "pqsig");
            builder.UseSetting("Fork:Height", "5000000");
            builder.UseSetting("Fork:MinClientVersion", "2.1.0");
            builder.ConfigureServices(services =>
            {
                var walker = services.Single(d => d.ImplementationType == typeof(ChainWalkerService));
                services.Remove(walker);
                var reg = services.Single(d => d.ServiceType == typeof(DaemonRpcClient));
                services.Remove(reg);
                services.AddSingleton<DaemonRpcClient>(rpc);
            });
        });
        var status = factory.Services.GetRequiredService<WalkerStatus>();
        status.DaemonReachable = true;
        status.DaemonHeight = 4_900_000;
        status.IndexedHeight = 4_900_000;

        foreach (var path in new[] { "/api/status", "/indexer/api/status" })
        {
            var body = await factory.CreateClient().GetStringAsync(path);
            Assert.Contains("\"forkName\":\"pqsig\"", body);
            Assert.Contains("\"forkHeight\":5000000", body);
            Assert.Contains("\"minClientVersion\":\"2.1.0\"", body);
            Assert.Contains("\"daemonHeight\":4900000", body);
        }

        // The real poller maps it, and a build WITHOUT the pqsig rules evaluates to a banner:
        // 100,000 blocks out (> 40,320) with minClientVersion above the build → Warning.
        // (2.0.5 itself carries pqsig since H_Q was chosen, so the "old build" is modelled explicitly.)
        var oldBuildForks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "phoenix413" };
        var svc = new BlazecoinWallet.Lite.Fork.GatewayForkStatusService(factory.CreateClient());
        await svc.RefreshAsync();
        var current = Assert.IsType<BlazecoinWallet.Lite.Fork.ForkStatus>(svc.Current);
        Assert.True(current.IsAnnounced);
        Assert.Equal("pqsig", current.ForkName);
        Assert.Equal(5_000_000, current.ForkHeight);
        Assert.Equal("2.1.0", current.MinClientVersion);
        Assert.Equal(4_900_000, current.Tip);
        Assert.Equal(BlazecoinWallet.Lite.Fork.ForkBannerState.Warning,
            BlazecoinWallet.Lite.Fork.ForkBanner.Evaluate(current, "2.0.5", oldBuildForks, current.Tip));
        Assert.Equal(BlazecoinWallet.Lite.Fork.ForkBannerState.Notice,
            BlazecoinWallet.Lite.Fork.ForkBanner.Evaluate(current, "2.1.0", oldBuildForks, current.Tip));
        // This build carries the fork: no banner for a client that is already fork-ready.
        Assert.Equal(BlazecoinWallet.Lite.Fork.ForkBannerState.None,
            BlazecoinWallet.Lite.Fork.ForkBanner.Evaluate(current, "2.1.0", BlazecoinWallet.Lite.Fork.LiteClientInfo.SupportedForks, current.Tip));
    }
}

/// <summary>Broadcast rate limiting — its own factory so the tiny window doesn't bleed
/// into the other contract tests.</summary>
public class BroadcastRateLimitTests
{
    [Fact]
    public async Task Excess_broadcasts_get_429_within_the_window()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-rate-{Guid.NewGuid():N}.db");
        var rpc = new FakeDaemonRpc();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Index:DbPath", dbPath);
            builder.UseSetting("Broadcast:PerMinute", "2");
            builder.ConfigureServices(services =>
            {
                var walker = services.Single(d => d.ImplementationType == typeof(ChainWalkerService));
                services.Remove(walker);
                var reg = services.Single(d => d.ServiceType == typeof(DaemonRpcClient));
                services.Remove(reg);
                services.AddSingleton<DaemonRpcClient>(rpc);
            });
        });

        var http = factory.CreateClient();
        var payload = () => System.Net.Http.Json.JsonContent.Create(new { hex = new string('a', 200) });
        Assert.True((await http.PostAsync("/api/tx/broadcast", payload())).IsSuccessStatusCode);
        Assert.True((await http.PostAsync("/api/tx/broadcast", payload())).IsSuccessStatusCode);
        var third = await http.PostAsync("/api/tx/broadcast", payload());
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, third.StatusCode);
    }
}

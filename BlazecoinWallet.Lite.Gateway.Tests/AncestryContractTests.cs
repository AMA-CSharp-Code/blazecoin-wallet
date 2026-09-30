using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.Gateway.Index;
using BlazecoinWallet.Lite.Gateway.Rpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Gateway.Tests;

/// <summary>
/// The /ancestry endpoint driven through the SHIPPING client (IndexerDataService), the
/// GatewayContractTests pattern: a scripted daemon chain, the real SQLite index, the
/// REAL AncestryTracer — proving the server-side Provenance trace round-trips into the
/// wallet's LiteAncestryReport with the vintages intact.
/// </summary>
public class AncestryContractTests : IDisposable
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-ancestry-{Guid.NewGuid():N}.db");
    private readonly FakeDaemonRpc _rpc = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IndexerDataService _client;

    public AncestryContractTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Index:DbPath", _dbPath);
            builder.UseSetting("Trace:PerMinute", "1000");   // throttling isn't under test
            builder.UseSetting("Trace:MaxOutputs", "3");     // small so the cap is testable
            builder.ConfigureServices(services =>
            {
                var walker = services.Single(d => d.ImplementationType == typeof(ChainWalkerService));
                services.Remove(walker);
                var rpc = services.Single(d => d.ServiceType == typeof(DaemonRpcClient));
                services.Remove(rpc);
                services.AddSingleton<DaemonRpcClient>(_rpc);
            });
        });
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

    private static string WalletAddress(int slot = 0) =>
        LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(slot);

    private void MarkSynced(long tip)
    {
        Status.IndexedHeight = tip;
        Status.DaemonHeight = tip;
        Status.DaemonReachable = true;
    }

    [Fact]
    public async Task Coinbase_funded_coin_traces_to_its_minting_block()
    {
        var address = WalletAddress();

        // Block 90's coinbase (413 BLZ to the miner) funds a spend in block 95 that pays
        // the wallet 100 BLZ with the rest going back to the miner — a real one-hop walk.
        var cb90 = FakeDaemonRpc.TxId("cb90");                 // AddBlock's own coinbase id
        _rpc.AddBlock(90, "h90");
        var spend = FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("spend"), (cb90, 0),
            FakeDaemonRpc.Out(0, address, 10_000_000_000),
            FakeDaemonRpc.Out(1, _rpc.CoinbaseAddress, 31_300_000_000));
        _rpc.AddBlock(95, "h95", spend);
        Db.ApplyBlock(_rpc.Blocks[90]);
        Db.ApplyBlock(_rpc.Blocks[95]);
        MarkSynced(100);

        var result = await _client.GetAncestryAsync(address);

        Assert.Null(result.Error);
        var report = result.Report!;
        Assert.Equal(10_000_000_000, report.TotalSatoshis);
        Assert.Equal(1, report.OutputCount);
        Assert.False(report.Truncated);   // the walk finished well inside the budget

        // Fully attributed under BOTH models — the lineage is one clean coinbase hop.
        Assert.Equal(10_000_000_000, report.Fifo.AttributedSatoshis);
        Assert.Equal(10_000_000_000, report.Haircut.AttributedSatoshis);

        // The vintage year/month come from the coinbase BLOCK's time (GenesisUnixTime+90).
        var minted = DateTimeOffset.FromUnixTimeSeconds(FakeDaemonRpc.GenesisUnixTime + 90).UtcDateTime;
        var year = Assert.Single(report.Fifo.Years);
        Assert.Equal(minted.Year.ToString(), year.Key);
        Assert.Equal(10_000_000_000, year.Satoshis);
        Assert.Equal(90, year.FirstHeight);
        var month = Assert.Single(report.Fifo.Months);
        Assert.Equal($"{minted:yyyy-MM}", month.Key);
    }

    [Fact]
    public async Task Empty_address_answers_an_empty_catalogue_without_touching_the_daemon()
    {
        _rpc.AddBlock(90, "h90");
        Db.ApplyBlock(_rpc.Blocks[90]);
        MarkSynced(100);
        _rpc.Unreachable = true;   // proves no daemon call is needed for an empty answer

        var result = await _client.GetAncestryAsync(WalletAddress(5));

        Assert.Null(result.Error);
        Assert.Equal(0, result.Report!.TotalSatoshis);
        Assert.Empty(result.Report.Fifo.Years);
    }

    [Fact]
    public async Task Unsynced_index_is_a_503_the_client_surfaces_as_an_error()
    {
        Status.DaemonReachable = true;   // synced stays false — no heights set

        var result = await _client.GetAncestryAsync(WalletAddress());

        Assert.Null(result.Report);
        Assert.Contains("syncing", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Output_cap_refuses_with_a_named_reason_rather_than_truncating()
    {
        var address = WalletAddress();
        var cb90 = FakeDaemonRpc.TxId("cb90");
        _rpc.AddBlock(90, "h90");
        // Four outputs to the same address — over the test cap of 3.
        var spend = FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("fanout"), (cb90, 0),
            FakeDaemonRpc.Out(0, address, 1_000_000_000),
            FakeDaemonRpc.Out(1, address, 1_000_000_000),
            FakeDaemonRpc.Out(2, address, 1_000_000_000),
            FakeDaemonRpc.Out(3, address, 1_000_000_000));
        _rpc.AddBlock(95, "h95", spend);
        Db.ApplyBlock(_rpc.Blocks[90]);
        Db.ApplyBlock(_rpc.Blocks[95]);
        MarkSynced(100);

        var result = await _client.GetAncestryAsync(address);

        Assert.Null(result.Report);
        Assert.Contains("traces at most", result.Error);
    }

    [Fact]
    public async Task Exhausted_read_budget_flags_the_report_truncated()
    {
        // A one-read budget can't even finish a single coinbase hop — the report must
        // say so, and attribute nothing rather than guess.
        var dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-trunc-{Guid.NewGuid():N}.db");
        var rpc = new FakeDaemonRpc();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Index:DbPath", dbPath);
            builder.UseSetting("Trace:PerMinute", "1000");
            builder.UseSetting("Trace:MaxTransactionReads", "1");
            builder.ConfigureServices(services =>
            {
                var walker = services.Single(d => d.ImplementationType == typeof(ChainWalkerService));
                services.Remove(walker);
                var reg = services.Single(d => d.ServiceType == typeof(DaemonRpcClient));
                services.Remove(reg);
                services.AddSingleton<DaemonRpcClient>(rpc);
            });
        });
        try
        {
            var address = WalletAddress();
            var cb90 = FakeDaemonRpc.TxId("cb90");
            rpc.AddBlock(90, "h90");
            var spend = FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("spend"), (cb90, 0),
                FakeDaemonRpc.Out(0, address, 10_000_000_000),
                FakeDaemonRpc.Out(1, rpc.CoinbaseAddress, 31_300_000_000));
            rpc.AddBlock(95, "h95", spend);
            var db = factory.Services.GetRequiredService<UtxoIndexDb>();
            db.ApplyBlock(rpc.Blocks[90]);
            db.ApplyBlock(rpc.Blocks[95]);
            var status = factory.Services.GetRequiredService<WalkerStatus>();
            status.IndexedHeight = 100; status.DaemonHeight = 100; status.DaemonReachable = true;

            var client = new IndexerDataService(factory.CreateClient(), NullLogger<IndexerDataService>.Instance);
            var result = await client.GetAncestryAsync(address);

            Assert.Null(result.Error);
            Assert.True(result.Report!.Truncated);
            Assert.Equal(0, result.Report.Fifo.AttributedSatoshis);
            Assert.Equal(0, result.Report.Haircut.AttributedSatoshis);
        }
        finally
        {
            factory.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Unreadable_ancestry_underclaims_instead_of_guessing()
    {
        var address = WalletAddress();
        // The funding parent is NOT in the scripted chain — getrawtransaction will 404 it,
        // so the coin's value must show as held but unattributed.
        var spend = FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("orphan-parent-spend"),
            (FakeDaemonRpc.TxId("missing"), 0),
            FakeDaemonRpc.Out(0, address, 5_000_000_000));
        _rpc.AddBlock(95, "h95", spend);
        Db.ApplyBlock(_rpc.Blocks[95]);
        MarkSynced(100);

        var result = await _client.GetAncestryAsync(address);

        Assert.Null(result.Error);
        Assert.Equal(5_000_000_000, result.Report!.TotalSatoshis);
        Assert.Equal(0, result.Report.Fifo.AttributedSatoshis);
        Assert.Equal(0, result.Report.Haircut.AttributedSatoshis);
        Assert.Empty(result.Report.Fifo.Years);
    }
}

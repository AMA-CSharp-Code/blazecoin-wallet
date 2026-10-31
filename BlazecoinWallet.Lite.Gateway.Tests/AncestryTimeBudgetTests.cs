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
/// The /ancestry wall-clock budget (2026-10-27, `Trace:MaxSeconds`). Mobile traces of coins that
/// descend through the payout wallet's consolidation history needed more than ten minutes of
/// daemon reads and every one died at the client's 30 s. Now the walk answers inside its slice
/// with <c>truncated</c> + <c>truncatedBy: "time"</c>, and because the gateway caches every read
/// a re-trace carries on from where the last one stopped until the lineage is complete. Driven
/// through the SHIPPING client against the real tracer and the real SQLite index, with a daemon
/// that takes a fixed time per read.
/// </summary>
public class AncestryTimeBudgetTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static string WalletAddress() => LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(0);

    /// <summary>One gateway with its own index DB and trace settings.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-timebudget-{Guid.NewGuid():N}.db");
        public readonly FakeDaemonRpc Rpc = new();
        public readonly WebApplicationFactory<Program> Factory;
        public readonly IndexerDataService Client;

        public Rig(string maxSeconds, string? maxReads = null)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Index:DbPath", _dbPath);
                builder.UseSetting("Trace:PerMinute", "1000");      // throttling isn't under test
                builder.UseSetting("Trace:MaxSeconds", maxSeconds);
                if (maxReads != null) builder.UseSetting("Trace:MaxTransactionReads", maxReads);
                builder.ConfigureServices(services =>
                {
                    var walker = services.Single(d => d.ImplementationType == typeof(ChainWalkerService));
                    services.Remove(walker);
                    var reg = services.Single(d => d.ServiceType == typeof(DaemonRpcClient));
                    services.Remove(reg);
                    services.AddSingleton<DaemonRpcClient>(Rpc);
                });
            });
            Client = new IndexerDataService(Factory.CreateClient(), NullLogger<IndexerDataService>.Instance);
        }

        /// <summary>
        /// A change chain the way the payout wallet makes one: block 90's coinbase feeds hop 1,
        /// each hop spends the previous hop's output, and the LAST hop pays the wallet. Every hop
        /// is one more sequential daemon read — exactly the walk shape that outgrew the phones.
        /// </summary>
        public (string Address, long Satoshis, int Hops) ChangeChain(int hops)
        {
            var address = WalletAddress();
            Rpc.AddBlock(90, "h90");
            var prev = (FakeDaemonRpc.TxId("cb90"), 0);
            var txs = new List<RpcTransaction>();
            const long sats = 10_000_000_000;
            for (var i = 1; i <= hops; i++)
            {
                var payee = i == hops ? address : Rpc.CoinbaseAddress;
                var tx = FakeDaemonRpc.Spend(FakeDaemonRpc.TxId($"hop{i}"), prev, FakeDaemonRpc.Out(0, payee, sats));
                txs.Add(tx);
                prev = (tx.TxId, 0);
            }
            Rpc.AddBlock(95, "h95", txs.ToArray());
            var db = Factory.Services.GetRequiredService<UtxoIndexDb>();
            db.ApplyBlock(Rpc.Blocks[90]);
            db.ApplyBlock(Rpc.Blocks[95]);
            var status = Factory.Services.GetRequiredService<WalkerStatus>();
            status.IndexedHeight = 100; status.DaemonHeight = 100; status.DaemonReachable = true;
            return (address, sats, hops);
        }

        public void Dispose()
        {
            Factory.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task A_slow_walk_answers_inside_the_time_budget_as_a_floor_that_names_the_time_limit()
    {
        using var rig = new Rig(maxSeconds: "0.25");
        rig.Rpc.ReadDelay = TimeSpan.FromMilliseconds(40);     // 41 reads ≈ 1.6 s unbudgeted
        var (address, sats, hops) = rig.ChangeChain(40);

        var started = DateTime.UtcNow;
        var result = await rig.Client.GetAncestryAsync(address);
        var took = DateTime.UtcNow - started;

        Assert.Null(result.Error);
        var report = result.Report!;
        Assert.True(report.Truncated, "a walk cut by the clock must be flagged");
        Assert.Equal("time", report.TruncatedBy);
        Assert.True(report.TransactionsRead < hops + 1, $"read {report.TransactionsRead} of {hops + 1}: the budget did not stop the walk");
        // The coinbase was never reached, so NOTHING is attributed — a floor, not a guess.
        Assert.Equal(sats, report.TotalSatoshis);
        Assert.Equal(0, report.Fifo.AttributedSatoshis);
        Assert.Equal(0, report.Haircut.AttributedSatoshis);
        // Answered within the slice (plus one read's grace), nowhere near the full walk.
        Assert.True(took < TimeSpan.FromSeconds(1.2), $"took {took.TotalSeconds:0.00} s");
    }

    [Fact]
    public async Task Re_tracing_goes_deeper_each_time_until_the_lineage_is_complete()
    {
        // The promise the page makes ("Re-trace to go deeper"): the gateway's read cache
        // survives between requests, so each budgeted slice starts where the last ended.
        using var rig = new Rig(maxSeconds: "0.25");
        rig.Rpc.ReadDelay = TimeSpan.FromMilliseconds(40);
        var (address, sats, hops) = rig.ChangeChain(40);

        var readsPerRun = new List<int>();
        LiteAncestryReport? final = null;
        for (var run = 0; run < 12; run++)
        {
            var result = await rig.Client.GetAncestryAsync(address);
            Assert.Null(result.Error);
            readsPerRun.Add(result.Report!.TransactionsRead);
            if (!result.Report.Truncated) { final = result.Report; break; }
            Assert.Equal("time", result.Report.TruncatedBy);
        }

        Assert.NotNull(final);
        Assert.True(readsPerRun.Count > 1, "the first slice should not have finished a 40-hop walk");
        // Monotone progress: every run read at least as far as the one before.
        for (var i = 1; i < readsPerRun.Count; i++)
            Assert.True(readsPerRun[i] >= readsPerRun[i - 1], $"run {i} regressed: {string.Join(",", readsPerRun)}");
        Assert.Equal(hops + 1, final.TransactionsRead);
        Assert.Null(final.TruncatedBy);
        Assert.Equal(sats, final.Fifo.AttributedSatoshis);
        Assert.Equal(sats, final.Haircut.AttributedSatoshis);
        Assert.Equal(90, Assert.Single(final.Fifo.Years).FirstHeight);
    }

    [Fact]
    public async Task MaxSeconds_zero_means_no_time_budget()
    {
        using var rig = new Rig(maxSeconds: "0");
        rig.Rpc.ReadDelay = TimeSpan.FromMilliseconds(15);     // 41 reads ≈ 0.6 s — longer than any slice above
        var (address, sats, hops) = rig.ChangeChain(40);

        var result = await rig.Client.GetAncestryAsync(address);

        Assert.Null(result.Error);
        Assert.False(result.Report!.Truncated);
        Assert.Null(result.Report.TruncatedBy);
        Assert.Equal(hops + 1, result.Report.TransactionsRead);
        Assert.Equal(sats, result.Report.Fifo.AttributedSatoshis);
    }

    [Fact]
    public async Task A_walk_that_finishes_inside_the_budget_is_not_flagged()
    {
        using var rig = new Rig(maxSeconds: "20");
        var (address, sats, _) = rig.ChangeChain(5);

        var result = await rig.Client.GetAncestryAsync(address);

        Assert.Null(result.Error);
        Assert.False(result.Report!.Truncated);
        Assert.Null(result.Report.TruncatedBy);
        Assert.Equal(sats, result.Report.Haircut.AttributedSatoshis);
    }

    [Fact]
    public async Task The_read_cap_names_itself_so_the_page_does_not_promise_a_deeper_re_trace()
    {
        // Reads, not time, stopped this one: re-tracing cannot help, and the response says so.
        using var rig = new Rig(maxSeconds: "20", maxReads: "3");
        var (address, _, _) = rig.ChangeChain(10);

        var result = await rig.Client.GetAncestryAsync(address);

        Assert.Null(result.Error);
        Assert.True(result.Report!.Truncated);
        Assert.Equal("reads", result.Report.TruncatedBy);
        Assert.Equal(0, result.Report.Fifo.AttributedSatoshis);
    }
}

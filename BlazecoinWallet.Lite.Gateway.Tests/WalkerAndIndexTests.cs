using BlazecoinWallet.Lite.Gateway.Index;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Gateway.Tests;

/// <summary>
/// The walker + SQLite index against a scripted daemon: forward sync, spend tracking, the
/// coinbase convention, reorg rewind (including un-spending), and the mempool overlay.
/// </summary>
public class WalkerAndIndexTests : IDisposable
{
    private const string Alice = "BAliceAliceAliceAliceAliceAliceAli";
    private const string Bob = "BBobBobBobBobBobBobBobBobBobBobBob";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-test-{Guid.NewGuid():N}.db");
    private readonly FakeDaemonRpc _rpc = new();
    private readonly UtxoIndexDb _db;
    private readonly MempoolOverlay _mempool = new();
    private readonly WalkerStatus _status = new();
    private readonly ChainWalkerService _walker;

    public WalkerAndIndexTests()
    {
        _db = new UtxoIndexDb(_dbPath);
        _walker = new ChainWalkerService(_rpc, _db, _mempool, _status,
            new ConfigurationBuilder().Build(), NullLogger<ChainWalkerService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private Task Tick() => _walker.TickAsync(CancellationToken.None);

    [Fact]
    public async Task Walker_syncs_the_chain_and_serves_wallet_grade_utxos()
    {
        _rpc.AddBlock(0, "g");
        _rpc.AddBlock(1, "b1", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("pay-alice"),
            (FakeDaemonRpc.TxId("cb0"), 0), FakeDaemonRpc.Out(0, Alice, 5_000_000), FakeDaemonRpc.Out(1, Bob, 1_000_000)));
        _rpc.AddBlock(2, "b2");

        await Tick();

        Assert.True(_status.Synced);
        Assert.Equal(2, _status.IndexedHeight);
        var alice = _db.GetUtxos(Alice);
        var u = Assert.Single(alice);
        Assert.Equal(5_000_000, u.Amount);
        Assert.Equal(1, u.BlockHeight);
        Assert.False(u.IsCoinbase);

        // The coinbase convention: position 0 in the block. cb0 was spent funding Alice,
        // so the miner is left with cb1 + cb2 only.
        var miner = _db.GetUtxos(_rpc.CoinbaseAddress);
        Assert.Equal(2, miner.Count);
        Assert.All(miner, m => Assert.True(m.IsCoinbase));
        Assert.DoesNotContain(miner, m => m.TxId == FakeDaemonRpc.TxId("cb0"));
    }

    [Fact]
    public async Task Spending_marks_coins_gone_and_summaries_reflect_activity()
    {
        _rpc.AddBlock(0, "g");
        _rpc.AddBlock(1, "b1", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("pay-alice"),
            (FakeDaemonRpc.TxId("cb0"), 0), FakeDaemonRpc.Out(0, Alice, 5_000_000)));
        _rpc.AddBlock(2, "b2", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("alice-spends"),
            (FakeDaemonRpc.TxId("pay-alice"), 0), FakeDaemonRpc.Out(0, Bob, 5_000_000)));

        await Tick();

        Assert.Empty(_db.GetUtxos(Alice));
        var summary = _db.GetSummary(Alice)!;
        Assert.Equal(0, summary.Balance);
        Assert.Equal(5_000_000, summary.TotalReceived);
        Assert.Equal(5_000_000, summary.TotalSent);
        Assert.Equal(2, summary.TxCount);           // funded by one tx, emptied by another

        Assert.Null(_db.GetSummary("BNeverSeenAddressNeverSeenAddressN")); // rotation's 404
    }

    [Fact]
    public async Task Address_history_lists_signed_received_and_sent_rows_newest_first()
    {
        _rpc.AddBlock(0, "g");
        _rpc.AddBlock(1, "b1", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("pay-alice"),
            (FakeDaemonRpc.TxId("cb0"), 0), FakeDaemonRpc.Out(0, Alice, 5_000_000)));
        _rpc.AddBlock(2, "b2", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("alice-spends"),
            (FakeDaemonRpc.TxId("pay-alice"), 0), FakeDaemonRpc.Out(0, Bob, 5_000_000)));
        await Tick();

        var (rows, total) = _db.GetAddressHistory(Alice, page: 1, pageSize: 25);

        Assert.Equal(2, total);
        // Newest first: the spend (height 2) then the receipt (height 1).
        Assert.Equal(FakeDaemonRpc.TxId("alice-spends"), rows[0].TxId);
        Assert.Equal(-5_000_000, rows[0].Amount);
        Assert.Equal("sent", rows[0].Type);
        Assert.Equal(FakeDaemonRpc.TxId("pay-alice"), rows[1].TxId);
        Assert.Equal(5_000_000, rows[1].Amount);
        Assert.Equal("received", rows[1].Type);

        // Bob only received.
        var (bobRows, bobTotal) = _db.GetAddressHistory(Bob, 1, 25);
        Assert.Equal(1, bobTotal);
        Assert.Equal("received", Assert.Single(bobRows).Type);
    }

    [Fact]
    public async Task Reorg_rewinds_orphaned_outputs_and_unspends_reversed_spends()
    {
        _rpc.AddBlock(0, "g");
        _rpc.AddBlock(1, "b1", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("pay-alice"),
            (FakeDaemonRpc.TxId("cb0"), 0), FakeDaemonRpc.Out(0, Alice, 5_000_000)));
        _rpc.AddBlock(2, "b2", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("alice-spends"),
            (FakeDaemonRpc.TxId("pay-alice"), 0), FakeDaemonRpc.Out(0, Bob, 5_000_000)));
        await Tick();
        Assert.Empty(_db.GetUtxos(Alice)); // spent on the old branch

        // The network reorgs: height 2 is replaced by a branch WITHOUT Alice's spend.
        _rpc.Blocks.Remove(2);
        _rpc.AddBlock(2, "b2x");
        _rpc.AddBlock(3, "b3x");
        await Tick();

        var alice = Assert.Single(_db.GetUtxos(Alice)); // un-spent by the rewind
        Assert.Equal(5_000_000, alice.Amount);
        Assert.Empty(_db.GetUtxos(Bob));                // the orphaned branch's output is gone
        Assert.Equal(3, _status.IndexedHeight);
    }

    [Fact]
    public async Task Mempool_overlay_serves_zero_conf_and_hides_coins_being_spent()
    {
        _rpc.AddBlock(0, "g");
        _rpc.AddBlock(1, "b1", FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("pay-alice"),
            (FakeDaemonRpc.TxId("cb0"), 0), FakeDaemonRpc.Out(0, Alice, 5_000_000)));
        await Tick();

        // An unconfirmed tx spends Alice's confirmed coin and pays Bob.
        var memTx = FakeDaemonRpc.Spend(FakeDaemonRpc.TxId("mem-spend"),
            (FakeDaemonRpc.TxId("pay-alice"), 0), FakeDaemonRpc.Out(0, Bob, 5_000_000));
        _rpc.MempoolTxs[memTx.TxId] = memTx;
        await Tick();

        Assert.True(_mempool.IsSpent(FakeDaemonRpc.TxId("pay-alice"), 0)); // Alice's coin is being spent
        var bob = Assert.Single(_mempool.GetForAddress(Bob));              // Bob sees it at 0-conf
        Assert.Equal(5_000_000, bob.Amount);

        // The tx confirms: overlay empties, the index takes over.
        _rpc.MempoolTxs.Clear();
        _rpc.AddBlock(2, "b2", memTx);
        await Tick();
        Assert.Empty(_mempool.GetForAddress(Bob));
        Assert.Single(_db.GetUtxos(Bob));
        Assert.Empty(_db.GetUtxos(Alice));
    }

    [Fact]
    public async Task Daemon_outage_pauses_the_walker_without_losing_state()
    {
        _rpc.AddBlock(0, "g");
        _rpc.AddBlock(1, "b1");
        await Tick();
        Assert.Equal(1, _status.IndexedHeight);

        _rpc.Unreachable = true;
        await Assert.ThrowsAsync<Rpc.DaemonUnreachableException>(Tick);

        _rpc.Unreachable = false;
        _rpc.AddBlock(2, "b2");
        await Tick();
        Assert.Equal(2, _status.IndexedHeight); // resumed exactly where it left off
    }
}

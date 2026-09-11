using BlazecoinWallet.Lite.Gateway.Rpc;
using Microsoft.Data.Sqlite;

namespace BlazecoinWallet.Lite.Gateway.Index;

public sealed record ConfirmedUtxo(string TxId, int Vout, long Amount, long BlockHeight, bool IsCoinbase);
public sealed record AddressSummary(long Balance, long TotalReceived, long TotalSent, int TxCount);

/// <summary>One address-ledger row: a coin RECEIVED (amount &gt; 0) or a coin SENT
/// (amount &lt; 0) by the address, keyed to the transaction that did it. Matches the full
/// Indexer's per-output/input convention (signed amount + received/sent type); the block
/// time is resolved separately (the index doesn't store it).</summary>
public sealed record HistoryRow(string TxId, long BlockHeight, long Amount, string Type);

/// <summary>
/// The appliance's only stateful piece: a minimal SQLite address→output index built by the
/// chain walker. Spent outputs are KEPT (marked, not deleted) — that's what makes address
/// summaries (rotation's used-address checks) and reorg un-spending possible. WAL mode so
/// wallet reads never block the walker's writes; a fresh connection per operation keeps the
/// store trivially thread-safe. Losing the file is harmless: the walker rebuilds from the
/// daemon (the chain is ~2.6 GB; the index is a fraction of that).
/// </summary>
public sealed class UtxoIndexDb
{
    /// <summary>How many recent block hashes to keep for reorg fork-finding (~an hour of
    /// 30 s blocks; the wallet's own verifier tolerates nothing deeper anyway).</summary>
    public const int BlockWindow = 120;

    private readonly string _connectionString;

    public UtxoIndexDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var db = Open();
        Exec(db, "PRAGMA journal_mode=WAL;");
        Exec(db, """
            CREATE TABLE IF NOT EXISTS blocks(
                height INTEGER PRIMARY KEY,
                hash   TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outputs(
                txid         TEXT    NOT NULL,
                vout         INTEGER NOT NULL,
                address      TEXT    NOT NULL,
                amount       INTEGER NOT NULL,
                block_height INTEGER NOT NULL,
                is_coinbase  INTEGER NOT NULL,
                spent_txid   TEXT,
                spent_height INTEGER,
                PRIMARY KEY(txid, vout));
            CREATE INDEX IF NOT EXISTS idx_outputs_address      ON outputs(address);
            CREATE INDEX IF NOT EXISTS idx_outputs_block_height ON outputs(block_height);
            CREATE INDEX IF NOT EXISTS idx_outputs_spent_height ON outputs(spent_height);
            """);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    /// <summary>The highest indexed block, or null on a virgin database.</summary>
    public (long Height, string Hash)? GetTip()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT height, hash FROM blocks ORDER BY height DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1)) : null;
    }

    /// <summary>The stored hash at a height inside the block window, or null.</summary>
    public string? GetBlockHash(long height)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT hash FROM blocks WHERE height = $h";
        cmd.Parameters.AddWithValue("$h", height);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Applies one block atomically: new outputs inserted (idempotent — a re-applied block
    /// overwrites its own rows), this block's inputs marked spent, hash recorded, window
    /// pruned. Coinbase convention mirrors the full Indexer: tx position 0 in the block.
    /// </summary>
    public void ApplyBlock(RpcBlock block)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();

        for (var i = 0; i < block.Tx.Count; i++)
        {
            var t = block.Tx[i];
            foreach (var vout in t.Vout)
            {
                var address = vout.ScriptPubKey?.EffectiveAddress;
                if (address == null) continue; // non-standard / OP_RETURN — not spendable-by-address
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO outputs(txid, vout, address, amount, block_height, is_coinbase, spent_txid, spent_height)
                    VALUES($txid, $vout, $addr, $amount, $height, $cb, NULL, NULL)
                    """;
                ins.Parameters.AddWithValue("$txid", t.TxId);
                ins.Parameters.AddWithValue("$vout", vout.N);
                ins.Parameters.AddWithValue("$addr", address);
                ins.Parameters.AddWithValue("$amount", vout.Satoshis);
                ins.Parameters.AddWithValue("$height", block.Height);
                ins.Parameters.AddWithValue("$cb", i == 0 ? 1 : 0);
                ins.ExecuteNonQuery();
            }

            foreach (var vin in t.Vin)
            {
                if (vin.TxId == null || vin.Vout == null) continue; // coinbase input
                using var spend = db.CreateCommand();
                spend.Transaction = tx;
                spend.CommandText = """
                    UPDATE outputs SET spent_txid = $spender, spent_height = $height
                    WHERE txid = $txid AND vout = $vout
                    """;
                spend.Parameters.AddWithValue("$spender", t.TxId);
                spend.Parameters.AddWithValue("$height", block.Height);
                spend.Parameters.AddWithValue("$txid", vin.TxId);
                spend.Parameters.AddWithValue("$vout", vin.Vout.Value);
                spend.ExecuteNonQuery();
            }
        }

        using (var blk = db.CreateCommand())
        {
            blk.Transaction = tx;
            blk.CommandText = "INSERT OR REPLACE INTO blocks(height, hash) VALUES($h, $hash)";
            blk.Parameters.AddWithValue("$h", block.Height);
            blk.Parameters.AddWithValue("$hash", block.Hash);
            blk.ExecuteNonQuery();
        }
        using (var prune = db.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = "DELETE FROM blocks WHERE height < $min";
            prune.Parameters.AddWithValue("$min", block.Height - BlockWindow + 1);
            prune.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Reorg rewind to (and including) the fork height: outputs created above it vanish,
    /// spends recorded above it are undone, orphaned block hashes dropped. The next walker
    /// pass re-applies the winning branch.
    /// </summary>
    public void RewindToFork(long forkHeight)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var sql in new[]
        {
            "DELETE FROM outputs WHERE block_height > $h",
            "UPDATE outputs SET spent_txid = NULL, spent_height = NULL WHERE spent_height > $h",
            "DELETE FROM blocks WHERE height > $h",
        })
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$h", forkHeight);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Confirmed unspent outputs for an address (the mempool overlay is applied by
    /// the endpoint on top of this).</summary>
    public List<ConfirmedUtxo> GetUtxos(string address)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT txid, vout, amount, block_height, is_coinbase FROM outputs
            WHERE address = $addr AND spent_txid IS NULL
            ORDER BY block_height ASC, txid ASC, vout ASC
            """;
        cmd.Parameters.AddWithValue("$addr", address);
        using var r = cmd.ExecuteReader();
        var utxos = new List<ConfirmedUtxo>();
        while (r.Read())
            utxos.Add(new ConfirmedUtxo(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4) == 1));
        return utxos;
    }

    /// <summary>
    /// Address summary from the outputs ledger, or null when the address has never been
    /// seen — the wallet's rotation gap-scan relies on that null (→ HTTP 404 → "unused").
    /// TxCount = distinct transactions touching the address (funding + spending), the same
    /// "has activity" semantic the full Indexer serves.
    /// </summary>
    public AddressSummary? GetSummary(string address)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN spent_txid IS NULL THEN amount ELSE 0 END), 0),
                COALESCE(SUM(amount), 0),
                COUNT(*),
                (SELECT COUNT(*) FROM (
                    SELECT txid FROM outputs WHERE address = $addr
                    UNION
                    SELECT spent_txid FROM outputs WHERE address = $addr AND spent_txid IS NOT NULL))
            FROM outputs WHERE address = $addr
            """;
        cmd.Parameters.AddWithValue("$addr", address);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetInt64(2) == 0) return null;
        var balance = r.GetInt64(0);
        var received = r.GetInt64(1);
        return new AddressSummary(balance, received, received - balance, r.GetInt32(3));
    }

    /// <summary>
    /// The address ledger, newest-first, paginated: one row per output the address received
    /// and one per output it spent (signed amount + received/sent type), exactly like the
    /// full Indexer's address-transactions endpoint. Kept in sync with reorgs for free —
    /// it reads the same outputs table the rewind operates on. Returns the page rows and the
    /// total row count (for the client's pagination shape).
    /// </summary>
    public (List<HistoryRow> Rows, int Total) GetAddressHistory(string address, int page, int pageSize)
    {
        using var db = Open();
        int total;
        using (var c = db.CreateCommand())
        {
            c.CommandText = """
                SELECT (SELECT COUNT(*) FROM outputs WHERE address = $addr)
                     + (SELECT COUNT(*) FROM outputs WHERE address = $addr AND spent_txid IS NOT NULL)
                """;
            c.Parameters.AddWithValue("$addr", address);
            total = Convert.ToInt32(c.ExecuteScalar());
        }

        var rows = new List<HistoryRow>();
        using (var c = db.CreateCommand())
        {
            // Received outputs (+amount) UNION spends of the address's outputs (−amount).
            c.CommandText = """
                SELECT txid, height, amount, type FROM (
                    SELECT txid AS txid, block_height AS height, amount AS amount, 'received' AS type
                    FROM outputs WHERE address = $addr
                    UNION ALL
                    SELECT spent_txid AS txid, spent_height AS height, -amount AS amount, 'sent' AS type
                    FROM outputs WHERE address = $addr AND spent_txid IS NOT NULL
                )
                ORDER BY height DESC, txid DESC
                LIMIT $limit OFFSET $offset
                """;
            c.Parameters.AddWithValue("$addr", address);
            c.Parameters.AddWithValue("$limit", pageSize);
            c.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            using var r = c.ExecuteReader();
            while (r.Read())
                rows.Add(new HistoryRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3)));
        }
        return (rows, total);
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

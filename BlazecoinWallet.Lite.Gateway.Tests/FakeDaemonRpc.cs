using BlazecoinWallet.Lite.Gateway.Rpc;

namespace BlazecoinWallet.Lite.Gateway.Tests;

/// <summary>
/// Scripted in-memory daemon: a height→block chain the walker can sync, a mempool, and
/// canned answers for the passthrough calls. Throws the SAME typed exceptions the real
/// client does, so classification paths are exercised for real.
/// </summary>
public sealed class FakeDaemonRpc : DaemonRpcClient
{
    public readonly Dictionary<long, RpcBlock> Blocks = [];
    public readonly Dictionary<string, RpcTransaction> MempoolTxs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> RawHex = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Proofs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<long, string> HeaderHex = [];
    public bool Unreachable;
    public bool AcceptAllowed = true;
    public string? RejectReason;
    public string? LastSentHex;
    public string SendResultTxId = new('c', 64);

    public FakeDaemonRpc() : base(new HttpClient(), new DaemonOptions()) { }

    private void Gate()
    {
        if (Unreachable) throw new DaemonUnreachableException("The node is temporarily unreachable.");
    }

    public override Task<long> GetBlockCountAsync(CancellationToken ct = default)
    {
        Gate();
        return Task.FromResult(Blocks.Count == 0 ? 0 : Blocks.Keys.Max());
    }

    public override Task<string> GetBlockHashAsync(long height, CancellationToken ct = default)
    {
        Gate();
        return Blocks.TryGetValue(height, out var b)
            ? Task.FromResult(b.Hash)
            : throw new DaemonRpcException("Block height out of range");
    }

    public override Task<RpcBlock> GetBlockVerboseAsync(string hash, CancellationToken ct = default)
    {
        Gate();
        var block = Blocks.Values.FirstOrDefault(b => b.Hash == hash);
        return block != null ? Task.FromResult(block) : throw new DaemonRpcException("Block not found");
    }

    public override Task<string[]> GetRawMempoolAsync(CancellationToken ct = default)
    {
        Gate();
        return Task.FromResult(MempoolTxs.Keys.ToArray());
    }

    /// <summary>Deterministic block time = a fixed epoch + height seconds, so history
    /// timestamps are checkable.</summary>
    public const long GenesisUnixTime = 1_700_000_000;
    public override Task<long> GetBlockTimeAsync(long height, CancellationToken ct = default)
    {
        Gate();
        return Task.FromResult(GenesisUnixTime + height);
    }

    public override Task<RpcTransaction> GetMempoolTransactionAsync(string txId, CancellationToken ct = default)
    {
        Gate();
        return MempoolTxs.TryGetValue(txId, out var t)
            ? Task.FromResult(t)
            : throw new DaemonRpcException("No such mempool or blockchain transaction.");
    }

    public override Task<string> GetRawTransactionHexAsync(string txId, CancellationToken ct = default)
    {
        Gate();
        return RawHex.TryGetValue(txId, out var hex)
            ? Task.FromResult(hex)
            : throw new DaemonRpcException("No such mempool or blockchain transaction.");
    }

    public override Task<string> GetBlockHeaderHexAsync(long height, CancellationToken ct = default)
    {
        Gate();
        return HeaderHex.TryGetValue(height, out var hex)
            ? Task.FromResult(hex)
            : throw new DaemonRpcException("Block height out of range");
    }

    public override Task<string> GetTxOutProofAsync(string txId, CancellationToken ct = default)
    {
        Gate();
        return Proofs.TryGetValue(txId, out var proof)
            ? Task.FromResult(proof)
            : throw new DaemonRpcException("Transaction not yet in block.");
    }

    public override Task<(bool Allowed, string? RejectReason)> TestMempoolAcceptAsync(string hex, CancellationToken ct = default)
    {
        Gate();
        return Task.FromResult((AcceptAllowed, RejectReason));
    }

    public override Task<string> SendRawTransactionAsync(string hex, CancellationToken ct = default)
    {
        Gate();
        LastSentHex = hex;
        return Task.FromResult(SendResultTxId);
    }

    /// <summary>Serves any tx in the scripted chain with its block's hash/time, mirroring
    /// a txindex daemon; mempool txs come back with no blockhash.</summary>
    public override Task<RpcRawTransaction> GetRawTransactionVerboseAsync(string txId, CancellationToken ct = default)
    {
        Gate();
        foreach (var b in Blocks.Values)
        {
            var tx = b.Tx.FirstOrDefault(t => string.Equals(t.TxId, txId, StringComparison.OrdinalIgnoreCase));
            if (tx != null)
                return Task.FromResult(new RpcRawTransaction(tx.TxId, tx.Vin, tx.Vout, b.Hash, GenesisUnixTime + b.Height));
        }
        if (MempoolTxs.TryGetValue(txId, out var m))
            return Task.FromResult(new RpcRawTransaction(m.TxId, m.Vin, m.Vout, null, null));
        throw new DaemonRpcException("No such mempool or blockchain transaction.", DaemonRpcException.NotFoundCode);
    }

    public override Task<(long Height, long Time)> GetBlockHeaderInfoAsync(string blockHash, CancellationToken ct = default)
    {
        Gate();
        var block = Blocks.Values.FirstOrDefault(b => b.Hash == blockHash);
        return block != null
            ? Task.FromResult((block.Height, GenesisUnixTime + block.Height))
            : throw new DaemonRpcException("Block not found", DaemonRpcException.NotFoundCode);
    }

    // ── Chain-building helpers ──────────────────────────────────────────────────────────

    public static RpcVout Out(int n, string address, long satoshis) =>
        new(satoshis / 100_000_000m, n, new RpcScriptPubKey(address, null));

    /// <summary>An output the daemon could NOT decode to an address (a pre-fork daemon
    /// serving a post-quantum P2PQH template): only the script hex is present.</summary>
    public static RpcVout OutHexOnly(int n, string scriptPubKeyHex, long satoshis) =>
        new(satoshis / 100_000_000m, n, new RpcScriptPubKey(null, null, scriptPubKeyHex));

    public static RpcTransaction Coinbase(string txId, string address, long satoshis) =>
        new(txId, [new RpcVin(null, null, "03deadbeef")], [Out(0, address, satoshis)]);

    public static RpcTransaction Spend(string txId, (string TxId, int Vout) input, params RpcVout[] outputs) =>
        new(txId, [new RpcVin(input.TxId, input.Vout, null)], [.. outputs]);

    /// <summary>Adds a block at the next height (or given height) whose first tx is a coinbase.</summary>
    public RpcBlock AddBlock(long height, string hash, params RpcTransaction[] nonCoinbaseTxs)
    {
        var txs = new List<RpcTransaction>
        {
            Coinbase(TxId($"cb{height}"), CoinbaseAddress, 41_300_000_000),
        };
        txs.AddRange(nonCoinbaseTxs);
        var prev = Blocks.TryGetValue(height - 1, out var p) ? p.Hash : null;
        var block = new RpcBlock(hash, height, prev, txs);
        Blocks[height] = block;
        return block;
    }

    public string CoinbaseAddress { get; set; } = "BMinerMinerMinerMinerMinerMinerMin";

    /// <summary>Deterministic 64-hex txid from a short tag.</summary>
    public static string TxId(string tag)
    {
        var hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(tag)).ToLowerInvariant();
        return hex.Length >= 64 ? hex[..64] : hex.PadRight(64, '0');
    }
}

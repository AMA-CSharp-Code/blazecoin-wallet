using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazecoinWallet.Lite.Gateway.Rpc;

/// <summary>How to reach the local blazecoind. Cookie auth is the VPS default (the daemon
/// writes a fresh .cookie per start; re-read per call so a daemon restart never strands
/// us); explicit user/password only for setups that disabled the cookie.</summary>
public sealed class DaemonOptions
{
    public string Url { get; init; } = "http://127.0.0.1:55413/";
    public string? CookiePath { get; init; } = "/var/lib/blazecoin/.cookie";
    public string? RpcUser { get; init; }
    public string? RpcPassword { get; init; }
}

/// <summary>The daemon judged the call and said no — its message is the verdict.
/// <paramref name="code"/> is the daemon's RPC error code (audit F3: classify on the code,
/// not the message wording — Core's messages are not a stable API). 0 = unknown.</summary>
public sealed class DaemonRpcException(string message, int code = 0) : Exception(message)
{
    /// <summary>Bitcoin Core RPC_INVALID_ADDRESS_OR_KEY — "no such tx" on getrawtransaction/gettxoutproof.</summary>
    public const int NotFoundCode = -5;

    public int Code { get; } = code;
    public bool IsNotFound => Code == NotFoundCode
        // Message fallback for daemons/fixtures that don't carry a code.
        || Message.Contains("No such", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("not yet in block", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The daemon could not be reached at all (nobody judged anything).</summary>
public sealed class DaemonUnreachableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Minimal JSON-RPC 1.0 client for exactly the calls this appliance makes. Kept
/// deliberately small (vs the full Indexer's RpcClient) — every method here is load-bearing.
/// Error classification mirrors the full Indexer's TxBroadcastService: transport failures
/// throw <see cref="DaemonUnreachableException"/> (retryable, 503 to clients), RPC-level
/// errors throw <see cref="DaemonRpcException"/> (a verdict, surfaced as-is).
/// </summary>
public class DaemonRpcClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly DaemonOptions _options;

    public DaemonRpcClient(HttpClient http, DaemonOptions options)
    {
        _http = http;
        _options = options;
    }

    // ── The walker's reads ──────────────────────────────────────────────────────────────
    public virtual Task<long> GetBlockCountAsync(CancellationToken ct = default) =>
        SendAsync<long>(ct, "getblockcount");

    public virtual Task<string> GetBlockHashAsync(long height, CancellationToken ct = default) =>
        SendAsync<string>(ct, "getblockhash", height);

    /// <summary>Verbosity 2: full transaction detail in one call per block.</summary>
    public virtual Task<RpcBlock> GetBlockVerboseAsync(string hash, CancellationToken ct = default) =>
        SendAsync<RpcBlock>(ct, "getblock", hash, 2);

    public virtual Task<string[]> GetRawMempoolAsync(CancellationToken ct = default) =>
        SendAsync<string[]>(ct, "getrawmempool");

    /// <summary>Raw serialized 80-byte block header at a height — for the wallet's trustless
    /// header-chain sync (it re-derives PoW + difficulty from these). Two RPCs (hash, then
    /// verbosity-0 header); heights past the tip surface as an "out of range" RPC error.</summary>
    public virtual async Task<string> GetBlockHeaderHexAsync(long height, CancellationToken ct = default)
    {
        var hash = await GetBlockHashAsync(height, ct);
        return await SendAsync<string>(ct, "getblockheader", hash, false);
    }

    /// <summary>Block time (unix seconds) at a height — for history timestamps. The index
    /// doesn't store block times (and its blocks table is pruned), so history resolves them
    /// here on demand; the caller caches per height (immutable).</summary>
    public virtual async Task<long> GetBlockTimeAsync(long height, CancellationToken ct = default)
    {
        var hash = await GetBlockHashAsync(height, ct);
        var header = await SendAsync<RpcBlockHeader>(ct, "getblockheader", hash, true);
        return header.Time;
    }

    public virtual Task<RpcTransaction> GetMempoolTransactionAsync(string txId, CancellationToken ct = default) =>
        SendAsync<RpcTransaction>(ct, "getrawtransaction", txId, true);

    // ── The wallet-facing passthroughs ──────────────────────────────────────────────────
    public virtual Task<string> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) =>
        SendAsync<string>(ct, "getrawtransaction", txId, false);

    /// <summary>NOTE the array wrapping: gettxoutproof takes an ARRAY of txids as its first
    /// param — without the explicit object[] nesting, covariance spreads it into two args
    /// (the bug the desktop verifier hit).</summary>
    public virtual Task<string> GetTxOutProofAsync(string txId, CancellationToken ct = default) =>
        SendAsync<string>(ct, "gettxoutproof", new object[] { new[] { txId } });

    public virtual async Task<(bool Allowed, string? RejectReason)> TestMempoolAcceptAsync(string hex, CancellationToken ct = default)
    {
        var results = await SendAsync<TestMempoolAcceptResult[]>(ct, "testmempoolaccept", new object[] { new[] { hex } });
        var r = results.FirstOrDefault();
        return r == null ? (false, "Empty testmempoolaccept response.") : (r.Allowed, r.RejectReason);
    }

    public virtual Task<string> SendRawTransactionAsync(string hex, CancellationToken ct = default) =>
        SendAsync<string>(ct, "sendrawtransaction", hex);

    // ── The ancestry reads (server-side Provenance) ─────────────────────────────────────
    /// <summary>Verbose getrawtransaction including which block holds it (needs txindex=1
    /// for anything outside the mempool — both VPS nodes run it, pre-seeded by the deploy
    /// kit). blockhash null = mempool-only.</summary>
    public virtual Task<RpcRawTransaction> GetRawTransactionVerboseAsync(string txId, CancellationToken ct = default) =>
        SendAsync<RpcRawTransaction>(ct, "getrawtransaction", txId, true);

    /// <summary>Height + time of a block by hash (verbose getblockheader) — the hop the
    /// ancestry reader needs because Core's verbose getrawtransaction carries no height.</summary>
    public virtual async Task<(long Height, long Time)> GetBlockHeaderInfoAsync(string blockHash, CancellationToken ct = default)
    {
        var h = await SendAsync<RpcBlockHeader>(ct, "getblockheader", blockHash, true);
        return (h.Height, h.Time);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────
    private async Task<T> SendAsync<T>(CancellationToken ct, string method, params object[] parameters)
    {
        var request = new { jsonrpc = "1.0", id = "lite-gw", method, @params = parameters };
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Url) { Content = content };
        message.Headers.Authorization = BuildAuth();

        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.SendAsync(message, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DaemonUnreachableException("The node is temporarily unreachable.", ex);
        }

        // Core answers RPC-level errors with non-2xx AND an error body — parse before failing.
        RpcResponse<T>? rpc = null;
        try { rpc = JsonSerializer.Deserialize<RpcResponse<T>>(body, Json); }
        catch (JsonException) { /* fall through to the status check */ }

        if (rpc?.Error != null) throw new DaemonRpcException(rpc.Error.Message ?? "RPC call failed.", rpc.Error.Code);
        if (!response.IsSuccessStatusCode)
            throw new DaemonUnreachableException($"RPC HTTP {(int)response.StatusCode}.");
        if (rpc == null) throw new DaemonUnreachableException("RPC response was not valid JSON.");
        return rpc.Result!;
    }

    private AuthenticationHeaderValue BuildAuth()
    {
        string credentials;
        if (!string.IsNullOrEmpty(_options.RpcUser))
        {
            credentials = $"{_options.RpcUser}:{_options.RpcPassword}";
        }
        else
        {
            // Missing config entirely is validated at startup (audit F6) — this throw is
            // only reachable if options were mutated to nonsense at runtime.
            var path = _options.CookiePath
                ?? throw new InvalidOperationException("No RPC credentials: neither user/password nor a cookie path is configured.");
            try { credentials = File.ReadAllText(path).Trim(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DaemonUnreachableException($"Could not read the RPC cookie at {path}.", ex);
            }
        }
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials)));
    }

    private sealed record RpcResponse<T>(T? Result, RpcError? Error);
    private sealed record RpcError(int Code, string? Message);

    private sealed record TestMempoolAcceptResult(
        [property: JsonPropertyName("txid")] string? TxId,
        [property: JsonPropertyName("allowed")] bool Allowed,
        [property: JsonPropertyName("reject-reason")] string? RejectReason);
}

// ── getblock verbosity-2 wire shapes (only the fields the walker uses) ──────────────────

public sealed record RpcBlock(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("height")] long Height,
    [property: JsonPropertyName("previousblockhash")] string? PreviousBlockHash,
    [property: JsonPropertyName("tx")] List<RpcTransaction> Tx);

public sealed record RpcBlockHeader(
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("height")] long Height = 0);

/// <summary>getrawtransaction verbose: <see cref="RpcTransaction"/>'s fields plus where the
/// transaction was mined. Height is deliberately absent — Core 28's verbose result doesn't
/// carry one; resolve it via <see cref="DaemonRpcClient.GetBlockHeaderInfoAsync"/>.</summary>
public sealed record RpcRawTransaction(
    [property: JsonPropertyName("txid")] string TxId,
    [property: JsonPropertyName("vin")] List<RpcVin> Vin,
    [property: JsonPropertyName("vout")] List<RpcVout> Vout,
    [property: JsonPropertyName("blockhash")] string? BlockHash,
    [property: JsonPropertyName("blocktime")] long? BlockTime);

public sealed record RpcTransaction(
    [property: JsonPropertyName("txid")] string TxId,
    [property: JsonPropertyName("vin")] List<RpcVin> Vin,
    [property: JsonPropertyName("vout")] List<RpcVout> Vout);

public sealed record RpcVin(
    [property: JsonPropertyName("txid")] string? TxId,
    [property: JsonPropertyName("vout")] int? Vout,
    [property: JsonPropertyName("coinbase")] string? Coinbase);

public sealed record RpcVout(
    [property: JsonPropertyName("value")] decimal Value,
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("scriptPubKey")] RpcScriptPubKey? ScriptPubKey)
{
    /// <summary>Value in satoshis — getblock serves BLZ decimals.</summary>
    public long Satoshis => (long)Math.Round(Value * 100_000_000m);
}

/// <summary>Core 28 serves a SINGULAR `address` (the legacy `addresses[]` is gone) — the
/// exact asymmetry that bit the full indexer post-cutover. Keep the fallback anyway; it
/// costs nothing and regtest fixtures sometimes carry old shapes.</summary>
public sealed record RpcScriptPubKey(
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("addresses")] List<string>? Addresses)
{
    public string? EffectiveAddress => Address ?? Addresses?.FirstOrDefault();
}

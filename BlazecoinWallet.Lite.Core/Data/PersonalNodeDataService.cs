using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BlazecoinWallet.Lite.Data;

/// <summary>How to reach YOUR daemon's RPC.</summary>
/// <param name="RpcUrl">e.g. http://127.0.0.1:55413/ (keep RPC loopback/LAN-bound — never internet-exposed).</param>
/// <param name="CookieFilePath">Path to the daemon's .cookie (re-read per call — it rotates on restart).</param>
/// <param name="RpcUser">rpcauth username when not using the cookie.</param>
/// <param name="RpcPassword">rpcauth password when not using the cookie.</param>
public sealed record PersonalNodeOptions(
    string RpcUrl,
    string? CookieFilePath = null,
    string? RpcUser = null,
    string? RpcPassword = null);

/// <summary>
/// PERSONAL-NODE mode — the sovereignty option: <see cref="ILiteWalletData"/> over your OWN
/// full node's RPC, trusting nobody's servers. UTXOs come from `scantxoutset` (a chainstate
/// scan — seconds, not milliseconds; fine for one person's wallet), broadcast goes straight
/// to `sendrawtransaction`. Trade-offs vs the Indexer: no transaction HISTORY (a bare
/// daemon has no address index — history returns empty) and no rich totals (the summary
/// carries the live balance only).
/// </summary>
public sealed class PersonalNodeDataService : ILiteWalletData
{
    private readonly HttpClient _http;
    private readonly PersonalNodeOptions _options;
    private readonly ILogger<PersonalNodeDataService> _logger;

    public PersonalNodeDataService(HttpClient http, PersonalNodeOptions options, ILogger<PersonalNodeDataService> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        // The RPC call carries the node's Basic-auth credentials — https or loopback only
        // (the rule lives in LitePersonalNode, shared with the settings screen).
        _http.BaseAddress ??= LitePersonalNode.NormalizeRpcUrl(options.RpcUrl);
    }

    /// <summary>A bare daemon has no address index — history is UNSUPPORTED, not empty (F2).</summary>
    public bool SupportsHistory => false;

    public async Task<LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default)
    {
        var utxos = await GetUtxosAsync(address, ct);
        if (utxos.Count == 0) return null;
        var balance = utxos.Sum(u => u.Amount);
        return new LiteAddressSummary(balance, TotalReceived: 0, TotalSent: 0, TxCount: 0);
    }

    public async Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
    {
        try
        {
            var result = await CallAsync("scantxoutset", ct, "start", new object[] { $"addr({address})" });
            if (!result.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return [];

            var tipHeight = result.GetProperty("height").GetInt64();
            var list = new List<LiteChainUtxo>();
            foreach (var u in result.GetProperty("unspents").EnumerateArray())
            {
                var height = u.GetProperty("height").GetInt64();
                list.Add(new LiteChainUtxo(
                    TxId: u.GetProperty("txid").GetString()!,
                    OutputIndex: u.GetProperty("vout").GetInt32(),
                    Amount: (long)Math.Round(u.GetProperty("amount").GetDecimal() * 100_000_000m),
                    Confirmations: (int)(tipHeight - height + 1),
                    BlockHeight: height,
                    // Core 25+ reports it; older builds omit it — default false.
                    IsCoinbase: u.TryGetProperty("coinbase", out var cb) && cb.GetBoolean()));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personal-node UTXO scan failed");
            return [];
        }
    }

    public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default)
        // A bare daemon has no address index — history needs the Indexer. Documented trade-off.
        => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);

    /// <summary>Own-chainstate UTXO values (scantxoutset) are already authoritative — there
    /// is nobody to distrust, so the send pipeline skips the verification round trips.</summary>
    public bool SupportsChainVerification => false;

    public async Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default)
    {
        try { return (await CallAsync("getrawtransaction", ct, txId, false)).GetString(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Personal-node raw-tx fetch failed"); return null; }
    }

    public async Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default)
    {
        try { return (await CallAsync("gettxoutproof", ct, new object[] { new[] { txId } })).GetString(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Personal-node merkle-proof fetch failed"); return null; }
    }

    public async Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default)
    {
        try
        {
            var result = await CallAsync("sendrawtransaction", ct, rawTxHex);
            var txId = result.GetString();
            return string.IsNullOrEmpty(txId)
                ? LiteBroadcastResult.Fail("The node returned no txid.", BroadcastFailureKind.Rejected)
                : LiteBroadcastResult.Ok(txId);
        }
        catch (RpcRejectionException ex)
        {
            // The node JUDGED the tx — classify (a duplicate means an earlier attempt landed).
            return LiteBroadcastResult.Fail(ex.Message, BroadcastReasons.Classify(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personal-node broadcast failed");
            return LiteBroadcastResult.Fail("Could not reach your node — is the daemon running?", BroadcastFailureKind.Unreachable);
        }
    }

    /// <summary>An RPC-level error response (the node answered and said no).</summary>
    public sealed class RpcRejectionException(string message) : Exception(message);

    private async Task<JsonElement> CallAsync(string method, CancellationToken ct, params object[] args)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "1.0", id = "lite", method, @params = args });
        using var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = BuildAuth();

        var response = await _http.SendAsync(request, ct);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            throw new RpcRejectionException(err.TryGetProperty("message", out var m) ? m.GetString() ?? "RPC error" : "RPC error");
        response.EnsureSuccessStatusCode();
        return doc.RootElement.GetProperty("result").Clone();
    }

    private AuthenticationHeaderValue BuildAuth()
    {
        // Cookie file re-read on every call: the daemon rotates it at each restart.
        var credentials = _options.CookieFilePath != null
            ? File.ReadAllText(_options.CookieFilePath).Trim()
            : $"{_options.RpcUser}:{_options.RpcPassword}";
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials)));
    }
}

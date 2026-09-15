using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlazecoinWallet.Core.Services;

/// <summary>Composite of the segregated RPC interfaces (see RpcInterfaces.cs).
/// Kept for the UI pages that genuinely span chain + wallet + security calls;
/// focused consumers (sync poller, miner, wallet manager, auto-payout) depend on
/// the narrow slice they use instead. One <see cref="BlazecoindRpcService"/>
/// instance backs them all.</summary>
public interface IBlazecoindRpcService
    : INodeRpc, IChainInfoRpc, IMiningRpc, IWalletRpc, ISecurityRpc
{
    // GetNewAddressAsync intentionally appears in both IMiningRpc (payout address)
    // and IWalletRpc (receive address). Re-declaring it here with `new` picks a
    // single member for composite callers, avoiding CS0121 ambiguity — the one
    // BlazecoindRpcService implementation still satisfies every interface.
    new Task<string?> GetNewAddressAsync(string label = "");
}

/// <summary>Thrown when blazecoind returns a JSON-RPC error object. Carries the
/// numeric error code so callers can branch (e.g. -18 = no wallet loaded,
/// -28 = loading block index, -4 = wallet already loaded).</summary>
public class RpcException : Exception
{
    public int Code { get; }
    public RpcException(int code, string message) : base(message) { Code = code; }
}

/// <summary>Thrown when blazecoind refuses a request because its RPC work queue is full
/// (HTTP 503 "Work queue depth exceeded" — see the fork's httpserver.cpp). This is
/// BACK-PRESSURE, NOT AN OUTAGE: the daemon is running and usually at the chain tip, it
/// just has every RPC worker busy — typically the minutes after a restart while a large
/// wallet catches up. The reply is written BEFORE the request is enqueued to a worker, so
/// the call provably never executed and is always safe to retry.
/// <para>Derives from <see cref="HttpRequestException"/> deliberately: the best-effort call
/// sites that already degrade on transport errors (ancestry tracing, address validation,
/// fee estimation) keep catching it unchanged; only the callers that want to tell "busy"
/// from "offline" — chiefly <see cref="BlazecoindRpcService.CheckConnectionAsync"/> —
/// need to name it.</para></summary>
public class DaemonBusyException : HttpRequestException
{
    public DaemonBusyException(string? detail = null)
        : base(string.IsNullOrWhiteSpace(detail)
            ? "The daemon's RPC work queue is full."
            : $"The daemon's RPC work queue is full: {detail!.Trim()}")
    { }
}

public class BlazecoindRpcService : IBlazecoindRpcService, Provenance.IAncestryRpc, Provenance.IVintageSendRpc, Quantum.IExposureRpc
{
    private readonly HttpClient _httpClient;
    private readonly string _rpcUrl;
    private readonly IWalletContext _walletContext;
    private readonly ILogger<BlazecoindRpcService>? _logger;
    private int _requestId;

    public BlazecoindRpcService(IConfiguration configuration, IWalletContext walletContext, IHttpClientFactory httpClientFactory, ILogger<BlazecoindRpcService>? logger = null)
    {
        _rpcUrl = configuration["Blazecoind:RpcUrl"] ?? "http://127.0.0.1:55413";
        _walletContext = walletContext;
        _logger = logger;

        // The "blazecoind" named client (configured in MauiProgram) owns the
        // infinite client-level timeout + basic-auth header. We still cap each
        // call with a per-request CancellationToken (30s default, longer for
        // migrate/rescan), so an infinite client timeout is intentional.
        _httpClient = httpClientFactory.CreateClient("blazecoind");
    }

    /// <summary>Default 30s per-call. Migrations and rescans pass a longer
    /// override via the overload that accepts a timeout.</summary>
    private static readonly TimeSpan DefaultRpcTimeout = TimeSpan.FromSeconds(30);

    /// <summary>For the few reads that walk a whole wallet history in one call. A mining wallet
    /// can hold tens of thousands of transactions; one pass over them takes seconds, but well
    /// past the 30s default on a busy daemon.</summary>
    private static readonly TimeSpan HistoryReadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Back-off between retries when the daemon answers HTTP 503 (RPC work queue
    /// full). Deliberately short and few: the point is to ride out a burst without the UI
    /// flickering to the offline panel, not to hide a daemon that is genuinely saturated.
    /// Total added latency stays under a second, well inside both the 30s call timeout and
    /// the sync poller's 8s cadence, so polls can never pile up on each other.</summary>
    private static readonly int[] BusyRetryDelaysMs = { 300, 600 };

    /// <summary>Matches the case-insensitive/camelCase behaviour of the
    /// HttpClient JSON extensions so manual body parsing on the error path
    /// deserializes identically to the success path.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private Task<T?> CallRpcMethodAsync<T>(string method, params object[] parameters)
        => CallRpcAtAsync<T>(_rpcUrl, method, DefaultRpcTimeout, parameters);

    /// <summary>Routes a wallet-scoped RPC through <c>/wallet/&lt;active&gt;</c>.
    /// Core demands this once more than one wallet is loaded (error -19);
    /// when no wallet is active it falls back to the base URL, preserving the
    /// original single-wallet behaviour. If the daemon answers -18 (the active
    /// wallet is no longer loaded — e.g. the daemon restarted and an RPC-loaded
    /// wallet didn't persist), the call re-points the context at a wallet that
    /// IS loaded ("Primary" preferred) and retries once, so an already-open
    /// window heals itself instead of dead-ending every page on the error.</summary>
    private async Task<T?> CallWalletRpcAsync<T>(string method, params object[] parameters)
    {
        try
        {
            return await CallRpcAtAsync<T>(WalletUrl(), method, DefaultRpcTimeout, parameters);
        }
        catch (RpcException ex) when (ex.Code == -18)
        {
            if (!await TryReassignActiveWalletAsync()) throw;
            return await CallRpcAtAsync<T>(WalletUrl(), method, DefaultRpcTimeout, parameters);
        }
    }

    /// <summary>Same wallet routing and -18 self-heal as <see cref="CallWalletRpcAsync{T}"/>, with a
    /// caller-chosen timeout for whole-history reads.</summary>
    private async Task<T?> CallWalletRpcWithTimeoutAsync<T>(string method, TimeSpan timeout, params object[] parameters)
    {
        try
        {
            return await CallRpcAtAsync<T>(WalletUrl(), method, timeout, parameters);
        }
        catch (RpcException ex) when (ex.Code == -18)
        {
            if (!await TryReassignActiveWalletAsync()) throw;
            return await CallRpcAtAsync<T>(WalletUrl(), method, timeout, parameters);
        }
    }

    /// <summary>Recovery half of the -18 self-heal above: asks the daemon which
    /// wallets are actually loaded and, if the active one isn't among them,
    /// switches the context to "Primary" (or the first loaded wallet). Returns
    /// false when there is nothing sensible to switch to — the original -18
    /// then propagates so pages can explain the situation.</summary>
    private async Task<bool> TryReassignActiveWalletAsync()
    {
        List<string> loaded;
        try
        {
            loaded = await CallRpcAtAsync<List<string>>(_rpcUrl, "listwallets", DefaultRpcTimeout) ?? new List<string>();
        }
        catch
        {
            return false;
        }

        var active = _walletContext.Active;
        if (!string.IsNullOrEmpty(active) && loaded.Contains(active)) return false; // -18 wasn't about the active wallet
        if (loaded.Count == 0) return false;

        var fallback = loaded.Contains("Primary") ? "Primary" : loaded[0];
        _logger?.LogWarning("Active wallet \"{Active}\" is not loaded on the daemon; switching to \"{Fallback}\".",
            active, fallback);
        _walletContext.SetActive(fallback);
        return true;
    }

    private string WalletUrl()
    {
        var active = _walletContext.Active;
        return string.IsNullOrEmpty(active)
            ? _rpcUrl
            : $"{_rpcUrl.TrimEnd('/')}/wallet/{Uri.EscapeDataString(active)}";
    }

    private Task<T?> CallRpcAtAsync<T>(string url, string method, params object[] parameters)
        => CallRpcAtAsync<T>(url, method, DefaultRpcTimeout, parameters);

    private async Task<T?> CallRpcAtAsync<T>(string url, string method, TimeSpan timeout, params object[] parameters)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var request = new
            {
                jsonrpc = "1.0",
                id = Interlocked.Increment(ref _requestId),
                method,
                @params = parameters
            };
            var payload = JsonSerializer.Serialize(request);

            for (var attempt = 1; ; attempt++)
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content, cts.Token);

                // Bitcoin Core returns a non-2xx status (almost always HTTP 500)
                // for *most* JSON-RPC errors — not just malformed HTTP. That
                // includes RPC_IN_WARMUP (-28) while it loads the block index and
                // "wallet already loaded" (-4), each with the normal JSON-RPC
                // error envelope in the body. Read the body once and try to parse
                // that envelope first, so real RPC errors surface as RpcException
                // (carrying the numeric code callers branch on) instead of being
                // flattened into a generic HttpRequestException.
                var rawBody = await response.Content.ReadAsStringAsync();

                RpcResponse<T>? jsonResponse = null;
                if (!string.IsNullOrWhiteSpace(rawBody))
                {
                    try
                    {
                        jsonResponse = JsonSerializer.Deserialize<RpcResponse<T>>(rawBody, JsonOpts);
                    }
                    catch (JsonException)
                    {
                        // Body isn't a JSON-RPC envelope (HTML/plain error page or
                        // empty). Fall through to the transport-error path below.
                    }
                }

                if (jsonResponse?.Error != null)
                {
                    _logger?.LogError("RPC error from {Method}: {ErrorCode} - {ErrorMessage}",
                        method, jsonResponse.Error.Code, jsonResponse.Error.Message);
                    throw new RpcException(jsonResponse.Error.Code, jsonResponse.Error.Message ?? "Unknown RPC error");
                }

                // HTTP 503 = the daemon's RPC work queue is full ("Work queue depth
                // exceeded"). It is up, just saturated — commonly for a few minutes
                // after a restart while a large wallet catches up, during which the
                // 8-second sync poll would otherwise blank every page behind the
                // offline panel. The rejection happens before the request reaches a
                // worker, so nothing ran and a retry is always safe: ride out a short
                // burst here, and only then surface a typed DaemonBusyException so the
                // caller can say "busy", never "offline".
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    if (attempt <= BusyRetryDelaysMs.Length)
                    {
                        var delay = BusyRetryDelaysMs[attempt - 1];
                        _logger?.LogWarning(
                            "Daemon busy on {Method} (HTTP 503, attempt {Attempt}/{Max}) — retrying in {Delay}ms",
                            method, attempt, BusyRetryDelaysMs.Length + 1, delay);
                        await Task.Delay(delay, cts.Token);
                        continue;
                    }

                    _logger?.LogWarning("Daemon still busy on {Method} after {Attempts} attempts (HTTP 503)",
                        method, attempt);
                    throw new DaemonBusyException(rawBody);
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Non-2xx with no parseable JSON-RPC error: the earliest
                    // startup window, where the HTTP server is listening but the
                    // RPC dispatcher isn't registered yet, so blazecoind replies
                    // HTTP 500 with an empty body. Surface as a transport error —
                    // the initializer treats this (like a refused connection) as
                    // "daemon still starting" and keeps waiting.
                    _logger?.LogWarning("RPC call to {Method} got HTTP {StatusCode} with no JSON-RPC body (daemon starting?)",
                        method, (int)response.StatusCode);
                    throw new HttpRequestException(
                        $"RPC call to {method} failed with status {(int)response.StatusCode} {response.StatusCode} (empty body)");
                }

                return jsonResponse != null ? jsonResponse.Result : default;
            }
        }
        catch (TaskCanceledException ex)
        {
            _logger?.LogError(ex, "RPC call to {Method} timed out", method);
            throw new TimeoutException($"RPC call to {method} timed out after {timeout.TotalSeconds:F0} seconds", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "HTTP error calling RPC method {Method}", method);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error calling RPC method {Method}", method);
            throw;
        }
    }

    /// <summary>Generic RPC passthrough for the Console page. Returns the raw
    /// JSON result pretty-printed (or the bare value for string results). RPC
    /// errors surface as RpcException, exactly like the typed calls above.</summary>
    public async Task<string> ExecuteCommandAsync(string method, params object[] parameters)
    {
        var result = await CallRpcAtAsync<JsonElement>(_rpcUrl, method, DefaultRpcTimeout, parameters);
        if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "(no result)";
        return result.ValueKind == JsonValueKind.String
            ? result.GetString() ?? string.Empty
            : JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<WalletInfo?> GetWalletInfoAsync()
    {
        return await CallWalletRpcAsync<WalletInfo>("getwalletinfo");
    }

    // ---- Provenance / ancestry (IAncestryRpc — see Services/Provenance) ----
    // Read-only calls behind the mint-ancestry tracer. Amounts cross the boundary in
    // SATOSHIS: the tracer's haircut is integer maths, and decimal BLZ would round.

    private static long ToSats(decimal blz) => (long)decimal.Round(blz * 100_000_000m, 0, MidpointRounding.AwayFromZero);

    public async Task<IReadOnlyList<Provenance.UnspentOutput>> ListUnspentAsync(CancellationToken ct = default)
    {
        var rows = await CallWalletRpcAsync<List<JsonElement>>("listunspent");
        var list = new List<Provenance.UnspentOutput>(rows?.Count ?? 0);
        foreach (var r in rows ?? new List<JsonElement>())
        {
            list.Add(new Provenance.UnspentOutput(
                r.GetProperty("txid").GetString() ?? string.Empty,
                r.GetProperty("vout").GetInt32(),
                ToSats(r.GetProperty("amount").GetDecimal()),
                r.TryGetProperty("address", out var a) ? a.GetString() : null,
                r.TryGetProperty("confirmations", out var c) ? c.GetInt32() : 0));
        }
        return list;
    }

    /// <summary>
    /// One transaction, normalised for the tracer. Asks the WALLET first
    /// (<c>gettransaction</c>, whose envelope carries blockhash/height/time directly and
    /// needs no tx index) and falls back to the node-level <c>getrawtransaction</c> for
    /// ancestry that runs outside this wallet — that one needs <c>txindex=1</c>. Wallet
    /// first is never more calls and usually fewer: a mining wallet's coinbases are all
    /// its own, so it traces fully on a node with no tx index at all. Returns null when
    /// neither can read it — the tracer treats that as no ancestry (dilute, never invent).
    /// </summary>
    public async Task<Provenance.RawTransaction?> GetTransactionAsync(string txid, CancellationToken ct = default)
    {
        try
        {
            var wallet = await CallWalletRpcAsync<JsonElement>("gettransaction", txid, true, true);
            if (wallet.ValueKind == JsonValueKind.Object &&
                wallet.TryGetProperty("decoded", out var decoded))
                return ParseTransaction(decoded, wallet);
        }
        catch (RpcException) { /* -5 not ours / -18 no wallet loaded — try the node */ }
        catch (HttpRequestException) { /* daemon busy — still worth the node attempt */ }
        catch (TimeoutException) { }

        try
        {
            var raw = await CallRpcMethodAsync<JsonElement>("getrawtransaction", txid, true);
            if (raw.ValueKind == JsonValueKind.Object) return ParseTransaction(raw, raw);
        }
        catch (RpcException) { /* -5 without txindex: genuinely unreadable here */ }
        catch (HttpRequestException) { }
        catch (TimeoutException) { }

        return null;
    }

    // ---- Quantum exposure (IExposureRpc — see Services/Quantum) ----
    // ListUnspentAsync above serves this slice too (same signature as IAncestryRpc's).

    /// <summary>Distinct txids of the wallet's own sends, oldest-first pages via listtransactions.</summary>
    public async Task<IReadOnlyList<string>> ListSpendingTxIdsAsync(int maxTransactions = 100_000, CancellationToken ct = default)
    {
        // ONE listtransactions call for the whole history (newest first, up to maxTransactions rows).
        // Paging with count/skip was quadratic — every page walks all the newer rows again — so a
        // 65,702-transaction mining wallet sat at "listing" for about five minutes. One pass takes
        // seconds; HistoryReadTimeout covers a busy daemon.
        ct.ThrowIfCancellationRequested();
        var rows = await CallWalletRpcWithTimeoutAsync<List<JsonElement>>(
            "listtransactions", HistoryReadTimeout, "*", Math.Max(1, maxTransactions), 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var row in rows ?? new List<JsonElement>())
        {
            if (row.TryGetProperty("category", out var cat) && cat.GetString() == "send" &&
                row.TryGetProperty("txid", out var t) && t.GetString() is { Length: > 0 } id && seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>scriptSig hex of every non-coinbase input of a wallet tx (gettransaction … verbose).</summary>
    public async Task<IReadOnlyList<string>> GetInputScriptSigHexAsync(string txid, CancellationToken ct = default)
    {
        var wallet = await CallWalletRpcAsync<JsonElement>("gettransaction", txid, true, true);
        var result = new List<string>();
        if (wallet.ValueKind != JsonValueKind.Object ||
            !wallet.TryGetProperty("decoded", out var decoded) ||
            !decoded.TryGetProperty("vin", out var vin) || vin.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var v in vin.EnumerateArray())
        {
            if (v.TryGetProperty("coinbase", out _)) continue;
            if (v.TryGetProperty("scriptSig", out var ss) && ss.TryGetProperty("hex", out var hex) &&
                hex.GetString() is { Length: > 0 } h)
                result.Add(h);
        }
        return result;
    }

    // ---- Vintage sends (IVintageSendRpc) ----
    // Raw-transaction path on purpose: sendtoaddress/fundrawtransaction pick their own
    // inputs and may re-order outputs, and under FIFO either of those silently sends
    // different satoshis than the plan promised.

    public async Task<string?> GetChangeAddressAsync(CancellationToken ct = default)
        => await GetNewAddressAsync("vintage change");

    public async Task<string?> CreateRawTransactionAsync(
        IReadOnlyList<(string TxId, int Vout)> inputs,
        IReadOnlyList<(string Address, long Satoshis)> outputs,
        CancellationToken ct = default)
    {
        // Outputs go as an ARRAY of single-key objects: that form preserves ORDER and
        // tolerates the same address appearing twice (leading + trailing change).
        var ins = inputs.Select(i => new { txid = i.TxId, vout = i.Vout }).ToArray();
        var outs = outputs
            .Select(o => new Dictionary<string, string>
            {
                [o.Address] = (o.Satoshis / 100_000_000m).ToString("0.00000000", CultureInfo.InvariantCulture)
            })
            .ToArray();
        return await CallRpcMethodAsync<string>("createrawtransaction", ins, outs);
    }

    public async Task<string?> SignRawTransactionAsync(string rawHex, CancellationToken ct = default)
    {
        var signed = await CallWalletRpcAsync<JsonElement>("signrawtransactionwithwallet", rawHex);
        if (signed.ValueKind != JsonValueKind.Object) return null;
        if (signed.TryGetProperty("complete", out var complete) && !complete.GetBoolean()) return null;
        return signed.TryGetProperty("hex", out var hex) ? hex.GetString() : null;
    }

    public async Task<string?> SendRawTransactionAsync(string signedHex, CancellationToken ct = default)
        => await CallRpcMethodAsync<string>("sendrawtransaction", signedHex);

    public async Task<Provenance.BlockRef?> GetBlockRefAsync(string blockHash, CancellationToken ct = default)
    {
        try
        {
            var header = await CallRpcMethodAsync<JsonElement>("getblockheader", blockHash);
            if (header.ValueKind != JsonValueKind.Object) return null;
            return new Provenance.BlockRef(
                header.GetProperty("height").GetInt64(),
                DateTimeOffset.FromUnixTimeSeconds(header.GetProperty("time").GetInt64()).UtcDateTime);
        }
        catch (RpcException) { return null; }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Shapes a decoded transaction; <paramref name="envelope"/> may carry the
    /// block fields (gettransaction has them at the top level, getrawtransaction inline).</summary>
    private static Provenance.RawTransaction ParseTransaction(JsonElement tx, JsonElement envelope)
    {
        var inputs = new List<Provenance.RawTxInput>();
        var isCoinbase = false;
        if (tx.TryGetProperty("vin", out var vin) && vin.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in vin.EnumerateArray())
            {
                if (i.TryGetProperty("coinbase", out _)) { isCoinbase = true; continue; }
                inputs.Add(new Provenance.RawTxInput(
                    i.TryGetProperty("txid", out var t) ? t.GetString() : null,
                    i.TryGetProperty("vout", out var v) ? v.GetInt32() : 0));
            }
        }

        var outputs = new List<Provenance.RawTxOutput>();
        if (tx.TryGetProperty("vout", out var vout) && vout.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in vout.EnumerateArray())
                outputs.Add(new Provenance.RawTxOutput(
                    o.TryGetProperty("n", out var n) ? n.GetInt32() : 0,
                    ToSats(o.TryGetProperty("value", out var val) ? val.GetDecimal() : 0m)));
        }

        static long? Height(JsonElement e) =>
            e.TryGetProperty("blockheight", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt64() : null;
        static DateTime? Time(JsonElement e) =>
            e.TryGetProperty("blocktime", out var t) && t.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).UtcDateTime : null;
        static string? Hash(JsonElement e) =>
            e.TryGetProperty("blockhash", out var b) ? b.GetString() : null;

        return new Provenance.RawTransaction(
            tx.TryGetProperty("txid", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            isCoinbase,
            Hash(envelope) ?? Hash(tx),
            Height(envelope) ?? Height(tx),
            Time(envelope) ?? Time(tx),
            inputs,
            outputs);
    }

    // ---- Wallet encryption / passphrase (Security page) ----
    // encryptwallet returns a status message; the rest return null on success and
    // throw RpcException on failure (e.g. -14 wrong passphrase, -15 already
    // encrypted, -16 unencrypted wallet).
    public async Task<string?> EncryptWalletAsync(string passphrase)
        => await CallWalletRpcAsync<string>("encryptwallet", passphrase);

    public async Task ChangeWalletPassphraseAsync(string oldPassphrase, string newPassphrase)
        => await CallWalletRpcAsync<object>("walletpassphrasechange", oldPassphrase, newPassphrase);

    public async Task UnlockWalletAsync(string passphrase, int seconds)
        => await CallWalletRpcAsync<object>("walletpassphrase", passphrase, seconds);

    public async Task LockWalletAsync()
        => await CallWalletRpcAsync<object>("walletlock");

    public async Task<Balance?> GetBalanceAsync()
    {
        var balance = await CallWalletRpcAsync<decimal>("getbalance");
        return new Balance { Total = balance };
    }

    public async Task<WalletBalances?> GetBalancesAsync()
        => await CallWalletRpcAsync<WalletBalances>("getbalances");

    public async Task<List<Transaction>?> ListTransactionsAsync(int count = 10)
    {
        return await CallWalletRpcAsync<List<Transaction>>("listtransactions", "*", count);
    }

    /// <summary>Load the wallet's FULL transaction history by paging
    /// <c>listtransactions "*" batch skip</c> until a short page signals the end
    /// (Core returns at most <paramref name="batch"/> rows per call). The pages come
    /// back as overlapping recency windows, so the caller should sort the combined
    /// list by time; <paramref name="maxTotal"/> is a safety cap for huge wallets.</summary>
    public async Task<List<Transaction>?> ListAllTransactionsAsync(int batch = 1000, int maxTotal = 100_000)
    {
        var all = new List<Transaction>();
        int skip = 0;
        while (all.Count < maxTotal)
        {
            var page = await CallWalletRpcAsync<List<Transaction>>("listtransactions", "*", batch, skip);
            if (page is null || page.Count == 0) break;
            all.AddRange(page);
            if (page.Count < batch) break;   // reached the oldest transaction
            skip += batch;
        }
        return all;
    }

    public async Task<string?> GetNewAddressAsync(string label = "")
    {
        // Legacy P2PKH (B...) — segwit is consensus-disabled on Blazecoin V2,
        // so bech32/segwit addresses (blz1q...) would lock funds unrecoverably.
        return await CallWalletRpcAsync<string>("getnewaddress", label, "legacy");
    }

    public async Task SetLabelAsync(string address, string label)
    {
        await CallWalletRpcAsync<object>("setlabel", address, label);
    }

    public async Task<List<AddressBook.AddressBookEntry>> ListLabelledAddressesAsync()
    {
        // listlabels → ["", "payout-hot-main", ...]; getaddressesbylabel <label> → { "B...": { "purpose": "receive"|"send" } }.
        // Only "receive" addresses belong on the Receive page ("send" = address-book entries for foreign addresses).
        var result = new List<AddressBook.AddressBookEntry>();
        var labels = await CallWalletRpcAsync<List<string>>("listlabels") ?? new List<string>();
        foreach (var label in labels)
        {
            var map = await CallWalletRpcAsync<JsonElement>("getaddressesbylabel", label);
            if (map.ValueKind != JsonValueKind.Object) continue;
            foreach (var p in map.EnumerateObject())
            {
                var purpose = p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("purpose", out var pu) ? pu.GetString() : "receive";
                if (!string.Equals(purpose, "receive", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new AddressBook.AddressBookEntry { Address = p.Name, Label = label });
            }
        }
        return result;
    }
    public async Task<string?> SendToAddressAsync(string address, decimal amount, bool subtractFeeFromAmount = false)
    {
        // sendtoaddress positional args: address, amount, comment, comment_to,
        // subtractfeefromamount. With subtractfeefromamount=true the fee is taken OUT
        // of the amount (so a full-balance "Send Max" can zero the wallet — otherwise
        // amount+fee > balance and the daemon returns -4 "Insufficient funds").
        return subtractFeeFromAmount
            ? await CallWalletRpcAsync<string>("sendtoaddress", address, amount, "", "", true)
            : await CallWalletRpcAsync<string>("sendtoaddress", address, amount);
    }

    /// <summary>Best-effort precheck so the Send page can reject a malformed/mistyped
    /// address with a clear message up front instead of a cryptic RPC error at send
    /// time. `validateaddress` is a pure node call (no wallet, no broadcast). Returns
    /// null on any failure so a daemon hiccup never blocks a send — the actual send
    /// path and the offline panel handle real connectivity problems.</summary>
    public async Task<bool?> ValidateAddressAsync(string address)
    {
        try
        {
            var r = await CallRpcMethodAsync<ValidateAddressResult>("validateaddress", address);
            return r?.IsValid;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Estimate the network fee for a send WITHOUT broadcasting: build an
    /// unsigned raw tx paying <paramref name="amount"/> to <paramref name="address"/>,
    /// then fund it (input selection + fee calc) via fundrawtransaction. Returns the
    /// fee in BLZ, or null if it can't be estimated (caller shows a rate fallback).</summary>
    public async Task<decimal?> EstimateSendFeeAsync(string address, decimal amount, bool subtractFeeFromAmount = false)
    {
        try
        {
            var outputs = new Dictionary<string, decimal> { [address] = amount };
            var raw = await CallRpcMethodAsync<string>("createrawtransaction", new object[0], outputs);
            if (string.IsNullOrEmpty(raw)) return null;
            // For a "Send Max" the output IS the whole balance, so fund it with
            // subtractFeeFromOutputs (the fee comes out of output 0) — otherwise
            // fundrawtransaction can't add a fee on top and reports insufficient funds.
            var funded = subtractFeeFromAmount
                ? await CallWalletRpcAsync<FundRawResult>("fundrawtransaction", raw, new { subtractFeeFromOutputs = new[] { 0 } })
                : await CallWalletRpcAsync<FundRawResult>("fundrawtransaction", raw);
            return funded?.Fee;
        }
        catch
        {
            return null;   // best-effort — funding can fail (e.g. insufficient funds)
        }
    }

    public async Task<NetworkInfo?> GetNetworkInfoAsync()
    {
        return await CallRpcMethodAsync<NetworkInfo>("getnetworkinfo");
    }

    public async Task<List<PeerSummary>> GetPeersAsync()
    {
        var peers = await CallRpcMethodAsync<List<PeerInfo>>("getpeerinfo");
        return peers?
            .Where(p => !string.IsNullOrEmpty(p.Addr))
            .Select(p => new PeerSummary(p.Addr!, string.IsNullOrWhiteSpace(p.Subver) ? "unknown" : p.Subver!))
            .ToList() ?? new List<PeerSummary>();
    }

    public async Task<BlockchainInfo?> GetBlockchainInfoAsync()
    {
        return await CallRpcMethodAsync<BlockchainInfo>("getblockchaininfo");
    }

    /// <summary>Lightweight daemon-reachability probe used by the pages to render a
    /// shared offline panel. Returns null when the daemon answers, or a ready-to-show
    /// error message (matching the Dashboard's wording) when it can't be reached.
    /// getblockchaininfo needs no wallet, so this works even before a wallet loads.</summary>
    public async Task<string?> CheckConnectionAsync()
    {
        try
        {
            await GetBlockchainInfoAsync();
            return null;
        }
        catch (RpcException ex) when (ex.Code == -28)
        {
            // Daemon is up but still warming up (loading block index / verifying).
            return $"The Blazecoin daemon is starting up — {ex.Message}. This can take a minute or two on launch; click Retry shortly.";
        }
        catch (TimeoutException)
        {
            return "The Blazecoin daemon isn't responding (request timed out). Make sure it's running, then click Retry.";
        }
        catch (DaemonBusyException)
        {
            // MUST come before the HttpRequestException arm below (it derives from it).
            // The daemon is up and almost certainly at the chain tip — saying "offline"
            // here is both wrong and alarming, and it is what made a busy payout daemon
            // look like a dropped connection.
            return "The Blazecoin daemon is busy — every RPC worker is in use, which usually means it's still catching up after a restart. It's running; this normally clears within a few minutes. Click Retry.";
        }
        catch (HttpRequestException ex)
        {
            return $"The Blazecoin daemon appears to be offline. {ex.Message} Make sure the daemon is running on the correct port, then click Retry.";
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the Blazecoin daemon: {ex.Message}";
        }
    }

    public async Task<List<string>> ListWalletsAsync()
    {
        return await CallRpcMethodAsync<List<string>>("listwallets") ?? new List<string>();
    }

    public async Task<List<string>> ListWalletDirAsync()
    {
        var result = await CallRpcMethodAsync<WalletDirResult>("listwalletdir");
        return result?.Wallets?.Select(w => w.Name).Where(n => n != null).Cast<string>().ToList() ?? new List<string>();
    }

    public async Task LoadWalletAsync(string name)
    {
        await CallRpcMethodAsync<LoadWalletResult>("loadwallet", name);
    }

    public async Task UnloadWalletAsync(string name)
    {
        // unloadwallet takes the target wallet name as an argument, so it goes
        // to the BASE endpoint (not /wallet/<active>) — we may be unloading a
        // wallet other than the active one. The call returns only after the
        // daemon has flushed and released the wallet's files, so a caller can
        // safely delete the wallet directory immediately afterwards.
        await CallRpcMethodAsync<object?>("unloadwallet", name);
    }

    public async Task BackupWalletAsync(string destination, string? walletName = null)
    {
        // backupwallet is wallet-scoped — it acts on the wallet hit by the
        // RPC endpoint URL. When the user has multiple wallets loaded we
        // need to route the call to the right one via /wallet/<name>.
        var url = string.IsNullOrEmpty(walletName) ? _rpcUrl : $"{_rpcUrl.TrimEnd('/')}/wallet/{Uri.EscapeDataString(walletName)}";
        await CallRpcAtAsync<object?>(url, "backupwallet", destination);
    }

    // Loading a legacy wallet (restorewallet) or converting it (migratewallet)
    // triggers a FULL-chain rescan. Measured ~40 min for a 31,658-tx wallet on
    // the 4M-block chain, so the old 10-min cap timed out mid-restore (the daemon
    // kept working server-side, but the GUI threw a spurious timeout error and
    // never reached migrate). 2h comfortably covers a large, history-heavy wallet.
    private static readonly TimeSpan LongWalletOpTimeout = TimeSpan.FromHours(2);

    public async Task RestoreWalletAsync(string name, string backupFile)
    {
        await CallRpcAtAsync<LoadWalletResult>(_rpcUrl, "restorewallet", LongWalletOpTimeout, name, backupFile);
    }

    public async Task MigrateWalletAsync(string name, string? passphrase = null)
    {
        var timeout = LongWalletOpTimeout;
        if (string.IsNullOrEmpty(passphrase))
            await CallRpcAtAsync<object?>(_rpcUrl, "migratewallet", timeout, name);
        else
            await CallRpcAtAsync<object?>(_rpcUrl, "migratewallet", timeout, name, passphrase);
    }

    public async Task<Mining.BlockTemplate?> GetBlockTemplateAsync()
    {
        // The "rules" array tells the daemon what soft-fork rules the caller
        // understands. We pass "segwit" so Core 28 accepts the request even
        // though Blazecoin V2's SegwitHeight is NEVER_ACTIVE — Core insists
        // callers acknowledge segwit before serving any template.
        var request = new { rules = new[] { "segwit" } };
        return await CallRpcMethodAsync<Mining.BlockTemplate>("getblocktemplate", request);
    }

    public async Task<string?> SubmitBlockAsync(string hexBlock)
    {
        // submitblock returns null/empty on success, or a string error code
        // ("rejected", "high-hash", "bad-txns-*", "duplicate", etc.).
        return await CallRpcMethodAsync<string>("submitblock", hexBlock);
    }

    private class WalletDirResult
    {
        public List<WalletDirEntry>? Wallets { get; set; }
    }

    private class WalletDirEntry
    {
        public string? Name { get; set; }
    }

    private class LoadWalletResult
    {
        public string? Name { get; set; }
        public string? Warning { get; set; }
    }

    private class PeerInfo
    {
        public string? Addr { get; set; }
        public string? Subver { get; set; }
    }
}

public class RpcResponse<T>
{
    public T? Result { get; set; }
    public RpcError? Error { get; set; }
    public int Id { get; set; }
}

public class RpcError
{
    public int Code { get; set; }
    public string? Message { get; set; }
}

public class WalletInfo
{
    public string? WalletName { get; set; }
    public int WalletVersion { get; set; }
    public decimal Balance { get; set; }
    public decimal UnconfirmedBalance { get; set; }
    public decimal ImmatureBalance { get; set; }
    public int TxCount { get; set; }

    // getwalletinfo includes unlocked_until only when the wallet is encrypted:
    // null = not encrypted; 0 = encrypted & locked; >0 = unix time it stays
    // unlocked until.
    [System.Text.Json.Serialization.JsonPropertyName("unlocked_until")]
    public long? UnlockedUntil { get; set; }
}

public class Balance
{
    public decimal Total { get; set; }
    public decimal Confirmed { get; set; }
    public decimal Unconfirmed { get; set; }
}

/// <summary>Result of getbalances — the "mine" breakdown: trusted (spendable),
/// untrusted_pending (unconfirmed), and immature (coinbase below maturity).</summary>
public class WalletBalances
{
    public BalanceMine? Mine { get; set; }
}

public class BalanceMine
{
    public decimal Trusted { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("untrusted_pending")]
    public decimal UntrustedPending { get; set; }
    public decimal Immature { get; set; }
}

public class Transaction
{
    public string? Address { get; set; }
    public string? Category { get; set; }
    public decimal Amount { get; set; }
    // Only present on "send" rows in listtransactions (negative = fee paid);
    // 0 for receive/generate. Maps from the JSON "fee" field.
    public decimal Fee { get; set; }
    public int Confirmations { get; set; }
    public string? TxId { get; set; }
    public long Time { get; set; }
    // Block the tx landed in (listtransactions "blockheight"); 0 when unconfirmed.
    public int BlockHeight { get; set; }
}

/// <summary>Result of fundrawtransaction — used to read the computed fee for the
/// Send-page estimate (no broadcast).</summary>
public class FundRawResult
{
    public string? Hex { get; set; }
    public decimal Fee { get; set; }
}

// validateaddress result. Web JSON defaults are case-insensitive, so "isvalid"
// maps to IsValid without an explicit attribute.
public class ValidateAddressResult
{
    public bool IsValid { get; set; }
    public string? Address { get; set; }
}

public class NetworkInfo
{
    public int Version { get; set; }
    public string? SubVersion { get; set; }
    public int ProtocolVersion { get; set; }
    public int Connections { get; set; }
    public List<LocalAddress>? LocalAddresses { get; set; }
}

public class LocalAddress
{
    public string? Address { get; set; }
    public int Port { get; set; }
}

public record PeerSummary(string Address, string SubVersion);

public class BlockchainInfo
{
    public string? Chain { get; set; }
    public int Blocks { get; set; }
    public int Headers { get; set; }
    public string? BestBlockHash { get; set; }
    public decimal Difficulty { get; set; }
    public long MedianTime { get; set; }
    public decimal VerificationProgress { get; set; }
    public bool InitialBlockDownload { get; set; }
}

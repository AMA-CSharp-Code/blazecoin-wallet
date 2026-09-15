using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// <see cref="ILiteWalletData"/> over the public gateway. Paths are the gateway's public
/// surface: the Indexer's explorer reads via the <c>/indexer</c> prefix route and the
/// dedicated <c>/api/tx/broadcast</c> relay route. Read failures follow the ecosystem's
/// never-throw contract (null/empty + a log line) so the UI degrades instead of crashing;
/// Broadcast returns a typed failure because the user must SEE why a send didn't go out.
/// </summary>
public sealed class IndexerDataService : ILiteWalletData, IHeaderReader, IAncestryReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<IndexerDataService> _logger;

    public IndexerDataService(HttpClient http, ILogger<IndexerDataService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>The Indexer serves the full derived address ledger.</summary>
    public bool SupportsHistory => true;

    public async Task<LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"indexer/api/address/{Uri.EscapeDataString(address)}", ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return null; // never seen on-chain
            res.EnsureSuccessStatusCode();
            var dto = await res.Content.ReadFromJsonAsync<AddressJson>(Json, ct);
            return dto == null ? null : new LiteAddressSummary(dto.Balance, dto.TotalReceived, dto.TotalSent, dto.TxCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Address summary fetch failed");
            return null;
        }
    }

    public async Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
    {
        try
        {
            var utxos = await _http.GetFromJsonAsync<List<UtxoJson>>(
                $"indexer/api/address/{Uri.EscapeDataString(address)}/utxos", Json, ct);
            return utxos?.Select(u => new LiteChainUtxo(u.TxId, u.OutputIndex, u.Amount, u.Confirmations, u.BlockHeight, u.IsCoinbase, u.ScriptPubKey))
                        .ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UTXO fetch failed");
            return [];
        }
    }

    public async Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        try
        {
            var body = await _http.GetFromJsonAsync<HistoryPageJson>(
                $"indexer/api/address/{Uri.EscapeDataString(address)}/transactions?page={page}&pageSize={pageSize}", Json, ct);
            return body?.Items?.Select(t => new LiteHistoryEntry(t.TxId, t.BlockHeight, t.Timestamp, t.Amount, t.Type))
                       .ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "History fetch failed");
            return [];
        }
    }

    /// <summary>Gateway mode is semi-trusted — chain verification is exactly the point.</summary>
    public bool SupportsChainVerification => true;

    public async Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default)
    {
        try
        {
            var body = await _http.GetFromJsonAsync<RawTxJson>($"indexer/api/tx/{Uri.EscapeDataString(txId)}/raw", Json, ct);
            return body?.Hex;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raw-tx fetch failed");
            return null;
        }
    }

    public async Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default)
    {
        try
        {
            var body = await _http.GetFromJsonAsync<ProofJson>($"indexer/api/tx/{Uri.EscapeDataString(txId)}/proof", Json, ct);
            return body?.Proof;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Merkle-proof fetch failed");
            return null;
        }
    }

    /// <summary>Raw block headers for the wallet's trustless header-chain sync (IHeaderReader).
    /// Never-throw like the other reads — an empty list just stalls the verified tip.</summary>
    public async Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default)
    {
        try
        {
            var body = await _http.GetFromJsonAsync<HeadersJson>($"indexer/api/headers/{fromHeight}?count={count}", Json, ct);
            return body?.Headers ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Header range fetch failed");
            return [];
        }
    }

    /// <summary>Server-side Provenance (IAncestryReader): the gateway runs the desktop
    /// wallet's tracer over this address's coins. Typed result rather than never-throw —
    /// the server's refusals (busy, syncing, output cap) are messages the user must see.</summary>
    public async Task<LiteAncestryResult> GetAncestryAsync(string address, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"indexer/api/address/{Uri.EscapeDataString(address)}/ancestry", ct);
            if (res.IsSuccessStatusCode)
            {
                var dto = await res.Content.ReadFromJsonAsync<AncestryJson>(Json, ct);
                return dto?.Fifo == null || dto.Haircut == null
                    ? LiteAncestryResult.Fail("The gateway returned an empty ancestry response.")
                    : LiteAncestryResult.Ok(new LiteAncestryReport(
                        address, dto.TotalSatoshis, dto.OutputCount, dto.TransactionsRead,
                        MapModel(dto.Fifo), MapModel(dto.Haircut), dto.Truncated));
            }

            string? reason = null;
            try { reason = (await res.Content.ReadFromJsonAsync<BroadcastErrorJson>(Json, ct))?.Error; }
            catch { /* non-JSON error body — fall through to the status-based message */ }
            // A 404 is an older gateway without the endpoint, not a broken one.
            reason ??= res.StatusCode == HttpStatusCode.NotFound
                ? "This gateway doesn't serve provenance yet."
                : $"Ancestry unavailable ({(int)res.StatusCode}).";
            return LiteAncestryResult.Fail(reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ancestry fetch failed");
            return LiteAncestryResult.Fail("Could not reach the Blazecoin service — check your connection.");
        }
    }

    private static LiteVintageModel MapModel(AncestryModelJson m) => new(
        m.AttributedSatoshis, MapRows(m.Years), MapRows(m.Months), MapRows(m.Specials));

    private static IReadOnlyList<LiteVintageRow> MapRows(List<AncestryRowJson>? rows) =>
        rows?.Select(r => new LiteVintageRow(r.Key, r.Label, r.Kind, r.Satoshis, r.OutputCount,
            r.FirstHeight, r.FirstTimeUtc, r.LastTimeUtc)).ToList() ?? [];

    public async Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsJsonAsync("api/tx/broadcast", new { hex = rawTxHex }, Json, ct);
            if (res.IsSuccessStatusCode)
            {
                var ok = await res.Content.ReadFromJsonAsync<BroadcastOkJson>(Json, ct);
                return string.IsNullOrEmpty(ok?.TxId)
                    ? LiteBroadcastResult.Fail("The relay accepted the transaction but returned no txid.", BroadcastFailureKind.Rejected)
                    : LiteBroadcastResult.Ok(ok.TxId);
            }

            string? reason = null;
            try
            {
                var err = await res.Content.ReadFromJsonAsync<BroadcastErrorJson>(Json, ct);
                reason = err?.Error;
            }
            catch { /* non-JSON error body — fall through to the status-based message */ }

            if (res.StatusCode == HttpStatusCode.ServiceUnavailable)
                return LiteBroadcastResult.Fail(
                    reason ?? "The network node is temporarily unreachable — try again shortly.",
                    BroadcastFailureKind.Unreachable);

            reason ??= $"The network rejected the transaction ({(int)res.StatusCode}).";
            return LiteBroadcastResult.Fail(reason, BroadcastReasons.Classify(reason));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Broadcast request failed");
            return LiteBroadcastResult.Fail(
                "Could not reach the Blazecoin service — check your connection.",
                BroadcastFailureKind.Unreachable);
        }
    }

    // Wire shapes (camelCase via JsonSerializerDefaults.Web).
    private sealed record AddressJson(long Balance, long TotalReceived, long TotalSent, int TxCount);
    // scriptPubKey is optional on the wire: older gateways don't send it (backwards compatible).
    private sealed record UtxoJson(string TxId, int OutputIndex, long Amount, int Confirmations, long BlockHeight, bool IsCoinbase,
        string? ScriptPubKey = null);
    private sealed record HistoryTxJson(string TxId, long BlockHeight, DateTime Timestamp, long Amount, string Type);
    private sealed record HistoryPageJson(List<HistoryTxJson>? Items);
    private sealed record BroadcastOkJson(string? TxId);
    private sealed record BroadcastErrorJson(string? Error);
    private sealed record RawTxJson(string? Hex);
    private sealed record ProofJson(string? Proof);
    private sealed record HeadersJson(List<string>? Headers);
    private sealed record AncestryRowJson(string Key, string Label, string Kind, long Satoshis,
        int OutputCount, long FirstHeight, DateTime FirstTimeUtc, DateTime LastTimeUtc);
    private sealed record AncestryModelJson(long AttributedSatoshis,
        List<AncestryRowJson>? Years, List<AncestryRowJson>? Months, List<AncestryRowJson>? Specials);
    private sealed record AncestryJson(long TotalSatoshis, int OutputCount, int TransactionsRead,
        AncestryModelJson? Fifo, AncestryModelJson? Haircut, bool Truncated = false);
}

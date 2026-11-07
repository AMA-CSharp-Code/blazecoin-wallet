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
public sealed class IndexerDataService : ILiteWalletData, IHeaderReader, IAncestryReader, IAddressUsageCrossCheck, ISpendCrossCheck
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

    /// <summary>
    /// Restore discovery's question, asked of up to two gateways (<see cref="IAddressUsageCrossCheck"/>).
    /// The first answer stands when it says "used". When it says "unused" — or is an error, which
    /// proves nothing either way — the same question goes to a gateway OTHER than the one that
    /// answered (<see cref="GatewayRouting"/>), and its "used" wins. With no failover list there
    /// is nobody else to ask and the first answer is all there is.
    /// </summary>
    public async Task<bool> IsAddressUsedByAnyGatewayAsync(string address, CancellationToken ct = default)
    {
        var first = await AskUsedAsync(address, exclude: null, ct);
        if (first.Used) return true;
        if (first.AnsweredBy == null) return false;
        return (await AskUsedAsync(address, [first.AnsweredBy], ct)).Used;
    }

    private async Task<(bool Used, Uri? AnsweredBy)> AskUsedAsync(string address, IReadOnlyCollection<Uri>? exclude, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"indexer/api/address/{Uri.EscapeDataString(address)}");
            if (exclude != null) request.Options.Set(GatewayRouting.Exclude, exclude);
            using var res = await _http.SendAsync(request, ct);
            request.Options.TryGetValue(GatewayRouting.AnsweredBy, out var answeredBy);
            if (!res.IsSuccessStatusCode) return (false, answeredBy);   // 404 = never seen; an error = no evidence of use
            var dto = await res.Content.ReadFromJsonAsync<AddressJson>(Json, ct);
            return (dto is { TxCount: > 0 }, answeredBy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, exclude == null ? "Address usage fetch failed" : "No second gateway answered the address usage question");
            return (false, null);
        }
    }

    public async Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
    {
        try
        {
            // Sent as a request (not GetFromJsonAsync) so the listing can be stamped with the
            // gateway that served it: a second opinion on these coins must come from another one.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"indexer/api/address/{Uri.EscapeDataString(address)}/utxos");
            using var res = await _http.SendAsync(request, ct);
            res.EnsureSuccessStatusCode();
            request.Options.TryGetValue(GatewayRouting.AnsweredBy, out var source);
            var utxos = await res.Content.ReadFromJsonAsync<List<UtxoJson>>(Json, ct);
            return utxos?.Select(u => new LiteChainUtxo(u.TxId, u.OutputIndex, u.Amount, u.Confirmations, u.BlockHeight, u.IsCoinbase, u.ScriptPubKey) { Source = source })
                        .ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UTXO fetch failed");
            return [];
        }
    }

    /// <summary>One request's worth of outputs for <c>outputs/spends</c> (the gateway's own cap).</summary>
    public int MaxOutpointsPerCall => 50;

    /// <summary>The most claimed spends whose evidence is fetched in one call — two requests each,
    /// so a gateway answering "all spent" cannot make the wallet burn its read allowance.</summary>
    internal const int MaxSpendEvidencePerCall = 10;

    /// <summary>
    /// <see cref="ISpendCrossCheck"/>: asks a gateway other than the one that listed each coin
    /// which of them it holds a mined spend for, and fetches the spending transaction and its
    /// merkle proof FROM THAT SAME SIDE (never from the gateway that listed the coin — it would
    /// simply not have them). Coins are grouped by the gateway that listed them; a coin with no
    /// known source has nobody else to be checked against. Null = no second opinion was had.
    /// </summary>
    public async Task<IReadOnlyList<LiteSpendClaim>?> FindSpendsFromAnotherGatewayAsync(
        IReadOnlyList<LiteOutpoint> outpoints, CancellationToken ct = default)
    {
        var claims = new List<LiteSpendClaim>();
        var answered = false;
        foreach (var group in outpoints.Where(o => o.Source != null).GroupBy(o => o.Source!))
        {
            IReadOnlyCollection<Uri> exclude = [group.Key];
            foreach (var chunk in group.Chunk(MaxOutpointsPerCall))
            {
                var spent = await AskSpendsAsync(chunk, exclude, ct);
                if (spent == null) continue;          // nobody else answered for this chunk
                answered = true;
                foreach (var s in spent.Take(MaxSpendEvidencePerCall))
                    claims.Add(new LiteSpendClaim(s.TxId, s.Vout, s.SpentTxId!, s.SpentHeight ?? 0,
                        await GetFromAnotherGatewayAsync<RawTxJson>($"indexer/api/tx/{Uri.EscapeDataString(s.SpentTxId!)}/raw", exclude, b => b.Hex, ct),
                        await GetFromAnotherGatewayAsync<ProofJson>($"indexer/api/tx/{Uri.EscapeDataString(s.SpentTxId!)}/proof", exclude, b => b.Proof, ct)));
            }
        }
        return answered ? claims : null;
    }

    /// <summary>The outputs a second gateway says were spent in a block; null when it gave no usable
    /// answer (unreachable, none configured, or it does not serve the question — an older gateway
    /// or a full Indexer site answers 404).</summary>
    private async Task<List<SpendJson>?> AskSpendsAsync(IReadOnlyList<LiteOutpoint> outpoints, IReadOnlyCollection<Uri> exclude, CancellationToken ct)
    {
        try
        {
            var asked = string.Join(',', outpoints.Select(o => $"{o.TxId}:{o.Vout}"));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"indexer/api/outputs/spends?o={asked}");
            request.Options.Set(GatewayRouting.Exclude, exclude);
            using var res = await _http.SendAsync(request, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<SpendsJson>(Json, ct);
            if (body?.Items == null) return null;
            // Only answers to what was asked, and only ones that name a spender.
            return body.Items
                .Where(i => i is { TxId: not null, SpentTxId.Length: 64 } &&
                            outpoints.Any(o => o.Vout == i.Vout && string.Equals(o.TxId, i.TxId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No second gateway answered the spent-output question");
            return null;
        }
    }

    private async Task<string?> GetFromAnotherGatewayAsync<TBody>(string path, IReadOnlyCollection<Uri> exclude,
        Func<TBody, string?> pick, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Options.Set(GatewayRouting.Exclude, exclude);
            using var res = await _http.SendAsync(request, ct);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<TBody>(Json, ct);
            return body == null ? null : pick(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spend evidence fetch from a second gateway failed");
            return null;
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
            // The one long gateway call: it runs under the trace budget, not the read budget
            // (RequestTimeoutHandler) — the server may legitimately spend tens of seconds here.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"indexer/api/address/{Uri.EscapeDataString(address)}/ancestry");
            request.Options.Set(RequestTimeoutHandler.Budget, RequestBudget.Trace);
            using var res = await _http.SendAsync(request, ct);
            if (res.IsSuccessStatusCode)
            {
                var dto = await res.Content.ReadFromJsonAsync<AncestryJson>(Json, ct);
                return dto?.Fifo == null || dto.Haircut == null
                    ? LiteAncestryResult.Fail("The gateway returned an empty ancestry response.")
                    : LiteAncestryResult.Ok(new LiteAncestryReport(
                        address, dto.TotalSatoshis, dto.OutputCount, dto.TransactionsRead,
                        MapModel(dto.Fifo), MapModel(dto.Haircut), dto.Truncated, dto.TruncatedBy));
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
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The trace budget ran out, not the network: say so, and say what helps — the
            // gateway caches what it read, so trying again picks up where this stopped.
            _logger.LogWarning(ex, "Ancestry trace timed out");
            return LiteAncestryResult.Fail(
                "The gateway did not finish the trace in time — try again; it carries on from where it stopped.");
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

    /// <summary>
    /// Relays a signed transaction and reports what the NETWORK made of it — as far as that can
    /// be known through gateways the wallet does not trust (bug-bounty reports 2026-11-06).
    /// <list type="bullet">
    /// <item>The txid returned is the wallet's own (<see cref="LiteTxId"/>), never the relay's string.</item>
    /// <item>Whatever the first gateway answers, a SECOND gateway is given the same transaction.
    /// Broadcasting is safe to repeat: a node that already has the transaction says so. This both
    /// delivers a transaction the first gateway only claimed to relay and tests a rejection the
    /// first gateway may have invented.</item>
    /// <item>A rejection is the network's judgement only when two gateways agree; one gateway's
    /// rejection is reported as exactly that. An acceptance stands on one honest gateway.</item>
    /// <item>Only an answer that judges the transaction (400/422) is a verdict; a 429 or any 5xx
    /// judged nothing and is treated as an outage, so the peer-to-peer fallback still runs.</item>
    /// </list>
    /// With a single gateway (a custom setup, or every other gateway down) there is nobody to
    /// ask, and the result says so through <see cref="LiteBroadcastResult.Uncorroborated"/>.
    /// </summary>
    public async Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default)
    {
        var ownTxId = LiteTxId.OfHex(rawTxHex);

        var first = await PostBroadcastAsync(rawTxHex, exclude: null, ct);
        if (first.Verdict == RelayVerdict.NoAnswer)
            return LiteBroadcastResult.Fail(
                "Could not reach the Blazecoin service — check your connection.",
                BroadcastFailureKind.Unreachable);

        // Ask someone else. Without a failover list (AnsweredBy unknown) there is no one to ask.
        var second = first.AnsweredBy == null
            ? RelayAttempt.None
            : await PostBroadcastAsync(rawTxHex, exclude: [first.AnsweredBy], ct);

        foreach (var attempt in new[] { first, second })
            if (attempt.RelayTxId != null && ownTxId != null &&
                !string.Equals(attempt.RelayTxId, ownTxId, StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("A gateway returned a transaction id that is not the id of the transaction it was sent; ignored.");

        var acceptances = Count(first, second, RelayVerdict.Accepted);
        var duplicates = Count(first, second, RelayVerdict.Duplicate);
        var rejections = Count(first, second, RelayVerdict.Rejected);

        if (acceptances > 0)
        {
            // Backed when the other gateway also took it or already had it.
            var backed = acceptances + duplicates == 2;
            var txId = ownTxId ?? first.RelayTxId ?? second.RelayTxId;
            return string.IsNullOrEmpty(txId)
                ? LiteBroadcastResult.Fail("The relay accepted the transaction but returned no txid.", BroadcastFailureKind.Rejected, uncorroborated: !backed)
                : LiteBroadcastResult.Ok(txId, uncorroborated: !backed);
        }

        if (duplicates > 0)
        {
            var reason = (first.Verdict == RelayVerdict.Duplicate ? first : second).Reason
                ?? "The network already has this transaction.";
            return LiteBroadcastResult.Fail(reason, BroadcastFailureKind.Duplicate, uncorroborated: duplicates < 2);
        }

        if (rejections == 2)
            return LiteBroadcastResult.Fail(first.Reason!, BroadcastFailureKind.Rejected);

        if (rejections == 1)
        {
            var reason = (first.Verdict == RelayVerdict.Rejected ? first : second).Reason!;
            return LiteBroadcastResult.Fail(
                reason + " — reported by one gateway only; no second gateway could confirm it. " +
                "If this looks wrong, switch gateway in Settings and try again.",
                BroadcastFailureKind.Rejected, uncorroborated: true);
        }

        // Nobody judged anything (outage answers only) — fallback-eligible.
        return LiteBroadcastResult.Fail(
            first.Reason ?? second.Reason ?? "The network node is temporarily unreachable — try again shortly.",
            BroadcastFailureKind.Unreachable);
    }

    private static int Count(RelayAttempt a, RelayAttempt b, RelayVerdict verdict)
        => (a.Verdict == verdict ? 1 : 0) + (b.Verdict == verdict ? 1 : 0);

    /// <summary>What ONE gateway said about a broadcast.</summary>
    private enum RelayVerdict
    {
        /// <summary>No gateway could be reached (or there was no other gateway to ask).</summary>
        NoAnswer,
        /// <summary>It answered without judging the transaction: 503, 429, any other 5xx.</summary>
        NotJudged,
        Accepted,
        Duplicate,
        Rejected,
    }

    private sealed record RelayAttempt(RelayVerdict Verdict, string? Reason, string? RelayTxId, Uri? AnsweredBy)
    {
        public static readonly RelayAttempt None = new(RelayVerdict.NoAnswer, null, null, null);
    }

    private async Task<RelayAttempt> PostBroadcastAsync(string rawTxHex, IReadOnlyCollection<Uri>? exclude, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/tx/broadcast")
            {
                Content = JsonContent.Create(new { hex = rawTxHex }, options: Json),
            };
            if (exclude != null) request.Options.Set(GatewayRouting.Exclude, exclude);

            using var res = await _http.SendAsync(request, ct);
            request.Options.TryGetValue(GatewayRouting.AnsweredBy, out var answeredBy);

            if (res.IsSuccessStatusCode)
            {
                var ok = await res.Content.ReadFromJsonAsync<BroadcastOkJson>(Json, ct);
                return new RelayAttempt(RelayVerdict.Accepted, null, ok?.TxId, answeredBy);
            }

            string? reason = null;
            try
            {
                var err = await res.Content.ReadFromJsonAsync<BroadcastErrorJson>(Json, ct);
                reason = err?.Error;
            }
            catch { /* non-JSON error body — fall through to the status-based message */ }

            // Only 400 (not a transaction) and 422 (the daemon refused it) judge the transaction.
            var judged = res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
            if (!judged)
                return new RelayAttempt(RelayVerdict.NotJudged,
                    res.StatusCode == HttpStatusCode.ServiceUnavailable ? reason : null, null, answeredBy);

            reason ??= $"The network rejected the transaction ({(int)res.StatusCode}).";
            return new RelayAttempt(
                BroadcastReasons.Classify(reason) == BroadcastFailureKind.Duplicate ? RelayVerdict.Duplicate : RelayVerdict.Rejected,
                reason, null, answeredBy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, exclude == null ? "Broadcast request failed" : "No second gateway answered the broadcast");
            return RelayAttempt.None;
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
    private sealed record SpendJson(string? TxId, int Vout, bool Known, string? SpentTxId, long? SpentHeight);
    private sealed record SpendsJson(List<SpendJson>? Items);
    private sealed record AncestryRowJson(string Key, string Label, string Kind, long Satoshis,
        int OutputCount, long FirstHeight, DateTime FirstTimeUtc, DateTime LastTimeUtc);
    private sealed record AncestryModelJson(long AttributedSatoshis,
        List<AncestryRowJson>? Years, List<AncestryRowJson>? Months, List<AncestryRowJson>? Specials);
    private sealed record AncestryJson(long TotalSatoshis, int OutputCount, int TransactionsRead,
        AncestryModelJson? Fifo, AncestryModelJson? Haircut, bool Truncated = false, string? TruncatedBy = null);
}

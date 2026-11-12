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
public sealed class IndexerDataService : ILiteWalletData, IHeaderReader, IHeaderCrossCheck, IAncestryReader, IAddressUsageCrossCheck, ISpendCrossCheck
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<IndexerDataService> _logger;
    // Every question put to a SECOND gateway goes through this one client (audit 2026-11-09).
    private readonly SecondOpinionClient _secondOpinion;

    public IndexerDataService(HttpClient http, ILogger<IndexerDataService> logger)
    {
        _http = http;
        _logger = logger;
        _secondOpinion = new SecondOpinionClient(http, logger);
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
        var path = $"indexer/api/address/{Uri.EscapeDataString(address)}";
        Uri? answeredBy = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var res = await _http.SendAsync(request, ct);
            if (request.Options.TryGetValue(GatewayRouting.AnsweredBy, out var who)) answeredBy = who;
            // 404 = never seen; an error = no evidence of use.
            if (res.IsSuccessStatusCode && await res.Content.ReadFromJsonAsync<AddressJson>(Json, ct) is { TxCount: > 0 }) return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A first answer that failed or could not be READ proves nothing either way, so it goes
            // to the second gateway like an "unused" does. Until 2026-11-12 this returned "unused"
            // on the spot: a gateway answering HTTP 200 with a body that was not JSON ended a
            // restore's scan early, and the honest second gateway was never asked (bug-bounty
            // report, round fourteen). The summary above always said an error gets a second ask.
            _logger.LogWarning(ex, "Address usage fetch failed; asking another gateway");
        }

        var second = await AskAnotherGatewayAsync<AddressJson>(path, answeredBy == null ? null : [answeredBy], "the address usage question", ct);
        return second.Body is { TxCount: > 0 };
    }

    /// <summary>A GET put to a second gateway and its JSON answer read (<see cref="SecondOpinionClient"/>
    /// decides who "someone else" is and what happened). <c>Body</c> is set only when another
    /// server answered with success and the answer could be read; an answer that could not be
    /// read is no answer (<see cref="SecondGateway.Unavailable"/>).</summary>
    private async Task<(SecondGateway Status, bool Success, TBody? Body, Uri? AnsweredBy)> AskAnotherGatewayAsync<TBody>(
        string path, IReadOnlyCollection<Uri>? exclude, string question, CancellationToken ct) where TBody : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var opinion = await _secondOpinion.SendAsync(request, exclude, question, ct);
        if (opinion.Status != SecondGateway.Answered) return (opinion.Status, false, null, null);
        if (!opinion.IsSuccess) return (SecondGateway.Answered, false, null, opinion.AnsweredBy);
        try
        {
            return (SecondGateway.Answered, true, await opinion.Response!.Content.ReadFromJsonAsync<TBody>(Json, ct), opinion.AnsweredBy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A second gateway's answer to {Question} could not be read", question);
            return (SecondGateway.Unavailable, false, null, opinion.AnsweredBy);
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

    /// <summary>The most claimed spends whose evidence is fetched for one question — two requests
    /// each, so a gateway answering "all spent" cannot make the wallet burn its read allowance.
    /// The claims beyond it are still returned, marked <see cref="LiteSpendClaim.EvidencePending"/>.</summary>
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
                // Every spend named becomes a claim. Past the cap the evidence waits for a later
                // round and the claim says so — it used to be dropped, and a coin missing from the
                // answer reads as "no spend named" (audit 2026-11-09). Evidence goes to the outputs
                // in the order they were ASKED about, whatever order the gateway answered in, and
                // one answer per output — a gateway repeating itself cannot use up the cap.
                var fetched = 0;
                foreach (var s in spent.GroupBy(s => AskedPosition(chunk, s)).OrderBy(g => g.Key).Select(g => g.First()))
                {
                    if (fetched++ >= MaxSpendEvidencePerCall)
                    {
                        claims.Add(new LiteSpendClaim(s.TxId!, s.Vout, s.SpentTxId!, s.SpentHeight ?? 0, null, null) { EvidencePending = true });
                        continue;
                    }
                    var tx = Uri.EscapeDataString(s.SpentTxId!);
                    claims.Add(new LiteSpendClaim(s.TxId!, s.Vout, s.SpentTxId!, s.SpentHeight ?? 0,
                        (await AskAnotherGatewayAsync<RawTxJson>($"indexer/api/tx/{tx}/raw", exclude, "for a spend's transaction", ct)).Body?.Hex,
                        (await AskAnotherGatewayAsync<ProofJson>($"indexer/api/tx/{tx}/proof", exclude, "for a spend's proof", ct)).Body?.Proof));
                }
            }
        }
        return answered ? claims : null;
    }

    private static int AskedPosition(LiteOutpoint[] asked, SpendJson answer) =>
        Array.FindIndex(asked, o => o.Vout == answer.Vout && string.Equals(o.TxId, answer.TxId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The outputs a second gateway says were spent in a block; null when it gave no usable
    /// answer (unreachable, none configured, or it does not serve the question — an older gateway
    /// or a full Indexer site answers 404).</summary>
    private async Task<List<SpendJson>?> AskSpendsAsync(IReadOnlyList<LiteOutpoint> outpoints, IReadOnlyCollection<Uri> exclude, CancellationToken ct)
    {
        var asked = string.Join(',', outpoints.Select(o => $"{o.TxId}:{o.Vout}"));
        var answer = await AskAnotherGatewayAsync<SpendsJson>($"indexer/api/outputs/spends?o={asked}", exclude, "the spent-output question", ct);
        if (answer.Body?.Items == null) return null;
        // Only answers to what was asked, and only ones that name a spender.
        return answer.Body.Items
            .Where(i => i is { TxId: not null, SpentTxId.Length: 64 } &&
                        outpoints.Any(o => o.Vout == i.Vout && string.Equals(o.TxId, i.TxId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
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
    public async Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default) =>
        (await GetHeaderBatchAsync(fromHeight, count, null, ct)).Headers;

    /// <summary>The same read, stamped with the gateway that served it — the batch carries its own
    /// source, so nothing here remembers "the last one" (audit 2026-11-09). With
    /// <paramref name="notFrom"/> the request goes to another server, or comes back empty.</summary>
    public async Task<HeaderBatch> GetHeaderBatchAsync(long fromHeight, int count,
        IReadOnlyCollection<Uri>? notFrom = null, CancellationToken ct = default)
    {
        var path = $"indexer/api/headers/{fromHeight}?count={count}";
        try
        {
            if (notFrom is { Count: > 0 })
            {
                var steered = await AskAnotherGatewayAsync<HeadersJson>(path, notFrom, "for the headers that place a block", ct);
                return steered.Status == SecondGateway.Answered ? new(steered.Body?.Headers ?? [], steered.AnsweredBy) : HeaderBatch.Nothing;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var res = await _http.SendAsync(request, ct);
            request.Options.TryGetValue(GatewayRouting.AnsweredBy, out var source);
            if (!res.IsSuccessStatusCode) return new([], source);
            var body = await res.Content.ReadFromJsonAsync<HeadersJson>(Json, ct);
            return new(body?.Headers ?? [], source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Header range fetch failed");
            return HeaderBatch.Nothing;
        }
    }

    /// <summary>
    /// The header at <paramref name="height"/> from a gateway other than the ones that served the
    /// headers in question (<see cref="IHeaderCrossCheck"/>; bug-bounty report 2026-11-07). Without
    /// a failover list nobody is named and there is nobody else to ask — and a client that ignores
    /// the exclusion (no <see cref="GatewayFailoverHandler"/>) is recognised by the same server
    /// answering again, under whichever of its names.
    /// </summary>
    public async Task<HeaderSecondOpinion> HeaderFromAnotherGatewayAsync(long height, IReadOnlyCollection<Uri> servedBy, CancellationToken ct = default)
    {
        var answer = await AskAnotherGatewayAsync<HeadersJson>($"indexer/api/headers/{height}?count=1", servedBy, "the header question", ct);
        if (answer.Status != SecondGateway.Answered) return new(answer.Status, null);
        // It answered, but with an error: that is no word on the header either way.
        return answer.Success
            ? new(SecondGateway.Answered, answer.Body?.Headers?.FirstOrDefault())
            : new(SecondGateway.Unavailable, null);
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
    /// This method only collects the two answers; the rule and its wording are <see cref="BroadcastVerdict"/>.
    /// </summary>
    public async Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default)
    {
        var ownTxId = LiteTxId.OfHex(rawTxHex);

        // The gateway in use first; then, whatever it said, someone else (SecondOpinionClient:
        // another server, or nobody). What the two answers add up to is BroadcastVerdict's rule.
        var first = await PostBroadcastAsync(rawTxHex, ct);
        var second = first.Verdict == RelayVerdict.NoAnswer
            ? RelayAttempt.None
            : await PostBroadcastElsewhereAsync(rawTxHex, first.AnsweredBy, ct);

        if (BroadcastVerdict.NamesAnotherTransaction(first, ownTxId) || BroadcastVerdict.NamesAnotherTransaction(second, ownTxId))
            _logger.LogWarning("A gateway returned a transaction id that is not the id of the transaction it was sent; ignored.");

        return BroadcastVerdict.Combine(first, second, ownTxId);
    }

    private HttpRequestMessage BroadcastRequest(string rawTxHex) =>
        new(HttpMethod.Post, "api/tx/broadcast") { Content = JsonContent.Create(new { hex = rawTxHex }, options: Json) };

    private async Task<RelayAttempt> PostBroadcastAsync(string rawTxHex, CancellationToken ct)
    {
        try
        {
            using var request = BroadcastRequest(rawTxHex);
            using var res = await _http.SendAsync(request, ct);
            request.Options.TryGetValue(GatewayRouting.AnsweredBy, out var answeredBy);
            return await ReadRelayAttemptAsync(res, answeredBy, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Broadcast request failed");
            return RelayAttempt.None;
        }
    }

    /// <summary>The same transaction, given to a gateway other than the one that just answered.</summary>
    private async Task<RelayAttempt> PostBroadcastElsewhereAsync(string rawTxHex, Uri? answeredBy, CancellationToken ct)
    {
        using var request = BroadcastRequest(rawTxHex);
        using var opinion = await _secondOpinion.SendAsync(request, answeredBy == null ? null : [answeredBy], "the broadcast", ct);
        if (opinion.Status != SecondGateway.Answered) return RelayAttempt.None;
        try { return await ReadRelayAttemptAsync(opinion.Response!, opinion.AnsweredBy, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A second gateway's answer to the broadcast could not be read");
            return RelayAttempt.None;
        }
    }

    /// <summary>Reads one gateway's reply to a broadcast; what it amounts to is <see cref="RelayAttempt.FromReply"/>.</summary>
    private static async Task<RelayAttempt> ReadRelayAttemptAsync(HttpResponseMessage res, Uri? answeredBy, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            var ok = await res.Content.ReadFromJsonAsync<BroadcastOkJson>(Json, ct);
            return RelayAttempt.FromReply(res.StatusCode, ok?.TxId, null, answeredBy);
        }

        string? reason = null;
        try
        {
            var err = await res.Content.ReadFromJsonAsync<BroadcastErrorJson>(Json, ct);
            reason = err?.Error;
        }
        catch { /* non-JSON error body — fall through to the status-based message */ }
        return RelayAttempt.FromReply(res.StatusCode, null, reason, answeredBy);
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

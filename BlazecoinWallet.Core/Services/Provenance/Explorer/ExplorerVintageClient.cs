using System.Net;
using System.Text.Json;

namespace BlazecoinWallet.Core.Services.Provenance.Explorer;

/// <summary>
/// One bucket of an address's lineage as the block explorer stores it: the Indexer's
/// <c>YearKey</c> (a calendar year, or a special-vintage key below 2000 — see
/// <see cref="ExplorerVintageKeys"/>), the satoshis ever created to the address with that
/// lineage (what a collector claim is judged on), and the slice still unspent.
/// </summary>
public sealed record ExplorerVintageRow(int YearKey, long EverHeldSatoshis, long UnspentSatoshis);

/// <summary>The explorer's answer for one address, with the ancestry worker's watermark.</summary>
public sealed record ExplorerAddressVintage(string Address, long ComputedToHeight, IReadOnlyList<ExplorerVintageRow> Rows);

/// <summary>One calendar year of the chain and the block heights it spans (block-timestamp years, the explorer's own vintage bucket).</summary>
public sealed record ExplorerBlockYear(int Year, long FirstHeight, long LastHeight, long BlockCount);

/// <summary>
/// The chain's years as the explorer knows them (<c>/indexer/api/blocks/years</c>, added
/// 2026-10-31), so the Provenance page can show a year vintage's block range beside its
/// lineage figures. <see cref="TipHeight"/> marks the year still being mined.
/// </summary>
public sealed record ExplorerBlockYears(long TipHeight, IReadOnlyList<ExplorerBlockYear> Years)
{
    /// <summary>The year's range in words: "0 – 586,042", or "3,958,824 – now" for the year the tip is in.</summary>
    public string? RangeLabel(int year)
    {
        var y = Years.FirstOrDefault(r => r.Year == year);
        if (y is null) return null;
        var first = y.FirstHeight.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var last = y.LastHeight >= TipHeight
            ? "now"
            : y.LastHeight.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return $"{first} – {last}";
    }

    /// <summary>The year's block count, or null when the explorer did not list the year.</summary>
    public long? BlockCount(int year) => Years.FirstOrDefault(r => r.Year == year)?.BlockCount;
}

/// <summary>
/// The explorer could not answer: unreachable, an error status, or (451) a region it does
/// not serve. The message is user-facing.
/// </summary>
public sealed class ExplorerUnavailableException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>On a 429, how long the explorer asked us to wait (its <c>Retry-After</c>), when it said.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// Reads an address's stored lineage from the block explorer. The desktop wallet's own
/// tracer walks the chain through the daemon, which is trustless but exhausts its read
/// budget on wallets whose coins run through deep consolidation history (the 2026-10-30
/// measurement: 1,000,000 reads attributed 0.3 % of a heavily consolidated wallet). The explorer's
/// ancestry worker computes every output's lineage once as blocks arrive, so it can answer
/// for the whole wallet at once. Asking it sends the wallet's addresses to that server, so
/// the page only does it on a click or an explicit preference.
/// </summary>
public interface IExplorerVintageClient
{
    /// <summary>
    /// The explorer's vintage breakdown for <paramref name="address"/>, or null when the
    /// explorer has never seen the address (HTTP 404). Throws
    /// <see cref="ExplorerUnavailableException"/> for any other failure.
    /// </summary>
    Task<ExplorerAddressVintage?> GetAddressVintageAsync(string explorerBaseUrl, string address, CancellationToken ct = default);

    /// <summary>
    /// The explorer's lineage split for each UNSPENT output of <paramref name="address"/>
    /// (<c>/indexer/api/address/{address}/outputs/vintage</c>, added 2026-10-31 for "send by
    /// explorer lineage"), or null when the explorer has never seen the address. Outputs the
    /// ancestry worker has not reached carry an empty split. Throws
    /// <see cref="ExplorerUnavailableException"/> for any other failure.
    /// </summary>
    Task<ExplorerAddressOutputs?> GetAddressOutputVintagesAsync(string explorerBaseUrl, string address, CancellationToken ct = default);

    /// <summary>
    /// The block heights each calendar year spans (<c>/indexer/api/blocks/years</c>), or null
    /// when the explorer does not have the route (an older Indexer answers 404). Throws
    /// <see cref="ExplorerUnavailableException"/> for any other failure.
    /// </summary>
    Task<ExplorerBlockYears?> GetBlockYearsAsync(string explorerBaseUrl, CancellationToken ct = default);
}

public sealed class ExplorerVintageClient : IExplorerVintageClient
{
    /// <summary>The named <see cref="HttpClient"/> this client asks the factory for.</summary>
    public const string HttpClientName = "explorer";

    private readonly IHttpClientFactory _factory;

    public ExplorerVintageClient(IHttpClientFactory factory) => _factory = factory;

    /// <summary>
    /// The explorer's per-address vintage endpoint, reached through the site's gateway:
    /// <c>{explorer}/indexer/api/address/{address}/vintage</c>. Public, no account needed.
    /// </summary>
    public static Uri VintageUri(string explorerBaseUrl, string address)
    {
        var root = (explorerBaseUrl ?? "").Trim().TrimEnd('/');
        if (root.Length == 0) throw new ArgumentException("An explorer URL is required.", nameof(explorerBaseUrl));
        return new Uri($"{root}/indexer/api/address/{Uri.EscapeDataString(address)}/vintage");
    }

    /// <summary>The per-output endpoint: <c>{explorer}/indexer/api/address/{address}/outputs/vintage</c>.</summary>
    public static Uri OutputsVintageUri(string explorerBaseUrl, string address)
    {
        var root = (explorerBaseUrl ?? "").Trim().TrimEnd('/');
        if (root.Length == 0) throw new ArgumentException("An explorer URL is required.", nameof(explorerBaseUrl));
        return new Uri($"{root}/indexer/api/address/{Uri.EscapeDataString(address)}/outputs/vintage");
    }

    /// <summary>The chain-years endpoint: <c>{explorer}/indexer/api/blocks/years</c>.</summary>
    public static Uri BlockYearsUri(string explorerBaseUrl)
    {
        var root = (explorerBaseUrl ?? "").Trim().TrimEnd('/');
        if (root.Length == 0) throw new ArgumentException("An explorer URL is required.", nameof(explorerBaseUrl));
        return new Uri($"{root}/indexer/api/blocks/years");
    }

    public async Task<ExplorerBlockYears?> GetBlockYearsAsync(string explorerBaseUrl, CancellationToken ct = default)
    {
        using var doc = await FetchAsync(BlockYearsUri(explorerBaseUrl), ct);
        return doc is null ? null : ParseBlockYears(doc.RootElement);
    }

    public async Task<ExplorerAddressVintage?> GetAddressVintageAsync(string explorerBaseUrl, string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("An address is required.", nameof(address));
        using var doc = await FetchAsync(VintageUri(explorerBaseUrl, address), ct);
        return doc is null ? null : Parse(doc.RootElement, address);
    }

    public async Task<ExplorerAddressOutputs?> GetAddressOutputVintagesAsync(string explorerBaseUrl, string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("An address is required.", nameof(address));
        using var doc = await FetchAsync(OutputsVintageUri(explorerBaseUrl, address), ct);
        return doc is null ? null : ParseOutputs(doc.RootElement, address);
    }

    /// <summary>GET + the shared failure contract: null on 404, a plain-words exception otherwise.</summary>
    private async Task<JsonDocument?> FetchAsync(Uri uri, CancellationToken ct)
    {
        var client = _factory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(uri, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ExplorerUnavailableException($"The explorer could not be reached: {ex.Message}");
        }

        using (response)
        {
            if ((int)response.StatusCode == 451)
                throw new ExplorerUnavailableException("The explorer is not available from your region (HTTP 451).", 451);
            if ((int)response.StatusCode == 429)
                throw new ExplorerUnavailableException("The explorer is limiting requests from this computer (HTTP 429) — wait a minute and ask again.", 429)
                { RetryAfter = RetryAfterOf(response) };
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new ExplorerUnavailableException($"The explorer answered HTTP {(int)response.StatusCode}.", (int)response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }

    /// <summary>
    /// The response's <c>Retry-After</c> as a wait (seconds or an HTTP date), clamped to
    /// 1 s – 10 min so a bad header can neither hammer nor hang; null when absent.
    /// </summary>
    public static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        TimeSpan? wait = header.Delta ?? (header.Date is { } at ? at - DateTimeOffset.UtcNow : null);
        if (wait is not { } w) return null;
        if (w < TimeSpan.FromSeconds(1)) return TimeSpan.FromSeconds(1);
        return w > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : w;
    }

    /// <summary>Parses the endpoint's JSON; tolerant of extra fields, strict about the ones it needs.</summary>
    public static ExplorerAddressVintage Parse(JsonElement root, string fallbackAddress)
    {
        var address = root.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()! : fallbackAddress;
        var computedTo = root.TryGetProperty("computedToHeight", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt64() : -1;

        var rows = new List<ExplorerVintageRow>();
        if (root.TryGetProperty("years", out var years) && years.ValueKind == JsonValueKind.Array)
        {
            foreach (var y in years.EnumerateArray())
            {
                if (!y.TryGetProperty("year", out var key) || key.ValueKind != JsonValueKind.Number) continue;
                var ever = y.TryGetProperty("everHeldSatoshis", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
                var unspent = y.TryGetProperty("unspentSatoshis", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0;
                rows.Add(new ExplorerVintageRow(key.GetInt32(), ever, unspent));
            }
        }
        return new ExplorerAddressVintage(address, computedTo, rows);
    }

    /// <summary>Parses the chain-years endpoint's JSON; tolerant of extra fields, strict about the ones it needs.</summary>
    public static ExplorerBlockYears ParseBlockYears(JsonElement root)
    {
        var tip = root.TryGetProperty("tipHeight", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : -1;
        var years = new List<ExplorerBlockYear>();
        if (root.TryGetProperty("years", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var y in arr.EnumerateArray())
            {
                if (!y.TryGetProperty("year", out var yr) || yr.ValueKind != JsonValueKind.Number) continue;
                if (!y.TryGetProperty("firstHeight", out var f) || f.ValueKind != JsonValueKind.Number) continue;
                if (!y.TryGetProperty("lastHeight", out var l) || l.ValueKind != JsonValueKind.Number) continue;
                var count = y.TryGetProperty("blockCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
                years.Add(new ExplorerBlockYear(yr.GetInt32(), f.GetInt64(), l.GetInt64(), count));
            }
        }
        return new ExplorerBlockYears(tip, years);
    }

    /// <summary>Parses the per-output endpoint's JSON; tolerant of extra fields, strict about the ones it needs.</summary>
    public static ExplorerAddressOutputs ParseOutputs(JsonElement root, string fallbackAddress)
    {
        var address = root.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()! : fallbackAddress;
        var computedTo = root.TryGetProperty("computedToHeight", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt64() : -1;

        var outputs = new List<ExplorerOutputVintage>();
        if (root.TryGetProperty("outputs", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in arr.EnumerateArray())
            {
                if (!o.TryGetProperty("txId", out var tx) || tx.ValueKind != JsonValueKind.String) continue;
                if (!o.TryGetProperty("vout", out var v) || v.ValueKind != JsonValueKind.Number) continue;
                var sats = o.TryGetProperty("satoshis", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                var rows = new List<ExplorerOutputVintageRow>();
                if (o.TryGetProperty("vintages", out var vs) && vs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in vs.EnumerateArray())
                    {
                        if (!r.TryGetProperty("year", out var y) || y.ValueKind != JsonValueKind.Number) continue;
                        var rs = r.TryGetProperty("satoshis", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt64() : 0;
                        rows.Add(new ExplorerOutputVintageRow(y.GetInt32(), rs));
                    }
                }
                outputs.Add(new ExplorerOutputVintage(tx.GetString()!, v.GetInt32(), sats, rows));
            }
        }
        return new ExplorerAddressOutputs(address, computedTo, outputs);
    }
}

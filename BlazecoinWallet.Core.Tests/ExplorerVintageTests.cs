using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Provenance.Explorer;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The explorer-lineage path added 2026-10-30: the desktop Provenance page can ask the
/// block explorer for a wallet's stored lineage when the local walk stops at its read
/// budget. These pin (1) the explorer key map against the wallet's own classifier, so the
/// two catalogues share keys; (2) the HTTP client's parsing and failure modes, including
/// the geo-block's 451; (3) the per-address aggregation; (4) the side-by-side verdicts.
/// </summary>
public class ExplorerVintageTests
{
    // ---- key map ----

    [Theory]
    [InlineData(0, 1, "premine")]
    [InlineData(1, 4_113_625, "first-v2")]
    [InlineData(2, 2, "first-strike")]
    [InlineData(3, 3_712_545, "resurrection")]
    [InlineData(4, 0, "genesis")]
    [InlineData(5, 413, "block-413")]
    [InlineData(6, 3_141_592, "pi-block")]
    [InlineData(7, 4_194_001, "phoenix-413")]
    [InlineData(8, 4_250_880, "quantum-epoch")]
    [InlineData(10, 1_051_200, "halving-1")]
    [InlineData(13, 4_204_800, "halving-4")]
    [InlineData(51, 1_111_111, "repdigit-1")]
    [InlineData(53, 3_333_333, "repdigit-3")]
    [InlineData(101, 500_000, "block-500000")]
    [InlineData(106, 3_000_000, "block-3000000")]
    public void Explorer_keys_land_on_the_wallets_own_bucket_for_single_blocks(int key, long height, string expectedKey)
    {
        var r = ExplorerVintageKeys.Resolve(key);
        Assert.NotNull(r);
        Assert.Equal(expectedKey, r!.Vintage.Key);
        Assert.Equal(height, r.Height);
        // The label must be the classifier's, so the explorer card and the local tables agree.
        Assert.Equal(VintageClassifier.Classify(height, DateTime.UnixEpoch).Label, r.Vintage.Label);
    }

    [Theory]
    [InlineData(61, 2016, 1_254_539)]
    [InlineData(62, 2020, 2_565_317)]
    [InlineData(63, 2024, 0)]   // no block was mined on 29 Feb 2024 — key known, height not
    public void Leap_day_keys_name_the_year_and_the_first_block_when_known(int key, int year, long height)
    {
        var r = ExplorerVintageKeys.Resolve(key);
        Assert.NotNull(r);
        Assert.Equal(VintageKind.LeapDay, r!.Vintage.Kind);
        Assert.Equal($"leap-{year}", r.Vintage.Key);
        Assert.Equal(height, r.Height);
        // The key must be what the local classifier gives a plain block mined that day
        // (a special height would outrank the date rule, so use an ordinary one).
        Assert.Equal(VintageClassifier.Classify(777_777, new DateTime(year, 2, 29, 0, 0, 0, DateTimeKind.Utc)).Key, r.Vintage.Key);
    }

    [Theory]
    [InlineData(2014)]
    [InlineData(2026)]
    public void Year_keys_are_plain_years(int key)
    {
        var r = ExplorerVintageKeys.Resolve(key);
        Assert.NotNull(r);
        Assert.Equal(VintageKind.Year, r!.Vintage.Kind);
        Assert.Equal(key.ToString(), r.Vintage.Key);
        Assert.Equal(0, r.Height);
    }

    [Theory]
    [InlineData(9)]     // halving 0 is not a bucket
    [InlineData(50)]    // repdigit 0
    [InlineData(60)]    // leap base itself
    [InlineData(100)]   // milestone 0
    [InlineData(-1)]
    public void Unmapped_keys_resolve_to_null_rather_than_a_guess(int key)
        => Assert.Null(ExplorerVintageKeys.Resolve(key));

    // ---- HTTP client ----

    private sealed class CannedHandler(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null) : HttpMessageHandler
    {
        public Uri? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            configure?.Invoke(response);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_rate_limit_is_reported_with_the_explorers_wait()
    {
        var client = ClientWith(new CannedHandler((HttpStatusCode)429, "",
            r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30))));

        var ex = await Assert.ThrowsAsync<ExplorerUnavailableException>(() => client.GetAddressVintageAsync("https://blazecoin.co.uk", "BQx"));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Contains("limiting", ex.Message);
    }

    [Fact]
    public async Task A_rate_limit_without_a_wait_leaves_RetryAfter_null()
    {
        var client = ClientWith(new CannedHandler((HttpStatusCode)429, ""));
        var ex = await Assert.ThrowsAsync<ExplorerUnavailableException>(() => client.GetBlockYearsAsync("https://blazecoin.co.uk"));
        Assert.Equal(429, ex.StatusCode);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public void Retry_after_is_clamped_to_a_sane_window()
    {
        static TimeSpan? Of(TimeSpan delta)
        {
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
            return ExplorerVintageClient.RetryAfterOf(r);
        }
        Assert.Equal(TimeSpan.FromSeconds(1), Of(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(45), Of(TimeSpan.FromSeconds(45)));
        Assert.Equal(TimeSpan.FromMinutes(10), Of(TimeSpan.FromHours(3)));
        Assert.Null(ExplorerVintageClient.RetryAfterOf(new HttpResponseMessage((HttpStatusCode)429)));
    }

    private static ExplorerVintageClient ClientWith(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(ExplorerVintageClient.HttpClientName).Returns(_ => new HttpClient(handler));
        return new ExplorerVintageClient(factory);
    }

    private const string SampleJson = """
        {"address":"BQexampleAddressUsedOnlyInTheseTestsXXXXXXXXXXXXXXXX","computedToHeight":4382894,
         "years":[{"year":2026,"isPremine":false,"isFirstV2":false,"isSpecial":false,"everHeldSatoshis":61085873500,"unspentSatoshis":61085873500},
                  {"year":0,"isPremine":true,"isFirstV2":false,"isSpecial":true,"everHeldSatoshis":62800000,"unspentSatoshis":62800000},
                  {"year":61,"isPremine":false,"isFirstV2":false,"isSpecial":true,"everHeldSatoshis":520000,"unspentSatoshis":520000}]}
        """;

    [Fact]
    public async Task Client_asks_the_gateway_path_and_parses_the_rows()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, SampleJson);
        var client = ClientWith(handler);

        var answer = await client.GetAddressVintageAsync("https://blazecoin.co.uk/", "BQexampleAddressUsedOnlyInTheseTestsXXXXXXXXXXXXXXXX");

        Assert.Equal("https://blazecoin.co.uk/indexer/api/address/BQexampleAddressUsedOnlyInTheseTestsXXXXXXXXXXXXXXXX/vintage",
            handler.LastUri!.ToString());
        Assert.NotNull(answer);
        Assert.Equal(4_382_894, answer!.ComputedToHeight);
        Assert.Equal(3, answer.Rows.Count);
        Assert.Contains(answer.Rows, r => r.YearKey == 0 && r.UnspentSatoshis == 62_800_000);
        Assert.Contains(answer.Rows, r => r.YearKey == 2026 && r.EverHeldSatoshis == 61_085_873_500);
    }

    [Fact]
    public async Task A_region_block_is_reported_in_plain_words()
    {
        var client = ClientWith(new CannedHandler((HttpStatusCode)451, ""));
        var ex = await Assert.ThrowsAsync<ExplorerUnavailableException>(
            () => client.GetAddressVintageAsync("https://blazecoin.co.uk", "BQexampleAddressUsedOnlyInTheseTestsXXXXXXXXXXXXXXXX"));
        Assert.Equal(451, ex.StatusCode);
        Assert.Contains("region", ex.Message);
    }

    [Fact]
    public async Task An_unknown_address_is_null_and_a_server_error_throws()
    {
        var notFound = ClientWith(new CannedHandler(HttpStatusCode.NotFound, ""));
        Assert.Null(await notFound.GetAddressVintageAsync("https://blazecoin.co.uk", "BQnobody"));

        var broken = ClientWith(new CannedHandler(HttpStatusCode.InternalServerError, "oops"));
        var ex = await Assert.ThrowsAsync<ExplorerUnavailableException>(
            () => broken.GetAddressVintageAsync("https://blazecoin.co.uk", "BQnobody"));
        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public void Parse_tolerates_missing_optional_fields()
    {
        using var doc = JsonDocument.Parse("""{"years":[{"year":2015,"unspentSatoshis":5}]}""");
        var parsed = ExplorerVintageClient.Parse(doc.RootElement, "Bfallback");
        Assert.Equal("Bfallback", parsed.Address);
        Assert.Equal(-1, parsed.ComputedToHeight);
        Assert.Single(parsed.Rows);
        Assert.Equal(0, parsed.Rows[0].EverHeldSatoshis);
    }

    // ---- aggregation ----

    private const string YearsJson = """
        {"tipHeight":4385140,
         "years":[{"year":2014,"firstHeight":0,"lastHeight":586042,"blockCount":586043},
                  {"year":2015,"firstHeight":586043,"lastHeight":1166287,"blockCount":580245},
                  {"year":2026,"firstHeight":3958824,"lastHeight":4385140,"blockCount":426317},
                  {"year":2027,"firstHeight":1}]}
        """;

    [Fact]
    public async Task Block_years_are_asked_at_the_blocks_route_and_parsed()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, YearsJson);
        var client = ClientWith(handler);

        var years = await client.GetBlockYearsAsync("https://blazecoin.co.uk/");

        Assert.Equal("https://blazecoin.co.uk/indexer/api/blocks/years", handler.LastUri!.ToString());
        Assert.NotNull(years);
        Assert.Equal(4_385_140, years!.TipHeight);
        Assert.Equal(3, years.Years.Count);                       // the row missing lastHeight is skipped, not guessed
        Assert.Contains(years.Years, y => y.Year == 2015 && y.FirstHeight == 586_043 && y.LastHeight == 1_166_287 && y.BlockCount == 580_245);
    }

    [Fact]
    public void Block_years_label_a_closed_year_by_its_ends_and_the_tip_year_to_now()
    {
        var years = ExplorerVintageClient.ParseBlockYears(JsonDocument.Parse(YearsJson).RootElement);

        Assert.Equal("0 – 586,042", years.RangeLabel(2014));
        Assert.Equal("586,043 – 1,166,287", years.RangeLabel(2015));
        Assert.Equal("3,958,824 – now", years.RangeLabel(2026));
        Assert.Null(years.RangeLabel(2019));                       // a year the chain skipped, or not listed
        Assert.Equal(586_043, years.BlockCount(2014));
        Assert.Null(years.BlockCount(2019));
    }

    [Fact]
    public async Task An_explorer_without_the_years_route_answers_null()
    {
        var client = ClientWith(new CannedHandler(HttpStatusCode.NotFound, ""));
        Assert.Null(await client.GetBlockYearsAsync("https://blazecoin.co.uk"));
    }

    [Fact]
    public void Aggregate_sums_across_addresses_and_splits_years_from_specials()
    {
        var a = new ExplorerAddressVintage("A", 4_382_894, new[]
        {
            new ExplorerVintageRow(2014, 100, 60),
            new ExplorerVintageRow(0, 10, 10),
            new ExplorerVintageRow(61, 5, 5),
            new ExplorerVintageRow(60, 1, 1),     // the leap base itself is not a bucket — a key this build does not know
        });
        var b = new ExplorerAddressVintage("B", 4_382_800, new[]
        {
            new ExplorerVintageRow(2014, 40, 40),
            new ExplorerVintageRow(2026, 7, 7),
            new ExplorerVintageRow(0, 1, 1),
        });

        var s = ExplorerVintageAggregator.Aggregate(new[] { a, b });

        Assert.Equal(2, s.AddressCount);
        Assert.Equal(1, s.UnknownKeys);
        Assert.Equal(4_382_800, s.ComputedToHeight);          // the lowest watermark
        Assert.Equal(60 + 40 + 10 + 5 + 7 + 1, s.UnspentSatoshis);
        Assert.Equal(new[] { "2014", "2026" }, s.Years.Select(y => y.Key));
        Assert.Equal(100, s.Years[0].UnspentSatoshis);
        Assert.Equal(new[] { "premine", "leap-2016" }, s.Specials.Select(y => y.Key));   // by height
        Assert.Equal(11, s.Specials[0].UnspentSatoshis);
        Assert.Equal(11, s.Specials[0].EverHeldSatoshis);
    }

    [Fact]
    public void Aggregate_of_nothing_is_empty_with_no_watermark()
    {
        var s = ExplorerVintageAggregator.Aggregate(Array.Empty<ExplorerAddressVintage>());
        Assert.Empty(s.Years);
        Assert.Empty(s.Specials);
        Assert.Equal(-1, s.ComputedToHeight);
        Assert.Equal(0, s.AddressCount);
    }

    // ---- reconciliation ----

    private static VintageRow Row(string key, long sats, long height, VintageKind kind = VintageKind.Year)
        => new(key, key, kind, sats, 1, height, DateTime.UnixEpoch, DateTime.UnixEpoch);

    private static ProvenanceReport LocalWith(IReadOnlyList<VintageRow> years, IReadOnlyList<VintageRow> specials)
    {
        var breakdown = new VintageBreakdown(years, specials, years.Sum(y => y.Satoshis) + specials.Sum(s => s.Satoshis), Array.Empty<VintageRow>());
        return new ProvenanceReport(Array.Empty<OutputProvenance>(), new Dictionary<long, MintPoint>(),
            breakdown, breakdown, breakdown.AttributedSatoshis, 1_000_000, ReadBudgetReached: true);
    }

    [Fact]
    public void Verified_explorer_only_and_local_only_are_told_apart()
    {
        var explorer = new ExplorerVintageSummary(
            Years: new[]
            {
                new ExplorerVintageEntry("2014", "2014", VintageKind.Year, 0, 23_000, 23_000),
                new ExplorerVintageEntry("2026", "2026", VintageKind.Year, 0, 1_000, 1_000),
            },
            Specials: new[]
            {
                new ExplorerVintageEntry("block-413", "Block 413", VintageKind.Block413, 413, 10, 10),
                new ExplorerVintageEntry("premine", "Premine", VintageKind.Premine, 1, 0, 50),   // held once, none left
            },
            UnspentSatoshis: 24_010, EverHeldSatoshis: 24_060, ComputedToHeight: 4_382_894, AddressCount: 8, UnknownKeys: 0);

        var local = LocalWith(
            years: new[] { Row("2014", 2_335, 100), Row("2027", 5, 9_000_000) },
            specials: new[] { Row("block-413", 2, 413, VintageKind.Block413) });

        var r = ProvenanceReconciler.Reconcile(explorer, local);

        var y2014 = Assert.Single(r.Years, y => y.Key == "2014");
        Assert.Equal(ReconcileStatus.Verified, y2014.Status);
        Assert.Equal(23_000, y2014.ExplorerSatoshis);
        Assert.Equal(2_335, y2014.LocalSatoshis);

        Assert.Equal(ReconcileStatus.ExplorerOnly, Assert.Single(r.Years, y => y.Key == "2026").Status);
        Assert.Equal(ReconcileStatus.LocalOnly, Assert.Single(r.Years, y => y.Key == "2027").Status);
        Assert.Equal(ReconcileStatus.Verified, Assert.Single(r.Specials, s => s.Key == "block-413").Status);
        Assert.DoesNotContain(r.Specials, s => s.Key == "premine");   // nothing unspent on either side

        Assert.Equal(2, r.VerifiedCount);
        Assert.Equal(1, r.ExplorerOnlyCount);
        Assert.Equal(1, r.LocalOnlyCount);
    }

    [Fact]
    public void Without_a_local_trace_everything_is_explorer_only()
    {
        var explorer = new ExplorerVintageSummary(
            new[] { new ExplorerVintageEntry("2014", "2014", VintageKind.Year, 0, 5, 5) },
            Array.Empty<ExplorerVintageEntry>(), 5, 5, 1, 1, 0);
        var r = ProvenanceReconciler.Reconcile(explorer, null);
        Assert.Equal(ReconcileStatus.ExplorerOnly, Assert.Single(r.Years).Status);
        Assert.Empty(r.Specials);
    }
}

using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Pins the composition root (<see cref="LiteWalletServiceCollectionExtensions.AddLiteWallet"/>)
/// — the single wiring every head shares. The security-relevant invariants: the graph resolves,
/// gateway mode gets a semi-trusted data source (IndexerDataService) WITH the trustless verifier,
/// personal-node mode gets the RPC source WITHOUT one (own-chainstate values are authoritative),
/// and the null QR scanner is present unless a head registered its own.
/// </summary>
public class AddLiteWalletCompositionTests
{
    private sealed class StubVault : ISeedVault
    {
        public Task<bool> HasWalletAsync() => Task.FromResult(false);
        public Task SaveMnemonicAsync(string m) => Task.CompletedTask;
        public Task<string?> LoadMnemonicAsync() => Task.FromResult<string?>(null);
        public Task ClearAsync() => Task.CompletedTask;
    }

    private static ServiceProvider Build(LiteWalletOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISeedVault, StubVault>();
        services.AddLiteWallet(options);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Gateway_mode_resolves_the_indexer_source_with_the_verifier_wired()
    {
        using var sp = Build(new LiteWalletOptions { GatewayUrls = ["https://gw.test/"] });

        // The whole graph resolves.
        var wallet = sp.GetRequiredService<LiteWalletService>();
        Assert.NotNull(wallet);

        // Gateway mode is the semi-trusted source — chain verification MUST be on.
        var reader = sp.GetRequiredService<IChainReader>();
        Assert.IsType<IndexerDataService>(reader);
        Assert.True(reader.SupportsChainVerification);

        // Read + relay slices alias the SAME instance (ISP without duplication).
        Assert.Same(reader, sp.GetRequiredService<ITxRelay>());
        Assert.Same(reader, sp.GetRequiredService<ILiteWalletData>());
    }

    [Fact]
    public void Header_sync_can_be_disabled_per_head_without_touching_the_verifier()
    {
        // The WASM head's trade-off (2026-08-03): ~56 ms/header interpreted scrypt on a
        // single thread makes header sync freeze the tab, so a head may opt out — but
        // the C1/M3 verifier (funds safety) must stay wired regardless.
        using var sp = Build(new LiteWalletOptions
        {
            GatewayUrls = ["https://gw.test/"],
            DisableHeaderChainSync = true,
        });

        // Gateway mode's reader IS an IHeaderReader, but the opted-out sync got no feed.
        Assert.IsAssignableFrom<IHeaderReader>(sp.GetRequiredService<IChainReader>());
        Assert.False(sp.GetRequiredService<HeaderChainSync>().Available);

        // Chain verification (the theft-proof spine) is unaffected by the opt-out.
        Assert.True(sp.GetRequiredService<IChainReader>().SupportsChainVerification);
        Assert.NotNull(sp.GetRequiredService<LiteWalletService>());
    }

    [Fact]
    public void Personal_node_mode_resolves_the_rpc_source_without_verification()
    {
        using var sp = Build(new LiteWalletOptions
        {
            UsePersonalNode = true,
            PersonalNode = new PersonalNodeOptions("http://127.0.0.1:55413/", RpcUser: "u", RpcPassword: "p"),
        });

        var reader = sp.GetRequiredService<IChainReader>();
        Assert.IsType<PersonalNodeDataService>(reader);
        // Own-chainstate values are authoritative — no verifier round trips.
        Assert.False(reader.SupportsChainVerification);
        Assert.NotNull(sp.GetRequiredService<LiteWalletService>());
    }

    [Fact]
    public void Personal_node_mode_without_options_fails_fast()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build(new LiteWalletOptions { UsePersonalNode = true, PersonalNode = null }));
    }

    [Fact]
    public void A_null_qr_scanner_is_registered_by_default_and_a_head_can_override_it()
    {
        using var defaultSp = Build(new LiteWalletOptions { GatewayUrls = ["https://gw.test/"] });
        Assert.IsType<NullQrScanner>(defaultSp.GetRequiredService<IQrScanner>());
        Assert.False(defaultSp.GetRequiredService<IQrScanner>().IsAvailable);

        // A head that registers its scanner FIRST wins (TryAdd in AddLiteWallet).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISeedVault, StubVault>();
        services.AddSingleton<IQrScanner, FakeScanner>();
        services.AddLiteWallet(new LiteWalletOptions { GatewayUrls = ["https://gw.test/"] });
        using var sp = services.BuildServiceProvider();
        Assert.IsType<FakeScanner>(sp.GetRequiredService<IQrScanner>());
    }

    private sealed class FakeScanner : IQrScanner
    {
        public bool IsAvailable => true;
        public Task<string?> ScanAsync() => Task.FromResult<string?>("Bxyz");
    }

    [Fact]
    public void A_cleartext_off_box_gateway_is_rejected_when_the_client_is_built()
    {
        // The transport-scheme guard (audit M1) fires in the HttpClient factory — resolving
        // the wallet triggers it, so a cleartext gateway can never reach a live send.
        using var sp = Build(new LiteWalletOptions { GatewayUrls = ["http://evil.test/"] });
        Assert.Throws<ArgumentException>(() => sp.GetRequiredService<LiteWalletService>());
    }


    // ── per-request budgets (2026-10-27) ──

    /// <summary>A gateway that takes a fixed time to answer anything (honouring cancellation).</summary>
    private sealed class SlowGateway : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public SlowGateway(TimeSpan delay) => _delay = delay;

        private const string EmptyAncestry =
            """{"address":"B","totalSatoshis":0,"outputCount":0,"transactionsRead":0,"truncated":false,"fifo":{"attributedSatoshis":0,"years":[],"months":[],"specials":[]},"haircut":{"attributedSatoshis":0,"years":[],"months":[],"specials":[]}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/ancestry", StringComparison.Ordinal) ? EmptyAncestry : "[]";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Gateway_mode_times_the_trace_under_its_own_budget_not_the_read_ceiling()
    {
        // Same slow gateway for both calls. The ordinary read is cut at HttpTimeout (and
        // degrades, never throws); the trace, under TraceTimeout, is answered. This is the
        // 2026-10-26 outage in miniature: one ceiling for both would fail the trace too.
        using var sp = Build(new LiteWalletOptions
        {
            GatewayUrls = ["https://gw.test/"],
            HttpTimeout = TimeSpan.FromMilliseconds(100),
            TraceTimeout = TimeSpan.FromSeconds(10),
            InnerHandlerFactory = () => new SlowGateway(TimeSpan.FromMilliseconds(400)),
        });
        var data = Assert.IsType<IndexerDataService>(sp.GetRequiredService<ILiteWalletData>());

        Assert.Empty(await data.GetUtxosAsync("Baddr"));          // read: timed out → degraded
        var trace = await data.GetAncestryAsync("Baddr");          // trace: waited for
        Assert.Null(trace.Error);
        Assert.NotNull(trace.Report);

        // HttpClient's own ceiling is off — the handler owns every budget.
        Assert.Equal(Timeout.InfiniteTimeSpan, sp.GetRequiredService<HttpClient>().Timeout);
    }

    [Fact]
    public async Task The_trace_budget_is_still_a_ceiling_in_the_composed_graph()
    {
        using var sp = Build(new LiteWalletOptions
        {
            GatewayUrls = ["https://gw.test/"],
            HttpTimeout = TimeSpan.FromMilliseconds(50),
            TraceTimeout = TimeSpan.FromMilliseconds(150),
            InnerHandlerFactory = () => new SlowGateway(TimeSpan.FromSeconds(5)),
        });
        var data = (IndexerDataService)sp.GetRequiredService<ILiteWalletData>();

        var started = DateTime.UtcNow;
        var trace = await data.GetAncestryAsync("Baddr");

        Assert.Null(trace.Report);
        Assert.Contains("did not finish the trace in time", trace.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }
}

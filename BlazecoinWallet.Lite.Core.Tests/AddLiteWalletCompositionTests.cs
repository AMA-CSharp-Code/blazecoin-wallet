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
}

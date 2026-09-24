using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BlazecoinWallet.Lite;

/// <summary>Composition settings for one lite-wallet head.</summary>
public sealed class LiteWalletOptions
{
    /// <summary>Ordered gateway failover list (Electrum model). Two independent Lite Mobile
    /// Indexer appliances — Strasbourg (SBG) then Beauharnois (BHS, different region) — each
    /// reachable by a vanity domain first (2026-08-17: the domain can outlive a VPS replacement,
    /// so a box swap becomes a DNS edit instead of a binary release) with its sslip.io name kept
    /// as the belt-and-braces fallback (works even if blazecoin.co.uk DNS itself breaks).
    /// blazecoin.co.uk was REMOVED: it points at domain parking today and at the production
    /// website gateway after cutover — neither serves the lite-gateway API. If the production
    /// site ever hosts one, add gw3.blazecoin.co.uk rather than the apex.</summary>
    public IReadOnlyList<string> GatewayUrls { get; init; } =
        ["https://gw.blazecoin.co.uk/", "https://gw2.blazecoin.co.uk/",
         "https://51-210-47-141.sslip.io/", "https://54-39-23-245.sslip.io/"];

    /// <summary>P2P fallback node endpoints; null → the chain's seed nodes.</summary>
    public IReadOnlyList<string>? P2PNodes { get; init; }

    /// <summary>True → read/relay via YOUR OWN daemon's RPC instead of the gateway.</summary>
    public bool UsePersonalNode { get; init; }

    /// <summary>Required when <see cref="UsePersonalNode"/> is true.</summary>
    public PersonalNodeOptions? PersonalNode { get; init; }

    /// <summary>Hook for the transport (e.g. the dev host's dev-cert bypass). Null → default handler.</summary>
    public Func<HttpMessageHandler>? InnerHandlerFactory { get; init; }

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Disables the trustless header-chain sync for heads where per-header scrypt is
    /// prohibitive — measured 2026-08-03 on the WASM web head: ~56 ms/header interpreted,
    /// on a single thread, so one 250-header gateway batch freezes the tab ~14 s and a
    /// day's catch-up (~2,880 headers) is minutes. Confirmation counts are then taken
    /// from the gateway as-is — the same posture as personal-node mode, and the
    /// pre-header-sync wallet. FUNDS SAFETY IS UNAFFECTED: the C1/M3 value verifier
    /// (one scrypt per input) stays fully on. Revisit with WASM AOT or a JS scrypt
    /// bridge; the switch exists so a head can make that trade-off explicitly.
    /// </summary>
    public bool DisableHeaderChainSync { get; init; }
}

/// <summary>
/// THE composition root for a lite-wallet head (F3 — every head calls this instead of
/// wiring the graph by hand, so Droid/dev-host/iOS can never drift): data source
/// (gateway-with-failover or personal node), segregated read/relay registrations, the P2P
/// outage fallback, the null QR scanner (heads with a camera register theirs FIRST — TryAdd
/// lets the real one win), and the wallet service. The head must register its platform
/// <see cref="ISeedVault"/> itself — key custody is inherently per-platform.
/// </summary>
public static class LiteWalletServiceCollectionExtensions
{
    public static IServiceCollection AddLiteWallet(this IServiceCollection services, LiteWalletOptions options)
    {
        if (options.UsePersonalNode)
        {
            var node = options.PersonalNode
                ?? throw new InvalidOperationException("PersonalNode options are required when UsePersonalNode is set.");
            services.AddSingleton<ILiteWalletData>(sp => new PersonalNodeDataService(
                // scantxoutset is a chainstate scan — give it more rope than gateway calls.
                new HttpClient(options.InnerHandlerFactory?.Invoke() ?? new HttpClientHandler())
                { Timeout = TimeSpan.FromSeconds(60) },
                node,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<PersonalNodeDataService>()));
        }
        else
        {
            services.AddSingleton(_ =>
            {
                var failover = new GatewayFailoverHandler(
                    options.GatewayUrls,
                    options.InnerHandlerFactory?.Invoke() ?? new HttpClientHandler());
                return new HttpClient(failover) { BaseAddress = failover.Primary, Timeout = options.HttpTimeout };
            });
            services.AddSingleton<ILiteWalletData>(sp => new IndexerDataService(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IndexerDataService>()));
        }

        // ISP aliases — consumers depend on the slice they need, one instance serves both.
        services.AddSingleton<IChainReader>(sp => sp.GetRequiredService<ILiteWalletData>());
        services.AddSingleton<ITxRelay>(sp => sp.GetRequiredService<ILiteWalletData>());

        services.AddSingleton<IP2PBroadcaster>(new P2PBroadcaster());
        services.TryAddSingleton<IQrScanner, NullQrScanner>();
        // Gateway settings for the (advanced) Settings screen. Heads that persist across
        // launches register their own BEFORE this; TryAdd leaves theirs in place. The default
        // list here is the "reset" target and the current value at first run.
        services.TryAddSingleton<IGatewaySettings>(new InMemoryGatewaySettings(options.GatewayUrls));
        services.TryAddSingleton<IPersonalNodeSettings>(new InMemoryPersonalNodeSettings());
        services.TryAddSingleton<IP2PNodeSettings>(new InMemoryP2PNodeSettings(
            options.P2PNodes ?? P2PBroadcaster.DefaultSeedNodes));
        services.TryAddSingleton<IUnitSettings>(new InMemoryUnitSettings());
        services.TryAddSingleton<IExplorerSettings>(new InMemoryExplorerSettings());
        services.TryAddSingleton<ISkinSettings>(new InMemorySkinSettings());
        // Local convenience stores (labels / address book / watch-only) + app-lock. All
        // non-secret and TryAdd'd, so a head registers a persistent one BEFORE this to win.
        services.TryAddSingleton<ILabelStore, InMemoryLabelStore>();
        services.TryAddSingleton<IAddressBook, InMemoryAddressBook>();
        services.TryAddSingleton<IWatchList, InMemoryWatchList>();
        services.TryAddSingleton<IPinLock, InMemoryPinLock>();
        services.TryAddSingleton<IBiometricAuth, NullBiometricAuth>();
        services.AddSingleton<AppLockSession>();
        // Locking (2026-08-23): the shared 🔒 button + idle policy call these. Default = the PIN
        // gate only; a head whose vault can be re-locked (web) registers its own BEFORE this.
        services.TryAddSingleton<IWalletLocker, PinGateWalletLocker>();
        services.TryAddSingleton<IAutoLockSettings, InMemoryAutoLockSettings>();
        // Address-rotation position. Heads that persist across launches register their own
        // BEFORE this; losing it is safe (restore gap-scans the chain).
        services.TryAddSingleton<IWalletStateStore, InMemoryWalletStateStore>();

        // Trustless header-chain sync (M3 residual): only the gateway feed serves headers
        // (IndexerDataService is an IHeaderReader; the personal node isn't and doesn't need to
        // be — its own chainstate is authoritative). When present, it caps maturity to the
        // PoW-verified tip so a gateway can't fake confirmations. A head can opt out via
        // DisableHeaderChainSync (the WASM head must — see the option's doc); a null feed
        // makes Available false, which every consumer already handles as "fall back to the
        // gateway's counts".
        services.AddSingleton(sp => new HeaderChainSync(
            options.DisableHeaderChainSync ? null : sp.GetRequiredService<IChainReader>() as IHeaderReader,
            sp.GetRequiredService<IWalletStateStore>()));

        // Trustless input verifier (C1/M3) ONLY for a semi-trusted source — gateway mode.
        // Personal-node values come from the user's own chainstate, so there's nobody to
        // distrust and the extra round trips are pointless.
        services.AddSingleton(sp => new LiteWalletService(
            sp.GetRequiredService<ISeedVault>(),
            sp.GetRequiredService<IChainReader>(),
            sp.GetRequiredService<ITxRelay>(),
            sp.GetRequiredService<IP2PBroadcaster>(),
            options.P2PNodes,
            sp.GetRequiredService<IChainReader>().SupportsChainVerification
                ? new LiteTxVerifier(sp.GetRequiredService<IChainReader>(),
                    headerChain: sp.GetRequiredService<HeaderChainSync>())
                : null,
            sp.GetRequiredService<IWalletStateStore>(),
            sp.GetRequiredService<HeaderChainSync>()));

        // Foreground incoming-payment detection (the layout starts it once unlocked).
        services.AddSingleton(sp => new IncomingPaymentWatcher(sp.GetRequiredService<LiteWalletService>()));

        // PQ_SIGNATURES §7.1 "update required before block N": polls the gateway's status for
        // the fork announcement (30 s, the layout starts it). Personal-node mode has no gateway
        // and nobody to announce — a null client keeps the banner silent for ever.
        services.AddSingleton<Fork.IForkStatusService>(sp => new Fork.GatewayForkStatusService(
            options.UsePersonalNode ? null : sp.GetRequiredService<HttpClient>()));

        // Transient comet recolour on the Home balance card (green = received, orange = sent).
        services.TryAddSingleton<CometPulse>();

        return services;
    }
}

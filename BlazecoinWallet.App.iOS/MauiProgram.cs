using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.UI;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace BlazecoinWallet.App.iOS;

public static class MauiProgram
{
	/// <summary>
	/// The public gateways the lite wallet talks to (Indexer reads + broadcast relay) —
	/// the SAME ordered failover list as the Droid head (Electrum model): the wallet
	/// tries each in turn on delivery failure and sticks with what works. Overridable at
	/// runtime via Preferences["gateway_urls"] (semicolon-separated); the iOS simulator
	/// reaches a dev host on the Mac at https://localhost:7002 directly (no 10.0.2.2
	/// indirection — the simulator shares the host network).
	/// </summary>
	public const string DefaultGatewayUrls = "https://gw.blazecoin.co.uk/;https://gw2.blazecoin.co.uk/;https://51-210-47-141.sslip.io/;https://54-39-23-245.sslip.io/";

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// ── Lite wallet composition root (shared AddLiteWallet — audit F3) ───────────
		// This head contributes only what is platform-specific: the Keychain seed vault
		// (MAUI SecureStorage = iOS Keychain) and its Preferences-sourced settings
		// (NSUserDefaults). The graph itself is wired once in Lite.Core so heads can
		// never drift — this file is deliberately a line-for-line twin of the Droid
		// head's MauiProgram minus the Android WebChromeClient camera mapper.
		builder.Services.AddSingleton<ISeedVault, SecureStorageSeedVault>();
		// Persistent settings (advanced Settings screen) — registered BEFORE AddLiteWallet
		// so their TryAdd defaults don't win.
		builder.Services.AddSingleton<IGatewaySettings>(new PreferencesGatewaySettings(DefaultGatewayUrls));
		var nodeSettings = new PreferencesPersonalNodeSettings();
		builder.Services.AddSingleton<IPersonalNodeSettings>(nodeSettings);
		builder.Services.AddSingleton<IP2PNodeSettings>(
			new PreferencesP2PNodeSettings(string.Join(';', P2PBroadcaster.DefaultSeedNodes)));
		builder.Services.AddSingleton<IUnitSettings>(new PreferencesUnitSettings());
		builder.Services.AddSingleton<IExplorerSettings>(new PreferencesExplorerSettings());
		builder.Services.AddSingleton<ISkinSettings>(new PreferencesSkinSettings());
		builder.Services.AddSingleton<IWalletStateStore>(new PreferencesWalletStateStore());
		// Local convenience stores + app-lock, persisted via Preferences (registered before
		// AddLiteWallet so their TryAdd defaults don't win).
		builder.Services.AddSingleton<ILabelStore, PreferencesLabelStore>();
		builder.Services.AddSingleton<IAddressBook, PreferencesAddressBook>();
		builder.Services.AddSingleton<IWatchList, PreferencesWatchList>();
		builder.Services.AddSingleton<IPinLock, PreferencesPinLock>();
		// Real device biometric (before AddLiteWallet so it wins over NullBiometricAuth).
		builder.Services.AddSingleton<IBiometricAuth, IosBiometricAuth>();

		// Re-lock the app when it's backgrounded, so returning requires the PIN/biometric
		// again (not just at cold launch). DidEnterBackground is the iOS analogue of the
		// Android OnStop hook; LockForBackgrounding (not Lock) for the same reason as on
		// Android — the wallet's own outbound flows (photo-library picker for the QR
		// image-upload fallback) present system UI, and a hard re-lock there would tear
		// down the requesting page mid-scan (2026-07-27 emulator smoke finding).
		builder.ConfigureLifecycleEvents(events => events.AddiOS(ios =>
			ios.DidEnterBackground(_ => IPlatformApplication.Current?.Services.GetService<AppLockSession>()?.LockForBackgrounding())));
		builder.Services.AddLiteWallet(new LiteWalletOptions
		{
			GatewayUrls = Preferences.Default.Get("gateway_urls", DefaultGatewayUrls)
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			P2PNodes = Preferences.Default.Get("p2p_nodes", string.Join(';', P2PBroadcaster.DefaultSeedNodes))
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			// Personal-node mode (the trustless option) when the user enabled it in Settings.
			UsePersonalNode = nodeSettings.Enabled,
			PersonalNode = nodeSettings.Config,
		});
		// QR scanner: getUserMedia + jsQR inside the WebView (JsQrScanner), decorated with
		// the OS camera-permission request (CameraPermissionQrScanner — cross-platform MAUI
		// Permissions, reused from the Droid head). NSCameraUsageDescription is in
		// Info.plist. ⚠️ ON-DEVICE GATE (needs the Mac smoke pass): live getUserMedia
		// inside the BlazorWebView's WKWebView must be verified on real iOS — the app is
		// served from a custom scheme and WKWebView's secure-context/permission behaviour
		// there is unproven (the Android equivalent needed the WebChromeClient wrapper +
		// the https://0.0.0.1 origin discovery). The image-upload fallback in the scan
		// overlay works regardless, exactly as it did on Android before live camera.
		builder.Services.AddScoped<JsQrScanner>();
		builder.Services.AddScoped<IQrScanner>(sp => new CameraPermissionQrScanner(
			sp.GetRequiredService<JsQrScanner>(), sp.GetRequiredService<AppLockSession>()));

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}

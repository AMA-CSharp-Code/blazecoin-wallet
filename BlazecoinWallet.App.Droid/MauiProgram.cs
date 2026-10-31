using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.UI;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

namespace BlazecoinWallet.App.Droid;

public static class MauiProgram
{
	/// <summary>
	/// The public gateways the lite wallet talks to (Indexer reads + broadcast relay) —
	/// an ORDERED FAILOVER LIST (Electrum model): the wallet tries each in turn on
	/// delivery failure and sticks with what works. Overridable at runtime via
	/// Preferences["gateway_urls"] (semicolon-separated) — dev builds point it at a
	/// LAN/emulator address (Android emulator reaches the host at https://10.0.2.2:7002);
	/// release builds use the public deployment(s) (the wallet's release gate).
	/// Order: gw -> gw2 (vanity names, live 2026-08-17) -> the two sslip.io fallbacks.
	/// The blazecoin.co.uk apex was REMOVED from the list (74750a4) — do not re-add it.
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
		// This head contributes only what is platform-specific: the Keystore seed vault
		// and its Preferences-sourced settings. The graph itself is wired once in
		// Lite.Core so heads can never drift.
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
		builder.Services.AddSingleton<IBiometricAuth, DroidBiometricAuth>();

		// Re-lock the app when it's backgrounded, so returning requires the PIN/biometric
		// again (not just at cold launch). The layout observes AppLockSession.StateChanged.
		builder.ConfigureLifecycleEvents(events => events.AddAndroid(android =>
			// LockForBackgrounding, not Lock: the wallet's own outbound flows (the QR-scan
			// file chooser) stop the activity too, and a hard re-lock there tears down the
			// requesting page mid-scan (2026-07-27 emulator smoke finding).
			android.OnStop(_ => IPlatformApplication.Current?.Services.GetService<AppLockSession>()?.LockForBackgrounding())));
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
		// the runtime CAMERA permission request (CameraPermissionQrScanner). The WebView-level
		// getUserMedia grant is CameraWebChromeClient below; the image-upload fallback rides
		// MAUI's own file chooser, which that wrapper preserves. Scoped: IJSRuntime is scoped.
		builder.Services.AddScoped<JsQrScanner>();
		builder.Services.AddScoped<IQrScanner>(sp => new CameraPermissionQrScanner(
			sp.GetRequiredService<JsQrScanner>(), sp.GetRequiredService<AppLockSession>()));

		// Live-camera QR grant: wrap MAUI's internal WebChromeClient with the delegating
		// camera-granting client. The API-26 WebView.WebChromeClient GETTER is what makes
		// this possible without replacing MAUI's client (whose file-chooser handling the
		// upload fallback needs) — the long-standing blocker for this feature. Appended to
		// the handler mapping so it runs after MAUI has installed its own client.
		Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
			"BlazecoinCameraGrant", (handler, view) =>
			{
				if (OperatingSystem.IsAndroidVersionAtLeast(26)
					&& handler.PlatformView is Android.Webkit.WebView webView
					&& webView.WebChromeClient is { } innerClient
					&& innerClient is not CameraWebChromeClient)
				{
					Android.Util.Log.Info("BlzCamera", $"wrapping inner WebChromeClient {innerClient.GetType().FullName}");
					webView.SetWebChromeClient(new CameraWebChromeClient(innerClient));
				}
				else
				{
					Android.Util.Log.Info("BlzCamera", "mapper skipped: platformView="
						+ (handler.PlatformView?.GetType().FullName ?? "null")
						+ ", client=" + ((handler.PlatformView as Android.Webkit.WebView)?.WebChromeClient?.GetType().FullName ?? "null"));
				}
			});

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}

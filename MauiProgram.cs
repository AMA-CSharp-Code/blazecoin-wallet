using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using BlazecoinWallet.Maui.Services;             // app platform impl: WalletContext
using BlazecoinWallet.Maui.Services.Storage;     // app platform impl: JsKeyValueStore
using BlazecoinWallet.Core.Services;             // Core: IWalletContext, RPC slices, sync state
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Wallets;
using BlazecoinWallet.Core.Services.Storage;     // Core: IKeyValueStore
using BlazecoinWallet.Core.Services.AutoPayout;
using BlazecoinWallet.Core.Services.Settings;
using BlazecoinWallet.Core.Services.AddressBook;
using BlazecoinWallet.Core.Services.Export;
using BlazecoinWallet.Core.Services.Import;
using System.Reflection;
using System.Text;
using System.Net.Http.Headers;
#if WINDOWS
using Microsoft.Maui.LifecycleEvents;
using System.Runtime.InteropServices;
#endif

namespace BlazecoinWallet.Maui;

public static class MauiProgram
{
	// Captured at Build() so the Windows window-close handler can resolve the daemon
	// manager reliably (IPlatformApplication.Current can be null mid-shutdown).
	private static IServiceProvider? _appServices;

	/// <summary>Profile name from a <c>--profile=</c> arg (e.g. Indexer/Pool/Payout),
	/// or null when launched normally. Used for the window title so several
	/// daemon-bound instances are distinguishable.</summary>
	public static string? ProfileName { get; private set; }

	// --key=value command-line overrides, parsed once. Let multiple wallet windows
	// each target + manage a DIFFERENT daemon (see ApplyProfileArgs).
	private static readonly Dictionary<string, string> _cliArgs = ParseCliArgs();

	private static Dictionary<string, string> ParseCliArgs()
	{
		var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var a in Environment.GetCommandLineArgs())
		{
			if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
			var eq = a.IndexOf('=');
			if (eq > 2) d[a.Substring(2, eq - 2)] = a.Substring(eq + 1).Trim('"');
		}
		return d;
	}

	// Overlay the embedded appsettings defaults with per-instance daemon overrides.
	// ExePath is still validated (absolute local path, must exist) by
	// DaemonProcessManager before any launch, so an arg can't point the launch at a
	// remote/odd path.
	private static void ApplyProfileArgs(IConfiguration config)
	{
		void Set(string arg, string key)
		{
			if (_cliArgs.TryGetValue(arg, out var v) && !string.IsNullOrWhiteSpace(v)) config[key] = v;
		}
		Set("rpcurl", "Blazecoind:RpcUrl");
		Set("rpcuser", "Blazecoind:RpcUser");
		Set("rpcpass", "Blazecoind:RpcPassword");
		Set("datadir", "Blazecoind:DataDir");
		Set("exepath", "Blazecoind:ExePath");
	}

	public static MauiApp CreateMauiApp()
	{
		ProfileName = _cliArgs.TryGetValue("profile", out var pn) ? pn : null;
#if WINDOWS
		// The Blazor Hybrid UI runs on WebView2/Chromium, which spins up an audio
		// service and enumerates audio devices on startup even though this app
		// plays no sound — on an old/flaky sound card that enumeration crackles
		// and pops. Tell WebView2 to leave audio alone entirely. Must be set
		// before the WebView2 environment is created (i.e. before the first
		// BlazorWebView render), so it goes at the very top of CreateMauiApp.
		Environment.SetEnvironmentVariable(
			"WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
			"--mute-audio --disable-features=AudioServiceOutOfProcess --disable-audio-output");

		// When running as a named profile (several daemon-bound instances at once),
		// give each its own WebView2 user-data folder so they don't share — or
		// lock-contend over — one cache / localStorage. Must precede WebView2 init.
		if (!string.IsNullOrWhiteSpace(ProfileName))
			Environment.SetEnvironmentVariable(
				"WEBVIEW2_USER_DATA_FOLDER",
				System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BlazecoinWalletV2-WebView2", ProfileName));
#endif

		// QuestPDF Community licence (free for individuals / small businesses) —
		// must be set before generating any PDF (Transactions CSV/JSON/XLSX/PDF export).
		QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

#if WINDOWS
			// Windows 11 auto-rounds top-level window corners. That OS-level
			// rounding clips the web content's corners, which made the
			// bottom status bar read as a pill no matter what CSS we set.
			// Opt the window out so it (and the bar) are true rectangles.
			builder.ConfigureLifecycleEvents(events =>
			{
				events.AddWindows(windows => windows.OnWindowCreated(window =>
				{
					static void ApplyWindowStyle(object w)
					{
						var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);

						// Square corners (Win11 rounds them otherwise).
						int preference = DWMWCP_DONOTROUND;
						DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

						// Red window border (#cf2013) — COLORREF 0x00bbggrr.
						int border = 0x001320CF;
						DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

						// MAUI's Window.TitleBar gives us the black caption, but
						// the system min/max/close glyphs default to a dark
						// colour — invisible on black. MAUI's TitleBar.Foreground
						// only tints the title text, so set the caption-button
						// colours via WinUI directly. With MAUI's TitleBar active
						// (ExtendsContentIntoTitleBar on) this layer is honoured.
						try
						{
							if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
							{
								var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
								var appWin = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wid);
								var tb = appWin.TitleBar;
								var clear = Windows.UI.Color.FromArgb(0, 0, 0, 0);
								var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
								// White glyphs, transparent background → buttons
								// read white over the black bar before hover.
								tb.ButtonForegroundColor = white;
								tb.ButtonInactiveForegroundColor = white;
								tb.ButtonBackgroundColor = clear;
								tb.ButtonInactiveBackgroundColor = clear;
								// Hover: nav-bar red (#9E000F). AppWindowTitleBar
								// has one hover colour for all three buttons (no
								// per-button API), so close shares it too. Glyph
								// stays white; press is a touch brighter for feedback.
								tb.ButtonHoverForegroundColor = white;
								tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 158, 0, 15);
								tb.ButtonPressedForegroundColor = white;
								tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 207, 32, 19);
							}
						}
						catch { /* caption-button customisation unavailable */ }
					}
					// Apply now, and re-apply on every activation — DWM can
					// reset these if set before the HWND is fully realized,
					// so doing it on activation makes them stick.
					ApplyWindowStyle(window);
					window.Activated += (_, _) => ApplyWindowStyle(window);

						// Graceful daemon shutdown on close (the window X). If THIS app
						// launched blazecoind, hold the close, send a graceful stop so
						// LevelDB flushes, then quit. A daemon that was already running
						// (adopted — e.g. kept up for solo-proxy mining) is left alone and
						// the close proceeds immediately.
						var closeHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
						var closeWid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(closeHwnd);
						var closeAppWin = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(closeWid);
						closeAppWin.Closing += (s, e) =>
						{
							if (_daemonShutdownStarted) return;   // 2nd pass after our Quit() — allow close
							var mgr = _appServices?
								.GetService(typeof(IDaemonProcessManager)) as IDaemonProcessManager;
							if (mgr is null || !mgr.WeStartedIt) return;   // adopted / none → close now
							_daemonShutdownStarted = true;
							e.Cancel = true;                      // hold the close while we stop the daemon
							try { closeAppWin.Title = "Shutting down Blazecoin…"; } catch { /* best-effort */ }
							_ = StopDaemonThenQuitAsync(mgr);
						};
				}));
			});
#endif

		// Load configuration
		var assembly = Assembly.GetExecutingAssembly();
		using var stream = assembly.GetManifestResourceStream("BlazecoinWallet.Maui.appsettings.json");
		if (stream != null)
		{
			var configBuilder = new ConfigurationBuilder();
			configBuilder.AddJsonStream(stream);
			var config = configBuilder.Build();

			foreach (var section in config.GetChildren())
			{
				builder.Configuration[section.Key] = section.Value;
				foreach (var child in section.GetChildren())
				{
					builder.Configuration[$"{section.Key}:{child.Key}"] = child.Value;
				}
			}
		}

		// Per-instance daemon overrides (--profile/--rpcurl/--rpcuser/--rpcpass/
		// --datadir/--exepath) applied after the embedded appsettings defaults, so
		// multiple wallet windows can each target + manage a different daemon.
		ApplyProfileArgs(builder.Configuration);

		// Register services
		// Holds the active wallet so wallet RPCs can be routed through
		// /wallet/<name> when more than one wallet is loaded. Registered
		// before the RPC service, which depends on it.
		builder.Services.AddSingleton<IWalletContext, WalletContext>();
		// Named HttpClient for the daemon, built by IHttpClientFactory (SOLID
		// audit #6) instead of `new HttpClient()` inside the RPC service. Timeout
		// is infinite per-client — the service caps each call with a per-request
		// CancellationToken (30s default, longer for migrate/rescan). Auth is applied
		// PER-REQUEST by RpcAuthHandler from RpcCredentials — explicit rpcuser/rpcpassword
		// if configured, otherwise Core's auto-generated datadir .cookie. No shared
		// password is baked into the client, and a daemon cookie rotation is picked up.
		builder.Services.AddSingleton<RpcCredentials>();
		builder.Services.AddTransient<RpcAuthHandler>();
		builder.Services.AddHttpClient("blazecoind", c =>
		{
			c.Timeout = Timeout.InfiniteTimeSpan;
		}).AddHttpMessageHandler<RpcAuthHandler>();

		// One BlazecoindRpcService instance, exposed through the composite
		// interface AND each segregated slice (SOLID audit #3) so focused
		// consumers depend only on what they use. All forwards resolve the same
		// singleton (it owns the factory-built HttpClient).
		builder.Services.AddSingleton<BlazecoindRpcService>();
		builder.Services.AddSingleton<IBlazecoindRpcService>(sp => sp.GetRequiredService<BlazecoindRpcService>());
		builder.Services.AddSingleton<INodeRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
		builder.Services.AddSingleton<IChainInfoRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
		builder.Services.AddSingleton<IMiningRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
		builder.Services.AddSingleton<IWalletRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
		builder.Services.AddSingleton<ISecurityRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
			// Provenance: the mint-ancestry tracer's read-only slice (same singleton).
			builder.Services.AddSingleton<Core.Services.Provenance.IAncestryRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
			builder.Services.AddSingleton<Core.Services.Provenance.IVintageSendRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
			builder.Services.AddSingleton<Core.Services.Quantum.IExposureRpc>(sp => sp.GetRequiredService<BlazecoindRpcService>());
		// One shared chain/sync poller so the footer and the dashboard
		// Blockchain card always show the same numbers (single sample).
		builder.Services.AddSingleton<ISyncStateService, SyncStateService>();
		// Owns the local blazecoind process: launches it on startup if not already
		// running, and gracefully stops it on app close (only if we started it).
		builder.Services.AddSingleton<IDaemonProcessManager, DaemonProcessManager>();
		builder.Services.AddSingleton<IMiningService, MiningService>();

		// Wallet lifecycle (load/unload/remove + on-disk delete) extracted from
		// Dashboard.razor. Stateless apart from the singletons it composes.
		builder.Services.AddSingleton<IWalletManager, WalletManager>();

		// Auto-payout engine extracted from Send.razor. Transient so each visit to
		// the Send page gets a fresh instance that starts STOPPED and reloads its
		// durable accounting from storage — matching the old per-component lifecycle.
		// The store is what it persists settings/state through (localStorage).
		// NOTE (security audit 2026-06-15): the durable accounting (sent totals,
		// last-batch state) lives in this instance's fields, so correctness relies on
		// there being exactly ONE active AutoPayoutService at a time — i.e. a single
		// Send page. That holds today (one Send route; each mount reloads the tally
		// from the store and persists after every batch). If a second concurrent
		// consumer is ever added, two Transient instances would double-count: promote
		// to a Singleton (or guard the accounting with the store as the source of truth).
		builder.Services.AddTransient<IKeyValueStore, JsKeyValueStore>();
		builder.Services.AddTransient<IAutoPayoutService, AutoPayoutService>();
		// Typed preference accessors over the key/value store — replaces scattered
		// localStorage key strings across the pages (SOLID audit #4).
		builder.Services.AddTransient<ISettingsService, SettingsService>();

		// Shared address-book / receive-address persistence (SOLID audit #5) —
		// one model + store replacing the 4 duplicate classes + raw localStorage in
		// Send/Receive/AddressBook/Transactions. Transient like the kv store it wraps
		// (its JS-backed store needs the scoped IJSRuntime, so it can't be a root singleton).
		builder.Services.AddTransient<IAddressBookStore, AddressBookStore>();

		// Transaction export (CSV/JSON/XLSX/PDF) extracted from Transactions.razor
		// (SOLID audit #7). Stateless — operates on the ExportTable it's handed.
		builder.Services.AddSingleton<ITransactionExportService, TransactionExportService>();

		// Wallet-file import orchestration extracted from Import.razor (SOLID audit
		// #7). Composes the wallet RPC slice + active-wallet context (both singletons).
		builder.Services.AddSingleton<IWalletImportService, WalletImportService>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();
		_appServices = app.Services;
		return app;
	}

#if WINDOWS
	// DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE = 33) with
	// DWMWCP_DONOTROUND = 1 squares the window corners on Windows 11.
	private static bool _daemonShutdownStarted;

	private static async Task StopDaemonThenQuitAsync(IDaemonProcessManager mgr)
	{
		try { await mgr.StopIfOwnedAsync(); } catch { /* best-effort graceful stop */ }
		Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(
			() => Microsoft.Maui.Controls.Application.Current?.Quit());
	}

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
	private const int DWMWCP_DONOTROUND = 1;
	// Windows 11 (build 22000+) window border colour attribute.
	private const int DWMWA_BORDER_COLOR = 34;

	[DllImport("dwmapi.dll", SetLastError = true)]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
#endif
}

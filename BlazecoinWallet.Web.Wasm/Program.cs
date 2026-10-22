using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI;
using BlazecoinWallet.Web.Wasm;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ── Lite wallet composition root (WASM web head — shared AddLiteWallet, audit F3) ────
// Same shape as every other head: this project contributes only its substitutes. All
// of Lite.Core (keys, signing, the C1/M3 verifier) runs INSIDE the browser — which is
// the whole point: the DevHost's Blazor Server circuit executes server-side and must
// never front real coins; this head keeps keys client-side like the Android app.
//
// STEP-3 vault: at rest the seed is AES-256-GCM under a PBKDF2(600k) wallet password
// (WebCrypto — vault-crypto.js); the VaultGate at the app root owns the unlock/protect
// ceremony, so pages only ever see the unlocked in-memory side. Registered once, aliased
// so the gate and ISeedVault consumers share the instance.
builder.Services.AddSingleton<BrowserEncryptedSeedVault>();
builder.Services.AddSingleton<ISeedVault>(sp => sp.GetRequiredService<BrowserEncryptedSeedVault>());
// Real QR scanner (getUserMedia + jsQR): a browser is its natural habitat. Scoped
// because the IJSRuntime it bridges is scoped.
builder.Services.AddScoped<IQrScanner, JsQrScanner>();

// ── Browser-persistent stores (step 4 — registered BEFORE AddLiteWallet so its
// in-memory TryAdd defaults lose). localStorage via synchronous JSImport, which is
// what lets the persisted gateway list be read RIGHT HERE at composition time.
// IPersonalNodeSettings/IP2PNodeSettings deliberately keep their in-memory defaults —
// one carries an RPC secret, the other's feature can't exist in a browser.
var gatewaySettings = new BrowserGatewaySettings(
    "https://gw.blazecoin.co.uk/;https://gw2.blazecoin.co.uk/;https://51-210-47-141.sslip.io/;https://54-39-23-245.sslip.io/");
builder.Services.AddSingleton<IGatewaySettings>(gatewaySettings);
builder.Services.AddSingleton<IWalletStateStore, BrowserWalletStateStore>();
builder.Services.AddSingleton<ILabelStore, BrowserLabelStore>();
builder.Services.AddSingleton<IAddressBook, BrowserAddressBook>();
builder.Services.AddSingleton<IWatchList, BrowserWatchList>();
builder.Services.AddSingleton<IPinLock, BrowserPinLock>();
builder.Services.AddSingleton<IUnitSettings, BrowserUnitSettings>();
builder.Services.AddSingleton<IExplorerSettings, BrowserExplorerSettings>();
builder.Services.AddSingleton<ISkinSettings, BrowserSkinSettings>();
// Locking (2026-08-23): in a browser a real lock drops the keys and re-runs the password
// vault ceremony; idle auto-lock (default 15 min, Settings card) + the header 🔒 button
// both go through IWalletLocker. Registered BEFORE AddLiteWallet so its PIN-gate default loses.
builder.Services.AddSingleton<IAutoLockSettings, BrowserAutoLockSettings>();
builder.Services.AddSingleton<IWalletLocker, BrowserVaultLocker>();
builder.Services.AddSingleton<BrowserIdleLock>();

builder.Services.AddLiteWallet(new LiteWalletOptions
{
    // The persisted failover list (Settings → Advanced can change it), defaulting to
    // the shipped order — same as the Android head. Same-origin on its own box; the
    // partner hop is covered by the gateways' CORS policy (step 2).
    GatewayUrls = gatewaySettings.Current,
    // Measured on this head 2026-08-03 (/perf): scrypt is ~56 ms/header INTERPRETED
    // (a 250-header batch freezes the single-threaded tab ~14 s) but ~0.6 ms/header
    // AOT — so published builds (AOT, see the csproj) run the full trustless header
    // sync, while dev-server runs (always interpreted) keep it off. Funds safety is
    // identical either way: the C1/M3 verifier stays on in both.
    DisableHeaderChainSync = builder.HostEnvironment.IsDevelopment(),
});

await builder.Build().RunAsync();

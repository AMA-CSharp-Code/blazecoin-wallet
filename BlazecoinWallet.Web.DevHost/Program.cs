using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.UI;
using BlazecoinWallet.Web.DevHost;
using BlazecoinWallet.Web.DevHost.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Lite wallet composition root (DESKTOP DEV HOST — shared AddLiteWallet, audit F3) ──
// This host contributes only its substitutes: the plain-FILE dev vault (NOT secure) and
// dev-cert-trusting transport. Data mode: "gateway" (default) or Node:Mode=personal →
// YOUR OWN daemon's RPC (sovereignty option; no history, scantxoutset balances).
var usePersonalNode = string.Equals(builder.Configuration["Node:Mode"], "personal", StringComparison.OrdinalIgnoreCase);

builder.Services.AddSingleton<ISeedVault, DevFileSeedVault>();
builder.Services.AddSingleton<IWalletStateStore, DevFileWalletStateStore>();
// Real QR scanner (getUserMedia + jsQR in the WebView) — registered BEFORE AddLiteWallet so its
// NullQrScanner TryAdd default doesn't win. Scoped: the JS runtime it bridges is scoped. Works in
// the desktop browser too, so the Send-page scan flow is testable here.
builder.Services.AddScoped<IQrScanner, JsQrScanner>();
builder.Services.AddLiteWallet(new LiteWalletOptions
{
    // Default to the LIVE public appliance so the dev host shows real chain data; override
    // with Gateway:BaseUrl(s) for local backend work (e.g. https://localhost:7002/).
    GatewayUrls = builder.Configuration.GetSection("Gateway:BaseUrls").Get<string[]>()
        ?? [builder.Configuration["Gateway:BaseUrl"] ?? "https://51-210-47-141.sslip.io/"],
    P2PNodes = builder.Configuration.GetSection("P2P:Nodes").Get<string[]>(),
    UsePersonalNode = usePersonalNode,
    PersonalNode = usePersonalNode
        ? new PersonalNodeOptions(
            RpcUrl: builder.Configuration["Node:RpcUrl"] ?? "http://127.0.0.1:55413/",
            CookieFilePath: builder.Configuration["Node:CookieFile"],
            RpcUser: builder.Configuration["Node:RpcUser"],
            RpcPassword: builder.Configuration["Node:RpcPassword"])
        : null,
    InnerHandlerFactory = builder.Environment.IsDevelopment()
        ? () => new HttpClientHandler
        {
            // The local gateway runs on the ASP.NET dev certificate.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        }
        : null,
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(BlazecoinWallet.Lite.UI.Shared.LiteLayout).Assembly);

app.Run();

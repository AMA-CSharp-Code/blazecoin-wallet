using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Lite.Gateway;
using BlazecoinWallet.Lite.Gateway.Ancestry;
using BlazecoinWallet.Lite.Gateway.Index;
using BlazecoinWallet.Lite.Gateway.Rpc;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Kestrel sits on loopback behind Caddy. Hard caps so a flood can't exhaust the 4 GB VPS:
// bodies stay just above the broadcast hex cap, and concurrent connections are bounded
// (security audit S1/M2 — a public appliance must not let an anonymous caller OOM the box).
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 256 * 1024;      // broadcast hex caps at ~100 KB; reads carry no body
    k.Limits.MaxConcurrentConnections = 256;
    k.Limits.MaxConcurrentUpgradedConnections = 16;
});

// ── Daemon RPC (loopback blazecoind, cookie auth by default) ────────────────────────────
var daemonOptions = new DaemonOptions
{
    Url = builder.Configuration.GetValue("Daemon:Url", "http://127.0.0.1:55413/")!,
    CookiePath = builder.Configuration.GetValue<string?>("Daemon:CookiePath", "/var/lib/blazecoin/.cookie"),
    RpcUser = builder.Configuration.GetValue<string?>("Daemon:RpcUser", null),
    RpcPassword = builder.Configuration.GetValue<string?>("Daemon:RpcPassword", null),
};
// Fail fast on a credential-less config (SOLID F6): a misconfiguration is permanent —
// surfacing it as per-call 503s would be indistinguishable from a daemon outage.
if (string.IsNullOrEmpty(daemonOptions.RpcUser) && string.IsNullOrEmpty(daemonOptions.CookiePath))
    throw new InvalidOperationException(
        "Daemon RPC credentials missing: set Daemon:CookiePath (cookie auth) or Daemon:RpcUser/RpcPassword.");
builder.Services.AddSingleton(daemonOptions);
builder.Services.AddSingleton(sp => new DaemonRpcClient(
    // Cap sockets to the daemon (security audit S1): the passthrough reads must never be able
    // to open unbounded connections and over-drive blazecoind's small RPC work queue.
    new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 8 }) { Timeout = TimeSpan.FromSeconds(30) },
    sp.GetRequiredService<DaemonOptions>()));

// ── The index (SQLite + mempool overlay + walker) ───────────────────────────────────────
builder.Services.AddSingleton(sp => new UtxoIndexDb(
    builder.Configuration.GetValue("Index:DbPath", Path.Combine("data", "lite-index.db"))!));
builder.Services.AddSingleton<MempoolOverlay>();
builder.Services.AddSingleton<WalkerStatus>();
builder.Services.AddSingleton<BlockTimeResolver>();
builder.Services.AddSingleton<LiteBroadcastService>();
builder.Services.AddHostedService<ChainWalkerService>();

// ── Server-side Provenance (the /ancestry endpoint) ─────────────────────────────────────
builder.Services.AddSingleton<AncestryReadCache>();
builder.Services.AddSingleton<TraceGate>();

// ── Rate limits ─────────────────────────────────────────────────────────────────────────
// Every wallet-facing route is anonymous by design, so per-IP throttling is the only lever.
// Two policies: a tight window for the daemon-touching broadcast (as the full Indexer), and
// a generous one for the read surface (security audit S1: raw/proof/address/utxos each drive
// a daemon RPC or SQLite scan and were previously unthrottled). Partition keys are IP-MASKED
// (security audit M1): a bare RemoteIpAddress lets an IPv6 caller rotate a /64 for unlimited
// fresh windows, so mask v6→/64 and v4→/32.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("tx-broadcast", ctx => FixedWindow(ctx, "b:", PerMinute(ctx, "Broadcast:PerMinute", 6)));
    options.AddPolicy("reads", ctx => FixedWindow(ctx, "r:", PerMinute(ctx, "Reads:PerMinute", 240)));
    // Ancestry traces are the most expensive read on the box (a walk = many daemon RPCs),
    // but the wallet legitimately fires one per FUNDED address in quick succession — the
    // limit covers a wallet's burst, while the TraceGate serialises the actual daemon work.
    options.AddPolicy("trace", ctx => FixedWindow(ctx, "t:", PerMinute(ctx, "Trace:PerMinute", 12)));
});

// ── CORS — the WASM web head ────────────────────────────────────────────────────────────
// The Android/desktop heads call this API from .NET (never CORS-bound); the browser-based
// web head uses fetch, which is. The wallet app is served same-origin from THIS box's
// Caddy, so CORS only matters for the FAILOVER hop (the app loaded from one gateway
// reading the other) and for dev heads on localhost. This API is anonymous and cookieless
// (no AllowCredentials), so the policy governs which foreign pages' JS may READ responses
// — nothing here authenticates anyone, and the per-IP rate limits protect load either way.
var walletOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["https://gw.blazecoin.co.uk", "https://gw2.blazecoin.co.uk",
        "https://51-210-47-141.sslip.io", "https://54-39-23-245.sslip.io"];
builder.Services.AddCors(o => o.AddPolicy("wallet", p => p
    .SetIsOriginAllowed(origin => IsWalletOrigin(origin, walletOrigins))
    .WithMethods("GET", "POST")
    .WithHeaders("Content-Type")));

var app = builder.Build();

// Trust ONLY the loopback Caddy hop for X-Forwarded-For (security audit M1/S4): pinning
// KnownNetworks/Proxies to loopback means a co-tenant or a mis-set ForwardLimit can't let a
// forged header poison the rate-limit partition key. Caddy's reverse_proxy appends the real
// client IP and ForwardLimit defaults to 1, so the rightmost (real) hop is read.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 8));       // 127.0.0.0/8
forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128)); // ::1/128
app.UseForwardedHeaders(forwarded);
// Before the rate limiter so CORS preflights (OPTIONS) are answered by the CORS
// middleware itself rather than consuming a read-window slot.
app.UseCors("wallet");
app.UseRateLimiter();

// ── Routes ──────────────────────────────────────────────────────────────────────────────
// The wallet's IndexerDataService calls reads under "indexer/api/…" (the full stack's YARP
// prefix) and the relay at "api/tx/broadcast". This appliance IS the terminal server, so it
// answers BOTH spellings — the failover list can mix lite and full sites freely.
MapWalletSurface(app.MapGroup("/api").RequireRateLimiting("reads"));
MapWalletSurface(app.MapGroup("/indexer/api").RequireRateLimiting("reads"));

// Ops-only: is this site healthy / synced? (Not part of the wallet contract; also lets a
// client's failover probe read health.) Deliberately minimal — no secrets, only heights.
app.MapGet("/api/status", (WalkerStatus status, MempoolOverlay mempool) => Results.Ok(new
{
    daemonReachable = status.DaemonReachable,
    daemonHeight = status.DaemonHeight,
    indexedHeight = status.IndexedHeight,
    mempoolTransactions = mempool.TxCount,
    synced = status.Synced,
}));

app.Run();

static void MapWalletSurface(RouteGroupBuilder api)
{
    // Address summary — 404 when never seen on-chain (the wallet's rotation gap-scan and
    // Home summary both rely on exactly that). Same shape as the full Indexer's AddressDto.
    api.MapGet("/address/{address}", (string address, UtxoIndexDb db, WalkerStatus status) =>
    {
        if (!IsAddress(address)) return Results.BadRequest(new { error = "Invalid address." });
        if (!status.Synced) return IndexNotReady();
        var s = db.GetSummary(address);
        return s == null
            ? Results.NotFound()
            : Results.Ok(new { address, balance = s.Balance, totalReceived = s.TotalReceived, totalSent = s.TotalSent, txCount = s.TxCount });
    });

    // Wallet-grade UTXO listing: confirmed rows (minus outpoints being spent in the
    // mempool) plus unconfirmed incoming at 0 confirmations. Field-for-field the full
    // Indexer's UtxoDto.
    api.MapGet("/address/{address}/utxos", (string address, UtxoIndexDb db, MempoolOverlay mempool, WalkerStatus status) =>
    {
        if (!IsAddress(address)) return Results.BadRequest(new { error = "Invalid address." });
        // Serving an under-built index as authoritative would make a restore's gap-scan stop
        // early and report zero balance (security audit S3) — 503 until we're at the tip.
        if (!status.Synced) return IndexNotReady();
        var tip = status.IndexedHeight;
        var confirmed = db.GetUtxos(address)
            .Where(u => !mempool.IsSpent(u.TxId, u.Vout))
            .Select(u => new UtxoResponse(u.TxId, u.Vout, u.Amount,
                (int)Math.Max(1, tip - u.BlockHeight + 1), u.BlockHeight, u.IsCoinbase));
        var unconfirmed = mempool.GetForAddress(address)
            .Select(u => new UtxoResponse(u.TxId, u.Vout, u.Amount, 0, 0, false));
        return Results.Ok(confirmed.Concat(unconfirmed)
            .OrderByDescending(u => u.Confirmations).ThenBy(u => u.TxId).ThenBy(u => u.OutputIndex));
    });

    // Address transaction history (newest-first, paginated): signed per-output/input rows,
    // same shape as the full Indexer's /address/{addr}/transactions. Block times are resolved
    // from the daemon (cached per height) since the index doesn't store them.
    api.MapGet("/address/{address}/transactions", async (string address, UtxoIndexDb db, BlockTimeResolver times,
        WalkerStatus status, CancellationToken ct, int page = 1, int pageSize = 25) =>
    {
        if (!IsAddress(address)) return Results.BadRequest(new { error = "Invalid address." });
        if (!status.Synced) return IndexNotReady();
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var (rows, total) = db.GetAddressHistory(address, page, pageSize);
        try
        {
            var items = new List<object>(rows.Count);
            foreach (var r in rows)
            {
                var unix = await times.GetUnixTimeAsync(r.BlockHeight, ct);
                items.Add(new
                {
                    txId = r.TxId,
                    blockHeight = r.BlockHeight,
                    timestamp = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
                    amount = r.Amount,
                    type = r.Type,
                });
            }
            return Results.Ok(new { page, pageSize, total, items });
        }
        catch (Exception ex) when (ex is DaemonRpcException or DaemonUnreachableException)
        {
            // Only the timestamps need the daemon; if it's briefly unreachable, 503 so the
            // client retries (and the resolved times are cached for next time).
            return Results.Json(new { error = "The node is temporarily unreachable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    // Raw tx + merkle proof: daemon passthroughs, response shapes and status mapping
    // mirrored from the full Indexer's TransactionsController. (These read the daemon
    // directly, so they work independent of index sync.)
    api.MapGet("/tx/{txId}/raw", async (string txId, DaemonRpcClient rpc, CancellationToken ct) =>
    {
        if (!IsTxId(txId)) return Results.BadRequest(new { error = "Invalid transaction id." });
        try
        {
            var hex = await rpc.GetRawTransactionHexAsync(txId, ct);
            return Results.Ok(new { txId, hex });
        }
        catch (DaemonRpcException ex) when (ex.IsNotFound)
        {
            return Results.NotFound(new { error = "Transaction not found." });
        }
        catch (Exception ex) when (ex is DaemonRpcException or DaemonUnreachableException)
        {
            return Results.Json(new { error = "The node is temporarily unreachable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    api.MapGet("/tx/{txId}/proof", async (string txId, DaemonRpcClient rpc, CancellationToken ct) =>
    {
        if (!IsTxId(txId)) return Results.BadRequest(new { error = "Invalid transaction id." });
        try
        {
            var proof = await rpc.GetTxOutProofAsync(txId, ct);
            return Results.Ok(new { txId, proof });
        }
        catch (DaemonRpcException ex) when (ex.IsNotFound)
        {
            return Results.NotFound(new { error = "Transaction not found or not yet confirmed." });
        }
        catch (Exception ex) when (ex is DaemonRpcException or DaemonUnreachableException)
        {
            return Results.Json(new { error = "The node is temporarily unreachable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    // Raw block headers by height range — the wallet's trustless header-chain sync re-derives
    // proof-of-work + difficulty from these to verify the tip height itself (rather than trust
    // this appliance's scalar). Capped at 250/request and bounded to the daemon tip, so an
    // over-range ask just returns fewer (the client stops when a short batch arrives).
    api.MapGet("/headers/{fromHeight:long}", async (long fromHeight, DaemonRpcClient rpc,
        WalkerStatus status, CancellationToken ct, int count = 250) =>
    {
        if (fromHeight < 1) return Results.BadRequest(new { error = "Invalid height." });
        if (count is < 1 or > 250) count = 250;
        var tip = status.DaemonHeight;
        if (tip > 0 && fromHeight + count - 1 > tip) count = (int)Math.Max(0, tip - fromHeight + 1);
        if (count == 0) return Results.Ok(new { fromHeight, headers = Array.Empty<string>() });
        try
        {
            var headers = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                try { headers.Add(await rpc.GetBlockHeaderHexAsync(fromHeight + i, ct)); }
                // A reorg between the tip check and the fetch can push a height out of range —
                // return what we have (a short batch), which the client reads as "at the tip".
                catch (DaemonRpcException ex) when (ex.Message.Contains("out of range", StringComparison.OrdinalIgnoreCase)) { break; }
            }
            return Results.Ok(new { fromHeight, headers });
        }
        catch (Exception ex) when (ex is DaemonRpcException or DaemonUnreachableException)
        {
            return Results.Json(new { error = "The node is temporarily unreachable." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    // Mint-ancestry catalogue for one address — the desktop wallet's AncestryTracer run
    // SERVER-side, because a lite wallet has no node to walk (ROADMAP §F: the tracer is
    // reused unchanged, so lite and desktop numbers can never drift). This is the one
    // expensive read on the appliance — a walk is many daemon RPCs — so beyond the usual
    // guards it carries its own rate-limit policy, hard read budgets, an output cap, and
    // the whole-appliance TraceGate so concurrent traces queue instead of interleaving.
    // Trust posture: ancestry is DISPLAY data — a wrong answer can mislabel lineage but
    // can never move funds (unlike balances, which the wallet C1/M3-verifies).
    api.MapGet("/address/{address}/ancestry", async (string address, UtxoIndexDb db, MempoolOverlay mempool,
        WalkerStatus status, DaemonRpcClient rpc, AncestryReadCache cache, TraceGate gate,
        IConfiguration cfg, CancellationToken ct) =>
    {
        if (!IsAddress(address)) return Results.BadRequest(new { error = "Invalid address." });
        if (!status.Synced) return IndexNotReady();

        // Confirmed coins only — a 0-conf transaction's ancestry isn't settled fact yet.
        var utxos = db.GetUtxos(address).Where(u => !mempool.IsSpent(u.TxId, u.Vout)).ToList();

        var maxOutputs = cfg.GetValue("Trace:MaxOutputs", 200);
        if (utxos.Count > maxOutputs)
            return Results.UnprocessableEntity(new
            {
                error = $"This address holds {utxos.Count:N0} unspent outputs; this server traces at most {maxOutputs:N0}."
            });

        var reader = new GatewayAncestryRpc(rpc, cache);
        // 100k reads resolves real pool-descended ancestry (the first live wallet's two
        // dust coins ran through a 413-input consolidation and blew straight past the
        // original 25k); the TraceGate + cache keep the daemon cost acceptable.
        var maxReads = cfg.GetValue("Trace:MaxTransactionReads", 100_000);
        var options = new AncestryTracerOptions
        {
            // Depth is a pathology backstop, not the budget — MaxTransactionReads is.
            // 100 starved real payout coins to zero once the payout wallet's 15-min
            // change-chain grew past 100 hops (2026-08-24).
            MaxDepth = cfg.GetValue("Trace:MaxDepth", 100_000),
            MaxTransactionReads = maxReads,
        };

        // An empty address needs no daemon work — answer without taking the gate.
        if (utxos.Count == 0)
            return Results.Ok(AncestryDto(address,
                await new AncestryTracer(reader, options).TraceOutputsAsync([], null, ct), maxReads));

        if (!await gate.TryEnterAsync(TimeSpan.FromSeconds(15), ct))
            return Results.Json(new { error = "The server is busy with another trace — try again shortly." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        try
        {
            var tip = status.IndexedHeight;
            var outputs = utxos.Select(u => new UnspentOutput(u.TxId, u.Vout, u.Amount, address,
                (int)Math.Max(1, tip - u.BlockHeight + 1))).ToList();
            var report = await new AncestryTracer(reader, options).TraceOutputsAsync(outputs, null, ct);
            return Results.Ok(AncestryDto(address, report, maxReads));
        }
        catch (Exception ex) when (ex is DaemonRpcException or DaemonUnreachableException)
        {
            return Results.Json(new { error = "The node is temporarily unreachable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally { gate.Exit(); }
    }).RequireRateLimiting("trace");

    // The anonymous, rate-limited send path — status mapping mirrored from the full
    // Indexer's TxBroadcastController. Broadcast additionally requires the tighter window.
    api.MapPost("/tx/broadcast", async (BroadcastRequest? request, LiteBroadcastService broadcast, CancellationToken ct) =>
    {
        var outcome = await broadcast.BroadcastAsync(request?.Hex, ct);
        return outcome.Status switch
        {
            "accepted" => Results.Ok(new { txId = outcome.TxId }),
            "invalid" => Results.BadRequest(new { error = outcome.Reason }),
            "rejected" => Results.UnprocessableEntity(new { error = outcome.Reason }),
            _ => Results.Json(new { error = outcome.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }).RequireRateLimiting("tx-broadcast");
}

static IResult IndexNotReady() =>
    Results.Json(new { error = "The index is still syncing — try again shortly." },
        statusCode: StatusCodes.Status503ServiceUnavailable);

// ── /ancestry response shape (consumed by the client's LiteAncestryReport) ──────────────
static object AncestryDto(string address, ProvenanceReport report, int maxReads) => new
{
    address,
    totalSatoshis = report.TotalSatoshis,
    outputCount = report.Outputs.Count,
    transactionsRead = report.TransactionsRead,
    // The budget was exhausted, so unwalked ancestry counted as unproven — the figures
    // are a floor. (A trace landing EXACTLY on the cap while complete would flag too;
    // "may be incomplete" is the honest reading either way.)
    truncated = report.TransactionsRead >= maxReads,
    fifo = AncestryModel(report.Fifo),
    haircut = AncestryModel(report.Haircut),
};

static object AncestryModel(VintageBreakdown b) => new
{
    attributedSatoshis = b.AttributedSatoshis,
    years = b.Years.Select(AncestryRow).ToList(),
    months = b.Months.Select(AncestryRow).ToList(),
    specials = b.Specials.Select(AncestryRow).ToList(),
};

static object AncestryRow(VintageRow r) => new
{
    key = r.Key,
    label = r.Label,
    kind = r.Kind.ToString(),
    satoshis = r.Satoshis,
    outputCount = r.OutputCount,
    firstHeight = r.FirstHeight,
    firstTimeUtc = r.FirstTimeUtc,
    lastTimeUtc = r.LastTimeUtc,
};

static bool IsTxId(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

// Configured origins (the public gateway sites, exact match — an Origin header carries
// no trailing slash), plus any localhost/loopback origin so dev heads on arbitrary
// ports work without per-box config.
static bool IsWalletOrigin(string origin, string[] configured)
{
    if (configured.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
    return Uri.TryCreate(origin, UriKind.Absolute, out var u)
        && (string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase) || u.Host == "127.0.0.1");
}

// Cheap guard symmetric with IsTxId (security audit S6): a legacy Blazecoin address is a
// Base58Check string (no 0/O/I/l); reject anything else before it reaches SQLite.
static bool IsAddress(string s) =>
    s.Length is >= 26 and <= 64 &&
    s.All(c => "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains(c));

static int PerMinute(HttpContext ctx, string key, int fallback) =>
    ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue(key, fallback);

static RateLimitPartition<string> FixedWindow(HttpContext ctx, string prefix, int perMinute) =>
    RateLimitPartition.GetFixedWindowLimiter(prefix + ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = perMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

// Mask the caller IP so one host can't rotate addresses for fresh windows (security audit
// M1): IPv4 → /32 (itself), IPv6 → /64 (the smallest routable allocation).
static string ClientKey(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip == null) return "unknown";
    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
    {
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8); // zero the host half → /64
        return new IPAddress(bytes).ToString();
    }
    return ip.ToString();
}

public sealed record BroadcastRequest(string? Hex);
public sealed record UtxoResponse(string TxId, int OutputIndex, long Amount, int Confirmations, long BlockHeight, bool IsCoinbase);

/// <summary>Marker for WebApplicationFactory-based contract tests.</summary>
public partial class Program;

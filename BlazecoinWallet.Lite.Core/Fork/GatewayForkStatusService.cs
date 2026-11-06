using System.Net.Http.Json;
using System.Text.Json;

namespace BlazecoinWallet.Lite.Fork;

/// <summary>
/// Polls the lite gateway's <c>/api/status</c> (the same failover-backed HttpClient the data
/// services use, so the announcement follows whichever gateway is answering) and evaluates the
/// §7.1 banner for this build. A null client (personal-node mode — no gateway, nobody to
/// announce) keeps <see cref="Current"/> null for ever, which renders nothing.
/// Mirrors <see cref="IncomingPaymentWatcher"/>: a PeriodicTimer loop started by the layout,
/// per-cycle failures contained, nothing kills the loop silently.
/// <para>The status response is unauthenticated and comes from a gateway the wallet does not
/// trust (bug-bounty report 2026-11-06), so what is read is held to <see cref="ForkAnnouncement"/>'s
/// rules: fields of the wrong shape are dropped, a fork this build does not follow cannot be
/// announced at or below the shipped checkpoint, and the announcement is
/// <see cref="ForkStatus.Authenticated"/> only with a valid project signature. The banner is
/// judged at the wallet's own verified tip where the head has one.</para>
/// </summary>
public sealed class GatewayForkStatusService : IForkStatusService, IDisposable
{
    /// <summary>Default poll cadence — the chain's block spacing.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient? _http;
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _clock;
    private readonly Func<long?> _verifiedTip;
    private readonly IReadOnlyList<string> _announcementKeys;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    /// <param name="verifiedTip">The wallet's own proof-of-work verified tip, or null when the
    /// head has none (the web head, or header sync not wired). Null → the gateway's claim.</param>
    /// <param name="announcementKeys">The project keys an announcement may be signed with;
    /// null → the keys pinned in this build (<see cref="ForkAnnouncement.PinnedKeys"/>).</param>
    public GatewayForkStatusService(HttpClient? http, TimeSpan? interval = null, Func<DateTime>? clock = null,
        Func<long?>? verifiedTip = null, IReadOnlyList<string>? announcementKeys = null)
    {
        _http = http;
        _interval = interval ?? DefaultInterval;
        _clock = clock ?? (() => DateTime.UtcNow);
        _verifiedTip = verifiedTip ?? (() => null);
        _announcementKeys = announcementKeys ?? ForkAnnouncement.PinnedKeys;
    }

    public ForkStatus? Current { get; private set; }

    /// <inheritdoc />
    public long EffectiveTip => _verifiedTip() is long verified and > 0 ? verified : Current?.Tip ?? 0;

    public ForkBannerState State =>
        ForkBanner.Evaluate(Current, LiteClientInfo.Version, LiteClientInfo.SupportedForks, EffectiveTip);

    public event Action? Changed;

    public bool NoticeDismissed { get; private set; }

    public void DismissNotice()
    {
        NoticeDismissed = true;
        RaiseChanged();
    }

    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_http == null) return;
        lock (_gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _ = LoopAsync(_cts.Token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            await RefreshAsync(ct);
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshAsync(ct);
        }
        catch (OperationCanceledException) { /* Stop() */ }
        catch { /* the last net — RefreshAsync already contains per-cycle failures */ }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_http == null) return;
        StatusJson? body;
        try
        {
            // Same "indexer/api" spelling as every other wallet-side read (the lite gateway
            // answers both /api/status and /indexer/api/status; a full-stack site only the latter).
            body = await _http.GetFromJsonAsync<StatusJson>("indexer/api/status", Json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return; } // a flaky poll keeps the previous status; next tick retries
        if (body == null) return;

        // Nothing the gateway sent is used as it came: a name or version of the wrong shape is
        // dropped, and a height that cannot be a coming fork for this build withdraws the
        // announcement altogether (IsAnnounced needs both a name and a height).
        var name = ForkAnnouncement.CleanName(body.ForkName);
        var height = name != null && ForkAnnouncement.IsPlausibleHeight(
            body.ForkHeight, name, LiteClientInfo.SupportedForks, ForkAnnouncement.ShippedCheckpointHeight)
            ? body.ForkHeight
            : null;
        var minVersion = ForkAnnouncement.CleanVersion(body.MinClientVersion);
        var authenticated = height != null &&
            ForkAnnouncement.IsSigned(name, height, minVersion, body.ForkSignature, _announcementKeys);

        var next = new ForkStatus(
            height == null ? null : name,
            height,
            minVersion,
            Math.Max(body.DaemonHeight, 0),
            _clock(),
            authenticated);

        var previous = Current;
        Current = next;
        if (previous == null || !SameAnnouncement(previous, next)) RaiseChanged();
    }

    /// <summary>Equality that ignores the fetch time; the tip counts because it drives the days
    /// figure and the Notice→Warning→Stopped transitions.</summary>
    private static bool SameAnnouncement(ForkStatus a, ForkStatus b) =>
        a.ForkName == b.ForkName && a.ForkHeight == b.ForkHeight &&
        a.MinClientVersion == b.MinClientVersion && a.Tip == b.Tip &&
        a.Authenticated == b.Authenticated;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* a broken subscriber must not stop future notifications */ }
    }

    public void Dispose() => Stop();

    // Wire shape of the gateway's /api/status (camelCase via JsonSerializerDefaults.Web);
    // the sync-health fields are read for the tip, the three announcement fields are §7.1.
    private sealed class StatusJson
    {
        public bool DaemonReachable { get; set; }
        public long DaemonHeight { get; set; }
        public long IndexedHeight { get; set; }
        public string? ForkName { get; set; }
        public long? ForkHeight { get; set; }
        public string? MinClientVersion { get; set; }
        public string? ForkSignature { get; set; }
    }
}

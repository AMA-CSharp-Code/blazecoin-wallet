using System.Net;
using System.Text;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.Fork;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The §7.1 poller against a scripted gateway: maps the three announcement fields + the tip,
/// raises Changed only when something changed, keeps the last good status through a flaky
/// poll, stays silent with no gateway (personal-node mode), and is wired by AddLiteWallet.
/// </summary>
public class GatewayForkStatusServiceTests
{
    private sealed class ScriptedGateway : HttpMessageHandler
    {
        public string Body = """{"daemonReachable":true,"daemonHeight":4300000,"indexedHeight":4300000,"mempoolTransactions":0,"synced":true,"forkName":null,"forkHeight":null,"minClientVersion":null}""";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Dead;
        public int Calls;
        public string? LastPath;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastPath = request.RequestUri!.AbsolutePath;
            if (Dead) throw new HttpRequestException("connection refused");
            return Task.FromResult(new HttpResponseMessage(Status)
            { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private static GatewayForkStatusService Build(ScriptedGateway gw, TimeSpan? interval = null) =>
        new(new HttpClient(gw) { BaseAddress = new Uri("https://gw.test/") }, interval, () => new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc));

    private const string Announced =
        """{"daemonReachable":true,"daemonHeight":4900000,"indexedHeight":4900000,"mempoolTransactions":0,"synced":true,"forkName":"pqsig","forkHeight":5000000,"minClientVersion":"2.1.0"}""";

    [Fact]
    public async Task Reads_the_wallet_side_status_path_and_maps_a_null_announcement()
    {
        var gw = new ScriptedGateway();
        var svc = Build(gw);
        Assert.Null(svc.Current);

        await svc.RefreshAsync();

        Assert.Equal("/indexer/api/status", gw.LastPath);
        var s = Assert.IsType<ForkStatus>(svc.Current);
        Assert.False(s.IsAnnounced);
        Assert.Null(s.ForkName);
        Assert.Null(s.ForkHeight);
        Assert.Null(s.MinClientVersion);
        Assert.Equal(4_300_000, s.Tip);
        Assert.Equal(new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc), s.FetchedAtUtc);
        Assert.Equal(ForkBannerState.None, svc.State);
    }

    [Fact]
    public async Task Maps_an_announcement_and_evaluates_the_banner_for_this_build()
    {
        var gw = new ScriptedGateway { Body = Announced };
        var svc = Build(gw);
        await svc.RefreshAsync();

        var s = Assert.IsType<ForkStatus>(svc.Current);
        Assert.True(s.IsAnnounced);
        Assert.Equal("pqsig", s.ForkName);
        Assert.Equal(5_000_000, s.ForkHeight);
        Assert.Equal("2.1.0", s.MinClientVersion);
        Assert.Equal(4_900_000, s.Tip);
        // This build lacks pqsig → a banner (Warning: the floor is above any 2.0.x build).
        Assert.NotEqual(ForkBannerState.None, svc.State);
        Assert.Equal(svc.State, ForkBanner.Evaluate(s, LiteClientInfo.Version, LiteClientInfo.SupportedForks, s.Tip));
    }

    [Fact]
    public async Task Blank_strings_are_normalised_to_null()
    {
        var gw = new ScriptedGateway
        {
            Body = """{"daemonHeight":10,"forkName":"  ","forkHeight":null,"minClientVersion":""}"""
        };
        var svc = Build(gw);
        await svc.RefreshAsync();
        var s = Assert.IsType<ForkStatus>(svc.Current);
        Assert.Null(s.ForkName);
        Assert.Null(s.MinClientVersion);
        Assert.False(s.IsAnnounced);
    }

    [Fact]
    public async Task Changed_fires_on_first_read_and_on_a_change_but_not_on_an_identical_poll()
    {
        var gw = new ScriptedGateway();
        var svc = Build(gw);
        var changes = 0;
        svc.Changed += () => changes++;

        await svc.RefreshAsync();
        Assert.Equal(1, changes);
        await svc.RefreshAsync(); // identical
        Assert.Equal(1, changes);

        gw.Body = gw.Body.Replace("4300000", "4300001"); // the tip moved — days/transitions depend on it
        await svc.RefreshAsync();
        Assert.Equal(2, changes);

        gw.Body = Announced; // the operator set Fork:*
        await svc.RefreshAsync();
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task A_flaky_poll_keeps_the_previous_status()
    {
        var gw = new ScriptedGateway { Body = Announced };
        var svc = Build(gw);
        await svc.RefreshAsync();
        var before = svc.Current;

        gw.Dead = true;
        await svc.RefreshAsync();
        Assert.Same(before, svc.Current);

        gw.Dead = false; gw.Status = HttpStatusCode.ServiceUnavailable;
        await svc.RefreshAsync();
        Assert.Same(before, svc.Current);

        gw.Status = HttpStatusCode.OK; gw.Body = "not json";
        await svc.RefreshAsync();
        Assert.Same(before, svc.Current);
    }

    [Fact]
    public async Task No_gateway_means_no_announcement_ever()
    {
        var svc = new GatewayForkStatusService(null);
        await svc.RefreshAsync();
        Assert.Null(svc.Current);
        Assert.Equal(ForkBannerState.None, svc.State);
        svc.Start();
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void Notice_dismissal_is_per_instance_and_raises_Changed()
    {
        var svc = new GatewayForkStatusService(null);
        var changes = 0;
        svc.Changed += () => changes++;
        Assert.False(svc.NoticeDismissed);
        svc.DismissNotice();
        Assert.True(svc.NoticeDismissed);
        Assert.Equal(1, changes);
        // A fresh service (next launch) starts undismissed — the Notice returns.
        Assert.False(new GatewayForkStatusService(null).NoticeDismissed);
    }

    [Fact]
    public async Task Start_polls_on_the_interval_until_Stop()
    {
        var gw = new ScriptedGateway();
        var svc = Build(gw, TimeSpan.FromMilliseconds(15));
        svc.Start();
        svc.Start(); // idempotent
        Assert.True(svc.IsRunning);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (gw.Calls < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(gw.Calls >= 3, $"polled {gw.Calls} times");

        svc.Stop();
        Assert.False(svc.IsRunning);
        var after = gw.Calls;
        await Task.Delay(80);
        Assert.InRange(gw.Calls, after, after + 1); // at most one in-flight poll completes
    }

    [Fact]
    public void A_throwing_subscriber_does_not_break_later_notifications()
    {
        var svc = new GatewayForkStatusService(null);
        var seen = 0;
        svc.Changed += () => seen++;
        svc.Changed += () => throw new InvalidOperationException("boom");
        svc.DismissNotice(); // the throw is contained inside the service
        svc.DismissNotice(); // ...and the next notification still reaches the healthy subscriber
        Assert.Equal(2, seen);
    }

    // ── Composition (AddLiteWallet wires it for every head) ──

    private sealed class StubVault : ISeedVault
    {
        public Task<bool> HasWalletAsync() => Task.FromResult(false);
        public Task SaveMnemonicAsync(string m) => Task.CompletedTask;
        public Task<string?> LoadMnemonicAsync() => Task.FromResult<string?>(null);
        public Task ClearAsync() => Task.CompletedTask;
    }

    private static ServiceProvider Compose(LiteWalletOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISeedVault, StubVault>();
        services.AddLiteWallet(options);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Gateway_mode_registers_the_poller_on_the_failover_client()
    {
        using var sp = Compose(new LiteWalletOptions { GatewayUrls = ["https://gw.test/"] });
        var svc = Assert.IsType<GatewayForkStatusService>(sp.GetRequiredService<IForkStatusService>());
        Assert.Same(svc, sp.GetRequiredService<IForkStatusService>()); // singleton
        svc.Start();
        Assert.True(svc.IsRunning); // a client exists → it polls
        svc.Stop();
    }

    [Fact]
    public void Personal_node_mode_registers_a_silent_poller()
    {
        using var sp = Compose(new LiteWalletOptions
        {
            UsePersonalNode = true,
            PersonalNode = new PersonalNodeOptions("http://127.0.0.1:55413/", RpcUser: "u", RpcPassword: "p"),
        });
        var svc = Assert.IsType<GatewayForkStatusService>(sp.GetRequiredService<IForkStatusService>());
        svc.Start();
        Assert.False(svc.IsRunning); // nobody to announce
        Assert.Equal(ForkBannerState.None, svc.State);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using BlazecoinWallet.Lite.Gateway.Index;
using BlazecoinWallet.Lite.Gateway.Peers;
using BlazecoinWallet.Lite.Gateway.Rpc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Gateway.Tests;

/// <summary>
/// The public peer listing (2026-11-13): only nodes that listen are named. Outbound peers
/// qualify by construction; inbound ones only after a successful probe of the P2P port;
/// private and loopback addresses never appear. The endpoint serves it under both prefixes.
/// </summary>
public class PublicPeersTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 13, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("62.146.173.159:55414", "62.146.173.159", 55414)]
    [InlineData("[2001:db8::1]:55414", "2001:db8::1", 55414)]
    [InlineData("47.198.120.157", "47.198.120.157", 0)]
    [InlineData("2001:db8::2", "2001:db8::2", 0)]
    public void Addresses_parse_with_and_without_ports(string addr, string ip, int port)
    {
        Assert.Equal((ip, port), PublicPeerList.ParseAddress(addr));
    }

    [Theory]
    [InlineData("not-an-address:1")]
    [InlineData("[2001:db8::1")]
    [InlineData("1.2.3.4:notaport")]
    [InlineData("")]
    public void Garbage_does_not_parse(string addr) => Assert.Null(PublicPeerList.ParseAddress(addr));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.10.100.20", false)]
    [InlineData("192.168.60.2", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("62.146.173.159", true)]
    [InlineData("2001:db8::1", true)]
    public void Only_routable_addresses_are_public(string ip, bool expected) => Assert.Equal(expected, PublicPeerList.IsPublicIp(ip));

    [Fact]
    public void Outbound_peers_are_listed_and_inbound_ones_only_when_the_probe_said_yes()
    {
        var peers = new List<RpcPeerInfo>
        {
            FakeDaemonRpc.Peer("54.39.23.245:55414", inbound: false),           // the other relay, dialled by us
            FakeDaemonRpc.Peer("62.146.173.159:41022", inbound: true),          // a public node that dialled us
            FakeDaemonRpc.Peer("217.155.50.155:50112", inbound: true),          // a NAT'd home node: not listening
            FakeDaemonRpc.Peer("86.1.2.3:60001", inbound: true),                // a wallet on a home line, never probed
            FakeDaemonRpc.Peer("127.0.0.1:55416", inbound: false),              // a local daemon
        };
        var verdicts = new Dictionary<string, bool?> { ["62.146.173.159"] = true, ["217.155.50.155"] = false };

        var s = PublicPeerList.Build(peers, ip => verdicts.GetValueOrDefault(ip), 55414, Now);

        Assert.Equal(5, s.Connections);
        Assert.Equal(2, s.HiddenInbound);
        Assert.Equal(["54.39.23.245", "62.146.173.159"], s.Peers.Select(p => p.Ip));
        var confirmed = s.Peers.Single(p => p.Ip == "62.146.173.159");
        Assert.True(confirmed.Inbound);
        Assert.Equal(55414, confirmed.Port);       // the probed P2P port, not the ephemeral source port
        Assert.DoesNotContain(s.Peers, p => p.Ip is "217.155.50.155" or "86.1.2.3" or "127.0.0.1");
    }

    [Fact]
    public void One_entry_per_address_and_the_outbound_observation_wins()
    {
        var peers = new List<RpcPeerInfo>
        {
            FakeDaemonRpc.Peer("62.146.173.159:41022", inbound: true),
            FakeDaemonRpc.Peer("62.146.173.159:55414", inbound: false),
            FakeDaemonRpc.Peer("109.123.234.230", inbound: false),   // outbound with no port in addr
        };
        var s = PublicPeerList.Build(peers, _ => true, 55414, Now);

        Assert.Equal(2, s.Peers.Count);
        Assert.False(s.Peers[0].Inbound);
        Assert.Equal(55414, s.Peers.Single(p => p.Ip == "109.123.234.230").Port);
    }

    [Fact]
    public void Probe_verdicts_expire_and_a_no_expires_sooner_than_a_yes()
    {
        var cache = new ReachabilityCache(TimeSpan.FromHours(12), TimeSpan.FromHours(1));
        cache.Set("1.1.1.1", true, Now);
        cache.Set("2.2.2.2", false, Now);

        Assert.True(cache.Lookup("1.1.1.1", Now.AddHours(11)));
        Assert.Null(cache.Lookup("1.1.1.1", Now.AddHours(13)));
        Assert.False(cache.Lookup("2.2.2.2", Now.AddMinutes(59)));
        Assert.Null(cache.Lookup("2.2.2.2", Now.AddMinutes(61)));
        Assert.Null(cache.Lookup("3.3.3.3", Now));

        cache.Prune(Now.AddHours(25), TimeSpan.FromHours(24));
        Assert.Equal(0, cache.Count);
    }

    private sealed class ScriptedProbe : IReachabilityProbe
    {
        public readonly List<string> Asked = [];
        public HashSet<string> Listening = new(StringComparer.OrdinalIgnoreCase);
        public Task<bool> IsListeningAsync(string ip, int port, TimeSpan timeout, CancellationToken ct)
        {
            Asked.Add($"{ip}:{port}");
            return Task.FromResult(Listening.Contains(ip));
        }
    }

    [Fact]
    public async Task The_observer_probes_each_inbound_address_once_and_lists_what_answered()
    {
        var rpc = new FakeDaemonRpc();
        rpc.Peers.Add(FakeDaemonRpc.Peer("54.39.23.245:55414", inbound: false));
        rpc.Peers.Add(FakeDaemonRpc.Peer("62.146.173.159:41022", inbound: true));
        rpc.Peers.Add(FakeDaemonRpc.Peer("86.1.2.3:60001", inbound: true));
        rpc.Peers.Add(FakeDaemonRpc.Peer("10.10.100.40:55418", inbound: true));   // LAN: never probed
        var probe = new ScriptedProbe { Listening = { "62.146.173.159" } };
        var config = new ConfigurationBuilder().Build();
        var observer = new PublicPeerObserver(rpc, probe, new PublicPeerState(), config, NullLogger<PublicPeerObserver>.Instance);

        var first = await observer.RefreshAsync(CancellationToken.None);
        var second = await observer.RefreshAsync(CancellationToken.None);

        Assert.Equal(["62.146.173.159:55414", "86.1.2.3:55414"], probe.Asked.Order());   // once each, the LAN peer skipped
        Assert.Equal(["54.39.23.245", "62.146.173.159"], first.Peers.Select(p => p.Ip).Order());
        Assert.Equal(1, first.HiddenInbound);    // the public but unreachable home line; the LAN peer is dropped, not hidden
        Assert.Equal(first.Peers.Select(p => p.Ip), second.Peers.Select(p => p.Ip));
    }

    [Fact]
    public async Task The_endpoint_serves_the_listing_under_both_prefixes_and_says_when_it_is_not_ready()
    {
        var state = new PublicPeerState();
        var dbPath = Path.Combine(Path.GetTempPath(), $"lite-gw-peers-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Index:DbPath", dbPath);
            b.UseSetting("Peers:ObserverName", "gw-test");
            b.ConfigureServices(services =>
            {
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType is { } t && (t == typeof(ChainWalkerService) || t == typeof(PublicPeerObserver))).ToList())
                    services.Remove(hosted);
                services.Remove(services.Single(d => d.ServiceType == typeof(DaemonRpcClient)));
                services.AddSingleton<DaemonRpcClient>(new FakeDaemonRpc());
                services.Remove(services.Single(d => d.ServiceType == typeof(PublicPeerState)));
                services.AddSingleton(state);
            });
        });
        try
        {
            var client = factory.CreateClient();

            var notReady = await client.GetFromJsonAsync<JsonElement>("/api/node/peers");
            Assert.False(notReady.GetProperty("ready").GetBoolean());
            Assert.Equal(0, notReady.GetProperty("peers").GetArrayLength());
            Assert.Equal("gw-test", notReady.GetProperty("observer").GetString());

            state.Current = new PublicPeerSnapshot(Now,
                [new PublicPeer("62.146.173.159", 55414, true, 70016, "/Blazecoin:2.1.3/", "0409", 1_794_570_000, 0.03, 0.02, 4_423_000, 4_423_965, 4_423_965, 10, 20)],
                HiddenInbound: 3, Connections: 9);

            foreach (var url in new[] { "/api/node/peers", "/indexer/api/node/peers" })
            {
                var ready = await client.GetFromJsonAsync<JsonElement>(url);
                Assert.True(ready.GetProperty("ready").GetBoolean());
                Assert.Equal(9, ready.GetProperty("connections").GetInt32());
                Assert.Equal(3, ready.GetProperty("hiddenInbound").GetInt32());
                var peer = Assert.Single(ready.GetProperty("peers").EnumerateArray());
                Assert.Equal("62.146.173.159", peer.GetProperty("ip").GetString());
                Assert.Equal(55414, peer.GetProperty("port").GetInt32());
                Assert.True(peer.GetProperty("inbound").GetBoolean());
                Assert.Equal("/Blazecoin:2.1.3/", peer.GetProperty("subVer").GetString());
                Assert.Equal(4_423_965, peer.GetProperty("syncedBlocks").GetInt64());
            }
        }
        finally
        {
            factory.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}

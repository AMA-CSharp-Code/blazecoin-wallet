using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BlazecoinWallet.Lite.Gateway.Rpc;

namespace BlazecoinWallet.Lite.Gateway.Peers;

// Public peer listing (2026-11-13). Why: the website's Network map was built from ONE node's
// getpeerinfo (the home Indexer node), which dials out to a handful of peers and accepts nothing
// in, so a new public node that only ever connected to the relays never appeared (a relay
// operator's US West node, reported in Telegram). Each relay now publishes the peers IT sees.
//
// Privacy rule: only nodes that LISTEN on the P2P port are listed. An outbound connection is
// one we made, so that address listens by definition. An inbound connection may come from a
// desktop wallet's bundled daemon on a home line — such an address is published only after a
// TCP connect to the P2P port succeeds; the rest are counted, never named. The probe is what
// every node does to an address it learns (and what public crawlers do), nothing more.

/// <summary>One listening node as the listing publishes it. Port = the port a node can dial
/// (an inbound peer's source port is ephemeral; its probed P2P port is given instead).</summary>
public sealed record PublicPeer(
    string Ip, int Port, bool Inbound, int Version, string SubVer, string Services, long ConnTime,
    double? PingTime, double? MinPing, long StartingHeight, long SyncedHeaders, long SyncedBlocks,
    long BytesSent, long BytesRecv);

/// <summary>What the relay saw at one moment: the listening peers, how many inbound
/// connections were withheld (not confirmed listening), and the raw connection count.</summary>
public sealed record PublicPeerSnapshot(DateTime TimestampUtc, IReadOnlyList<PublicPeer> Peers, int HiddenInbound, int Connections);

/// <summary>Pure listing rules: no I/O, fully unit-tested.</summary>
public static class PublicPeerList
{
    /// <summary>"1.2.3.4:55414" → (1.2.3.4, 55414); "[2001:db8::1]:55414" → (2001:db8::1, 55414);
    /// a bare address gives port 0. Null when nothing parses as an IP.</summary>
    public static (string Ip, int Port)? ParseAddress(string? addr)
    {
        if (string.IsNullOrWhiteSpace(addr)) return null;
        addr = addr.Trim();
        string host;
        var port = 0;
        if (addr.StartsWith('['))
        {
            var close = addr.IndexOf(']');
            if (close < 0) return null;
            host = addr[1..close];
            var rest = addr[(close + 1)..];
            if (rest.StartsWith(':') && !int.TryParse(rest[1..], out port)) return null;
        }
        else
        {
            var colons = addr.Count(c => c == ':');
            if (colons == 1)
            {
                var i = addr.IndexOf(':');
                host = addr[..i];
                if (!int.TryParse(addr[(i + 1)..], out port)) return null;
            }
            else host = addr; // v4 without a port, or a bare v6
        }
        if (!IPAddress.TryParse(host, out var ip)) return null;
        if (port is < 0 or > 65535) return null;
        return (ip.ToString(), port);
    }

    /// <summary>Routable on the internet: not loopback, private (RFC 1918 / fc00::/7), link-local or unspecified.</summary>
    public static bool IsPublicIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return false;
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (a.IsIPv4MappedToIPv6) return IsPublicIp(a.MapToIPv4().ToString());
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal) return false;
            return true;
        }
        var b = a.GetAddressBytes();
        return !(b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
            || b[0] == 0);
    }

    /// <summary>
    /// Builds the listing from a getpeerinfo answer. <paramref name="reachable"/> answers for an
    /// inbound peer's IP: true = confirmed listening on <paramref name="probePort"/>, false or null
    /// = not confirmed (hidden). One entry per IP: an outbound observation wins over an inbound one
    /// because it carries the port the node really listens on.
    /// </summary>
    public static PublicPeerSnapshot Build(IReadOnlyList<RpcPeerInfo> peers, Func<string, bool?> reachable, int probePort, DateTime nowUtc)
    {
        var byIp = new Dictionary<string, PublicPeer>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var hidden = 0;
        foreach (var p in peers)
        {
            var parsed = ParseAddress(p.Addr);
            if (parsed == null || !IsPublicIp(parsed.Value.Ip)) continue;
            var (ip, port) = parsed.Value;
            if (p.Inbound)
            {
                if (reachable(ip) != true) { hidden++; continue; }
                port = probePort;
            }
            else if (port == 0) port = probePort;

            var entry = new PublicPeer(ip, port, p.Inbound, p.Version, p.SubVer ?? string.Empty, p.Services ?? string.Empty,
                p.ConnTime, p.PingTime, p.MinPing, p.StartingHeight, p.SyncedHeaders, p.SyncedBlocks, p.BytesSent, p.BytesRecv);
            if (!byIp.TryGetValue(ip, out var existing)) { byIp[ip] = entry; order.Add(ip); }
            else if (existing.Inbound && !entry.Inbound) byIp[ip] = entry;
        }
        return new PublicPeerSnapshot(nowUtc, order.Select(ip => byIp[ip]).ToList(), hidden, peers.Count);
    }
}

/// <summary>Remembers probe verdicts so an address is tried once per TTL, not once per refresh.
/// A node that was down may come up, so a "no" is kept for less time than a "yes".</summary>
public sealed class ReachabilityCache(TimeSpan reachableTtl, TimeSpan unreachableTtl)
{
    private readonly ConcurrentDictionary<string, (bool Ok, DateTime At)> _byIp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cached verdict, or null when there is none or it has expired.</summary>
    public bool? Lookup(string ip, DateTime nowUtc)
    {
        if (!_byIp.TryGetValue(ip, out var v)) return null;
        var ttl = v.Ok ? reachableTtl : unreachableTtl;
        return nowUtc - v.At <= ttl ? v.Ok : null;
    }

    public void Set(string ip, bool ok, DateTime nowUtc) => _byIp[ip] = (ok, nowUtc);

    /// <summary>Forgets addresses not probed for <paramref name="maxAge"/> (peers that left).</summary>
    public void Prune(DateTime nowUtc, TimeSpan maxAge)
    {
        foreach (var kv in _byIp)
            if (nowUtc - kv.Value.At > maxAge) _byIp.TryRemove(kv.Key, out _);
    }

    public int Count => _byIp.Count;
}

/// <summary>Can a TCP connection be opened to ip:port? (Connect only; no protocol handshake.)</summary>
public interface IReachabilityProbe
{
    Task<bool> IsListeningAsync(string ip, int port, TimeSpan timeout, CancellationToken ct);
}

public sealed class TcpReachabilityProbe : IReachabilityProbe
{
    public async Task<bool> IsListeningAsync(string ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var address)) return false;
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(address, port, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }
}

/// <summary>The latest listing, for the endpoint to serve. Null until the first refresh.</summary>
public sealed class PublicPeerState
{
    public PublicPeerSnapshot? Current { get; set; }
}

/// <summary>
/// Refreshes the listing every <c>Peers:RefreshSeconds</c> (default 300): reads getpeerinfo,
/// probes inbound addresses whose verdict is missing or expired (bounded concurrency, short
/// timeout), and publishes the result. A failed refresh keeps the previous listing.
/// </summary>
public sealed class PublicPeerObserver : BackgroundService
{
    private readonly DaemonRpcClient _rpc;
    private readonly IReachabilityProbe _probe;
    private readonly PublicPeerState _state;
    private readonly ILogger<PublicPeerObserver> _logger;
    private readonly int _refreshSeconds;
    private readonly int _probePort;
    private readonly TimeSpan _probeTimeout;
    private readonly int _probeConcurrency;
    private readonly ReachabilityCache _cache;

    public PublicPeerObserver(DaemonRpcClient rpc, IReachabilityProbe probe, PublicPeerState state,
        IConfiguration config, ILogger<PublicPeerObserver> logger)
    {
        _rpc = rpc;
        _probe = probe;
        _state = state;
        _logger = logger;
        _refreshSeconds = Math.Max(30, config.GetValue("Peers:RefreshSeconds", 300));
        _probePort = config.GetValue("Peers:ProbePort", 55414);
        _probeTimeout = TimeSpan.FromMilliseconds(Math.Clamp(config.GetValue("Peers:ProbeTimeoutMs", 3000), 500, 15000));
        _probeConcurrency = Math.Clamp(config.GetValue("Peers:ProbeConcurrency", 8), 1, 32);
        _cache = new ReachabilityCache(
            TimeSpan.FromHours(Math.Max(1, config.GetValue("Peers:ReachableTtlHours", 12))),
            TimeSpan.FromHours(Math.Max(1, config.GetValue("Peers:UnreachableTtlHours", 1))));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { _state.Current = await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Public peer listing refresh failed; keeping the previous listing"); }
            try { await Task.Delay(TimeSpan.FromSeconds(_refreshSeconds), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One refresh, exposed for tests: getpeerinfo, probe what needs probing, build.</summary>
    public async Task<PublicPeerSnapshot> RefreshAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var peers = await _rpc.GetPeerInfoAsync(ct);

        var toProbe = peers.Where(p => p.Inbound)
            .Select(p => PublicPeerList.ParseAddress(p.Addr)?.Ip)
            .Where(ip => ip != null && PublicPeerList.IsPublicIp(ip) && _cache.Lookup(ip, now) == null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toProbe.Count > 0)
        {
            using var gate = new SemaphoreSlim(_probeConcurrency);
            await Task.WhenAll(toProbe.Select(async ip =>
            {
                await gate.WaitAsync(ct);
                try { _cache.Set(ip!, await _probe.IsListeningAsync(ip!, _probePort, _probeTimeout, ct), now); }
                finally { gate.Release(); }
            }));
            _logger.LogInformation("Public peer listing: probed {Count} inbound address(es) on port {Port}", toProbe.Count, _probePort);
        }
        _cache.Prune(now, TimeSpan.FromHours(24));

        var snapshot = PublicPeerList.Build(peers, ip => _cache.Lookup(ip, now), _probePort, now);
        _logger.LogDebug("Public peer listing: {Listed} listening of {Connections} connections ({Hidden} inbound withheld)",
            snapshot.Peers.Count, snapshot.Connections, snapshot.HiddenInbound);
        return snapshot;
    }
}

using NBitcoin;
using NBitcoin.Protocol;

namespace BlazecoinWallet.Lite.Data;

/// <summary>Seam for the P2P push so the send pipeline is testable without sockets.</summary>
public interface IP2PBroadcaster
{
    /// <summary>Returns the endpoint that accepted delivery, or null when none was reachable.</summary>
    Task<string?> TryBroadcastAsync(string rawTxHex, IEnumerable<string>? nodeEndpoints = null,
        TimeSpan? perNodeTimeout = null, CancellationToken ct = default);
}

/// <summary>
/// Last-resort broadcast over the raw Blazecoin P2P protocol (magic fb c0 b6 db): when
/// every gateway is unreachable, a signed transaction is pushed directly to any LISTENING
/// full node — the chain's seed nodes, or a desktop wallet with its P2P port open. This
/// keeps the SEND path alive through a total backend outage; balance/history reads still
/// need an indexer. Fire-and-forget semantics: a node that accepts the tx relays it
/// network-wide, so one successful push is enough.
/// </summary>
public sealed class P2PBroadcaster : IP2PBroadcaster
{
    /// <summary>The chain's hardcoded seed nodes (chainparams vSeeds equivalents).</summary>
    public static readonly string[] DefaultSeedNodes =
    {
        "85.15.179.171:55414",
        "91.206.16.214:55414",
    };

    private readonly Network _network;

    public P2PBroadcaster(Network? network = null) => _network = network ?? BlazecoinNetwork.Instance;

    /// <summary>
    /// Tries each endpoint in turn: TCP connect → version handshake → push the tx → ping/pong
    /// (proves the node processed our queue before we hang up). Returns the endpoint that
    /// accepted delivery, or null when every node was unreachable.
    /// </summary>
    public async Task<string?> TryBroadcastAsync(
        string rawTxHex,
        IEnumerable<string>? nodeEndpoints = null,
        TimeSpan? perNodeTimeout = null,
        CancellationToken ct = default)
    {
        var tx = Transaction.Parse(rawTxHex, _network);
        var timeout = perNodeTimeout ?? TimeSpan.FromSeconds(10);

        foreach (var endpoint in nodeEndpoints ?? DefaultSeedNodes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                using var node = await Task.Run(() => Node.Connect(_network, endpoint), cts.Token);
                node.VersionHandshake(cts.Token);

                // Unsolicited `tx` is accepted by Core-lineage nodes; the ping/pong round
                // trip afterwards guarantees the tx message was consumed, not left in a
                // socket buffer we abandon.
                using var listener = node.CreateListener();
                node.SendMessage(new TxPayload(tx));
                var nonce = (ulong)Random.Shared.NextInt64();
                node.SendMessage(new PingPayload { Nonce = nonce });
                while (listener.ReceivePayload<PongPayload>(cts.Token).Nonce != nonce) { }

                return endpoint;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — stop probing
            }
            catch
            {
                // Unreachable / handshake refused / timed out — try the next node.
            }
        }
        return null;
    }
}

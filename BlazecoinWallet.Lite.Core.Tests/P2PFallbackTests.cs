using System.Net;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The send pipeline's P2P fallback contract: it fires ONLY when the gateway relay was
/// UNREACHABLE (delivery failure — nobody judged the tx), never when the network REJECTED
/// the transaction, and a successful push reports Via="p2p" with our locally-computed txid.
/// </summary>
public class P2PFallbackTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    private sealed class FakeP2P : IP2PBroadcaster
    {
        public string? AcceptingNode = "seed1:55414";
        public int Calls;
        public string? LastHex;
        public bool Throw;
        public Task<string?> TryBroadcastAsync(string rawTxHex, IEnumerable<string>? nodes = null,
            TimeSpan? perNodeTimeout = null, CancellationToken ct = default)
        {
            Calls++;
            LastHex = rawTxHex;
            if (Throw) throw new IOException("socket closed");
            return Task.FromResult(AcceptingNode);
        }
    }

    private sealed class ScriptedData : ILiteWalletData
    {
        public bool SupportsHistory => true;
        public LiteBroadcastResult BroadcastResult = new(false, null, "down", BroadcastFailureKind.Unreachable);
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) =>
            Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiteChainUtxo>>(
                [new LiteChainUtxo(new string('7', 64), 0, 10_000_000, 3, 100, false)]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public bool SupportsChainVerification => false;
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default) =>
            Task.FromResult(BroadcastResult);
    }

    private static async Task<(LiteWalletService svc, ScriptedData data, FakeP2P p2p, string dest)> BuildAsync()
    {
        var data = new ScriptedData();
        var p2p = new FakeP2P();
        var svc = new LiteWalletService(new MemoryVault(), data, p2p, ["seed1:55414"]);
        await svc.RestoreWalletAsync(TestMnemonic);
        return (svc, data, p2p, LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5));
    }

    [Fact]
    public async Task Unreachable_gateways_trigger_the_p2p_fallback()
    {
        var (svc, _, p2p, dest) = await BuildAsync();

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.True(result.Success);
        Assert.Equal("p2p", result.Via);
        Assert.Equal(64, result.TxId!.Length);
        Assert.Equal(1, p2p.Calls);
        Assert.NotNull(p2p.LastHex); // the SIGNED tx went over the wire
    }

    [Fact]
    public async Task A_rejection_never_goes_to_p2p()
    {
        var (svc, data, p2p, dest) = await BuildAsync();
        data.BroadcastResult = new LiteBroadcastResult(false, null, "bad-txns-inputs-missingorspent", BroadcastFailureKind.Rejected);

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.False(result.Success);
        Assert.Equal(0, p2p.Calls); // the network judged it — retrying elsewhere would be wrong
    }

    [Fact]
    public async Task No_reachable_p2p_node_leaves_the_original_error()
    {
        var (svc, _, p2p, dest) = await BuildAsync();
        p2p.AcceptingNode = null;

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.False(result.Success);
        Assert.Equal("down", result.Error);
        Assert.Equal(1, p2p.Calls);
    }

    // ════════ one gateway's word is not a delivery (bug-bounty report 2026-11-09) ════════
    // When a single gateway says "accepted" or "already known" and no second gateway could be
    // asked, the pipeline pushed the transaction to network nodes itself — and then ignored
    // whether anyone took it. The receipt read "Sent!" on that one gateway's word alone. Now a
    // send nobody else confirmed says so.

    private static LiteBroadcastResult AloneDuplicate =>
        LiteBroadcastResult.Fail("txn-already-known", BroadcastFailureKind.Duplicate, uncorroborated: true);

    [Fact]
    public async Task A_lone_duplicate_claim_that_no_node_backs_is_a_receipt_marked_unconfirmed()
    {
        var (svc, data, p2p, dest) = await BuildAsync();
        data.BroadcastResult = AloneDuplicate;                 // a claim about a transaction that was never broadcast
        p2p.AcceptingNode = null;                              // and no network node took it from us

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.True(result.Success);                           // it may be genuine: an earlier attempt whose answer was lost
        Assert.True(result.Unconfirmed);                       // was: an ordinary success
        Assert.Equal(1, p2p.Calls);
        Assert.Equal(64, result.TxId!.Length);
    }

    [Fact]
    public async Task A_lone_acceptance_that_no_node_backs_is_marked_unconfirmed_too()
    {
        var (svc, data, p2p, dest) = await BuildAsync();
        data.BroadcastResult = LiteBroadcastResult.Ok(new string('9', 64), uncorroborated: true);
        p2p.AcceptingNode = null;

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.True(result.Success);
        Assert.True(result.Unconfirmed);
        Assert.Equal(1, p2p.Calls);
    }

    [Theory]
    [InlineData(true)]    // the lone gateway said "already known"
    [InlineData(false)]   // the lone gateway said "accepted"
    public async Task A_lone_gateway_backed_by_a_network_node_is_a_plain_success(bool duplicate)
    {
        var (svc, data, p2p, dest) = await BuildAsync();
        data.BroadcastResult = duplicate ? AloneDuplicate : LiteBroadcastResult.Ok(new string('9', 64), uncorroborated: true);

        var result = await svc.SendAsync(dest, 1_000_000);    // the fake node accepts

        Assert.True(result.Success);
        Assert.False(result.Unconfirmed);                      // a network node took it from us directly
        Assert.Equal(1, p2p.Calls);
    }

    [Fact]
    public async Task Two_gateways_agreeing_need_no_second_opinion()
    {
        var (svc, data, p2p, dest) = await BuildAsync();
        data.BroadcastResult = LiteBroadcastResult.Ok(new string('9', 64));

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.True(result.Success);
        Assert.False(result.Unconfirmed);
        Assert.Equal(0, p2p.Calls);
    }

    [Fact]
    public async Task A_direct_push_that_throws_leaves_the_receipt_unconfirmed()
    {
        var (svc, data, p2p, dest) = await BuildAsync();
        data.BroadcastResult = AloneDuplicate;
        p2p.Throw = true;

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.True(result.Success);
        Assert.True(result.Unconfirmed);
    }

    [Fact]
    public async Task With_no_way_to_reach_network_nodes_a_lone_gateway_is_always_unconfirmed()
    {
        // The web wallet: a browser has no raw sockets, so there is no direct push at all.
        var data = new ScriptedData { BroadcastResult = AloneDuplicate };
        var svc = new LiteWalletService(new MemoryVault(), data);
        await svc.RestoreWalletAsync(TestMnemonic);

        var result = await svc.SendAsync(LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(5), 1_000_000);

        Assert.True(result.Success);
        Assert.True(result.Unconfirmed);
    }

    [Fact]
    public async Task A_send_that_went_out_only_over_p2p_is_not_marked_unconfirmed()
    {
        var (svc, _, _, dest) = await BuildAsync();          // gateways unreachable, a node accepts

        var result = await svc.SendAsync(dest, 1_000_000);

        Assert.Equal("p2p", result.Via);
        Assert.False(result.Unconfirmed);
    }
}

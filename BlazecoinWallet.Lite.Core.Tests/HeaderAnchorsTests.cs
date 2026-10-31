using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using NBitcoin;

namespace BlazecoinWallet.Lite.Core.Tests;

/// <summary>
/// Chain membership for merkle proofs (bug-bounty report 2026-10-25): a proof's block counts
/// only when it is PROVEN to be the chain's block at its claimed height — its hash linked
/// forward, header by header, to a trusted anchor. Every positive assertion runs against REAL
/// mainnet data: the shipped anchor table, the harvested header run 4,149,841…4,150,167 and the
/// real coinbase proof in block 4,149,841 (see <see cref="HeaderChainFixture"/>); the shipped
/// anchor at 4,150,000 sits 159 headers above that block.
/// </summary>
public class HeaderAnchorsTests
{
    private const long AnchorAbove = 4_150_000; // the shipped anchor the fixture run passes through
    private static readonly string AnchorAboveHash =
        BlockHash(HeaderChainFixture.Headers[AnchorAbove - (HeaderChainFixture.CheckpointHeight + 1)]);

    private static HeaderCheckpoint Checkpoint =>
        new(HeaderChainFixture.CheckpointHeight, HeaderChainFixture.CheckpointHash, HeaderChainFixture.CheckpointBits);

    // ── The shipped table ───────────────────────────────────────────────────────────────

    [Fact]
    public void Shipped_table_starts_at_genesis_and_matches_the_real_chain()
    {
        var anchors = HeaderAnchors.Mainnet;

        Assert.Equal(HeaderAnchors.MainnetInterval, anchors.Interval);
        // src/kernel/chainparams.cpp mainnet genesis.
        Assert.Equal("5d871c1b6ea542c2bb8a3b3ac70028a591bbf81369e90c2446c1a2bbfb89459b", anchors.HashAt(0));
        // An independent check of the harvest: the real header at 4,150,000 hashes to the table's entry.
        Assert.Equal(AnchorAboveHash, anchors.HashAt(AnchorAbove));
        // It must reach the shipped checkpoint (the checkpoint is itself an anchor at release), or
        // upgraded wallets would have no nearby anchor.
        Assert.True(anchors.MaxHeight >= HeaderChainSync.MainnetCheckpoint.Height);
    }

    [Fact]
    public void Anchor_lookup_arithmetic()
    {
        var anchors = HeaderAnchors.Mainnet.UpTo(5_000);

        Assert.Equal(5_000, anchors.MaxHeight);
        Assert.Equal(0, anchors.NextAtOrAbove(0));
        Assert.Equal(1_000, anchors.NextAtOrAbove(1));
        Assert.Equal(1_000, anchors.NextAtOrAbove(1_000));
        Assert.Equal(5_000, anchors.NextAtOrAbove(4_001));
        Assert.Null(anchors.NextAtOrAbove(5_001));
        Assert.Null(anchors.HashAt(1_500));   // not an anchor height
        Assert.Null(anchors.HashAt(6_000));   // past the table
        Assert.Equal(-1, HeaderAnchors.Mainnet.UpTo(-1).MaxHeight);
    }

    // ── LocateAsync ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_real_block_is_proven_on_chain_by_linking_to_the_next_anchor()
    {
        // No sync has run: the block is found purely by linking 159 headers to the shipped anchor.
        var feed = new CountingFeed();
        var sync = new HeaderChainSync(feed, new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);

        var where = await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(ChainMembership.OnChain, where.Membership);
        Assert.Equal(HeaderChainFixture.ProofBlockHeight, where.Height);
        Assert.Equal(AnchorAbove, feed.HighestServed); // walked to the anchor, no further

        // Proven blocks are cached: asking again costs no header fetch.
        feed.Calls = 0;
        Assert.Equal(ChainMembership.OnChain, (await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight)).Membership);
        Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task A_lie_about_the_height_is_caught()
    {
        // The real block, but claimed one height up: the next header doesn't build on it.
        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);

        var where = await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight + 1);

        Assert.Equal(ChainMembership.NotOnChain, where.Membership);
    }

    [Fact]
    public async Task A_forged_block_is_not_on_chain_whatever_its_own_pow()
    {
        // The report's attack: a header ground to meet its own target, claimed at a real height.
        // Its hash is not the chain's block there, so the link fails — PoW never enters into it.
        var forged = new string('7', 64);
        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);

        var where = await sync.LocateAsync(forged, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(ChainMembership.NotOnChain, where.Membership);
    }

    [Fact]
    public async Task A_forged_block_claimed_exactly_at_an_anchor_height_is_not_on_chain()
    {
        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);

        Assert.Equal(ChainMembership.NotOnChain, (await sync.LocateAsync(new string('7', 64), AnchorAbove)).Membership);
        Assert.Equal(ChainMembership.OnChain, (await sync.LocateAsync(AnchorAboveHash, AnchorAbove)).Membership);
    }

    [Fact]
    public async Task No_verdict_without_a_feed_a_height_or_working_headers()
    {
        var noFeed = new HeaderChainSync(null, new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        Assert.Equal(ChainMembership.CannotCheck, (await noFeed.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight)).Membership);

        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        Assert.Equal(ChainMembership.CannotCheck, (await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, 0)).Membership);

        var broken = new HeaderChainSync(new CountingFeed { Available = 0 }, new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        Assert.Equal(ChainMembership.CannotCheck, (await broken.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight)).Membership);
    }

    [Fact]
    public async Task Above_everything_trusted_a_short_catch_up_runs_first()
    {
        // Table ends below the block and the tip is the checkpoint: the block is newer than
        // anything trusted. It's within a short catch-up, so the sync runs and places it.
        var anchors = HeaderAnchors.Mainnet.UpTo(4_149_000);
        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint, anchors);

        var where = await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(ChainMembership.OnChain, where.Membership);
        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
    }

    [Fact]
    public async Task Above_everything_trusted_with_no_new_headers_is_not_yet_verified()
    {
        var anchors = HeaderAnchors.Mainnet.UpTo(4_149_000);
        var sync = new HeaderChainSync(new CountingFeed { Available = 0 }, new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint, anchors);

        var where = await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(ChainMembership.NotYetVerified, where.Membership);
    }

    [Fact]
    public async Task Anchors_learned_by_the_sync_survive_a_restart()
    {
        // A table older than the wallet's chain: the sync verifies 4,150,000 and learns it.
        var anchors = HeaderAnchors.Mainnet.UpTo(4_149_000);
        var store = new InMemoryWalletStateStore();
        var first = new HeaderChainSync(new CountingFeed(), store, BlazecoinNetwork.Instance, Checkpoint, anchors);
        await first.SyncAsync();
        Assert.Contains($"{AnchorAbove}:{AnchorAboveHash}", await store.GetHeaderAnchorsAsync());

        // Restart: a fresh instance over the same store has only the persisted tip in memory, yet
        // links the block to the learned anchor — not all the way to the tip.
        var feed = new CountingFeed();
        var restarted = new HeaderChainSync(feed, store, BlazecoinNetwork.Instance, Checkpoint, anchors);

        var where = await restarted.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(ChainMembership.OnChain, where.Membership);
        Assert.Equal(AnchorAbove, feed.HighestServed);
    }

    [Fact]
    public async Task A_link_to_the_tip_backfills_the_anchors_it_passes()
    {
        // An upgraded wallet: a persisted tip far past a table that has no anchors near it, and
        // no learned anchors yet. The first check links to the tip and learns 4,150,000 on the way.
        var anchors = HeaderAnchors.Mainnet.UpTo(4_149_000);
        var synced = new InMemoryWalletStateStore();
        await new HeaderChainSync(new CountingFeed(), synced, BlazecoinNetwork.Instance, Checkpoint, anchors).SyncAsync();
        var upgraded = new InMemoryWalletStateStore();
        await upgraded.SetVerifiedCheckpointAsync((await synced.GetVerifiedCheckpointAsync())!);

        var sync = new HeaderChainSync(new CountingFeed(), upgraded, BlazecoinNetwork.Instance, Checkpoint, anchors);
        var where = await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(ChainMembership.OnChain, where.Membership);
        await Task.Yield(); // the anchor write is fire-and-forget (synchronous for the in-memory store)
        Assert.Contains($"{AnchorAbove}:{AnchorAboveHash}", await upgraded.GetHeaderAnchorsAsync());
    }

    // ── Reorg recovery ──────────────────────────────────────────────────────────────────

    private const long OrphanHeight = 4_150_100; // a height inside the fixture run, above the anchor
    private static string RealHashAt(long height) =>
        BlockHash(HeaderChainFixture.Headers[height - (HeaderChainFixture.CheckpointHeight + 1)]);

    [Fact]
    public async Task An_orphaned_tip_is_rewound_to_the_anchor_below_and_resynced()
    {
        // The wallet restarts holding a verified tip whose block the chain has since dropped
        // (a persisted record with a hash the feed no longer serves at that height). Without
        // recovery the first header would never link and the chain would stall there for ever.
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCheckpointAsync($"{OrphanHeight}:{new string('7', 64)}:{HeaderChainFixture.CheckpointBits}");
        var feed = new CountingFeed();
        var sync = new HeaderChainSync(feed, store, BlazecoinNetwork.Instance, Checkpoint);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        Assert.Equal(RealHashAt(OrphanHeight), sync.VerifiedHashAt(OrphanHeight));
        Assert.Null(sync.VerifiedHeightOf(new string('7', 64)));
        Assert.StartsWith($"{HeaderChainFixture.TipHeight}:", await store.GetVerifiedCheckpointAsync());
        // Rewound to the shipped anchor at 4,150,000, then forward — never further back.
        Assert.Equal(AnchorAbove, feed.LowestServed);
    }

    [Fact]
    public async Task A_canonical_tip_is_not_rewound_when_the_feed_serves_a_bad_next_header()
    {
        var real = HeaderChainFixture.Headers[OrphanHeight - (HeaderChainFixture.CheckpointHeight + 1)];
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCheckpointAsync($"{OrphanHeight}:{RealHashAt(OrphanHeight)}:{ParseBits(real)}");
        // The feed's header at the next height is a real header from elsewhere in the run: valid
        // on its own, but it does not build on our tip.
        var feed = new CountingFeed { Override = { [OrphanHeight + 1] = HeaderChainFixture.Headers[300] } };
        var sync = new HeaderChainSync(feed, store, BlazecoinNetwork.Instance, Checkpoint);

        await sync.SyncAsync();

        Assert.Equal(OrphanHeight, sync.VerifiedTipHeight); // stalled honestly at the last good height
        Assert.Equal(RealHashAt(OrphanHeight), sync.VerifiedHashAt(OrphanHeight));
    }

    [Fact]
    public async Task Locate_heals_a_stale_entry_instead_of_calling_the_real_block_a_lie()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCheckpointAsync($"{OrphanHeight}:{new string('7', 64)}:{HeaderChainFixture.CheckpointBits}");
        var sync = new HeaderChainSync(new CountingFeed(), store, BlazecoinNetwork.Instance, Checkpoint);

        // No sync has run: the wallet's own entry at this height is the stale orphan.
        var where = await sync.LocateAsync(RealHashAt(OrphanHeight), OrphanHeight);

        Assert.Equal(ChainMembership.OnChain, where.Membership);
        Assert.Equal(OrphanHeight, where.Height);
    }

    // ── LiteTxVerifier on top ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_path_accepts_a_real_coin_proven_on_chain()
    {
        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        var verifier = new LiteTxVerifier(new RealProofReader(), BlazecoinNetwork.Instance, sync);

        var v = await verifier.VerifyAsync(HeaderChainFixture.ProofTxId, 0, 0, requireInclusionProof: true,
            claimedBlockHeight: HeaderChainFixture.ProofBlockHeight);

        Assert.True(v.Verified, v.Error);
        Assert.True(v.IncludedInBlock);
        Assert.True(v.RealSatoshis > 0);
    }

    [Theory]
    [InlineData(0L, "Could not check")]                  // no height claim → no proof of membership
    [InlineData(4_149_842L, "not on your verified chain")] // a lie about the height
    public async Task Send_path_refuses_a_coin_it_cannot_prove_on_chain(long claimedHeight, string why)
    {
        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        var verifier = new LiteTxVerifier(new RealProofReader(), BlazecoinNetwork.Instance, sync);

        var v = await verifier.VerifyAsync(HeaderChainFixture.ProofTxId, 0, 0, requireInclusionProof: true,
            claimedBlockHeight: claimedHeight);

        Assert.False(v.Verified);
        Assert.False(v.IncludedInBlock);
        Assert.Contains(why, v.Error);
    }

    [Fact]
    public async Task Tx_page_check_places_an_unsynced_block_and_flags_a_contradiction()
    {
        var sync = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        var verifier = new LiteTxVerifier(new RealProofReader(), BlazecoinNetwork.Instance, sync);

        var real = await verifier.VerifyChainInclusionAsync(HeaderChainFixture.ProofTxId, HeaderChainFixture.ProofBlockHeight);
        Assert.True(real.OnVerifiedChain);
        Assert.Equal(HeaderChainFixture.ProofBlockHeight, real.Height);
        Assert.False(real.OffChain);

        var fresh = new HeaderChainSync(new CountingFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        var lied = await new LiteTxVerifier(new RealProofReader(), BlazecoinNetwork.Instance, fresh)
            .VerifyChainInclusionAsync(HeaderChainFixture.ProofTxId, HeaderChainFixture.ProofBlockHeight + 1);
        Assert.False(lied.OnVerifiedChain);
        Assert.True(lied.OffChain);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    private static uint ParseBits(string headerHex)
    {
        var factory = BlazecoinNetwork.Instance.Consensus.ConsensusFactory;
        var header = factory.CreateBlockHeader();
        header.ReadWrite(new BitcoinStream(NBitcoin.DataEncoders.Encoders.Hex.DecodeData(headerHex)) { ConsensusFactory = factory });
        return header.Bits.ToCompact();
    }

    private static string BlockHash(string headerHex)
    {
        var factory = BlazecoinNetwork.Instance.Consensus.ConsensusFactory;
        var header = factory.CreateBlockHeader();
        header.ReadWrite(new BitcoinStream(NBitcoin.DataEncoders.Encoders.Hex.DecodeData(headerHex)) { ConsensusFactory = factory });
        return header.GetHash().ToString();
    }

    /// <summary>Serves the fixture headers by height and records how far up it was asked.</summary>
    private sealed class CountingFeed : IHeaderReader
    {
        private const long Base = HeaderChainFixture.CheckpointHeight + 1;
        public int Available = HeaderChainFixture.Headers.Length;
        public int Calls;
        public long HighestServed;
        public long LowestServed = long.MaxValue;
        /// <summary>Headers served in place of the real one at a height (a misbehaving feed).</summary>
        public Dictionary<long, string> Override { get; } = [];

        public Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default)
        {
            Calls++;
            var start = (int)(fromHeight - Base);
            if (start < 0 || start >= Available) return Task.FromResult<IReadOnlyList<string>>([]);
            var served = HeaderChainFixture.Headers.Skip(start).Take(Math.Min(count, Available - start)).ToList();
            for (var i = 0; i < served.Count; i++)
                if (Override.TryGetValue(fromHeight + i, out var replaced)) served[i] = replaced;
            HighestServed = Math.Max(HighestServed, fromHeight + served.Count - 1);
            LowestServed = Math.Min(LowestServed, fromHeight);
            return Task.FromResult<IReadOnlyList<string>>(served);
        }
    }

    /// <summary>Serves the real fixture coinbase and its real merkle proof.</summary>
    private sealed class RealProofReader : IChainReader
    {
        private static bool Is(string txId) => string.Equals(txId, HeaderChainFixture.ProofTxId, StringComparison.OrdinalIgnoreCase);
        public bool SupportsHistory => true;
        public bool SupportsChainVerification => true;
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) =>
            Task.FromResult<string?>(Is(txId) ? HeaderChainFixture.ProofRawTx : null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) =>
            Task.FromResult<string?>(Is(txId) ? HeaderChainFixture.ProofHex : null);
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
    }
}

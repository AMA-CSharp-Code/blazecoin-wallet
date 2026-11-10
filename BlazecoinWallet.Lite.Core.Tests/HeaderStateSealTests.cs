using BlazecoinWallet.Lite.Core.Tests;
using BlazecoinWallet.Lite.Data;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Audit 2026-11-09: the verified header tip and the learned anchors are kept in ordinary app
/// storage (browser localStorage, Android preferences), and were adopted from it on their FORMAT
/// alone — the same storage whose remembered coins were sealed on 2026-11-09. They are what a
/// proof's block is linked to, so a well-formed line written there by anything else became the
/// chain the wallet checked proofs against. They are now adopted only under a seal keyed from the
/// wallet's own seed; anything else is ignored and the wallet re-syncs from the shipped checkpoint.
/// </summary>
public class HeaderStateSealTests
{
    private static HeaderCheckpoint Checkpoint =>
        new(HeaderChainFixture.CheckpointHeight, HeaderChainFixture.CheckpointHash, HeaderChainFixture.CheckpointBits);

    private const long ForgedHeight = HeaderChainFixture.CheckpointHeight + 5_000;
    private static readonly string ForgedHash = new('7', 64);
    /// <summary>A checkpoint line of exactly the stored shape that the wallet never wrote.</summary>
    private static readonly string ForgedCheckpoint = $"{ForgedHeight}:{ForgedHash}:{HeaderChainFixture.CheckpointBits}";

    private sealed class Vault(string? words = null) : ISeedVault
    {
        public string? Words = words ?? MinedBlockFixture.Mnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Words != null);
        public Task SaveMnemonicAsync(string m) { Words = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Words);
        public Task ClearAsync() { Words = null; return Task.CompletedTask; }
    }

    private sealed class NoChain : ILiteWalletData
    {
        public bool SupportsHistory => false;
        public bool SupportsChainVerification => false;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default) => Task.FromResult(LiteBroadcastResult.Fail("unused", BroadcastFailureKind.Unreachable));
    }

    /// <summary>Serves the first <see cref="Available"/> real fixture headers by height.</summary>
    private sealed class Feed : IHeaderReader
    {
        public int Available = HeaderChainFixture.Headers.Length;
        /// <summary>The highest height served so far (0 = nothing yet).</summary>
        public long HighestServed;
        public Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default)
        {
            var start = (int)(fromHeight - (HeaderChainFixture.CheckpointHeight + 1));
            if (start < 0 || start >= Available) return Task.FromResult<IReadOnlyList<string>>([]);
            var served = HeaderChainFixture.Headers.Skip(start).Take(Math.Min(count, Available - start)).ToList();
            HighestServed = Math.Max(HighestServed, fromHeight + served.Count - 1);
            return Task.FromResult<IReadOnlyList<string>>(served);
        }
    }

    private static DateTimeOffset FixtureTime()
    {
        var factory = BlazecoinNetwork.Instance.Consensus.ConsensusFactory;
        var header = factory.CreateBlockHeader();
        header.ReadWrite(new BitcoinStream(Encoders.Hex.DecodeData(HeaderChainFixture.Headers[^1])) { ConsensusFactory = factory });
        return header.BlockTime;
    }

    /// <summary>A different wallet from the fixture's.</summary>
    private const string OtherWallet =
        "legal winner thank year wave sausage worth useful legal winner thank yellow";

    /// <summary>An anchor table that stops below the fixture run, so the sync LEARNS the anchor at 4,150,000.</summary>
    private static HeaderAnchors OldTable => HeaderAnchors.Mainnet.UpTo(4_149_000);
    private const long LearnedAnchor = 4_150_000;

    /// <summary>One session: a wallet service and the header chain it is wired to, over a store.</summary>
    private static (LiteWalletService Service, HeaderChainSync Chain) Session(IWalletStateStore store, Feed feed,
        string? words = null, HeaderAnchors? anchors = null)
    {
        var chain = new HeaderChainSync(feed, store, BlazecoinNetwork.Instance, Checkpoint, anchors) { Clock = FixtureTime };
        var service = new LiteWalletService(new Vault(words), new NoChain(), stateStore: store, headerSync: chain);
        return (service, chain);
    }

    /// <summary>A store this wallet has synced the whole fixture run into, in an earlier session.</summary>
    private static async Task<InMemoryWalletStateStore> SyncedStoreAsync(HeaderAnchors? anchors = null)
    {
        var store = new InMemoryWalletStateStore();
        var (service, chain) = Session(store, new Feed(), anchors: anchors);
        Assert.True(await service.UnlockAsync());
        await chain.SyncAsync();
        Assert.Equal(HeaderChainFixture.TipHeight, chain.VerifiedTipHeight);
        return store;
    }

    // ── what is not the wallet's own is not adopted ───────────────────────────────────────

    [Fact]
    public async Task A_checkpoint_the_wallet_did_not_write_is_not_adopted()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCheckpointAsync(ForgedCheckpoint);
        var (service, chain) = Session(store, new Feed { Available = 0 });
        Assert.True(await service.UnlockAsync());

        await chain.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, chain.VerifiedTipHeight);   // was ForgedHeight: adopted on its format alone
        Assert.Null(chain.VerifiedHashAt(ForgedHeight));
    }

    [Fact]
    public async Task Anchors_the_wallet_did_not_write_are_not_adopted()
    {
        // The stored tip is the wallet's own; the anchors line beside it has been replaced with a
        // well-formed one naming a made-up block at 4,150,000 — the hash a proof's block is linked to.
        var store = await SyncedStoreAsync(OldTable);
        await store.SetHeaderAnchorsAsync($"{LearnedAnchor}:{ForgedHash}");
        var feed = new Feed();
        var (service, chain) = Session(store, feed, anchors: OldTable);
        Assert.True(await service.UnlockAsync());

        var real = await chain.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(ChainMembership.OnChain, real.Membership);           // was NotOnChain: the real block "contradicted" the forged anchor
        Assert.Equal(HeaderChainFixture.TipHeight, feed.HighestServed);   // linked to the tip — the forged anchor is not there
    }

    [Fact]
    public async Task A_sealed_tip_altered_afterwards_is_not_adopted()
    {
        var store = await SyncedStoreAsync();
        await store.SetVerifiedCheckpointAsync(Altered((await store.GetVerifiedCheckpointAsync())!, $"{HeaderChainFixture.TipHeight}:"));
        var (service, chain) = Session(store, new Feed { Available = 0 });
        Assert.True(await service.UnlockAsync());

        await chain.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, chain.VerifiedTipHeight);
    }

    [Fact]
    public async Task Sealed_anchors_altered_afterwards_are_dropped_whole_and_the_tip_still_stands()
    {
        var store = await SyncedStoreAsync(OldTable);
        var stored = (await store.GetHeaderAnchorsAsync())!;
        Assert.Contains($"{LearnedAnchor}:", stored);
        await store.SetHeaderAnchorsAsync(Altered(stored, $"{LearnedAnchor}:"));
        var feed = new Feed();
        var (service, chain) = Session(store, feed, anchors: OldTable);
        Assert.True(await service.UnlockAsync());

        var real = await chain.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(HeaderChainFixture.TipHeight, chain.VerifiedTipHeight);   // the tip's own seal opened
        Assert.Equal(ChainMembership.OnChain, real.Membership);
        Assert.Equal(HeaderChainFixture.TipHeight, feed.HighestServed);        // no anchor adopted: linked all the way to the tip
    }

    /// <summary>The stored text with the first hash character after <paramref name="marker"/> changed — the seal left as it was.</summary>
    private static string Altered(string stored, string marker)
    {
        var at = stored.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return stored[..at] + (stored[at] == '0' ? '1' : '0') + stored[(at + 1)..];
    }

    [Fact]
    public async Task Another_wallets_stored_chain_is_not_adopted()
    {
        var store = await SyncedStoreAsync();
        var (service, chain) = Session(store, new Feed { Available = 0 }, OtherWallet);
        Assert.True(await service.UnlockAsync());

        await chain.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, chain.VerifiedTipHeight);
    }

    // ── what is the wallet's own still saves the work ─────────────────────────────────────

    [Fact]
    public async Task The_wallets_own_stored_chain_is_adopted_after_a_restart_and_is_never_a_bare_line()
    {
        var store = await SyncedStoreAsync(OldTable);

        Assert.Null(HeaderCheckpoint.TryParse(await store.GetVerifiedCheckpointAsync()));   // not the bare "height:hash:bits" form
        Assert.Contains(";m:", await store.GetVerifiedCheckpointAsync());
        Assert.Contains(";m:", await store.GetHeaderAnchorsAsync());

        var feed = new Feed();
        var (service, chain) = Session(store, feed, anchors: OldTable);
        Assert.True(await service.UnlockAsync());
        var real = await chain.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight);

        Assert.Equal(HeaderChainFixture.TipHeight, chain.VerifiedTipHeight);   // the stored tip — no sync has run in this session
        Assert.Equal(ChainMembership.OnChain, real.Membership);
        Assert.Equal(LearnedAnchor, feed.HighestServed);                       // linked to the stored anchor, not on to the tip
    }

    // ── no key, no stored state ───────────────────────────────────────────────────────────

    [Fact]
    public async Task While_the_wallet_is_locked_nothing_stored_is_adopted_and_it_is_adopted_once_unlocked()
    {
        var store = await SyncedStoreAsync();
        var (service, chain) = Session(store, new Feed { Available = 0 });

        await chain.SyncAsync();                                              // kicked before the wallet was unlocked
        Assert.Equal(HeaderChainFixture.CheckpointHeight, chain.VerifiedTipHeight);

        Assert.True(await service.UnlockAsync());
        await chain.SyncAsync();
        Assert.Equal(HeaderChainFixture.TipHeight, chain.VerifiedTipHeight);
    }

    [Fact]
    public async Task While_the_wallet_is_locked_nothing_is_written_and_what_was_stored_is_left_alone()
    {
        var empty = new InMemoryWalletStateStore();
        var (_, verifying) = Session(empty, new Feed());
        await verifying.SyncAsync();                                          // locked: the run is verified in memory only
        Assert.Equal(HeaderChainFixture.TipHeight, verifying.VerifiedTipHeight);
        Assert.Null(await empty.GetVerifiedCheckpointAsync());

        // A locked session that verifies less than was stored does not write over it.
        var store = await SyncedStoreAsync();
        var before = await store.GetVerifiedCheckpointAsync();
        var (_, shorter) = Session(store, new Feed { Available = 100 });
        await shorter.SyncAsync();
        Assert.Equal(HeaderChainFixture.CheckpointHeight + 100, shorter.VerifiedTipHeight);
        Assert.Equal(before, await store.GetVerifiedCheckpointAsync());
    }

    [Fact]
    public async Task A_wallet_unlocked_mid_session_does_not_write_over_a_stored_chain_it_has_not_read()
    {
        // The sync started while locked (nothing adopted), and the wallet is unlocked by the time
        // its run is committed: the shorter chain of this session must not replace the stored one.
        var store = await SyncedStoreAsync();
        var before = await store.GetVerifiedCheckpointAsync();
        var (service, chain) = Session(store, new Feed { Available = 100 });
        var unlocked = false;
        chain.Clock = () =>
        {
            if (!unlocked) { unlocked = true; Assert.True(service.UnlockAsync().GetAwaiter().GetResult()); }
            return FixtureTime();
        };

        await chain.SyncAsync();

        Assert.True(unlocked);
        Assert.Equal(before, await store.GetVerifiedCheckpointAsync());
        await chain.SyncAsync();                                              // the next sync reads it first
        Assert.Equal(HeaderChainFixture.TipHeight, chain.VerifiedTipHeight);
    }

    [Fact]
    public async Task A_chain_with_no_wallet_attached_neither_adopts_nor_writes_stored_state()
    {
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCheckpointAsync(ForgedCheckpoint);
        var chain = new HeaderChainSync(new Feed { Available = 100 }, store, BlazecoinNetwork.Instance, Checkpoint) { Clock = FixtureTime };

        await chain.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight + 100, chain.VerifiedTipHeight);
        Assert.Equal(ForgedCheckpoint, await store.GetVerifiedCheckpointAsync());
        Assert.Null(await store.GetHeaderAnchorsAsync());
    }

    // ── the seal itself, and its use ──────────────────────────────────────────────────────

    [Fact]
    public void A_seal_opens_only_with_the_key_that_made_it_over_the_text_it_was_made_over()
    {
        var wallet = LiteHdWallet.Restore(MinedBlockFixture.Mnemonic);
        var key = wallet.DeriveStateKey(HeaderChainSync.CheckpointSealPurpose);
        var text = SealedValue.Seal(key, "4150167:abc:1");

        Assert.True(SealedValue.TryOpen(key, text, out var value));
        Assert.Equal("4150167:abc:1", value);
        Assert.False(SealedValue.TryOpen(wallet.DeriveStateKey(HeaderChainSync.AnchorsSealPurpose), text, out _));   // one purpose per value
        Assert.False(SealedValue.TryOpen(LiteHdWallet.Restore(OtherWallet).DeriveStateKey(HeaderChainSync.CheckpointSealPurpose), text, out _));
        Assert.False(SealedValue.TryOpen(key, text.Replace("4150167", "4159999"), out _));
        Assert.False(SealedValue.TryOpen(key, "4150167:abc:1", out _));       // no seal at all
        Assert.False(SealedValue.TryOpen(key, null, out _));
        // The remembered coins keep the form they were given on 2026-11-09: the same seal.
        Assert.Equal(RememberedCoinsSeal.Seal(key, "a:0:b:1:0:0"), SealedValue.Seal(key, "a:0:b:1:0:0"));
        Assert.NotEqual(HeaderChainSync.CheckpointSealPurpose, HeaderChainSync.AnchorsSealPurpose);
        Assert.NotEqual(RememberedCoinsSeal.Purpose, HeaderChainSync.CheckpointSealPurpose);
        Assert.NotEqual(RememberedCoinsSeal.Purpose, HeaderChainSync.AnchorsSealPurpose);
    }
}

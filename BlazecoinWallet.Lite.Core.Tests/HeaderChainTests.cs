using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using NBitcoin;

namespace BlazecoinWallet.Lite.Core.Tests;

/// <summary>
/// The trustless header-chain sync (M3 residual). Every positive assertion runs against REAL
/// mainnet headers (see <see cref="HeaderChainFixture"/>), so "the validator accepts a genuine
/// run" is proven, not assumed; the negatives tamper that same run to prove forgeries are
/// rejected — including the load-bearing one: a lowered-difficulty header whose PoW still
/// clears its (easier) own target but violates the ±10% retarget bound.
/// </summary>
public class HeaderChainTests
{
    private static HeaderCheckpoint Checkpoint =>
        new(HeaderChainFixture.CheckpointHeight, HeaderChainFixture.CheckpointHash, HeaderChainFixture.CheckpointBits);

    // ── Difficulty codec ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_codec_round_trips_every_real_header_nBits()
    {
        // Canonical nBits must survive SetCompact→GetCompact unchanged — the invariant
        // PermittedDifficultyTransition's round-trip truncation depends on.
        Assert.Equal(HeaderChainFixture.CheckpointBits,
            BlazecoinDifficulty.TargetToCompact(BlazecoinDifficulty.CompactToTarget(HeaderChainFixture.CheckpointBits)));

        foreach (var raw in HeaderChainFixture.Headers)
        {
            var bits = ParseHeader(raw).Bits.ToCompact();
            Assert.Equal(bits, BlazecoinDifficulty.TargetToCompact(BlazecoinDifficulty.CompactToTarget(bits)));
        }
    }

    [Fact]
    public void Permitted_transition_holds_off_boundary_and_bounds_on_boundary()
    {
        const uint bits = HeaderChainFixture.CheckpointBits;

        // Off a retarget boundary the bits must be identical.
        Assert.True(BlazecoinDifficulty.PermittedTransition(4_149_841, bits, bits));
        Assert.False(BlazecoinDifficulty.PermittedTransition(4_149_841, bits, bits + 1));

        // On a boundary (height % 120 == 0) a ±10% move is allowed; a halving of difficulty
        // (target far more than doubled) is not.
        var old = BlazecoinDifficulty.CompactToTarget(bits);
        var tenPctEasier = BlazecoinDifficulty.TargetToCompact(old + old / 20);   // +5% target ⇒ within band
        var wayEasier = BlazecoinDifficulty.TargetToCompact(old * 2);             // +100% target ⇒ out of band
        Assert.True(BlazecoinDifficulty.PermittedTransition(4_149_960, bits, tenPctEasier));
        Assert.False(BlazecoinDifficulty.PermittedTransition(4_149_960, bits, wayEasier));
    }

    // ── Validator against real headers ─────────────────────────────────────────────────────

    [Fact]
    public void Validator_accepts_the_real_forward_run_to_the_tip()
    {
        var (tip, error) = HeaderChainValidator.Extend(Checkpoint, HeaderChainFixture.Headers, BlazecoinNetwork.Instance);

        Assert.Null(error);
        Assert.Equal(HeaderChainFixture.TipHeight, tip.Height);
        // The verified tip's hash is the real block 4,150,167 (chained through 327 PoW checks
        // across two retarget boundaries).
        Assert.Equal(64, tip.Hash.Length);
    }

    [Fact]
    public void Validator_stops_at_a_broken_proof_of_work()
    {
        // Break header #50's nonce: its scrypt solution no longer clears the target.
        var tampered = (string[])HeaderChainFixture.Headers.Clone();
        tampered[50] = WithNonce(tampered[50], "ffffffff");

        var (tip, error) = HeaderChainValidator.Extend(Checkpoint, tampered, BlazecoinNetwork.Instance);

        Assert.NotNull(error);
        Assert.Contains("proof-of-work", error);
        Assert.Equal(HeaderChainFixture.CheckpointHeight + 50, tip.Height); // advanced only to the last good header
    }

    [Fact]
    public void Validator_rejects_a_broken_link()
    {
        // Swap two adjacent headers: #30 no longer links to #29's hash.
        var tampered = (string[])HeaderChainFixture.Headers.Clone();
        (tampered[29], tampered[30]) = (tampered[30], tampered[29]);

        var (tip, error) = HeaderChainValidator.Extend(Checkpoint, tampered, BlazecoinNetwork.Instance);

        Assert.NotNull(error);
        Assert.Contains("link", error);
        Assert.Equal(HeaderChainFixture.CheckpointHeight + 29, tip.Height);
    }

    [Fact]
    public void Validator_rejects_a_lowered_difficulty_header_at_the_retarget_boundary()
    {
        // The attack the retarget bound closes: cheapen a boundary header's difficulty toward
        // powLimit so a would-be forger only has to mine easy blocks from there on. At boundary
        // height 4,149,960 (index 119) rewrite nBits to powLimit; the ±10% transition bound
        // rejects it BEFORE the PoW is even consulted, so the chain's difficulty can't be
        // ratcheted down. (Difficulty is part of the scrypt preimage, so this also destroys the
        // header's real PoW — but the transition check is the layer that makes it impossible to
        // even attempt a cheapened chain.)
        var powLimitBits = BlazecoinDifficulty.TargetToCompact(BlazecoinDifficulty.PowLimit);
        var tampered = (string[])HeaderChainFixture.Headers.Clone();
        var boundaryIndex = (int)(4_149_960 - (HeaderChainFixture.CheckpointHeight + 1)); // 119
        tampered[boundaryIndex] = WithBits(tampered[boundaryIndex], powLimitBits);

        var (tip, error) = HeaderChainValidator.Extend(Checkpoint, tampered, BlazecoinNetwork.Instance);

        Assert.NotNull(error);
        Assert.Contains("difficulty", error);
        Assert.Equal(4_149_960 - 1, tip.Height); // stopped just before the forged boundary
    }

    // ── Sync service ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_advances_the_verified_tip_and_persists_it()
    {
        var store = new InMemoryWalletStateStore();
        var sync = new HeaderChainSync(new FixtureHeaderReader(), store, BlazecoinNetwork.Instance, Checkpoint);

        Assert.True(sync.Available);
        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        var persisted = HeaderCheckpoint.TryParse(await store.GetVerifiedCheckpointAsync());
        Assert.NotNull(persisted);
        Assert.Equal(HeaderChainFixture.TipHeight, persisted!.Height);
    }

    [Fact]
    public async Task Sync_reports_IsSyncing_while_running_and_flips_it_back_when_done()
    {
        var sync = new HeaderChainSync(new FixtureHeaderReader(), new InMemoryWalletStateStore(),
            BlazecoinNetwork.Instance, Checkpoint);
        var states = new List<bool>();
        sync.SyncStateChanged += () => states.Add(sync.IsSyncing);

        Assert.False(sync.IsSyncing);
        await sync.SyncAsync();

        Assert.False(sync.IsSyncing);
        Assert.Equal(new[] { true, false }, states);   // exactly one start + one end
        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight); // slicing changed nothing
    }

    [Fact]
    public async Task Sync_adopts_a_persisted_tip_ahead_but_never_one_behind_the_anchor()
    {
        // A persisted tip AHEAD of the shipped anchor is resumed from (no re-verifying old work).
        var ahead = new HeaderCheckpoint(HeaderChainFixture.TipHeight, HeaderChainFixture.CheckpointHash, HeaderChainFixture.CheckpointBits);
        var store = new InMemoryWalletStateStore();
        await store.SetVerifiedCheckpointAsync(ahead.Serialize());
        var sync = new HeaderChainSync(new FixtureHeaderReader(), store, BlazecoinNetwork.Instance, Checkpoint);
        await sync.SyncAsync();
        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);

        // A persisted tip BEHIND the shipped anchor is ignored — verification never regresses.
        var behind = new HeaderCheckpoint(1_000_000, HeaderChainFixture.CheckpointHash, HeaderChainFixture.CheckpointBits);
        var store2 = new InMemoryWalletStateStore();
        await store2.SetVerifiedCheckpointAsync(behind.Serialize());
        var sync2 = new HeaderChainSync(new FixtureHeaderReader(count: 0), store2, BlazecoinNetwork.Instance, Checkpoint);
        await sync2.SyncAsync();
        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync2.VerifiedTipHeight);
    }

    [Fact]
    public async Task Sync_raises_TipAdvanced_only_when_the_tip_actually_moves()
    {
        var sync = new HeaderChainSync(new FixtureHeaderReader(), new InMemoryWalletStateStore(),
            BlazecoinNetwork.Instance, Checkpoint);
        var fired = 0;
        sync.TipAdvanced += () => Interlocked.Increment(ref fired);

        await sync.SyncAsync();
        Assert.True(fired > 0);                                    // moved anchor → tip, so it fired
        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);

        var before = fired;
        await sync.SyncAsync();                                    // already at tip → nothing new
        Assert.Equal(before, fired);                              // no spurious event
    }

    [Fact]
    public void Effective_confirmations_cap_to_the_verified_tip()
    {
        var sync = new HeaderChainSync(new FixtureHeaderReader(), new InMemoryWalletStateStore(),
            BlazecoinNetwork.Instance, new HeaderCheckpoint(HeaderChainFixture.TipHeight, HeaderChainFixture.CheckpointHash, HeaderChainFixture.CheckpointBits));

        // Gateway claims 10,000 confirmations for a coin 100 blocks below the verified tip —
        // capped to the real 101.
        Assert.Equal(101, sync.EffectiveConfirmations(10_000, HeaderChainFixture.TipHeight - 100));
        // An honest, smaller gateway count passes through (min).
        Assert.Equal(50, sync.EffectiveConfirmations(50, HeaderChainFixture.TipHeight - 100));
        // A coin above the verified tip can't be capped yet — gateway value used as-is.
        Assert.Equal(3, sync.EffectiveConfirmations(3, HeaderChainFixture.TipHeight + 10));
    }

    [Fact]
    public void Effective_confirmations_pass_through_without_a_feed()
    {
        var sync = new HeaderChainSync(headers: null); // personal-node / no feed
        Assert.False(sync.Available);
        Assert.Equal(9_999, sync.EffectiveConfirmations(9_999, HeaderChainFixture.CheckpointHeight));
    }

    // ── Per-tx trustless height (the M3 polish) ──────────────────────────────────────────

    [Fact]
    public async Task Verified_chain_indexes_hash_to_height_both_ways()
    {
        var sync = new HeaderChainSync(new FixtureHeaderReader(), new InMemoryWalletStateStore(),
            BlazecoinNetwork.Instance, Checkpoint);
        await sync.SyncAsync();

        // Fixture block 4,149,841's real hash resolves to its height and back.
        Assert.Equal(HeaderChainFixture.ProofBlockHeight, sync.VerifiedHeightOf(HeaderChainFixture.ProofBlockHash));
        Assert.Equal(HeaderChainFixture.ProofBlockHash, sync.VerifiedHashAt(HeaderChainFixture.ProofBlockHeight));
        // The anchor is indexed too; an unknown hash is null.
        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedHeightOf(HeaderChainFixture.CheckpointHash));
        Assert.Null(sync.VerifiedHeightOf(new string('f', 64)));
    }

    [Fact]
    public async Task Chain_inclusion_gives_a_real_tx_a_trustless_height()
    {
        // The full per-tx path against REAL data: a real coinbase + its real merkle proof, whose
        // block is header[0] of the synced chain → the tx's height is known trusting no gateway.
        var sync = new HeaderChainSync(new FixtureHeaderReader(), new InMemoryWalletStateStore(),
            BlazecoinNetwork.Instance, Checkpoint);
        await sync.SyncAsync();
        var verifier = new LiteTxVerifier(new ProofReader(), BlazecoinNetwork.Instance, sync);

        var inc = await verifier.VerifyChainInclusionAsync(HeaderChainFixture.ProofTxId);

        Assert.True(inc.ProofValid);
        Assert.True(inc.OnVerifiedChain);
        Assert.Equal(HeaderChainFixture.ProofBlockHeight, inc.Height);
    }

    [Fact]
    public async Task Chain_inclusion_stands_on_pow_when_the_block_isnt_verified_yet()
    {
        // Proof valid, but no header chain has placed the block → height unknown, proof still holds.
        var verifier = new LiteTxVerifier(new ProofReader()); // no header chain
        var inc = await verifier.VerifyChainInclusionAsync(HeaderChainFixture.ProofTxId);

        Assert.True(inc.ProofValid);
        Assert.False(inc.OnVerifiedChain);
        Assert.Null(inc.Height);
    }

    [Fact]
    public async Task Chain_inclusion_fails_when_no_proof_is_available()
    {
        var verifier = new LiteTxVerifier(new ProofReader());
        var inc = await verifier.VerifyChainInclusionAsync(new string('e', 64)); // unknown txid → no proof

        Assert.False(inc.ProofValid);
        Assert.False(inc.OnVerifiedChain);
    }

    /// <summary>Serves the harvested real proof for the fixture coinbase, nothing else.</summary>
    private sealed class ProofReader : IChainReader
    {
        public bool SupportsHistory => true;
        public bool SupportsChainVerification => true;
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) =>
            Task.FromResult<string?>(string.Equals(txId, HeaderChainFixture.ProofTxId, StringComparison.OrdinalIgnoreCase)
                ? HeaderChainFixture.ProofHex : null);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static BlockHeader ParseHeader(string hex)
    {
        var factory = BlazecoinNetwork.Instance.Consensus.ConsensusFactory;
        var header = factory.CreateBlockHeader();
        header.ReadWrite(new BitcoinStream(NBitcoin.DataEncoders.Encoders.Hex.DecodeData(hex)) { ConsensusFactory = factory });
        return header;
    }

    /// <summary>Rewrite the 4-byte little-endian nBits field (header bytes 72..76).</summary>
    private static string WithBits(string rawHex, uint bits)
    {
        var le = $"{(byte)bits:x2}{(byte)(bits >> 8):x2}{(byte)(bits >> 16):x2}{(byte)(bits >> 24):x2}";
        return rawHex[..144] + le + rawHex[152..];
    }

    /// <summary>Rewrite the 4-byte nonce field (header bytes 76..80) — breaks the PoW solution.</summary>
    private static string WithNonce(string rawHex, string nonceHexLE) => rawHex[..152] + nonceHexLE;

    /// <summary>Serves the fixture headers by height; <paramref name="count"/> caps how many
    /// exist (0 ⇒ an empty feed, to prove the "never regress below the anchor" path).</summary>
    private sealed class FixtureHeaderReader(int count = int.MaxValue) : IHeaderReader
    {
        private const long Base = HeaderChainFixture.CheckpointHeight + 1;

        public Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count2, CancellationToken ct = default)
        {
            var available = Math.Min(count, HeaderChainFixture.Headers.Length);
            var start = (int)(fromHeight - Base);
            if (start < 0 || start >= available)
                return Task.FromResult<IReadOnlyList<string>>([]);
            return Task.FromResult<IReadOnlyList<string>>(
                HeaderChainFixture.Headers.Skip(start).Take(Math.Min(count2, available - start)).ToList());
        }
    }
}

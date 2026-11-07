using NBitcoin;
using NBitcoin.DataEncoders;
using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Lite.Data;

/// <summary>A verified point on the header chain — everything needed to extend from here:
/// the block's height, its id (SHA256d), the nBits the NEXT header's difficulty is
/// verified against, its timestamp (<paramref name="Time"/> — the parent-time input of the
/// Era-3 Phoenix-413 rule), and the Phoenix anchor once the chain has crossed H_A
/// (<paramref name="AnchorBits"/> = block H_A's nBits, <paramref name="AnchorParentTime"/> =
/// block H_A−1's timestamp; both 0 until captured). The anchor rides in the record so the
/// persisted tip stays self-sufficient across restarts after the fork.</summary>
public sealed record HeaderCheckpoint(long Height, string Hash, uint Bits,
    long Time = 0, uint AnchorBits = 0, long AnchorParentTime = 0)
{
    /// <summary>Compact "height:hash:bits:time:anchorBits:anchorParentTime" wire form for the
    /// state store (the legacy 3-part form parses too — it can only describe a pre-fork tip,
    /// where the extra fields are refilled by the next forward sync).</summary>
    public string Serialize() => $"{Height}:{Hash}:{Bits}:{Time}:{AnchorBits}:{AnchorParentTime}";

    public static HeaderCheckpoint? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(':');
        if (parts.Length != 3 && parts.Length != 6) return null;
        if (!long.TryParse(parts[0], out var h) || parts[1].Length != 64 || !uint.TryParse(parts[2], out var b))
            return null;
        if (parts.Length == 3)
            return new HeaderCheckpoint(h, parts[1], b);
        if (long.TryParse(parts[3], out var t) && uint.TryParse(parts[4], out var ab) && long.TryParse(parts[5], out var apt))
            return new HeaderCheckpoint(h, parts[1], b, t, ab, apt);
        return null;
    }
}

/// <summary>Where a block stands against the wallet's verified chain (see
/// <see cref="HeaderChainSync.LocateAsync"/>).</summary>
public enum ChainMembership
{
    /// <summary>Proven to be the chain's block at its height.</summary>
    OnChain,
    /// <summary>Proven NOT to be the chain's block at the claimed height — the data source lied.</summary>
    NotOnChain,
    /// <summary>The claimed height is above everything verified so far; retry after the header sync.</summary>
    NotYetVerified,
    /// <summary>No verdict: no header feed, no usable height claim, or the feed failed mid-check.</summary>
    CannotCheck,
}

/// <summary>A <see cref="ChainMembership"/> verdict plus the block's trustless height when on-chain.</summary>
public sealed record BlockLocation(ChainMembership Membership, long? Height = null);

/// <summary>
/// Optional header feed: a source that serves raw 80-byte block headers by height range. The
/// gateway implements it (a daemon passthrough); a bare personal node need not, so it lives
/// apart from <see cref="IChainReader"/> (ISP — only <see cref="HeaderChainSync"/> depends on it).
/// </summary>
public interface IHeaderReader
{
    /// <summary>Raw header hexes for heights [fromHeight, fromHeight+count), in order. Returns
    /// fewer than requested (or empty) at the tip, and empty on any failure — never throws.</summary>
    Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default);
}

/// <summary>Whether a gateway other than the one that served the last header batch could be asked.</summary>
public enum SecondGateway
{
    /// <summary>Another gateway answered; <see cref="HeaderSecondOpinion.RawHeader"/> is its header at
    /// that height, or null when it has not seen that height yet.</summary>
    Answered,
    /// <summary>Another gateway is configured but did not answer (unreachable, timed out, error).</summary>
    Unavailable,
    /// <summary>No other gateway is configured — there is nobody else to ask.</summary>
    None,
}

/// <summary>A second gateway's word on the header at a height (see <see cref="IHeaderCrossCheck"/>).</summary>
public sealed record HeaderSecondOpinion(SecondGateway Status, string? RawHeader);

/// <summary>
/// Optional capability of an <see cref="IHeaderReader"/> (the <see cref="ISpendCrossCheck"/> pattern):
/// the raw header at a height from a gateway OTHER than the one that served the most recent header
/// batch. The sync puts every run of verified headers to it before the verified tip moves (bug-bounty
/// report 2026-11-07: with the two-hour clock rule alone, one gateway could still forge the chain
/// cheaply — see <see cref="HeaderChainValidator"/>).
/// </summary>
public interface IHeaderCrossCheck
{
    Task<HeaderSecondOpinion> HeaderFromAnotherGatewayAsync(long height, CancellationToken ct = default);
}

/// <summary>How the last run of verified headers fared with a second gateway.</summary>
public enum HeaderCorroboration
{
    /// <summary>A second gateway served the same header hash at the run's top height; the tip moved.</summary>
    Confirmed,
    /// <summary>A second gateway served a DIFFERENT header at that height; the run was discarded and the tip held.</summary>
    Contradicted,
    /// <summary>A second gateway exists but gave no usable answer (unreachable, or not yet at that height); the tip held.</summary>
    Unavailable,
    /// <summary>Only one gateway is configured, so the run stood on its own proof-of-work; the tip moved.</summary>
    NoSecondGateway,
}

/// <summary>
/// Trustless verification of a forward run of block headers (the M3 residual: the maturity /
/// confirmation display previously trusted the gateway's tip scalar outright). Anchored at a
/// shipped checkpoint, each header is checked for (1) scrypt proof-of-work meeting its own
/// target, (2) that target being in range, (3) linkage to the prior header, (4) the difficulty
/// the retarget rule requires (a ±10% band before Phoenix-413, the exact value after it), and
/// (5) a timestamp no more than two hours ahead of this device's clock. Forging a chain that
/// passes all five requires real mining work bounded by the retarget rule, so the verified tip
/// cannot be inflated by a lying gateway — which is exactly what the maturity gate needs.
/// <para>Rule 5 is what makes rule 4 mean anything after Phoenix-413, where the required target
/// is computed from the PARENT's timestamp: without it (bug-bounty report 2026-11-06) a gateway
/// could date its headers days ahead, which drives the required target to the easiest the chain
/// allows — every forged header after the first then cost about 2^20 scrypt hashes instead of
/// the ~2^34 the real chain demands, and a run of them became "the verified chain".</para>
/// <para>Rule 5 is necessary, not sufficient (bug-bounty report 2026-11-07, Juan Alfonsus): the
/// Phoenix anchor is frozen at the fork while the clock keeps moving, so the LATEST time a header
/// may claim drifts away from the anchor at one second per second. One header stamped now+2h on top
/// of a checkpoint that is a day old eases every header after it by 2^((1 day + 2 h) / 3.44 h) ≈ 190×,
/// and the shipped checkpoint reaches powLimit within two days of its harvest. No per-header rule can
/// close that: a genuine stall and a forged one look the same header by header, and a bound on the
/// gap to the parent is defeated by chaining gaps (the whole forged chain then costs about three real
/// blocks). What closes it is the rule the rest of the lite wallet lives by — one gateway's word is not
/// enough: <see cref="HeaderChainSync"/> moves the verified tip only when a second gateway reproduces
/// the run's top header hash (<see cref="IHeaderCrossCheck"/>). Forging the verified chain then takes
/// every configured gateway, not one.</para>
/// </summary>
public static class HeaderChainValidator
{
    /// <summary>How far ahead of the clock a block's timestamp may be — the node's
    /// MAX_FUTURE_BLOCK_TIME (2 hours).</summary>
    public const long MaxFutureBlockTimeSeconds = 2 * 60 * 60;

    /// <summary>Extends <paramref name="from"/> by the given ordered raw headers. Returns the new
    /// verified tip and a null error on success; on the first bad header returns the last GOOD
    /// tip reached plus the reason (so a partially-valid batch still advances honestly).
    /// <paramref name="onAccepted"/> fires once per verified header (height + hash), so a caller
    /// can index the chain for per-tx height lookups.</summary>
    /// <param name="now">The device's clock (null = the system clock). A header dated more than
    /// <see cref="MaxFutureBlockTimeSeconds"/> past it is refused.</param>
    public static (HeaderCheckpoint Tip, string? Error) Extend(
        HeaderCheckpoint from, IReadOnlyList<string> rawHeaders, Network network,
        Action<HeaderCheckpoint>? onAccepted = null, DateTimeOffset? now = null)
    {
        var factory = network.Consensus.ConsensusFactory;
        var tip = from;
        var latestAllowed = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() + MaxFutureBlockTimeSeconds;

        for (var i = 0; i < rawHeaders.Count; i++)
        {
            var height = from.Height + i + 1;

            BlockHeader header;
            try
            {
                header = factory.CreateBlockHeader();
                var stream = new BitcoinStream(Encoders.Hex.DecodeData(rawHeaders[i])) { ConsensusFactory = factory };
                header.ReadWrite(stream);
            }
            catch { return (tip, $"Header at height {height} was malformed."); }

            // Linkage first — a header that doesn't build on the verified tip is off-chain.
            if (!string.Equals(header.HashPrevBlock.ToString(), tip.Hash, StringComparison.OrdinalIgnoreCase))
                return (tip, $"Header at height {height} does not link to the verified chain.");

            // Then its date: no block may claim a time more than two hours ahead of the clock (the
            // node's own rule). Only a LATER time eases the Phoenix-413 target, so this is the bound
            // that matters; an earlier one only makes a forger's work harder. Checked before the
            // proof-of-work — a header from the future is refused whatever work it carries.
            if (header.BlockTime.ToUnixTimeSeconds() > latestAllowed)
                return (tip, $"Header at height {height} is dated more than two hours ahead of this device's clock, so it is not accepted (if this persists, check the device's date and time).");

            var bits = header.Bits.ToCompact();

            if (height > BlazecoinDifficulty.PhoenixActivationHeight)
            {
                // Era 3 (Phoenix-413): the target is an EXACT function of the anchor and the
                // parent's timestamp, both of which this verified chain supplies — so the
                // lite client checks it bit-for-bit (stronger than the old ±10% band).
                if (tip.AnchorBits == 0 || tip.Time == 0)
                    return (tip, $"Header at height {height} cannot be verified: Phoenix anchor unavailable (re-sync from the shipped checkpoint).");
                var expected = BlazecoinDifficulty.PhoenixNextBits(tip.AnchorBits, tip.AnchorParentTime,
                    BlazecoinDifficulty.PhoenixActivationHeight, height, tip.Time);
                if (bits != expected)
                    return (tip, $"Header at height {height} has nBits {bits:x8}; Phoenix-413 requires {expected:x8}.");
            }
            // Eras 1–2: difficulty must be a permitted transition from the prior header's —
            // this is what stops a gateway fabricating cheap low-difficulty headers.
            else if (!BlazecoinDifficulty.PermittedTransition(height, tip.Bits, bits))
                return (tip, $"Header at height {height} has a disallowed difficulty transition.");

            // Range + scrypt PoW: the header's own hash must clear its own (in-range) target.
            if (!BlazecoinPoW.MeetsTarget(header))
                return (tip, $"Header at height {height} does not satisfy its proof-of-work.");

            // Carry the Phoenix anchor forward, capturing it as the chain crosses the fork:
            // block H_A−1 contributes its timestamp, block H_A its (last Era-2) nBits.
            var time = header.BlockTime.ToUnixTimeSeconds();
            var anchorBits = tip.AnchorBits;
            var anchorParentTime = tip.AnchorParentTime;
            if (height == BlazecoinDifficulty.PhoenixActivationHeight - 1) anchorParentTime = time;
            if (height == BlazecoinDifficulty.PhoenixActivationHeight) anchorBits = bits;

            tip = new HeaderCheckpoint(height, header.GetHash().ToString(), bits, time, anchorBits, anchorParentTime);
            onAccepted?.Invoke(tip);
        }

        return (tip, null);
    }
}

/// <summary>
/// Drives header sync: holds the current verified tip (starting from a persisted rolling
/// checkpoint, or the shipped one on a fresh install), fetches headers forward from an
/// <see cref="IHeaderReader"/> in batches, validates each batch, and advances + persists the
/// tip. The verified height then bounds the maturity/confirmation display so a gateway can
/// never make coins look MORE confirmed than real proof-of-work supports. A no-op when no
/// header feed is available (personal-node / tests) — the wallet falls back to gateway data.
/// </summary>
public sealed class HeaderChainSync
{
    /// <summary>The shipped anchor — a real mainnet block, refreshed at every lite release so a
    /// fresh install's forward sync stays short (harvested 2026-11-06 from the live daemon at tip
    /// 4,400,483; it is also an entry of the shipped <see cref="HeaderAnchors"/> table). Being
    /// past H_A, it BAKES the Phoenix-413 anchor the per-block rule needs: AnchorBits = block
    /// 4,194,000's nBits, AnchorParentTime = block 4,193,999's timestamp (both constant since
    /// the fork). Trusting it is the same assumption Core's checkpoints/assumevalid make.
    /// (The 2026-08-26 → 10-25 anchor was 4,193,880, deliberately below the fork so the sync
    /// captured those two values from the header stream; Phoenix413Tests pins them.)</summary>
    public static readonly HeaderCheckpoint MainnetCheckpoint =
        new(4_400_000, "6eb06d2a4b7d09606715b10e706e1b1bdd4be4846150b844ac22f848ecc7a03c", 0x1c315235, 1793940557,
            AnchorBits: 0x1c404193, AnchorParentTime: 1787765193);

    private const int BatchSize = 250;   // headers per gateway call (~40 KB)
    private const int MaxBatchesPerSync = 400; // ≥ 100k headers per call — plenty; a backstop
    private const int MaxIndexedHeaders = 100_000; // rolling hash↔height window (~5 weeks of blocks)

    /// <summary>Verified headers are committed — and first put to a second gateway — in runs of at
    /// most this many batches (2,500 headers): a long first catch-up asks the other gateway ~40
    /// times for 100k headers and loses at most one run to a crash; an incremental sync asks once.</summary>
    public const int BatchesPerCorroboration = 10;

    private readonly IHeaderReader? _headers;
    private readonly IHeaderCrossCheck? _crossCheck;
    private readonly IWalletStateStore _state;
    private readonly Network _network;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Hash↔height for verified headers in the current session's window — lets a merkle proof's
    // block be located in the chain, giving a tx a trustless height with no gateway trust.
    private readonly Dictionary<string, long> _hashToHeight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _heightToHash = [];
    private HeaderCheckpoint _tip;
    private bool _loaded;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // Trusted hashes every Interval blocks: the shipped table, plus the ones this wallet has
    // verified above it (persisted). A merkle proof's block is proven on-chain by linking it
    // forward to the next of these (LocateAsync).
    private readonly HeaderAnchors _anchors;
    private readonly SortedDictionary<long, string> _learnedAnchors = [];
    private bool _anchorsDirty;
    // Blocks proven on-chain by such a link, kept apart from the sync's own window.
    private readonly Dictionary<string, long> _linkedHashToHeight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _linkedHeightToHash = [];
    private const int MaxLinkedBlocks = 20_000;        // cache cap (~3 MB); cleared when full
    private const int MaxLinkWalk = MaxIndexedHeaders; // backstop on one link's length
    // Every index/anchor/link map is read by proof checks while a sync writes it.
    private readonly object _indexLock = new();

    /// <param name="crossCheck">Where a second gateway's header comes from. Null ⇒ the feed itself
    /// when it is an <see cref="IHeaderCrossCheck"/> (the gateway data service), else none — a run
    /// then stands on its own proof-of-work, as before the 2026-11-07 report.</param>
    public HeaderChainSync(IHeaderReader? headers = null, IWalletStateStore? state = null,
        Network? network = null, HeaderCheckpoint? checkpoint = null, HeaderAnchors? anchors = null,
        IHeaderCrossCheck? crossCheck = null)
    {
        _headers = headers;
        _crossCheck = crossCheck ?? headers as IHeaderCrossCheck;
        _state = state ?? new InMemoryWalletStateStore();
        _network = network ?? BlazecoinNetwork.Instance;
        _anchors = anchors ?? HeaderAnchors.Mainnet;
        _tip = checkpoint ?? MainnetCheckpoint;
        Index(_tip);
    }

    /// <summary>This device's clock, for the rule that no header may be dated more than two hours
    /// ahead of it (tests move it).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private void Index(HeaderCheckpoint c)
    {
        lock (_indexLock)
        {
            _hashToHeight[c.Hash] = c.Height;
            _heightToHash[c.Height] = c.Hash;
            LearnAnchor(c.Height, c.Hash);
            // Trim the oldest heights once the window is full — recent coins are what get verified.
            if (_heightToHash.Count > MaxIndexedHeaders)
            {
                var cutoff = _tip.Height - MaxIndexedHeaders;
                foreach (var h in _heightToHash.Keys.Where(k => k <= cutoff).ToList())
                {
                    if (_heightToHash.Remove(h, out var hash)) _hashToHeight.Remove(hash);
                }
            }
        }
    }

    /// <summary>Records a verified block as an anchor when it sits on an Interval boundary above
    /// the shipped table. Caller holds <see cref="_indexLock"/>.</summary>
    private void LearnAnchor(long height, string hash)
    {
        if (height <= _anchors.MaxHeight || height % _anchors.Interval != 0) return;
        if (_learnedAnchors.ContainsKey(height)) return;
        _learnedAnchors[height] = hash.ToLowerInvariant();
        _anchorsDirty = true;
    }

    /// <summary>The verified height of a block by its hash, or null when the wallet hasn't placed
    /// it — the trustless per-tx height a merkle proof resolves to.</summary>
    public long? VerifiedHeightOf(string blockHash)
    {
        lock (_indexLock)
        {
            if (_hashToHeight.TryGetValue(blockHash, out var h)) return h;
            return _linkedHashToHeight.TryGetValue(blockHash, out var l) ? l : null;
        }
    }

    /// <summary>The verified block hash at a height, or null when the wallet hasn't placed it.</summary>
    public string? VerifiedHashAt(long height)
    {
        lock (_indexLock)
        {
            if (_heightToHash.TryGetValue(height, out var hash)) return hash;
            return _linkedHeightToHash.TryGetValue(height, out var l) ? l : null;
        }
    }

    /// <summary>Confirmations for a block KNOWN to be on the verified chain at <paramref name="height"/>
    /// (verifiedTip − height + 1, ≥ 1). Caller supplies a height from <see cref="VerifiedHeightOf"/>.</summary>
    public int ConfirmationsForVerifiedHeight(long height) =>
        (int)Math.Max(1, _tip.Height - height + 1);

    /// <summary>Raised (off the sync's own thread) whenever the verified tip advances — the UI
    /// subscribes so a "verified to N" display updates live even when a DIFFERENT caller's
    /// background sync is what moved it (this sync is skip-if-busy, so an awaited SyncAsync can
    /// return before an in-flight one finishes).</summary>
    public event Action? TipAdvanced;

    /// <summary>True while a sync is pulling/verifying headers — the layout shows a quiet
    /// "verifying chain" strip so a long first catch-up reads as work in progress, not a hang.
    /// Raised through <see cref="SyncStateChanged"/> at start and end.</summary>
    public bool IsSyncing { get; private set; }

    /// <summary>Raised when <see cref="IsSyncing"/> flips (start/end of a sync run).</summary>
    public event Action? SyncStateChanged;

    /// <summary>Headers verified per slice before the sync yields to the host. On the
    /// single-threaded browser WASM runtime a whole 250-header batch of scrypt work would
    /// otherwise run as one uninterruptible chunk; slicing + a real timer yield between
    /// slices keeps the UI painting and input live during the first catch-up
    /// (the 2026-08-17 "first reload freezes ~2–3 min" finding).</summary>
    private const int VerifySlice = 50;

    /// <summary>A genuine event-loop yield. <c>Task.Yield()</c> does NOT return control to the
    /// browser on WASM (it just re-queues on the same synchronization context), whereas a
    /// 1 ms <c>Task.Delay</c> goes through a real timer and lets the renderer run.</summary>
    private static Task YieldToHostAsync(CancellationToken ct) => Task.Delay(1, ct);

    /// <summary>True when a header feed is wired — the only mode where the verified tip means
    /// anything. False ⇒ every confirmation query falls straight back to the gateway value.</summary>
    public bool Available => _headers != null;

    /// <summary>The highest height verified by proof-of-work so far (≥ the shipped checkpoint).</summary>
    public long VerifiedTipHeight => _tip.Height;

    /// <summary>
    /// Confirmations for a coin at <paramref name="blockHeight"/>, never exceeding what the
    /// verified tip supports. When the verified chain reaches the coin's block we return the
    /// MORE CONSERVATIVE of the gateway's count and (verifiedTip − blockHeight + 1); a gateway
    /// inflating confirmations to fake maturity is capped to the real PoW height. Falls back to
    /// the gateway count when header sync is unavailable or hasn't reached the coin yet.
    /// </summary>
    public int EffectiveConfirmations(int gatewayConfirmations, long blockHeight)
    {
        if (!Available || blockHeight <= 0 || _tip.Height < blockHeight) return gatewayConfirmations;
        var verified = (int)Math.Min(int.MaxValue, _tip.Height - blockHeight + 1);
        return Math.Min(gatewayConfirmations, verified);
    }

    /// <summary>
    /// Best-effort: load the persisted tip once, then pull + verify headers forward to the
    /// chain tip. Never throws — a fetch failure, an unreachable feed, or an invalid header
    /// just stops the advance at the last verified height. Safe to call opportunistically
    /// (e.g. whenever balances refresh); serialized so overlapping calls don't double-fetch.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (_headers == null) return;

        // Non-blocking: if a sync is already in flight, skip rather than queue — the frequent
        // fire-and-forget kicks from balance refreshes must not stack up behind each other.
        if (!await _gate.WaitAsync(0, ct)) return;
        IsSyncing = true;
        SyncStateChanged?.Invoke();
        try
        {
            await EnsureLoadedAsync();

            var rolledBack = false;
            // Headers verified by proof-of-work but not yet committed: the run grows across batches
            // and is committed — after a second gateway has confirmed its top — every
            // BatchesPerCorroboration batches and when the feed runs dry. Until then nothing below
            // reads it: the tip, the index and the store all stay where the last run left them.
            var run = new List<HeaderCheckpoint>();
            var runTip = _tip;
            var batchesInRun = 0;
            for (var batch = 0; batch < MaxBatchesPerSync; batch++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = await _headers.GetHeadersAsync(runTip.Height + 1, BatchSize, ct);
                if (raw.Count == 0)
                {
                    // Caught up to the tip (or the feed had nothing new): commit what the run holds.
                    if (run.Count > 0) await CommitRunAsync(run, ct);
                    break;
                }

                // The next header not building on our tip means one of two things: the feed is
                // serving junk, or OUR tip block was orphaned in a reorg. Only the second can be
                // fixed here — roll back to the nearest trusted anchor and re-sync from it — and
                // without it the verified chain would stall at the orphan for ever. A feed that
                // contradicts the run IT JUST SERVED is the first kind: the run is dropped unspent.
                if (!rolledBack && !LinksTo(raw[0], runTip.Hash))
                {
                    rolledBack = true; // once per sync: a feed that keeps contradicting itself gets no second rewind
                    if (run.Count > 0) break;
                    if (await TryRollBackAsync(ct)) { runTip = _tip; continue; }
                    break;
                }

                // Verify the batch in slices, yielding to the host between them (see VerifySlice).
                string? error = null;
                for (var offset = 0; offset < raw.Count && error == null; offset += VerifySlice)
                {
                    var slice = raw.Skip(offset).Take(VerifySlice).ToList();
                    var (newTip, sliceError) = HeaderChainValidator.Extend(runTip, slice, _network, run.Add, Clock());
                    error = sliceError;
                    runTip = newTip;
                    await YieldToHostAsync(ct);
                }
                batchesInRun++;

                var feedDone = error != null || raw.Count < BatchSize; // a bad header, or a short batch ⇒ the tip
                if (run.Count > 0 && (feedDone || batchesInRun >= BatchesPerCorroboration))
                {
                    var committed = await CommitRunAsync(run, ct);
                    run.Clear();
                    batchesInRun = 0;
                    if (!committed) break; // held — the tip stays; the next sync asks again
                    runTip = _tip;
                }
                if (feedDone) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* never-throw: leave the verified tip where it is */ }
        finally
        {
            IsSyncing = false;
            _gate.Release();
            SyncStateChanged?.Invoke();
        }
    }

    /// <summary>How the most recent run of verified headers fared with a second gateway (null until a
    /// run has been put to one this session). <see cref="HeaderCorroboration.Contradicted"/> and
    /// <see cref="HeaderCorroboration.Unavailable"/> mean the verified tip is being HELD below what
    /// the feed served — the wallet keeps working on the older tip (confirmations read lower, never
    /// higher) and asks again on the next sync.</summary>
    public HeaderCorroboration? LastCorroboration { get; private set; }

    /// <summary>True while the newest headers the feed served are being held back for want of a
    /// second gateway's agreement (see <see cref="LastCorroboration"/>).</summary>
    public bool TipHeld => LastCorroboration is HeaderCorroboration.Contradicted or HeaderCorroboration.Unavailable;

    /// <summary>Words for <see cref="TipHeld"/>, or null when the tip is not held.</summary>
    public string? TipHeldMessage => LastCorroboration switch
    {
        HeaderCorroboration.Contradicted => "The newest headers from the gateway in use are not confirmed by a second gateway (it knows a different block at that height), so the verified chain is held where it was. If this persists, the gateway in use is serving a chain of its own.",
        HeaderCorroboration.Unavailable => "The newest headers from the gateway in use could not be confirmed by a second gateway (none answered, or it has not seen those blocks yet), so the verified chain is held where it was until one does.",
        _ => null,
    };

    /// <summary>
    /// Commits a run of verified headers: the tip moves to the run's top, every header is indexed
    /// and the tip persisted — but only after a second gateway has reproduced the top header's hash
    /// (bug-bounty report 2026-11-07). With no second gateway configured the run stands on its own
    /// proof-of-work, which is where it stood before the report; with one that disagrees or is
    /// silent, nothing is committed and the caller stops — the run is re-fetched and re-asked on the
    /// next sync. Returns true when the tip moved.
    /// </summary>
    private async Task<bool> CommitRunAsync(List<HeaderCheckpoint> run, CancellationToken ct)
    {
        var top = run[^1];
        var verdict = await CorroborateAsync(top, ct);
        LastCorroboration = verdict;
        if (verdict is HeaderCorroboration.Contradicted or HeaderCorroboration.Unavailable)
        {
            SyncStateChanged?.Invoke(); // the hold is news even though the tip did not move
            return false;
        }

        foreach (var verified in run) Index(verified);
        _tip = top;
        await PersistAnchorsAsync(); // before the tip, so a crash never leaves the tip ahead of its anchors
        await _state.SetVerifiedCheckpointAsync(_tip.Serialize());
        TipAdvanced?.Invoke();
        return true;
    }

    private async Task<HeaderCorroboration> CorroborateAsync(HeaderCheckpoint top, CancellationToken ct)
    {
        if (_crossCheck == null) return HeaderCorroboration.NoSecondGateway;
        HeaderSecondOpinion opinion;
        try { opinion = await _crossCheck.HeaderFromAnotherGatewayAsync(top.Height, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return HeaderCorroboration.Unavailable; }

        switch (opinion.Status)
        {
            case SecondGateway.None: return HeaderCorroboration.NoSecondGateway;
            case SecondGateway.Unavailable: return HeaderCorroboration.Unavailable;
        }
        // Answered. No header at that height yet = it is behind the gateway in use; a header that
        // does not parse is no answer either. Only the same hash confirms the run.
        if (opinion.RawHeader == null || ParseHeader(opinion.RawHeader) is not { } theirs) return HeaderCorroboration.Unavailable;
        return Same(theirs.GetHash().ToString(), top.Hash) ? HeaderCorroboration.Confirmed : HeaderCorroboration.Contradicted;
    }

    private static HeaderCheckpoint? TryLoadPersisted(string? serialized) => HeaderCheckpoint.TryParse(serialized);

    /// <summary>Loads the persisted tip and learned anchors once (idempotent, concurrency-safe).</summary>
    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _loadGate.WaitAsync();
        try
        {
            if (_loaded) return;
            var persisted = TryLoadPersisted(await _state.GetVerifiedCheckpointAsync());
            // Only adopt a persisted tip AHEAD of the shipped one — never let a stale/rolled
            // store drag verification backwards below the baked-in anchor.
            if (persisted != null && persisted.Height > _tip.Height) { _tip = persisted; Index(_tip); TipAdvanced?.Invoke(); }

            var learned = ParseAnchors(await _state.GetHeaderAnchorsAsync()).ToList();
            lock (_indexLock)
            {
                var dirty = _anchorsDirty; // loading what is already persisted dirties nothing
                foreach (var (height, hash) in learned)
                    if (height <= _tip.Height) LearnAnchor(height, hash); // never one above the tip it came with
                _anchorsDirty = dirty;
            }
            _loaded = true;
        }
        finally { _loadGate.Release(); }
    }

    private async Task PersistAnchorsAsync()
    {
        string serialized;
        lock (_indexLock)
        {
            if (!_anchorsDirty) return;
            serialized = string.Join(",", _learnedAnchors.Select(a => $"{a.Key}:{a.Value}"));
            _anchorsDirty = false;
        }
        await _state.SetHeaderAnchorsAsync(serialized);
    }

    private static IEnumerable<(long Height, string Hash)> ParseAnchors(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)) yield break;
        foreach (var entry in serialized.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], out var h) && parts[1].Length == 64)
                yield return (h, parts[1]);
        }
    }

    /// <summary>
    /// Proves whether <paramref name="blockHash"/> is the chain's block at
    /// <paramref name="claimedHeight"/> (the gateway's untrusted claim). A block already placed
    /// by the sync or an earlier check answers at once. Otherwise the headers from the claimed
    /// height up to the next trusted hash — a shipped or learned anchor, or the verified tip —
    /// are fetched and their prev-hash links checked: a SHA256d chain from the block to a trusted
    /// hash cannot be forged, so a match proves membership whatever the headers' difficulty, and
    /// a broken link or a different end hash proves the claim false. At most one anchor interval
    /// of headers (~80 KB) in the normal case; proven blocks are cached, and anchors passed on
    /// the way above the shipped table are learned (backfilling after an upgrade).
    /// </summary>
    public async Task<BlockLocation> LocateAsync(string blockHash, long claimedHeight, CancellationToken ct = default)
    {
        if (_headers == null) return new(ChainMembership.CannotCheck);
        await EnsureLoadedAsync();

        if (VerifiedHeightOf(blockHash) is long placed) return new(ChainMembership.OnChain, placed);
        if (claimedHeight <= 0) return new(ChainMembership.CannotCheck);

        // Above everything trusted: a short catch-up is worth waiting for; a long one is the
        // background sync's job (it may take minutes on a fresh install).
        if (claimedHeight > _tip.Height && claimedHeight > _anchors.MaxHeight)
        {
            if (claimedHeight - _tip.Height <= 2 * BatchSize) await SyncAsync(ct);
            if (VerifiedHeightOf(blockHash) is long synced) return new(ChainMembership.OnChain, synced);
            if (claimedHeight > _tip.Height) return new(ChainMembership.NotYetVerified);
        }

        if (VerifiedHashAt(claimedHeight) is { } known)
        {
            if (Same(known, blockHash)) return new(ChainMembership.OnChain, claimedHeight);
            // Our block at that height may be a stale orphan rather than the gateway's lie: a sync
            // (which rolls back a reorged tip) settles it before the claim is called false.
            await SyncAsync(ct);
            if (VerifiedHashAt(claimedHeight) is { } settled)
                return Same(settled, blockHash) ? new(ChainMembership.OnChain, claimedHeight) : new(ChainMembership.NotOnChain);
        }

        var (anchorHeight, anchorHash) = NextTrustedAtOrAbove(claimedHeight);
        if (anchorHeight == claimedHeight)
            return Same(anchorHash, blockHash) ? Linked(claimedHeight, blockHash, []) : new(ChainMembership.NotOnChain);
        if (anchorHeight - claimedHeight > MaxLinkWalk) return new(ChainMembership.CannotCheck);

        var factory = _network.Consensus.ConsensusFactory;
        var walked = new List<(long Height, string Hash)>();
        var prev = blockHash;
        var height = claimedHeight;
        while (height < anchorHeight)
        {
            var raw = await _headers.GetHeadersAsync(height + 1, (int)Math.Min(BatchSize, anchorHeight - height), ct);
            if (raw.Count == 0) return new(ChainMembership.CannotCheck);
            foreach (var hex in raw)
            {
                BlockHeader header;
                try
                {
                    header = factory.CreateBlockHeader();
                    header.ReadWrite(new BitcoinStream(Encoders.Hex.DecodeData(hex)) { ConsensusFactory = factory });
                }
                catch { return new(ChainMembership.CannotCheck); }

                if (!Same(header.HashPrevBlock.ToString(), prev)) return new(ChainMembership.NotOnChain);
                prev = header.GetHash().ToString();
                height++;
                // Reached a block the wallet already trusts — the link is settled here.
                if (VerifiedHashAt(height) is { } trusted)
                    return Same(trusted, prev) ? Linked(claimedHeight, blockHash, walked) : new(ChainMembership.NotOnChain);
                walked.Add((height, prev));
                if (height == anchorHeight) break;
            }
        }
        return Same(prev, anchorHash) ? Linked(claimedHeight, blockHash, walked) : new(ChainMembership.NotOnChain);
    }

    private bool LinksTo(string rawHeaderHex, string prevHash)
    {
        try
        {
            var factory = _network.Consensus.ConsensusFactory;
            var header = factory.CreateBlockHeader();
            header.ReadWrite(new BitcoinStream(Encoders.Hex.DecodeData(rawHeaderHex)) { ConsensusFactory = factory });
            return Same(header.HashPrevBlock.ToString(), prevHash);
        }
        catch { return false; }
    }

    /// <summary>
    /// Reorg recovery. Asks the feed for the header AT our tip's height: the same hash means our
    /// tip stands (the feed served a bad next header — nothing to do); a different hash means our
    /// tip block was orphaned. Then the tip is rewound to the nearest trusted anchor below it —
    /// shipped or learned — whose header the feed must reproduce hash-for-hash (so the rebuilt
    /// checkpoint's bits/time are authentic), everything verified above that height is forgotten,
    /// and the caller re-syncs forward. A reorg deeper than the distance to that anchor (≥ 1,000
    /// blocks on mainnet) is not recovered — that is a chain emergency, not a lite-wallet job.
    /// </summary>
    private async Task<bool> TryRollBackAsync(CancellationToken ct)
    {
        if (_headers == null) return false;
        var old = _tip;

        var atTip = await _headers.GetHeadersAsync(old.Height, 1, ct);
        if (atTip.Count == 0 || ParseHeader(atTip[0]) is not { } feedTip) return false;
        if (Same(feedTip.GetHash().ToString(), old.Hash)) return false; // our tip is canonical

        var (height, hash) = NearestTrustedBelow(old.Height);
        if (height < 1) return false;
        var atAnchor = await _headers.GetHeadersAsync(height, 1, ct);
        if (atAnchor.Count == 0 || ParseHeader(atAnchor[0]) is not { } anchorHeader) return false;
        if (!Same(anchorHeader.GetHash().ToString(), hash)) return false; // the anchor itself is contradicted: give up

        var time = anchorHeader.BlockTime.ToUnixTimeSeconds();
        // Phoenix anchor fields: constants once the chain is past H_A (carried from the old tip),
        // recaptured by the forward sync when the rewind lands before the fork.
        var (anchorBits, anchorParentTime) = height >= BlazecoinDifficulty.PhoenixActivationHeight
            ? (old.AnchorBits, old.AnchorParentTime)
            : (0u, height == BlazecoinDifficulty.PhoenixActivationHeight - 1 ? time : 0L);
        var rewound = new HeaderCheckpoint(height, anchorHeader.GetHash().ToString(), anchorHeader.Bits.ToCompact(), time, anchorBits, anchorParentTime);

        lock (_indexLock)
        {
            foreach (var h in _heightToHash.Keys.Where(k => k > height).ToList())
                if (_heightToHash.Remove(h, out var gone)) _hashToHeight.Remove(gone);
            foreach (var h in _linkedHeightToHash.Keys.Where(k => k > height).ToList())
                if (_linkedHeightToHash.Remove(h, out var gone)) _linkedHashToHeight.Remove(gone);
            foreach (var h in _learnedAnchors.Keys.Where(k => k > height).ToList())
                _learnedAnchors.Remove(h);
            _anchorsDirty = true;
        }
        _tip = rewound;
        Index(_tip);
        await PersistAnchorsAsync();
        await _state.SetVerifiedCheckpointAsync(_tip.Serialize());
        TipAdvanced?.Invoke(); // moved, not advanced — subscribers just re-read the tip
        return true;
    }

    /// <summary>The highest trusted anchor strictly below <paramref name="height"/>, or (-1, "") if none.</summary>
    private (long Height, string Hash) NearestTrustedBelow(long height)
    {
        var best = (Height: -1L, Hash: string.Empty);
        lock (_indexLock)
        {
            foreach (var (h, hash) in _learnedAnchors)
                if (h < height) best = (h, hash);
        }
        var shipped = (height - 1) / _anchors.Interval * _anchors.Interval;
        if (height > 0 && shipped > best.Height && _anchors.HashAt(shipped) is { } shippedHash)
            best = (shipped, shippedHash);
        return best;
    }

    private BlockHeader? ParseHeader(string rawHeaderHex)
    {
        try
        {
            var factory = _network.Consensus.ConsensusFactory;
            var header = factory.CreateBlockHeader();
            header.ReadWrite(new BitcoinStream(Encoders.Hex.DecodeData(rawHeaderHex)) { ConsensusFactory = factory });
            return header;
        }
        catch { return null; }
    }

    /// <summary>The nearest trusted hash at or above <paramref name="height"/>: a shipped anchor,
    /// a learned one, or the verified tip (always ≥ height when this is called).</summary>
    private (long Height, string Hash) NextTrustedAtOrAbove(long height)
    {
        if (_anchors.NextAtOrAbove(height) is long shipped)
            return (shipped, _anchors.HashAt(shipped)!);
        lock (_indexLock)
        {
            foreach (var (h, hash) in _learnedAnchors)
                if (h >= height) return (h, hash);
        }
        var tip = _tip;
        return (tip.Height, tip.Hash);
    }

    /// <summary>Caches a proven link and learns any anchors it passed; returns OnChain.</summary>
    private BlockLocation Linked(long height, string blockHash, List<(long Height, string Hash)> walked)
    {
        lock (_indexLock)
        {
            if (_linkedHashToHeight.Count + walked.Count + 1 > MaxLinkedBlocks)
            {
                _linkedHashToHeight.Clear();
                _linkedHeightToHash.Clear();
            }
            foreach (var (h, hash) in walked.Prepend((height, blockHash)))
            {
                _linkedHashToHeight[hash] = h;
                _linkedHeightToHash[h] = hash;
                if (h <= _tip.Height) LearnAnchor(h, hash);
            }
        }
        _ = PersistAnchorsQuietlyAsync(); // best-effort; a lost write is re-learned
        return new(ChainMembership.OnChain, height);
    }

    private async Task PersistAnchorsQuietlyAsync()
    {
        try { await PersistAnchorsAsync(); }
        catch { /* a store that can't write (quota, private mode) only costs re-learning */ }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

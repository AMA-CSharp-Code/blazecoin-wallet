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

/// <summary>
/// Trustless verification of a forward run of block headers (the M3 residual: the maturity /
/// confirmation display previously trusted the gateway's tip scalar outright). Anchored at a
/// shipped checkpoint, each header is checked for (1) scrypt proof-of-work meeting its own
/// target, (2) that target being in range, (3) linkage to the prior header, and (4) a
/// permitted ±10% difficulty transition. Forging a chain that passes all four requires real
/// mining work bounded by the retarget rule, so the verified tip cannot be inflated by a
/// lying gateway — which is exactly what the maturity gate needs.
/// </summary>
public static class HeaderChainValidator
{
    /// <summary>Extends <paramref name="from"/> by the given ordered raw headers. Returns the new
    /// verified tip and a null error on success; on the first bad header returns the last GOOD
    /// tip reached plus the reason (so a partially-valid batch still advances honestly).
    /// <paramref name="onAccepted"/> fires once per verified header (height + hash), so a caller
    /// can index the chain for per-tx height lookups.</summary>
    public static (HeaderCheckpoint Tip, string? Error) Extend(
        HeaderCheckpoint from, IReadOnlyList<string> rawHeaders, Network network,
        Action<HeaderCheckpoint>? onAccepted = null)
    {
        var factory = network.Consensus.ConsensusFactory;
        var tip = from;

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

            // Range + scrypt PoW: the header's own hash must clear its own target.
            if (!BlazecoinDifficulty.TargetInRange(bits) || !BlazecoinPoW.MeetsTarget(header))
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
    /// <summary>The shipped anchor — a real mainnet retarget boundary (harvested 2026-08-26
    /// from the live daemon at frozen tip 4,193,990, for the Phoenix-413 release; the height
    /// is deliberately BELOW H_A−1 = 4,193,999 so a fresh install's forward sync passes
    /// through the fork and captures the Phoenix anchor from the header stream itself).
    /// Refresh at release time so a fresh install's forward sync stays short — any future
    /// refresh to a height ≥ H_A must bake AnchorBits/AnchorParentTime into this record.
    /// Trusting it is the same assumption Core's checkpoints/assumevalid make.</summary>
    public static readonly HeaderCheckpoint MainnetCheckpoint =
        new(4_193_880, "959a9aa81cc88f403bfcf0abdebec3275c4b4ec55070a4610899f96a269197a4", 0x1c3a6a29, 1787755148);

    private const int BatchSize = 250;   // headers per gateway call (~40 KB)
    private const int MaxBatchesPerSync = 400; // ≥ 100k headers per call — plenty; a backstop
    private const int MaxIndexedHeaders = 100_000; // rolling hash↔height window (~5 weeks of blocks)

    private readonly IHeaderReader? _headers;
    private readonly IWalletStateStore _state;
    private readonly Network _network;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Hash↔height for verified headers in the current session's window — lets a merkle proof's
    // block be located in the chain, giving a tx a trustless height with no gateway trust.
    private readonly Dictionary<string, long> _hashToHeight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _heightToHash = [];
    private HeaderCheckpoint _tip;
    private bool _loaded;

    public HeaderChainSync(IHeaderReader? headers = null, IWalletStateStore? state = null,
        Network? network = null, HeaderCheckpoint? checkpoint = null)
    {
        _headers = headers;
        _state = state ?? new InMemoryWalletStateStore();
        _network = network ?? BlazecoinNetwork.Instance;
        _tip = checkpoint ?? MainnetCheckpoint;
        Index(_tip);
    }

    private void Index(HeaderCheckpoint c)
    {
        _hashToHeight[c.Hash] = c.Height;
        _heightToHash[c.Height] = c.Hash;
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

    /// <summary>The verified height of a block by its hash, or null when that block isn't in the
    /// current verified window — the trustless per-tx height a merkle proof resolves to.</summary>
    public long? VerifiedHeightOf(string blockHash) =>
        _hashToHeight.TryGetValue(blockHash, out var h) ? h : null;

    /// <summary>The verified block hash at a height, or null when outside the verified window.</summary>
    public string? VerifiedHashAt(long height) =>
        _heightToHash.TryGetValue(height, out var hash) ? hash : null;

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
            if (!_loaded)
            {
                var persisted = HeaderChainSync.TryLoadPersisted(await _state.GetVerifiedCheckpointAsync());
                // Only adopt a persisted tip AHEAD of the shipped one — never let a stale/rolled
                // store drag verification backwards below the baked-in anchor.
                if (persisted != null && persisted.Height > _tip.Height) { _tip = persisted; Index(_tip); TipAdvanced?.Invoke(); }
                _loaded = true;
            }

            for (var batch = 0; batch < MaxBatchesPerSync; batch++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = await _headers.GetHeadersAsync(_tip.Height + 1, BatchSize, ct);
                if (raw.Count == 0) break; // caught up to the tip (or the feed had nothing new)

                // Verify the batch in slices, yielding to the host between them (see VerifySlice).
                string? error = null;
                for (var offset = 0; offset < raw.Count && error == null; offset += VerifySlice)
                {
                    var slice = raw.Skip(offset).Take(VerifySlice).ToList();
                    var (newTip, sliceError) = HeaderChainValidator.Extend(_tip, slice, _network, Index);
                    error = sliceError;
                    if (newTip.Height > _tip.Height)
                    {
                        _tip = newTip;
                        await _state.SetVerifiedCheckpointAsync(_tip.Serialize());
                        TipAdvanced?.Invoke();
                    }
                    await YieldToHostAsync(ct);
                }
                if (error != null) break;      // a bad header — stop at the last good height
                if (raw.Count < BatchSize) break; // short batch ⇒ reached the tip
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

    private static HeaderCheckpoint? TryLoadPersisted(string? serialized) => HeaderCheckpoint.TryParse(serialized);
}

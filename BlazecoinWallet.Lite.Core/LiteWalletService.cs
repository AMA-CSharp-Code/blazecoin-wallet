using BlazecoinWallet.Lite.Data;
using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>Outcome of a send attempt, ready for the UI.</summary>
/// <param name="Via">"gateway" normally; "p2p" when the broadcast rode the last-resort
/// peer-to-peer fallback because every gateway was down.</param>
public sealed record LiteSendResult(bool Success, string? TxId, string? Error, string Via = "gateway");

/// <summary>A spendable output together with the wallet address that owns it.</summary>
public sealed record LiteOwnedUtxo(string Address, LiteChainUtxo Utxo);

/// <summary>The Balance card's split: what can be spent now, coinbases still ripening,
/// and unconfirmed incoming. Coins leaving in an in-flight send are the remainder
/// against the confirmed on-chain summary (they vanish from the utxo list while the
/// summary still counts them until the spend confirms).</summary>
public sealed record LiteBalanceBreakdown(long Spendable, long ImmatureCoinbase, long UnconfirmedIncoming);

/// <summary>What a WIF key holds on-chain — shown before the user commits to a sweep.</summary>
public sealed record LiteWifCheck(bool Valid, string? Error, string? Address,
    long SpendableSatoshis, long ImmatureSatoshis, int UtxoCount);

/// <summary>A transaction checked against the wallet's own PoW-verified header chain.</summary>
/// <param name="ProofValid">The merkle+PoW inclusion proof verified.</param>
/// <param name="OnVerifiedChain">The proof's block sits on the verified header chain (trustless).</param>
/// <param name="Height">Trustless block height from the proof (null when outside the verified window).</param>
/// <param name="Confirmations">verifiedTip − Height + 1 when on-chain; null otherwise.</param>
/// <param name="Error">Why no proof could be established, when applicable.</param>
public sealed record LiteChainProof(bool ProofValid, bool OnVerifiedChain, long? Height, int? Confirmations, string? Error);

/// <summary>The wallet view the payment watcher needs — nothing more (2026-07-25 audit F2:
/// the watcher must not depend on the whole wallet service).</summary>
public interface IWalletUtxoSource
{
    bool IsUnlocked { get; }
    /// <summary>Txids this session broadcast itself — how a watcher tells our own change
    /// landing back from a genuine incoming payment.</summary>
    IReadOnlyCollection<string> SessionSentTxIds { get; }
    /// <summary>Every unspent output across every revealed address, immature included.</summary>
    Task<IReadOnlyList<LiteOwnedUtxo>> GetAllUtxosAsync(CancellationToken ct = default);
}

/// <summary>
/// The lite wallet's facade: session key state (unlocked from the <see cref="ISeedVault"/>),
/// chain reads aggregated across the rotating address set, and the spend entry points.
/// The HOW lives in focused collaborators (2026-07-25 audit F1): <see cref="AddressRotation"/>
/// owns the BIP44 reveal/gap/discovery policy, <see cref="LiteSendPipeline"/> owns
/// verify→sign→relay. Change always returns to a REVEALED receive address so a
/// mnemonic-only restore can never strand funds.
/// </summary>
public sealed class LiteWalletService : IWalletUtxoSource
{
    /// <summary>Coinbase outputs are spendable after this many confirmations (daemon COINBASE_MATURITY).</summary>
    public const int CoinbaseMaturity = 30;

    /// <summary>See <see cref="AddressRotation.GapLimit"/>.</summary>
    public const int GapLimit = AddressRotation.GapLimit;

    private readonly ISeedVault _vault;
    private readonly IChainReader _reader;
    private readonly AddressRotation _rotation;
    private readonly LiteSendPipeline _pipeline;
    private readonly HeaderChainSync? _headerSync;
    private readonly LiteTxVerifier? _verifier;
    private readonly IWalletStateStore _stateStore;
    private LiteHdWallet? _wallet;

    /// <summary>ISP ctor — reads and relay may come from different sources.</summary>
    /// <param name="p2pFallback">Optional last-resort broadcaster: when the relay is
    /// UNREACHABLE (not rejecting), the signed tx is pushed straight to listening full nodes.</param>
    /// <param name="p2pNodes">Node endpoints for the fallback; null → the chain's seed nodes.</param>
    /// <param name="verifier">Optional trustless input verifier (C1/M3): when present, every
    /// input's real value is confirmed against the chain before signing and the wallet builds
    /// with the VERIFIED value — a lying indexer can no longer inflate the fee. Null for a
    /// trusted source (personal node) or tests.</param>
    /// <param name="stateStore">Rotation-position persistence; null → volatile in-memory
    /// (tests / dev). Losing it is safe — restore rescans the chain.</param>
    /// <param name="headerSync">Optional trustless header-chain sync (M3 residual): when present
    /// AND its feed is live, coinbase maturity / confirmations are capped to the PoW-verified
    /// tip so a gateway can't make coins look more confirmed than real work supports. Null (or
    /// an unavailable feed) → the gateway's confirmation count is used as-is.</param>
    public LiteWalletService(ISeedVault vault, IChainReader reader, ITxRelay relay,
        IP2PBroadcaster? p2pFallback = null, IReadOnlyList<string>? p2pNodes = null,
        LiteTxVerifier? verifier = null, IWalletStateStore? stateStore = null,
        HeaderChainSync? headerSync = null)
    {
        _vault = vault;
        _reader = reader;
        _stateStore = stateStore ?? new InMemoryWalletStateStore();
        _rotation = new AddressRotation(reader, _stateStore, verifier);
        _pipeline = new LiteSendPipeline(relay, p2pFallback, p2pNodes, verifier);
        _headerSync = headerSync;
        _verifier = verifier;
    }

    /// <summary>Convenience ctor for the common case of one full data source.</summary>
    public LiteWalletService(ISeedVault vault, ILiteWalletData data,
        IP2PBroadcaster? p2pFallback = null, IReadOnlyList<string>? p2pNodes = null,
        LiteTxVerifier? verifier = null, IWalletStateStore? stateStore = null,
        HeaderChainSync? headerSync = null)
        : this(vault, data, data, p2pFallback, p2pNodes, verifier, stateStore, headerSync) { }

    /// <summary>The height verified by proof-of-work header sync (0 when unavailable) — the UI
    /// can surface "verified to N" so a user sees the maturity gate isn't just gateway-trusted.</summary>
    public long VerifiedTipHeight => _headerSync?.Available == true ? _headerSync.VerifiedTipHeight : 0;

    // ── Post-quantum activation gate (PQ_SIGNATURES.md §3.6, §7, §13 decision 8) ────────
    // Until H_Q is chosen AND reached, a BQ address must not be paid (the coin would park
    // until the fork), a P2PQH coin must not be spent (every node rejects it), and no BQ
    // receive address is offered. The gate is a wallet-side courtesy — the daemon enforces
    // the real rule — so a gateway-claimed tip is good enough to open it.

    /// <summary>H_Q as this wallet knows it (the chain constant; settable so the test suite
    /// can exercise the post-activation paths before the height is chosen).</summary>
    internal long? PqActivationHeight { get; set; } = BlazecoinChain.PqSigActivationHeight;

    private long _observedTip; // best tip seen in utxo reads (blockHeight + confirmations − 1)

    /// <summary>The highest chain tip this wallet has evidence of: the PoW-verified header tip
    /// when header sync runs, else the tip implied by the newest confirmed coin it has read.</summary>
    public long KnownTipHeight => Math.Max(VerifiedTipHeight, Volatile.Read(ref _observedTip));

    /// <summary>True once the post-quantum fork has an activation height AND the known tip has reached it.</summary>
    public bool PqActivated => PqActivationHeight is long h && KnownTipHeight >= h;

    /// <summary>Why post-quantum addresses can't be used right now (activation not chosen /
    /// not reached) — the text the Receive and Send surfaces show.</summary>
    public string PqUnavailableReason => PqActivationHeight is long h
        ? $"Post-quantum (BQ…) addresses become available after the post-quantum fork activates at block {h:N0}."
        : "Post-quantum (BQ…) addresses become available after the post-quantum fork; its activation height hasn't been set yet.";

    /// <summary>Activation check that first learns the tip if it hasn't yet (one utxo sweep) —
    /// for pages that ask before any balance read has happened.</summary>
    public async Task<bool> IsPqActivatedAsync(CancellationToken ct = default)
    {
        if (PqActivationHeight == null) return false;
        if (PqActivated) return true;
        await GetAllUtxosAsync(ct);
        return PqActivated;
    }

    private void ObserveTip(IEnumerable<LiteChainUtxo> utxos)
    {
        long tip = 0;
        foreach (var u in utxos)
            if (u.Confirmations > 0 && u.BlockHeight > 0)
                tip = Math.Max(tip, u.BlockHeight + u.Confirmations - 1);
        if (tip > Volatile.Read(ref _observedTip)) Volatile.Write(ref _observedTip, tip);
    }

    /// <summary>The newest revealed post-quantum (BQ…) receive address, or null when none has
    /// been revealed. Never offered before activation (<see cref="RevealNextPqAddressAsync"/>).</summary>
    public string? PqAddress => _rotation.CurrentPqAddress(RequireWallet());

    /// <summary>Every revealed BQ address, oldest first.</summary>
    public IReadOnlyList<string> PqAddresses => _rotation.PqAddresses(RequireWallet()).ToList();

    /// <summary>How many BQ addresses have been revealed (0 until the user asks after the fork).</summary>
    public int PqRevealedCount => _rotation.PqRevealedCount;

    /// <summary>Reveals the next BQ receive address — refused with <see cref="PqUnavailableReason"/>
    /// before activation, then under the same gap-limit discipline as legacy slots.</summary>
    public async Task<(bool Ok, string? Error)> RevealNextPqAddressAsync(CancellationToken ct = default)
    {
        var wallet = RequireWallet();
        if (!await IsPqActivatedAsync(ct)) return (false, PqUnavailableReason);
        return await _rotation.RevealNextPqAsync(wallet, ct);
    }

    /// <summary>
    /// Confirms a transaction against the wallet's OWN verified header chain (the per-tx M3
    /// polish): its merkle proof is verified, then the proof's block is located in the verified
    /// chain, yielding a TRUSTLESS block height and confirmation count that trust no gateway
    /// claim. The tx-detail view surfaces this. Available only in gateway mode (a personal node's
    /// own chainstate is already authoritative). When the block isn't in the verified window yet,
    /// <see cref="LiteChainProof.OnVerifiedChain"/> is false but the proof itself still stands.
    /// </summary>
    public async Task<LiteChainProof> VerifyTransactionOnChainAsync(string txId, CancellationToken ct = default)
    {
        if (_verifier == null)
            return new LiteChainProof(false, false, null, null, "This wallet trusts its own node here, so no proof is needed.");

        // Make sure the verified window is as current as it can be before we look the block up.
        if (_headerSync != null) await _headerSync.SyncAsync(ct);

        var inc = await _verifier.VerifyChainInclusionAsync(txId, ct);
        int? confs = inc.Height is long h && _headerSync != null
            ? _headerSync.ConfirmationsForVerifiedHeight(h)
            : null;
        return new LiteChainProof(inc.ProofValid, inc.OnVerifiedChain, inc.Height, confs, inc.Error);
    }

    /// <summary>Confirmations to trust for maturity: capped to the PoW-verified tip when header
    /// sync is live, else the gateway's own count.</summary>
    private int TrustedConfirmations(LiteChainUtxo u) =>
        _headerSync?.EffectiveConfirmations(u.Confirmations, u.BlockHeight) ?? u.Confirmations;

    /// <summary>False when the data source can't serve history (bare personal node) —
    /// the UI must say "unsupported here", never "no activity".</summary>
    public bool SupportsHistory => _reader.SupportsHistory;

    /// <summary>True once a wallet is unlocked in this session.</summary>
    public bool IsUnlocked => _wallet != null;

    /// <summary>The CURRENT receive address (latest revealed rotation slot). Throws when locked.</summary>
    public string Address => RequireWallet().GetReceiveAddress(_rotation.RevealedCount - 1);

    /// <summary>How many receive addresses have been revealed (always ≥ 1 once unlocked).</summary>
    public int RevealedCount => _rotation.RevealedCount;

    /// <summary>Every revealed receive address, oldest first. All of them stay live —
    /// coins sent to an older address are still found and spent.</summary>
    public IReadOnlyList<string> Addresses
    {
        get
        {
            var wallet = RequireWallet();
            return Enumerable.Range(0, _rotation.RevealedCount).Select(i => wallet.GetReceiveAddress(i)).ToList();
        }
    }

    /// <summary>Every address the wallet OWNS coins on: the revealed receive addresses plus
    /// the used internal-chain change addresses. This is the set to aggregate balances,
    /// resolve keys, and select spendable coins over — receive-only <see cref="Addresses"/>
    /// is the public share/receive view.</summary>
    private IReadOnlyList<string> OwnedAddresses
    {
        get
        {
            var wallet = RequireWallet();
            // Post-quantum addresses join the owned set only once the fork is active: before
            // that a P2PQH coin is unspendable by every node, so counting it would show a
            // balance the wallet cannot move.
            var pq = PqActivated ? _rotation.PqAddresses(wallet) : [];
            return Addresses.Concat(_rotation.UsedChangeAddresses(wallet)).Concat(pq).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SessionSentTxIds => _pipeline.SessionSentTxIds;

    private LiteHdWallet RequireWallet() =>
        _wallet ?? throw new InvalidOperationException("No wallet is unlocked.");

    public Task<bool> HasWalletAsync() => _vault.HasWalletAsync();

    /// <summary>Creates a brand-new wallet, stores it in the vault, and unlocks it.
    /// Returns the mnemonic for the one-time backup ceremony.</summary>
    /// <summary>Creates a new wallet with an optional BIP39 passphrase ("25th word" — an
    /// extra factor beyond the paper seed; empty = none).</summary>
    public async Task<string> CreateNewWalletAsync(int words = 12, string passphrase = "")
    {
        var wallet = LiteHdWallet.CreateNew(words, passphrase);
        await _vault.SaveMnemonicAsync(wallet.MnemonicWords);
        await _vault.SavePassphraseAsync(passphrase);
        // Unverified from the instant the seed exists — set BEFORE the backup ceremony so
        // killing the app mid-quiz still lands on the Home warning chip, not silence.
        await _stateStore.SetBackupVerifiedAsync(false);
        _wallet = wallet;
        await _rotation.InitializeNewAsync();
        return wallet.MnemonicWords;
    }

    /// <summary>Restores from a user-entered mnemonic (BIP39 checksum enforced) with an
    /// optional passphrase, stores + unlocks, then GAP-SCANS the chain to rediscover every
    /// used receive address. A wrong passphrase silently restores a DIFFERENT wallet.</summary>
    public async Task RestoreWalletAsync(string mnemonicWords, string passphrase = "")
    {
        var wallet = LiteHdWallet.Restore(mnemonicWords.Trim(), passphrase);
        await _vault.SaveMnemonicAsync(wallet.MnemonicWords);
        await _vault.SavePassphraseAsync(passphrase);
        // Typing the phrase IS proof of backup possession — restored wallets are verified.
        await _stateStore.SetBackupVerifiedAsync(true);
        _wallet = wallet;
        // The PQ chain is only worth scanning once the fork has a height — before that this
        // wallet can never have revealed a BQ address.
        await _rotation.DiscoverFromChainAsync(wallet, discoverPq: PqActivationHeight != null);
    }

    /// <summary>False until the recovery-phrase backup is PROVEN (quiz passed / restored
    /// from phrase). Drives the Home warning chip and the verify-backup flow.</summary>
    public Task<bool> IsBackupVerifiedAsync() => _stateStore.GetBackupVerifiedAsync();

    /// <summary>Records a passed backup check (the Setup or verify-backup quiz).</summary>
    public Task MarkBackupVerifiedAsync() => _stateStore.SetBackupVerifiedAsync(true);

    /// <summary>The stored mnemonic, for the verify-backup ceremony (view + quiz). Only
    /// while unlocked — the page sits behind the app lock like everything else.</summary>
    public async Task<string> RevealMnemonicAsync()
    {
        RequireWallet();
        return await _vault.LoadMnemonicAsync() ?? throw new InvalidOperationException("No mnemonic stored.");
    }

    /// <summary>Unlocks from the vault; false when no wallet is stored on this device.</summary>
    public async Task<bool> UnlockAsync()
    {
        if (_wallet != null) return true;
        var words = await _vault.LoadMnemonicAsync();
        if (string.IsNullOrWhiteSpace(words)) return false;
        _wallet = LiteHdWallet.Restore(words, await _vault.LoadPassphraseAsync());
        await _rotation.LoadAsync();
        return true;
    }

    /// <summary>Drops the in-memory keys WITHOUT touching the stored wallet — the re-lock
    /// primitive for heads whose vault can be re-locked (the web head's password vault on
    /// idle/manual lock). The next <see cref="UnlockAsync"/> re-reads the vault, so a vault
    /// that has been re-locked forces the unlock ceremony again. Rotation state is persisted
    /// and reloaded on unlock, so nothing is lost.</summary>
    public void Lock() => _wallet = null;

    /// <summary>Wipes the device wallet (the mnemonic backup remains the only recovery).</summary>
    public async Task ResetAsync()
    {
        await _vault.ClearAsync();
        await _rotation.ResetAsync();
        _wallet = null;
        _pipeline.Reset();
        _pendingSpent.Clear();
    }

    /// <summary>Reveals the next fresh receive address (rotation). See
    /// <see cref="AddressRotation.RevealNextAsync"/> for the gap-limit discipline.</summary>
    public Task<(bool Ok, string? Error)> RevealNextAddressAsync(CancellationToken ct = default)
        => _rotation.RevealNextAsync(RequireWallet(), ct);

    /// <summary>Aggregated on-chain summary across every revealed address; null when none
    /// has ever been seen on-chain.</summary>
    public async Task<LiteAddressSummary?> GetSummaryAsync(CancellationToken ct = default)
    {
        LiteAddressSummary? total = null;
        foreach (var address in Addresses)
        {
            var s = await _reader.GetAddressAsync(address, ct);
            if (s == null) continue;
            total = total == null ? s : new LiteAddressSummary(
                total.Balance + s.Balance,
                total.TotalReceived + s.TotalReceived,
                total.TotalSent + s.TotalSent,
                total.TxCount + s.TxCount);
        }
        return total;
    }

    /// <summary>
    /// Wallet history: each address's ledger merged into one wallet-level view, netted
    /// by txid. The gateway ledger is per-UTXO — a send that consumes N coins emits N
    /// "spent" rows sharing the spending txid — so netting is required even for a
    /// SINGLE-address wallet (2026-07-27: a 6 BLZ send rendered as −3/−2/−1 rows through
    /// the old single-address raw pass-through), and a transaction touching several of
    /// our addresses (rotation change, self-transfers) collapses the same way. Paging
    /// OVER-FETCHES from every address and windows the merged result (2026-07-25 audit
    /// C1: per-address page boundaries don't line up, so fetching "page N of each" both
    /// drops rows and mis-nets straddling transactions).
    /// </summary>
    public async Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(int page = 1, CancellationToken ct = default)
    {
        const int pageSize = 25;
        // OWNED addresses — receive AND used change — or the netting above cannot work: a
        // send's change lands on an internal-chain address, and without that +change row the
        // "sent" entry shows the whole consumed input total (an aggregate of the coins the
        // selection happened to grab) instead of the amount actually sent, and any later
        // send FUNDED by change is invisible entirely (its spend rows live on the change
        // address). Found 2026-08-17 on the web wallet; receive-only is the share view, not
        // the ledger view.
        var addresses = OwnedAddresses;

        // Enough rows from EACH address to fill the merged window even if one address
        // supplied everything (100 = the endpoint's pageSize cap; beyond ~4 pages of
        // multi-address history a wallet-level endpoint is the real fix).
        var fetch = Math.Min(100, page * pageSize + pageSize);
        var merged = new Dictionary<string, LiteHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in addresses)
        {
            foreach (var e in await _reader.GetHistoryAsync(address, 1, fetch, ct))
            {
                merged[e.TxId] = merged.TryGetValue(e.TxId, out var prior)
                    ? prior with { Amount = prior.Amount + e.Amount }
                    : e;
            }
        }
        return merged.Values
            .Select(e => e with { Type = e.Amount >= 0 ? "received" : "sent" })
            .OrderByDescending(e => e.BlockHeight)
            .ThenByDescending(e => e.TxId, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    /// <summary>Spendable satoshis right now: unspent, and past maturity if coinbase.</summary>
    public async Task<long> GetSpendableAsync(CancellationToken ct = default)
        => (await GetSpendableUtxosAsync(ct)).Sum(u => u.Utxo.Amount);

    /// <summary>
    /// The split behind the Balance card, from ONE utxo sweep — so the card can say WHY
    /// on-chain and spendable differ instead of guessing. The gap has three distinct
    /// causes with different user meanings: coinbases still ripening, incoming coins at
    /// 0-conf, and (by subtraction against the confirmed summary, which still counts
    /// outpoints the gateway has hidden as mempool-spent) coins leaving in an in-flight
    /// send. Same maturity rule as <see cref="GetSpendableUtxosAsync"/> — one source of truth.
    /// </summary>
    public async Task<LiteBalanceBreakdown> GetBalanceBreakdownAsync(CancellationToken ct = default)
    {
        long spendable = 0, immature = 0, incoming = 0;
        foreach (var u in await GetAllUtxosAsync(ct))
        {
            var conf = TrustedConfirmations(u.Utxo);
            if (conf >= (u.Utxo.IsCoinbase ? CoinbaseMaturity : 1)) spendable += u.Utxo.Amount;
            else if (conf == 0) incoming += u.Utxo.Amount;
            else immature += u.Utxo.Amount;   // a confirmed coinbase still ripening
        }
        return new LiteBalanceBreakdown(spendable, immature, incoming);
    }

    // ── Pending-spent overlay ────────────────────────────────────────────────────────────
    // Outpoints THIS session just spent, kept out of every UTXO read until the gateway's own
    // view reflects the spend. The gateway folds a new mempool tx into its UTXO answers on a
    // ~2 s poll, so for a moment after a successful broadcast it still returns the consumed
    // coins — which froze the Send page's "spendable" label at the pre-send value and, worse,
    // let a rapid second send re-select the just-spent inputs (a guaranteed conflict at
    // broadcast). Same idea as SessionSentTxIds, applied to inputs instead of txids.
    //
    // Self-cleaning: when a fetch no longer contains a tracked outpoint the gateway has caught
    // up and the entry is dropped. TTL backstop: if a "successful" broadcast in fact never
    // propagated, the coins would otherwise be hidden forever — after PendingSpentTtl we stop
    // filtering and let the gateway's answer stand (blocks are 30 s; 2 min is generous).
    internal static readonly TimeSpan PendingSpentTtl = TimeSpan.FromMinutes(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _pendingSpent =
        new(StringComparer.OrdinalIgnoreCase);

    private static string OutpointKey(string txId, int vout) => $"{txId}:{vout}";

    private void MarkPendingSpent(IEnumerable<LiteUtxo> inputs)
    {
        var now = DateTime.UtcNow;
        foreach (var u in inputs) _pendingSpent[OutpointKey(u.TxId, u.Vout)] = now;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LiteOwnedUtxo>> GetAllUtxosAsync(CancellationToken ct = default)
    {
        // Advance the PoW-verified tip in the background (self-throttled; no-op without a feed)
        // so the maturity clamp below sharpens over time without blocking the balance read.
        _ = _headerSync?.SyncAsync(CancellationToken.None);

        var wallet = RequireWallet();
        var owned = new List<LiteOwnedUtxo>();
        foreach (var address in Addresses.Concat(_rotation.UsedChangeAddresses(wallet))) // receive + used change
            owned.AddRange((await _reader.GetUtxosAsync(address, ct)).Select(u => new LiteOwnedUtxo(address, u)));

        // The legacy coins just read tell us the tip; only then can the activation gate open
        // and the post-quantum addresses be read in the SAME sweep (no stale first read).
        ObserveTip(owned.Select(u => u.Utxo));
        if (PqActivated)
        {
            foreach (var address in _rotation.PqAddresses(wallet))
                owned.AddRange((await _reader.GetUtxosAsync(address, ct)).Select(u => new LiteOwnedUtxo(address, u)));
            ObserveTip(owned.Select(u => u.Utxo));
        }

        if (_pendingSpent.IsEmpty) return owned;

        // Reconcile the overlay against this fresh gateway view, then filter with what's left.
        var now = DateTime.UtcNow;
        var present = owned.Select(u => OutpointKey(u.Utxo.TxId, u.Utxo.OutputIndex))
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, markedAt) in _pendingSpent)
        {
            if (!present.Contains(key) || now - markedAt > PendingSpentTtl)
                _pendingSpent.TryRemove(key, out _);
        }
        return _pendingSpent.IsEmpty
            ? owned
            : owned.Where(u => !_pendingSpent.ContainsKey(OutpointKey(u.Utxo.TxId, u.Utxo.OutputIndex))).ToList();
    }

    private async Task<List<LiteOwnedUtxo>> GetSpendableUtxosAsync(CancellationToken ct)
        // Coinbase needs full maturity; everything else needs ≥1 confirmation. Confirmations are
        // capped to the PoW-verified tip (M3) so a gateway can't inflate them to make an immature
        // coinbase look spendable. Excluding 0-conf (security audit S7) keeps a gateway-fabricated
        // mempool output out of the spendable balance AND sidesteps unconfirmed-txid malleability
        // on this zero-fee chain — the verifier would reject a 0-conf input at spend time anyway.
        => (await GetAllUtxosAsync(ct))
            .Where(u => TrustedConfirmations(u.Utxo) >= (u.Utxo.IsCoinbase ? CoinbaseMaturity : 1))
            .ToList();

    /// <summary>
    /// Send a specific amount: select mature coins (largest-first) to cover it, sign
    /// client-side at zero fee (the chain's normal case), relay through the gateway.
    /// </summary>
    public async Task<LiteSendResult> SendAsync(string destinationAddress, long amountSatoshis, CancellationToken ct = default)
    {
        var spendable = await GetSpendableUtxosAsync(ct);
        var available = spendable.Sum(u => u.Utxo.Amount);
        if (available < amountSatoshis)
            return new LiteSendResult(false, null,
                $"Insufficient spendable balance: {AmountText(available)} BLZ available, {AmountText(amountSatoshis)} BLZ requested.");

        // Largest-first keeps input counts (and tx size) small on a zero-fee chain.
        var inputs = new List<LiteUtxo>();
        long gathered = 0;
        foreach (var u in spendable.OrderByDescending(u => u.Utxo.Amount))
        {
            inputs.Add(ToInput(u));
            gathered += u.Utxo.Amount;
            if (gathered >= amountSatoshis) break;
        }

        if (PqSpendGate(destinationAddress, inputs) is string refused)
            return new LiteSendResult(false, null, refused);

        // Change goes to a FRESH internal-chain address (BIP44 change chain) — never a receive
        // address handed to payers; the pipeline advances the chain only if change is created.
        var wallet = RequireWallet();
        var result = await _pipeline.ExecuteAsync(destinationAddress, inputs, amountSatoshis,
            _rotation.NextChangeAddress(wallet), WalletKeyResolver(), ct,
            onChangeUsed: () => _rotation.AdvanceChangeAsync(), pqKeyResolver: PqKeyResolver());
        if (result.Success) MarkPendingSpent(inputs); // keep the consumed coins out of reads until the gateway catches up
        return result;
    }

    private static LiteUtxo ToInput(LiteOwnedUtxo u)
        => new(u.Utxo.TxId, u.Utxo.OutputIndex, u.Utxo.Amount, u.Address, u.Utxo.ScriptPubKey);

    /// <summary>The activation gate for a spend: a BQ destination or a P2PQH input before
    /// H_Q is refused with a plain reason (§3.6 — early payments park, early spends fail).
    /// Null = the spend may proceed.</summary>
    private string? PqSpendGate(string destinationAddress, IEnumerable<LiteUtxo> inputs)
    {
        if (PqActivated) return null;
        if (Pq.PqAddress.TryDecode(destinationAddress) != null)
            return "This wallet can't pay a post-quantum (BQ…) address yet — coins sent before the post-quantum fork " +
                   "would be parked until it activates. " + PqUnavailableReason;
        if (inputs.Any(u => u.IsPostQuantum))
            return "Post-quantum coins can't be spent before the post-quantum fork activates — the network would reject the transaction. " + PqUnavailableReason;
        return null;
    }

    /// <summary>
    /// Send the ENTIRE spendable balance to one address (no change output). Because the
    /// chain is zero-fee the whole verified input total lands at the destination. The exact
    /// amount is only known after chain-verification, so max always sweeps every mature coin.
    /// </summary>
    public async Task<LiteSendResult> SendMaxAsync(string destinationAddress, CancellationToken ct = default)
    {
        var spendable = await GetSpendableUtxosAsync(ct);
        if (spendable.Count == 0)
            return new LiteSendResult(false, null, "There are no spendable coins to send.");

        var inputs = spendable.Select(ToInput).ToList();
        if (PqSpendGate(destinationAddress, inputs) is string refused)
            return new LiteSendResult(false, null, refused);

        var result = await _pipeline.ExecuteAsync(destinationAddress, inputs, amountSatoshis: null, Address, WalletKeyResolver(), ct,
            pqKeyResolver: PqKeyResolver());
        if (result.Success) MarkPendingSpent(inputs); // keep the swept coins out of reads until the gateway catches up
        return result;
    }

    /// <summary>
    /// What a legacy WIF private key (2014-era wallet export, prefix 154) holds on-chain —
    /// shown to the user BEFORE committing to a sweep.
    /// </summary>
    public async Task<LiteWifCheck> CheckWifAsync(string wif, CancellationToken ct = default)
    {
        var holding = await LoadWifHoldingAsync(wif, ct);
        if (holding == null)
            return new LiteWifCheck(false, "That doesn't look like a valid Blazecoin private key (WIF). Check for typos — keys are case-sensitive.", null, 0, 0, 0);

        try
        {
            var spendable = holding.Spendable.Sum(u => u.Amount);
            var immature = holding.All.Sum(u => u.Amount) - spendable;
            return new LiteWifCheck(true, null, holding.Address, spendable, immature, holding.All.Count);
        }
        finally { holding.Key.Dispose(); } // the check only needs the address; wipe the key now
    }

    /// <summary>
    /// Sweeps EVERYTHING a legacy WIF key holds into this wallet's current receive address:
    /// chain-verify the old coins (C1/M3), sign with the imported key on-device, broadcast.
    /// The imported key is held only for the duration of the call — the mnemonic remains the
    /// wallet's single backup.
    /// </summary>
    public async Task<LiteSendResult> SweepWifAsync(string wif, CancellationToken ct = default)
    {
        var destination = Address; // requires an unlocked wallet before any chain work
        var holding = await LoadWifHoldingAsync(wif, ct);
        if (holding == null)
            return new LiteSendResult(false, null, "That doesn't look like a valid Blazecoin private key (WIF).");

        try
        {
            if (holding.Spendable.Count == 0)
                return new LiteSendResult(false, null, "That key holds no spendable coins (freshly mined coins need 30 confirmations first).");

            var inputs = holding.Spendable.Select(u => new LiteUtxo(u.TxId, u.OutputIndex, u.Amount, holding.Address)).ToList();
            return await _pipeline.ExecuteAsync(destination, inputs, amountSatoshis: null, destination,
                keyResolver: _ => holding.Key, ct);
        }
        finally { holding.Key.Dispose(); } // wipe the imported key the moment the sweep is done
    }

    private sealed record WifHolding(Key Key, string Address, IReadOnlyList<LiteChainUtxo> All, List<LiteChainUtxo> Spendable);

    /// <summary>Shared WIF parse + chain lookup for check and sweep (one truth for both).
    /// The caller OWNS the returned <see cref="WifHolding.Key"/> and must dispose it —
    /// NBitcoin's Key zeroizes its secret on Dispose (security audit S5).</summary>
    private async Task<WifHolding?> LoadWifHoldingAsync(string wif, CancellationToken ct)
    {
        Key key;
        try { key = new BitcoinSecret(wif.Trim(), BlazecoinNetwork.Instance).PrivateKey; }
        catch (FormatException) { return null; }

        var address = key.GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString();
        var utxos = await _reader.GetUtxosAsync(address, ct);
        var spendable = utxos.Where(u => !u.IsCoinbase || TrustedConfirmations(u) >= CoinbaseMaturity).ToList();
        return new WifHolding(key, address, utxos, spendable);
    }

    /// <summary>Maps every owned address — revealed receive slots AND used internal-chain
    /// change slots — to its derivation key, so change coins are spendable too.</summary>
    private Func<string, Key> WalletKeyResolver()
    {
        var wallet = RequireWallet();
        var byAddress = new Dictionary<string, Key>(StringComparer.Ordinal);
        for (var i = 0; i < _rotation.RevealedCount; i++)
            byAddress[wallet.GetReceiveAddress(i)] = wallet.GetPrivateKey(i);
        for (var i = 0; i < _rotation.ChangeCount; i++)
            byAddress[wallet.GetChangeAddress(i)] = wallet.GetPrivateKey(i, change: true);
        return address => byAddress.TryGetValue(address, out var key)
            ? key
            : throw new InvalidOperationException($"No private key for input address {address}.");
    }

    /// <summary>The post-quantum twin of <see cref="WalletKeyResolver"/>: maps every revealed
    /// BQ address to its slot and derives the ML-DSA-44 key on demand (each derivation is a
    /// fresh KeyGen from the slot seed — nothing PQ-secret is cached between sends).</summary>
    private Func<string, Pq.PqKey> PqKeyResolver()
    {
        var wallet = RequireWallet();
        var slotByAddress = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _rotation.PqRevealedCount; i++)
            slotByAddress[wallet.GetPqAddress(i)] = i;
        return address => slotByAddress.TryGetValue(address, out var slot)
            ? wallet.GetPqKey(slot)
            : throw new InvalidOperationException($"No post-quantum key for input address {address}.");
    }

    // ── Message signing (proof of address control) ──────────────────────────────────────

    /// <summary>
    /// Signs <paramref name="message"/> with the private key of one of the wallet's own
    /// receive addresses (defaults to the current one). The signature proves control of that
    /// address without revealing the key — for exchange listings, giveaways, disputes.
    /// Verification is the pure <see cref="BlazecoinMessage.Verify(string,string,string)"/>.
    /// </summary>
    public (string Address, string Signature) SignMessage(string message, string? withAddress = null)
    {
        var wallet = RequireWallet();
        var index = withAddress == null
            ? RevealedCount - 1
            : TryFindSlot(withAddress)
                ?? throw new InvalidOperationException("That address does not belong to this wallet.");
        return (wallet.GetReceiveAddress(index), wallet.SignMessage(index, message ?? string.Empty));
    }

    /// <summary>The revealed receive slot owning <paramref name="address"/>, or null — the
    /// ONE place address→slot resolution lives (audit round-3 F4).</summary>
    private int? TryFindSlot(string address)
    {
        var wallet = RequireWallet();
        for (var i = 0; i < RevealedCount; i++)
            if (wallet.GetReceiveAddress(i) == address) return i;
        return null;
    }

    private static string AmountText(long satoshis) => (satoshis / (decimal)BlazecoinChain.Coin).ToString("0.########");
}

using BlazecoinWallet.Lite.Data;
using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>Outcome of a send attempt, ready for the UI.</summary>
/// <param name="Via">"gateway" normally; "p2p" when the broadcast rode the last-resort
/// peer-to-peer fallback because every gateway was down.</param>
public sealed record LiteSendResult(bool Success, string? TxId, string? Error, string Via = "gateway");

/// <summary>Outcome of "Move everything to post-quantum": how many legacy coins (and satoshis) went
/// to the BQ… address, and how many legacy coins are still waiting (a move takes at most
/// <see cref="LiteWalletService.MaxInputsPerMove"/>; run it again for the rest once it confirms).</summary>
public sealed record LiteMoveResult(bool Success, string? TxId, int MovedCoins, long MovedSatoshis, int RemainingLegacyCoins, string? Error);

/// <summary>A spendable output together with the wallet address that owns it.</summary>
/// <param name="Verified">True when the coin's existence and value are PROVEN: the funding
/// transaction hashes to its id, pays this address this amount, and is mined on the chain — or
/// the data comes from the user's own node, whose chainstate is authoritative. False for a
/// coin the gateway lists that the wallet has not (yet) proven, and for every unconfirmed coin
/// (nothing to prove until it is mined). Only verified coins are spendable balance
/// (bug-bounty report 2026-11-06). A coin proven to exist is still NOT verified while the
/// question "has it been spent since?" is open or answered yes (sixth round).</param>
/// <param name="ProvenSpent">The gateway lists this coin, and a second gateway has PROVEN it was
/// already spent (the mined spending transaction, checked by the wallet). Never verified, never
/// counted — and a sign that the listing gateway is wrong or lying.</param>
public sealed record LiteOwnedUtxo(string Address, LiteChainUtxo Utxo, bool Verified = true, bool ProvenSpent = false);

/// <summary>The Balance card's split: what can be spent now, coinbases still ripening,
/// and unconfirmed incoming. Coins leaving in an in-flight send are the remainder
/// against the confirmed on-chain summary (they vanish from the utxo list while the
/// summary still counts them until the spend confirms).</summary>
/// <param name="SpendableLegacy">The part of <paramref name="Spendable"/> on legacy (ECDSA) B… addresses —
/// what "Move everything to post-quantum" would move (2026-09-28).</param>
/// <param name="Unverified">Confirmed coins the gateway lists that the wallet has NOT proven on
/// the chain — being checked, or failing the check. Never part of <paramref name="Spendable"/>
/// or <paramref name="ImmatureCoinbase"/>; shown on its own line so the user sees a claim, not
/// money (bug-bounty report 2026-11-06).</param>
/// <param name="ProvenSpent">Coins the gateway still lists that a second gateway has PROVEN were
/// already spent (bug-bounty report 2026-11-06, sixth round). Not part of any other figure;
/// shown on its own line, because it says the gateway in use cannot be relied on.</param>
public sealed record LiteBalanceBreakdown(long Spendable, long ImmatureCoinbase, long UnconfirmedIncoming,
    long SpendableLegacy = 0, long Unverified = 0, long ProvenSpent = 0);

/// <summary>What a WIF key holds on-chain — shown before the user commits to a sweep.</summary>
/// <param name="SpendableSatoshis">Coins PROVEN on the chain and mature — what a sweep moves now.</param>
/// <param name="ImmatureSatoshis">Proven mined coins still short of coinbase maturity.</param>
/// <param name="UnverifiedSatoshis">Confirmed coins the gateway lists for the key that the wallet
/// could NOT prove on the chain (bug-bounty report 2026-11-06, third round: this screen summed the
/// gateway's rows as served). A claim, never counted and never swept.</param>
/// <param name="UnconfirmedSatoshis">Coins the gateway lists with no confirmation yet — nothing to
/// prove until they are mined, so not recoverable yet.</param>
/// <param name="UnverifiedReason">Why the last unproven coin failed its check.</param>
public sealed record LiteWifCheck(bool Valid, string? Error, string? Address,
    long SpendableSatoshis, long ImmatureSatoshis, int UtxoCount,
    long UnverifiedSatoshis = 0, long UnconfirmedSatoshis = 0, string? UnverifiedReason = null);

/// <summary>A transaction fetched by id for display.</summary>
/// <param name="Details">The decoded transaction; null when it could not be fetched or decoded,
/// or when <paramref name="Mismatch"/> is set.</param>
/// <param name="Mismatch">The source answered with a transaction that does NOT hash to the id that
/// was asked for (bug-bounty report 2026-11-06, third round). Nothing from it may be shown.</param>
public sealed record LiteTxLookup(LiteTxDetails? Details, bool Mismatch);

/// <summary>A transaction checked against the wallet's own PoW-verified header chain.</summary>
/// <param name="ProofValid">The merkle+PoW inclusion proof is well-formed against its own header
/// (not a trust signal alone — see <see cref="LiteTxVerifier.ChainInclusion"/>).</param>
/// <param name="OnVerifiedChain">The proof's block sits on the verified header chain (trustless).</param>
/// <param name="Height">Trustless block height from the proof (null when outside the verified window).</param>
/// <param name="Confirmations">verifiedTip − Height + 1 when on-chain; null otherwise.</param>
/// <param name="Error">Why no proof could be established, when applicable.</param>
/// <param name="OffChain">The proof's block was proven NOT to be the chain's block — the gateway's data is wrong.</param>
public sealed record LiteChainProof(bool ProofValid, bool OnVerifiedChain, long? Height, int? Confirmations, string? Error, bool OffChain = false);

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
    private readonly ChainCheckQueue? _checks;   // null when the source is authoritative (no verifier)
    private readonly SpentCoinGuard? _spends;    // null when there is no verifier, or no way to ask a second gateway

    /// <summary>What a proven coin IS, read from the proof and not from the gateway's row: whether
    /// its funding transaction is a coinbase, and the height the header chain placed its block at
    /// (0 = no header feed). Keyed like the coin checks (bug-bounty report 2026-11-06, fourth round).</summary>
    private readonly record struct CoinFacts(bool IsCoinbase, long Height);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CoinFacts> _coinFacts =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _verifiedCoinsLoaded;
    private string? _verifiedCoinsSaved;
    private LiteHdWallet? _wallet;

    // How much chain-checking one read may wait for. An ordinary wallet's coins are proven
    // inside its first balance read; anything beyond this runs in the paced background queue.
    internal const int InlineCoinChecks = 6;
    internal static readonly TimeSpan InlineCoinBudget = TimeSpan.FromSeconds(4);
    /// <summary>How long one read waits for a second gateway's word on coins it has not been
    /// asked about yet (one request for up to fifty coins). Past it the coins simply are not
    /// counted yet, and the answer arrives through <see cref="VerificationChanged"/>.</summary>
    internal static readonly TimeSpan InlineSpendBudget = TimeSpan.FromSeconds(4);
    /// <summary>The same wait on the old-key screen, where the user asked and is watching.</summary>
    internal static readonly TimeSpan WifSpendBudget = TimeSpan.FromSeconds(12);
    /// <summary>The words shown for a coin a second gateway proved was already spent.</summary>
    internal const string SpentElsewhereMessage =
        "This coin has already been spent — a second gateway proved it. The gateway you are using still lists it; switch gateway in Settings.";
    /// <summary>The words shown while a second gateway's claim that a coin was spent is still being checked.</summary>
    internal const string SpendUnsettledMessage =
        "A second gateway reports this coin as already spent and the wallet has not been able to check that yet — try again in a minute.";
    internal const int InlineHistoryChecks = 4;
    internal static readonly TimeSpan InlineHistoryBudget = TimeSpan.FromSeconds(2);

    /// <summary>Most proven coins remembered across restarts (largest first) — bounds the store.</summary>
    internal const int MaxPersistedVerifiedCoins = 2000;

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
        if (verifier != null)
        {
            _checks = new ChainCheckQueue();
            _checks.Changed += () => VerificationChanged?.Invoke();
            if (reader is ISpendCrossCheck secondOpinion)
            {
                _spends = new SpentCoinGuard(secondOpinion, verifier);
                _spends.Changed += () => VerificationChanged?.Invoke();
            }
        }
    }

    /// <summary>Raised (on a worker thread) when background chain checks have settled more coins
    /// or history rows — a page showing balances re-reads them. Never raised for a source whose
    /// word is authoritative (personal node).</summary>
    public event Action? VerificationChanged;

    /// <summary>The read-path chain-check queue (tests pace and await it).</summary>
    internal ChainCheckQueue? Checks => _checks;

    /// <summary>The spent-coin guard (tests set its clock and intervals).</summary>
    internal SpentCoinGuard? Spends => _spends;

    /// <summary>Completes when no chain check is running or queued.</summary>
    internal async Task WhenVerificationIdleAsync()
    {
        if (_checks != null) await _checks.WhenIdleAsync();
        if (_spends != null) await _spends.WhenIdleAsync();
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
        await ReadOwnedUtxosAsync(ct);   // tip evidence only — nothing here counts a coin
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
    /// own chainstate is already authoritative). <paramref name="claimedBlockHeight"/> is the
    /// gateway's height for the tx (from history; checked, never trusted) — it lets a block the
    /// sync hasn't placed be linked to a trusted anchor. When the chain can't place the block,
    /// <see cref="LiteChainProof.OnVerifiedChain"/> is false and the tx is NOT independently
    /// confirmed — a standalone proof is cheap to forge, so never present ProofValid alone as verified.
    /// </summary>
    public async Task<LiteChainProof> VerifyTransactionOnChainAsync(string txId, long claimedBlockHeight = 0, CancellationToken ct = default)
    {
        if (_verifier == null)
            return new LiteChainProof(false, false, null, null, "This wallet trusts its own node here, so no proof is needed.");

        // Make sure the verified window is as current as it can be before we look the block up.
        if (_headerSync != null) await _headerSync.SyncAsync(ct);

        var inc = await _verifier.VerifyChainInclusionAsync(txId, claimedBlockHeight, ct);
        int? confs = inc.Height is long h && _headerSync != null
            ? _headerSync.ConfirmationsForVerifiedHeight(h)
            : null;
        return new LiteChainProof(inc.ProofValid, inc.OnVerifiedChain, inc.Height, confs, inc.Error, inc.OffChain);
    }

    /// <summary>
    /// A transaction for the detail page, bound to the id that was asked for: the id IS the hash
    /// of the transaction, so whatever the source returns must hash to it. Before (bug-bounty
    /// report 2026-11-06, third round) the page decoded whatever came back and showed its outputs,
    /// while the "verified on your own chain" badge was earned separately by the REQUESTED id —
    /// a gateway could put any other transaction's outputs under a genuine badge.
    /// </summary>
    public async Task<LiteTxLookup> GetTransactionAsync(string txId, CancellationToken ct = default)
    {
        var details = LiteTxDecoder.Decode(await _reader.GetRawTransactionHexAsync(txId, ct));
        if (details == null) return new LiteTxLookup(null, false);
        return string.Equals(details.TxId, (txId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            ? new LiteTxLookup(details, false)
            : new LiteTxLookup(null, true);
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
        _lookAhead = Task.CompletedTask;   // a brand-new seed has nothing further out to find
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
        _lookAhead = Task.CompletedTask;   // the scan above just looked; the next session looks again
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
    public void Lock()
    {
        _wallet = null;
        _lookAhead = null;
    }

    /// <summary>Wipes the device wallet (the mnemonic backup remains the only recovery).</summary>
    public async Task ResetAsync()
    {
        await _vault.ClearAsync();
        await _rotation.ResetAsync();
        _wallet = null;
        _lookAhead = null;
        _pipeline.Reset();
        _pendingSpent.Clear();
        _checks?.Clear();
        _spends?.Clear();
        _coinFacts.Clear();
        _verifiedCoinsLoaded = false;
        _verifiedCoinsSaved = null;
    }

    /// <summary>
    /// "Forget this wallet" — everything this device holds about it: the seed, the saved state
    /// (address positions, proven coins, the backup flag) and the app-lock PIN that guarded it.
    /// The web wallet's Forget used to clear the vault alone (bug-bounty report 2026-11-06, fourth
    /// round): the PIN stayed, so the lock screen came up over the setup page — and it has no
    /// "forgot PIN" path, because until now a PIN could not outlive its wallet — and the next
    /// wallet inherited the last one's saved state.
    /// </summary>
    public async Task ForgetDeviceWalletAsync(IPinLock? pin)
    {
        await ResetAsync();
        pin?.Clear();
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
        var window = merged.Values
            .Select(e => e with { Type = e.Amount >= 0 ? "received" : "sent" })
            .OrderByDescending(e => e.BlockHeight)
            .ThenByDescending(e => e.TxId, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return await MarkHistoryAsync(window, addresses, ct);
    }

    /// <summary>
    /// Stamps each row with where it stands against the chain (bug-bounty report 2026-11-06: a
    /// gateway could serve a "received" row for a transaction that does not exist, and the list
    /// showed it as income). A RECEIVED row is checked: its transaction must be on the chain and
    /// pay this wallet at least the amount shown. A few rows are checked inside this read, the
    /// rest in the background; until a row is proven it says so. Sent rows are not checked — a
    /// made-up one claims no income, and the balance never comes from history.
    /// </summary>
    private async Task<IReadOnlyList<LiteHistoryEntry>> MarkHistoryAsync(
        List<LiteHistoryEntry> rows, IReadOnlyList<string> ownedAddresses, CancellationToken ct)
    {
        if (_checks == null) return rows;   // own node: authoritative

        var provable = rows.Where(e => e.Amount > 0 && e.BlockHeight > 0).ToList();
        await _checks.EnsureAsync(provable.Select(e => ReceiptItem(e, ownedAddresses)).ToList(),
            InlineHistoryChecks, InlineHistoryBudget, ct);

        return rows.Select(e => e with
        {
            Check = e.Amount <= 0 ? LiteHistoryCheck.NotChecked
                : e.BlockHeight <= 0 ? LiteHistoryCheck.Unconfirmed
                : _checks.StateOf(ReceiptKey(e)) switch
                {
                    ChainCheckState.Verified => LiteHistoryCheck.Verified,
                    ChainCheckState.Unverified => LiteHistoryCheck.Unverified,
                    _ => LiteHistoryCheck.Pending,
                },
        }).ToList();
    }

    // The claimed amount is part of the key: a gateway that changes its story is checked again.
    private static string ReceiptKey(LiteHistoryEntry e) => $"r:{e.TxId}:{e.Amount}";

    private ChainCheckQueue.Item ReceiptItem(LiteHistoryEntry e, IReadOnlyList<string> ownedAddresses) =>
        new(ReceiptKey(e), async ct =>
        {
            var v = await _verifier!.VerifyReceiptAsync(e.TxId, ownedAddresses, e.Amount, e.BlockHeight, ct);
            return new ChainCheckQueue.Outcome(v.Verified, v.RealSatoshis, v.Error);
        }, Priority: e.BlockHeight);   // newest first, and behind every coin (a coin's priority is its value)

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
        long spendable = 0, immature = 0, incoming = 0, legacy = 0, unverified = 0, provenSpent = 0;
        foreach (var u in await GetAllUtxosAsync(ct))
        {
            var conf = TrustedConfirmations(u.Utxo);
            if (conf == 0) incoming += u.Utxo.Amount;           // nothing to prove until it is mined
            else if (u.ProvenSpent) provenSpent += u.Utxo.Amount;   // listed, and proven gone
            else if (!u.Verified) unverified += u.Utxo.Amount;  // the gateway's claim, not money
            else if (conf >= (u.Utxo.IsCoinbase ? CoinbaseMaturity : 1))
            {
                spendable += u.Utxo.Amount;
                if (!Pq.PqAddress.IsValid(u.Address)) legacy += u.Utxo.Amount;
            }
            else immature += u.Utxo.Amount;   // a confirmed coinbase still ripening
        }
        return new LiteBalanceBreakdown(spendable, immature, incoming, legacy, unverified, provenSpent);
    }

    // ── Address look-ahead ───────────────────────────────────────────────────────────────
    // A restore saves how many addresses it found in use, and the gateway it asked could have
    // under-reported (bug-bounty report 2026-11-06, third round): the saved count then hid every
    // coin further out, even after switching to an honest gateway, until the user restored again
    // — and nothing told them to. So once per unlocked session (a gateway change needs a restart,
    // so that is also once per gateway) the wallet looks one gap past each saved count for coins
    // it can PROVE and moves the count forward. It only ever moves forward, and only on proof, so
    // a lying gateway can neither shrink the view nor inflate it. Gateway mode only: the user's
    // own node is authoritative, and asking a bare node per address is expensive.
    private Task? _lookAhead;

    /// <summary>Whether the first coin read of a session starts the address look-ahead. The
    /// composition root switches it ON for every head (<c>AddLiteWallet</c>); a service built by
    /// hand leaves it off, so a caller that scripts its own data source is never read from a
    /// second flow it did not ask for.</summary>
    public bool LookAheadOnFirstRead { get; set; }

    private void StartLookAhead()
    {
        if (!LookAheadOnFirstRead || _verifier == null || _lookAhead != null || _wallet == null) return;
        _lookAhead = LookAheadAsync(_wallet);
    }

    private async Task LookAheadAsync(LiteHdWallet wallet)
    {
        try
        {
            await Task.Yield();   // never on the caller's read
            if (await _rotation.ExtendFromChainAsync(wallet, includePq: PqActivationHeight != null, CancellationToken.None))
                VerificationChanged?.Invoke();   // more addresses are owned now: pages re-read
        }
        catch { /* best effort — the next session looks again */ }
    }

    /// <summary>Completes when this session's address look-ahead has finished (tests await it).</summary>
    internal Task WhenLookAheadIdleAsync() => _lookAhead ?? Task.CompletedTask;

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
        foreach (var u in inputs)
        {
            _pendingSpent[OutpointKey(u.TxId, u.Vout)] = now;
            // What a second gateway said about the coin before is void now: if a gateway lists it
            // again once this overlay lapses, it is asked about afresh before it can count.
            _spends?.NoteOwnSpend(u.TxId, u.Vout);
        }
    }

    /// <summary>
    /// Every unspent output across every revealed address, immature included, each marked with
    /// whether the wallet has PROVEN it (<see cref="LiteOwnedUtxo.Verified"/>). In gateway mode
    /// the rows are a claim until checked: a confirmed coin is verified against the chain exactly
    /// as the spend path verifies an input (funding tx hashes to its id, pays this address, is
    /// mined on the chain), a few inside this read and the rest in the background, and a proven
    /// coin carries the value the chain commits to, whatever the gateway said.
    /// <para>That proves the coin was created. Whether it has been SPENT since is a separate
    /// question no proof of the coin can answer (bug-bounty report 2026-11-06, sixth round: a
    /// gateway that went on listing a spent coin had it counted and announced, every proof
    /// genuine), so each proven coin is also put to a second gateway (<see cref="SpentCoinGuard"/>):
    /// it counts once nobody has shown a spend, and a coin whose spend is proven is marked
    /// <see cref="LiteOwnedUtxo.ProvenSpent"/>.</para>
    /// </summary>
    public async Task<IReadOnlyList<LiteOwnedUtxo>> GetAllUtxosAsync(CancellationToken ct = default)
    {
        StartLookAhead();
        var owned = await ReadOwnedUtxosAsync(ct);
        if (_checks == null) return owned;                    // own node: authoritative

        await LoadVerifiedCoinsAsync();
        await _checks.EnsureAsync(
            owned.Where(u => u.Utxo.Confirmations > 0).Select(CoinItem).ToList(),
            InlineCoinChecks, InlineCoinBudget, ct);

        if (_spends != null)
            await _spends.EnsureAsync(
                owned.Where(u => u.Utxo.Confirmations > 0 && _checks.TryGetVerified(CoinKey(u), out _)).Select(Outpoint).ToList(),
                InlineSpendBudget, ct);

        var marked = owned.Select(u =>
        {
            if (u.Utxo.Confirmations <= 0 || !_checks.TryGetVerified(CoinKey(u), out var real))
                return u with { Verified = false };
            var proven = u with { Utxo = WithProvenFacts(u, real) };
            return StandingOf(u) switch
            {
                SpendStanding.Clear => proven with { Verified = true },
                SpendStanding.Spent => proven with { Verified = false, ProvenSpent = true },
                _ => proven with { Verified = false },   // still being asked about
            };
        }).ToList();
        await SaveVerifiedCoinsAsync(marked);
        return marked;
    }

    private static LiteOutpoint Outpoint(LiteOwnedUtxo u) => new(u.Utxo.TxId, u.Utxo.OutputIndex, u.Utxo.Source);

    /// <summary>Clear when there is no guard: a source with no second gateway to ask is exactly
    /// as trusted on this point as it was.</summary>
    private SpendStanding StandingOf(LiteOwnedUtxo u) =>
        _spends?.StandingOf(u.Utxo.TxId, u.Utxo.OutputIndex) ?? SpendStanding.Clear;

    /// <summary>A proven coin as the chain describes it: its real value, whether it is a coinbase
    /// (from its own transaction) and, where the header chain placed its block, its real height.
    /// The gateway's row supplied none of these — only the claim that was then checked. Before
    /// (bug-bounty report 2026-11-06, fourth round) the coinbase flag and height stayed the
    /// gateway's, so "isCoinbase: true" on an ordinary coin locked it as immature for as long
    /// as that gateway was used.</summary>
    private LiteChainUtxo WithProvenFacts(LiteOwnedUtxo u, long realSatoshis)
    {
        var utxo = u.Utxo with { Amount = realSatoshis };
        if (!_coinFacts.TryGetValue(CoinKey(u), out var facts)) return utxo;
        return utxo with
        {
            IsCoinbase = facts.IsCoinbase,
            BlockHeight = facts.Height > 0 ? facts.Height : utxo.BlockHeight,
        };
    }

    // The owning address is part of the key: the proof is "this output pays THIS address".
    private static string CoinKey(LiteOwnedUtxo u) => $"c:{u.Utxo.TxId}:{u.Utxo.OutputIndex}:{u.Address}";

    private ChainCheckQueue.Item CoinItem(LiteOwnedUtxo u) =>
        new(CoinKey(u), async ct =>
        {
            var v = await _verifier!.VerifyAsync(u.Utxo.TxId, u.Utxo.OutputIndex, u.Utxo.Amount,
                requireInclusionProof: true, expectedAddress: u.Address, claimedBlockHeight: u.Utxo.BlockHeight, ct: ct);
            if (v.Verified) _coinFacts[CoinKey(u)] = new CoinFacts(v.IsCoinbase, v.VerifiedHeight ?? 0);   // before the queue marks it proven
            return new ChainCheckQueue.Outcome(v.Verified, v.RealSatoshis, v.Error);
        }, Priority: u.Utxo.Amount);   // the coins that matter most first

    /// <summary>Adopts the coins proven in an earlier session (once per unlock of the store).</summary>
    private async Task LoadVerifiedCoinsAsync()
    {
        if (_verifiedCoinsLoaded) return;
        _verifiedCoinsLoaded = true;
        try
        {
            var text = await _stateStore.GetVerifiedCoinsAsync();
            _verifiedCoinsSaved = text;
            foreach (var entry in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // txid:vout:address:sats:coinbase(0|1):height. An entry in the four-field form
                // 1.3.3 wrote says nothing about the coin's kind, so it is not adopted — the coin
                // is simply proven again, once, and written back in full.
                var p = entry.Split(':');
                if (p.Length == 6 && p[0].Length == 64 && int.TryParse(p[1], out var vout) && vout >= 0 &&
                    long.TryParse(p[3], out var sats) && sats > 0 &&
                    (p[4] == "0" || p[4] == "1") && long.TryParse(p[5], out var height) && height >= 0)
                {
                    var key = $"c:{p[0]}:{vout}:{p[2]}";
                    _coinFacts[key] = new CoinFacts(p[4] == "1", height);
                    _checks!.Seed(key, sats);
                }
            }
        }
        catch { /* losing the remembered set only costs a re-verification */ }
    }

    /// <summary>Remembers the proven coins the gateway still lists. An empty view is not saved —
    /// a failed read returns no coins, and must not forget the proofs.</summary>
    private async Task SaveVerifiedCoinsAsync(IReadOnlyList<LiteOwnedUtxo> coins)
    {
        var proven = coins.Where(u => u.Verified && _coinFacts.ContainsKey(CoinKey(u))).ToList();
        if (proven.Count == 0) return;
        var text = string.Join(';', proven
            .OrderByDescending(u => u.Utxo.Amount)
            .Take(MaxPersistedVerifiedCoins)
            .OrderBy(u => u.Utxo.TxId, StringComparer.Ordinal).ThenBy(u => u.Utxo.OutputIndex)
            .Select(u =>
            {
                var facts = _coinFacts[CoinKey(u)];
                return $"{u.Utxo.TxId}:{u.Utxo.OutputIndex}:{u.Address}:{u.Utxo.Amount}:{(facts.IsCoinbase ? 1 : 0)}:{facts.Height}";
            }));
        if (text == _verifiedCoinsSaved) return;
        _verifiedCoinsSaved = text;
        try { await _stateStore.SetVerifiedCoinsAsync(text); }
        catch { /* best effort */ }
    }

    /// <summary>The gateway's rows as served — owned outputs minus this session's just-spent
    /// ones. NOTHING here is proven; callers that count coins go through
    /// <see cref="GetAllUtxosAsync"/>.</summary>
    private async Task<List<LiteOwnedUtxo>> ReadOwnedUtxosAsync(CancellationToken ct)
    {
        // Advance the PoW-verified tip in the background (self-throttled; no-op without a feed)
        // so the maturity clamp below sharpens over time without blocking the balance read.
        _ = _headerSync?.SyncAsync(CancellationToken.None);

        var wallet = RequireWallet();
        var owned = new List<LiteOwnedUtxo>();
        foreach (var address in Addresses.Concat(_rotation.UsedChangeAddresses(wallet))) // receive + used change
            owned.AddRange((await _reader.GetUtxosAsync(address, ct)).Select(u => new LiteOwnedUtxo(address, u)));

        // The coins read tell us the tip (when header sync is unavailable). Read the post-quantum
        // addresses too once the fork has a height — their coins are tip evidence as well, which
        // matters for a wallet whose coins are ALL on BQ… (the state "Move to post-quantum" leads
        // to): reading only legacy coins first would never learn the tip, never open the gate,
        // and hide the whole balance. BQ coins are OWNED only once the gate is open.
        ObserveTip(owned.Select(u => u.Utxo));
        if (PqActivationHeight != null && _rotation.PqRevealedCount > 0)
        {
            var pqOwned = new List<LiteOwnedUtxo>();
            foreach (var address in _rotation.PqAddresses(wallet))
                pqOwned.AddRange((await _reader.GetUtxosAsync(address, ct)).Select(u => new LiteOwnedUtxo(address, u)));
            ObserveTip(pqOwned.Select(u => u.Utxo));
            if (PqActivated) owned.AddRange(pqOwned);
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
        // And the coin must be PROVEN on the chain (2026-11-06): a row claiming confirmations is
        // still only a row.
        => (await GetAllUtxosAsync(ct))
            .Where(u => u.Verified)
            .Where(IsMature)
            .ToList();

    private bool IsMature(LiteOwnedUtxo u) =>
        TrustedConfirmations(u.Utxo) >= (u.Utxo.IsCoinbase ? CoinbaseMaturity : 1);

    /// <summary>The outcome of picking coins for a spend.</summary>
    /// <param name="Coins">Mature coins proven on the chain, largest first.</param>
    /// <param name="Candidates">How many mature coins the gateway listed (proven or not).</param>
    /// <param name="UnprovenSatoshis">What the gateway listed that could not be proven.</param>
    /// <param name="LastError">Why the last unproven coin failed its check.</param>
    private sealed record ProvenCoins(List<LiteOwnedUtxo> Coins, int Candidates, long UnprovenSatoshis, string? LastError)
    {
        public long Total => Coins.Sum(u => u.Utxo.Amount);

        /// <summary>Coins were listed and not one could be proven — the same words the send
        /// pipeline uses when an input fails its check.</summary>
        public string NothingProvenMessage =>
            $"Could not verify your coins against the chain: {LastError} Refresh and try again.";

        public string ShortfallMessage(long requested)
        {
            if (Coins.Count == 0 && UnprovenSatoshis > 0) return NothingProvenMessage;
            var text = $"Insufficient spendable balance: {AmountText(Total)} BLZ available, {AmountText(requested)} BLZ requested.";
            return UnprovenSatoshis > 0
                ? text + $" ({AmountText(UnprovenSatoshis)} BLZ could not be verified against the chain.)"
                : text;
        }
    }

    /// <summary>
    /// Picks mature coins for a spend, largest first, taking only coins PROVEN on the chain and
    /// proving them on demand — the user is waiting on exactly these, so their checks run now
    /// whatever the background queue is doing. A coin that fails its check is passed over, so a
    /// row the gateway made up cannot be selected (it used to be picked first, being the
    /// largest, and failed the whole send at signing). Stops once <paramref name="needSatoshis"/>
    /// is covered (null = take everything) or <paramref name="maxCoins"/> are picked.
    /// </summary>
    private async Task<ProvenCoins> SelectProvenCoinsAsync(long? needSatoshis, Func<LiteOwnedUtxo, bool>? filter,
        int maxCoins, CancellationToken ct)
    {
        var candidates = (await GetAllUtxosAsync(ct))
            .Where(IsMature)
            .Where(u => filter == null || filter(u))
            .OrderByDescending(u => u.Utxo.Amount)
            .ToList();

        var picked = new List<LiteOwnedUtxo>();
        long gathered = 0, unproven = 0;
        string? lastError = null;
        foreach (var candidate in candidates)
        {
            if (picked.Count >= maxCoins) break;
            if (needSatoshis is long need && gathered >= need) break;

            var coin = candidate;
            if (!coin.Verified)
            {
                var outcome = await _checks!.CheckNowAsync(CoinItem(candidate), ct);
                if (!outcome.Ok)
                {
                    unproven += candidate.Utxo.Amount;
                    lastError = outcome.Error;
                    continue;
                }
                // Proven to exist; now the other half — nobody may be able to show it was spent. Asked
                // afresh, whatever was answered before: the user is about to sign with this coin.
                var standing = _spends == null ? SpendStanding.Clear : await _spends.CheckNowAsync(Outpoint(candidate), ct);
                if (standing != SpendStanding.Clear)
                {
                    unproven += candidate.Utxo.Amount;
                    lastError = standing == SpendStanding.Spent ? SpentElsewhereMessage : SpendUnsettledMessage;
                    continue;
                }
                coin = candidate with { Verified = true, Utxo = candidate.Utxo with { Amount = outcome.Value } };
            }
            picked.Add(coin);
            gathered += coin.Utxo.Amount;
        }
        return new ProvenCoins(picked, candidates.Count, unproven, lastError);
    }

    /// <summary>
    /// Send a specific amount: select mature coins (largest-first) to cover it, sign
    /// client-side at zero fee (the chain's normal case), relay through the gateway.
    /// </summary>
    public async Task<LiteSendResult> SendAsync(string destinationAddress, long amountSatoshis, CancellationToken ct = default)
    {
        // Largest-first keeps input counts (and tx size) small on a zero-fee chain; only coins
        // proven on the chain are picked (see SelectProvenCoinsAsync).
        var pick = await SelectProvenCoinsAsync(amountSatoshis, filter: null, int.MaxValue, ct);
        if (pick.Total < amountSatoshis)
            return new LiteSendResult(false, null, pick.ShortfallMessage(amountSatoshis));
        var inputs = pick.Coins.Select(ToInput).ToList();

        if (PqSpendGate(destinationAddress, inputs) is string refused)
            return new LiteSendResult(false, null, refused);

        // Change: post-quantum once the fork is active (the wallet's current BQ… address), else a
        // FRESH internal-chain legacy address (BIP44 change chain) that only advances if change is
        // actually created — so a BQ change never leaves a gap in the legacy chain.
        var wallet = RequireWallet();
        var change = await ChangeTargetAsync(wallet);
        var result = await _pipeline.ExecuteAsync(destinationAddress, inputs, amountSatoshis,
            change.Address, WalletKeyResolver(), ct,
            onChangeUsed: change.IsPostQuantum ? null : () => _rotation.AdvanceChangeAsync(),
            pqKeyResolver: PqKeyResolver());
        if (result.Success) MarkPendingSpent(inputs); // keep the consumed coins out of reads until the gateway catches up
        return result;
    }

    private static LiteUtxo ToInput(LiteOwnedUtxo u)
        => new(u.Utxo.TxId, u.Utxo.OutputIndex, u.Utxo.Amount, u.Address, u.Utxo.ScriptPubKey, u.Utxo.BlockHeight);

    /// <summary>Where a spend's change goes (2026-09-28: it was legacy-only, so every spend of a BQ…
    /// coin returned its change to an ECDSA address).</summary>
    internal sealed record ChangeTarget(string Address, bool IsPostQuantum);

    private async Task<ChangeTarget> ChangeTargetAsync(LiteHdWallet wallet)
        => PqActivated
            ? new ChangeTarget(await _rotation.EnsurePqAddressAsync(wallet), IsPostQuantum: true)
            : new ChangeTarget(_rotation.NextChangeAddress(wallet), IsPostQuantum: false);

    /// <summary>
    /// Where incoming coins that the WALLET routes (a legacy-key sweep, a move to post-quantum)
    /// land: the current BQ… address once the fork is active, else the current legacy receive
    /// address. Payers still get whichever kind the Receive page shows.
    /// </summary>
    public async Task<string> GetInternalDestinationAsync(CancellationToken ct = default)
    {
        var wallet = RequireWallet();
        return await IsPqActivatedAsync(ct) ? await _rotation.EnsurePqAddressAsync(wallet) : Address;
    }

    /// <summary>How many legacy inputs one move takes at most — a legacy input is ~148 bytes, so this
    /// stays far below the 100 KB standard size (the rest moves on the next run).</summary>
    public const int MaxInputsPerMove = 400;

    /// <summary>
    /// Moves every spendable LEGACY coin (up to <see cref="MaxInputsPerMove"/>, largest first) to the
    /// wallet's post-quantum address in ONE send with ONE output and no change — the lite wallet's
    /// "Move everything to post-quantum". Refused before the fork. Coins already on BQ… are left alone.
    /// The pipeline broadcasts once (its duplicate recovery and P2P fallback included); the caller
    /// must not repeat a move whose result it could not see — rescan instead.
    /// </summary>
    public async Task<LiteMoveResult> MoveLegacyToPostQuantumAsync(CancellationToken ct = default)
    {
        var wallet = RequireWallet();
        var legacy = (await GetSpendableUtxosAsync(ct))
            .Where(u => !Pq.PqAddress.IsValid(u.Address))
            .OrderByDescending(u => u.Utxo.Amount)
            .ToList();
        if (!PqActivated)
            return new LiteMoveResult(false, null, 0, 0, legacy.Count, PqUnavailableReason);
        if (legacy.Count == 0)
            return new LiteMoveResult(false, null, 0, 0, 0, "There are no legacy coins to move — everything spendable is already on a post-quantum address.");

        // Largest first, proven on the chain (a legacy row the gateway made up is passed over).
        var pick = await SelectProvenCoinsAsync(needSatoshis: null,
            u => !Pq.PqAddress.IsValid(u.Address), MaxInputsPerMove, ct);
        if (pick.Coins.Count == 0)
            return new LiteMoveResult(false, null, 0, 0, legacy.Count, pick.NothingProvenMessage);
        var batch = pick.Coins;
        var inputs = batch.Select(ToInput).ToList();
        var destination = await _rotation.EnsurePqAddressAsync(wallet);
        var result = await _pipeline.ExecuteAsync(destination, inputs, amountSatoshis: null, destination,
            WalletKeyResolver(), ct, pqKeyResolver: PqKeyResolver());
        if (!result.Success)
            return new LiteMoveResult(false, null, 0, 0, legacy.Count, result.Error);
        MarkPendingSpent(inputs);
        return new LiteMoveResult(true, result.TxId, batch.Count, batch.Sum(u => u.Utxo.Amount), legacy.Count - batch.Count, null);
    }

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
        var pick = await SelectProvenCoinsAsync(needSatoshis: null, filter: null, int.MaxValue, ct);
        if (pick.Coins.Count == 0)
            return new LiteSendResult(false, null,
                pick.UnprovenSatoshis > 0 ? pick.NothingProvenMessage : "There are no spendable coins to send.");

        var inputs = pick.Coins.Select(ToInput).ToList();
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
            return new LiteWifCheck(true, null, holding.Address,
                holding.Spendable.Sum(u => u.Amount), holding.ImmatureSatoshis, holding.All.Count,
                holding.UnverifiedSatoshis, holding.UnconfirmedSatoshis, holding.UnverifiedReason);
        }
        finally { holding.Key.Dispose(); } // the check only needs the address; wipe the key now
    }

    /// <summary>
    /// Sweeps what a legacy WIF key PROVABLY holds into this wallet's current receive address:
    /// only coins already proven on the chain go in, so a row the gateway made up — or a genuine
    /// payment not yet mined — can no longer fail the whole sweep and strand the real coins
    /// (the pipeline verifies every input again before signing, C1/M3, and stops on any failure).
    /// Signed with the imported key on-device, then broadcast.
    /// The imported key is held only for the duration of the call — the mnemonic remains the
    /// wallet's single backup.
    /// </summary>
    public async Task<LiteSendResult> SweepWifAsync(string wif, CancellationToken ct = default)
    {
        // Post-quantum once the fork is active (the imported key's coins are ECDSA; the sweep is
        // the moment to put them behind ML-DSA), else the current legacy receive address.
        // Requires an unlocked wallet before any chain work.
        var destination = await GetInternalDestinationAsync(ct);
        var holding = await LoadWifHoldingAsync(wif, ct);
        if (holding == null)
            return new LiteSendResult(false, null, "That doesn't look like a valid Blazecoin private key (WIF).");

        try
        {
            if (holding.Spendable.Count == 0)
                return new LiteSendResult(false, null,
                    holding.UnverifiedSatoshis > 0
                        ? $"Could not verify this key's coins against the chain: {holding.UnverifiedReason} Refresh and try again, or switch gateway in Settings."
                        : holding.UnconfirmedSatoshis > 0
                            ? "This key's coins are not confirmed yet — sweep them after their first confirmation (about 30 seconds)."
                            : "That key holds no spendable coins (freshly mined coins need 30 confirmations first).");

            var inputs = holding.Spendable.Select(u => new LiteUtxo(u.TxId, u.OutputIndex, u.Amount, holding.Address, BlockHeight: u.BlockHeight)).ToList();
            return await _pipeline.ExecuteAsync(destination, inputs, amountSatoshis: null, destination,
                keyResolver: _ => holding.Key, ct);
        }
        finally { holding.Key.Dispose(); } // wipe the imported key the moment the sweep is done
    }

    /// <param name="All">The gateway's rows for the key, as served.</param>
    /// <param name="Spendable">Proven on the chain and mature, largest first, each carrying the
    /// value the chain commits to.</param>
    private sealed record WifHolding(Key Key, string Address, IReadOnlyList<LiteChainUtxo> All, List<LiteChainUtxo> Spendable,
        long ImmatureSatoshis, long UnverifiedSatoshis, long UnconfirmedSatoshis, string? UnverifiedReason);

    /// <summary>
    /// Shared WIF parse + chain lookup for check and sweep (one truth for both). In gateway mode
    /// the rows are a claim until checked, exactly as on the balance path: a coin counts only once
    /// its funding transaction hashes to its id, pays this key's address and is proven mined — and
    /// then at the value the chain commits to, whatever the gateway said (bug-bounty report
    /// 2026-11-06, third round: this flow summed the rows as served, so "Recoverable now" showed
    /// whatever a gateway listed, and one made-up row then failed the whole sweep). The checks run
    /// here, largest coin first — the user is waiting on exactly these.
    /// The caller OWNS the returned <see cref="WifHolding.Key"/> and must dispose it —
    /// NBitcoin's Key zeroizes its secret on Dispose (security audit S5).
    /// </summary>
    private async Task<WifHolding?> LoadWifHoldingAsync(string wif, CancellationToken ct)
    {
        Key key;
        try { key = new BitcoinSecret(wif.Trim(), BlazecoinNetwork.Instance).PrivateKey; }
        catch (FormatException) { return null; }

        var address = key.GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString();
        var utxos = await _reader.GetUtxosAsync(address, ct);

        var spendable = new List<LiteChainUtxo>();
        long immature = 0, unverified = 0, unconfirmed = 0;
        string? reason = null;
        var checkedCoins = 0;
        // The same second opinion the wallet's own coins get: a spent coin is not recoverable,
        // however genuine the proof that it once existed. One question for the coins a sweep can carry.
        if (_verifier != null && _spends != null)
            await _spends.EnsureAsync(
                utxos.Where(u => TrustedConfirmations(u) > 0).OrderByDescending(u => u.Amount).Take(MaxInputsPerMove)
                     .Select(u => new LiteOutpoint(u.TxId, u.OutputIndex, u.Source)).ToList(),
                WifSpendBudget, ct);
        foreach (var u in utxos.OrderByDescending(u => u.Amount))
        {
            var conf = TrustedConfirmations(u);
            if (conf <= 0) { unconfirmed += u.Amount; continue; }   // nothing to prove until it is mined

            var coin = u;
            if (_verifier != null)
            {
                if (checkedCoins >= MaxInputsPerMove)
                {
                    // One sweep cannot carry more inputs than this anyway: the rest wait for the next pass.
                    unverified += u.Amount;
                    reason ??= "This key holds more coins than one sweep can carry — sweep these, then check the key again.";
                    continue;
                }
                checkedCoins++;
                var v = await _verifier.VerifyAsync(u.TxId, u.OutputIndex, u.Amount,
                    requireInclusionProof: true, expectedAddress: address, claimedBlockHeight: u.BlockHeight, ct: ct);
                if (!v.Verified) { unverified += u.Amount; reason = v.Error; continue; }
                var standing = _spends?.StandingOf(u.TxId, u.OutputIndex) ?? SpendStanding.Clear;
                if (standing != SpendStanding.Clear)
                {
                    unverified += u.Amount;
                    reason = standing == SpendStanding.Spent ? SpentElsewhereMessage : SpendUnsettledMessage;
                    continue;
                }
                // The chain's value, kind and (where a header chain placed it) height — not the claim.
                coin = u with
                {
                    Amount = v.RealSatoshis,
                    IsCoinbase = v.IsCoinbase,
                    BlockHeight = v.VerifiedHeight ?? u.BlockHeight,
                };
                conf = TrustedConfirmations(coin);
            }

            if (coin.IsCoinbase && conf < CoinbaseMaturity) immature += coin.Amount;
            else spendable.Add(coin);
        }
        return new WifHolding(key, address, utxos, spendable, immature, unverified, unconfirmed, reason);
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

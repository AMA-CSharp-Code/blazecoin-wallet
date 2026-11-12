namespace BlazecoinWallet.Lite;

/// <summary>
/// The BIP44 external-chain rotation policy, extracted from LiteWalletService (2026-07-25
/// audit F1): owns the revealed-address count, the gap-limit reveal discipline, and the
/// restore-time chain discovery. The invariant this class exists to protect: a
/// mnemonic-only restore must ALWAYS rediscover every address that can hold funds — so
/// reveals stop at the gap limit, and discovery scans past interior gaps.
/// </summary>
internal sealed class AddressRotation
{
    /// <summary>BIP44 gap limit: restore scanning stops after this many consecutive unused
    /// addresses, so rotation refuses to reveal beyond it (funds past the gap would be
    /// invisible to a future restore).</summary>
    public const int GapLimit = 20;

    /// <summary>Safety ceiling on restore scanning (a wallet with 1000+ addresses is not a
    /// lite-wallet use case).</summary>
    private const int MaxScanIndex = 1000;

    /// <summary>Absolute cap on revealed addresses (security audit S8/M2 mitigation): a
    /// lying gateway that reports every slot "used" can otherwise push reveals arbitrarily
    /// far, inflating every subsequent all-address loop and widening the address-clustering
    /// leak. No real lite-wallet user reveals this many receive addresses; hitting it is a
    /// signal something is wrong. Stays &lt;= MaxScanIndex so restore can always recover them.</summary>
    public const int MaxRevealed = 200;

    private readonly Data.IChainReader _reader;
    private readonly IWalletStateStore _state;
    private readonly Data.LiteTxVerifier? _verifier;
    private int _revealedCount;
    private int _changeCount; // internal-chain change addresses used (BIP44 change chain)
    private int _pqCount;     // post-quantum (BQ…) receive addresses revealed (PQ_SIGNATURES.md §6 chain)

    /// <param name="verifier">When present (gateway mode), the REVEAL gate requires a
    /// wallet-VERIFIED receipt to treat an address as "used" — closing H1: a lying gateway
    /// reporting fake `TxCount` can no longer push reveals past a real gap so that a
    /// mnemonic-only restore under-discovers. Null (personal node) → own-chainstate TxCount
    /// is authoritative. Discovery is TxCount-based: a FALSE "used" only makes restore scan
    /// further, but a FALSE "unused" makes it stop short (bug-bounty report 2026-11-06, third
    /// round — the comment here used to claim "never less"). Two things answer that: restore
    /// asks a second gateway before it believes "unused" (<see cref="Data.IAddressUsageCrossCheck"/>),
    /// and <see cref="ExtendFromChainAsync"/> lets any later honest session find what a restore missed.</param>
    /// <param name="usageCrossCheck">That second gateway, handed in (audit 2026-11-09: it used to be
    /// found by testing what the reader happened to be, so a wrapped reader lost it silently).
    /// Null where there is nobody else to ask — a personal node.</param>
    public AddressRotation(Data.IChainReader reader, IWalletStateStore state, Data.LiteTxVerifier? verifier = null,
        Data.IAddressUsageCrossCheck? usageCrossCheck = null)
    {
        _reader = reader;
        _state = state;
        _verifier = verifier;
        _usageCrossCheck = usageCrossCheck;
    }

    private readonly Data.IAddressUsageCrossCheck? _usageCrossCheck;

    /// <summary>
    /// One reveal at a time, on either chain. The gap check reads the network and the counter
    /// moves after it, so two reveals that overlapped both passed the check on the same count
    /// and both moved it: with 19 unused slots the counter went to 21 and the wallet showed an
    /// address one past what a restore scans (bug-bounty report 2026-11-12). A page's own
    /// "busy" flag is not enough - a page left mid-reveal is replaced by a new one. The second
    /// caller now waits, then runs its check against the count the first one left.
    /// <para>The look-ahead moves the same counts, so it takes the gate for each move (not for its
    /// network reads): a count is compared and moved in one step, whoever moves it.</para>
    /// </summary>
    private readonly SemaphoreSlim _revealGate = new(1, 1);

    /// <summary>Moves a count FORWARD to <paramref name="target"/> under the gate, if it is still
    /// behind it. False when something else already moved it at least that far.</summary>
    private async Task<bool> MoveForwardAsync(Func<int> current, Func<int, Task> move, int target)
    {
        await _revealGate.WaitAsync();
        try
        {
            if (target <= current()) return false;
            await move(target);
            return true;
        }
        finally { _revealGate.Release(); }
    }

    /// <summary>How many receive addresses are revealed (always ≥ 1 once initialized).</summary>
    public int RevealedCount => Math.Max(1, _revealedCount);

    /// <summary>How many internal-chain change addresses have been used so far.</summary>
    public int ChangeCount => _changeCount;

    /// <summary>The NEXT change address to pay change to (internal chain, index =
    /// <see cref="ChangeCount"/>). The wallet only advances after a change output is actually
    /// created (see <see cref="AdvanceChangeAsync"/>), so the chain has no gaps.</summary>
    public string NextChangeAddress(LiteHdWallet wallet) => wallet.GetChangeAddress(_changeCount);

    /// <summary>Every change address the wallet has USED (internal chain 0..ChangeCount-1) —
    /// their coins are spendable and their keys must be resolvable.</summary>
    public IEnumerable<string> UsedChangeAddresses(LiteHdWallet wallet)
        // Explicit lambda, NOT `Select(wallet.GetChangeAddress)` — the method group binds to
        // LINQ's INDEXED Select overload (GetChangeAddress takes index + account), passing the
        // element AS the account and deriving the wrong chain.
        => Enumerable.Range(0, _changeCount).Select(i => wallet.GetChangeAddress(i));

    /// <summary>Move to a fresh change address after one was paid change.</summary>
    public async Task AdvanceChangeAsync()
    {
        _changeCount++;
        await _state.SetChangeAddressCountAsync(_changeCount);
    }

    // ── Post-quantum receive chain ───────────────────────────────────────────────────────
    // A THIRD chain beside receive/change: BQ… addresses (index 0..PqRevealedCount-1) under
    // the wallet's PQ master seed. Nothing is revealed until the user asks (and the service
    // only lets them ask once the fork is active — §13 decision 8), so a pre-fork wallet has
    // zero PQ addresses and pays nothing for the feature.

    /// <summary>How many post-quantum receive addresses have been revealed (0 until asked).</summary>
    public int PqRevealedCount => _pqCount;

    /// <summary>Every revealed BQ address, oldest first (all stay live, like legacy slots).</summary>
    public IEnumerable<string> PqAddresses(LiteHdWallet wallet)
        => Enumerable.Range(0, _pqCount).Select(i => wallet.GetPqAddress(i));

    /// <summary>The newest revealed BQ address, or null when none has been revealed yet.</summary>
    public string? CurrentPqAddress(LiteHdWallet wallet) => _pqCount == 0 ? null : wallet.GetPqAddress(_pqCount - 1);

    /// <summary>Reveals the next BQ address under the same gap-limit discipline as
    /// <see cref="RevealNextAsync"/> (a restore scans the PQ chain 20 deep too). The first
    /// reveal is always allowed. The activation gate lives in the service, not here.</summary>
    public async Task<(bool Ok, string? Error)> RevealNextPqAsync(LiteHdWallet wallet, CancellationToken ct = default)
    {
        await _revealGate.WaitAsync(ct);
        try { return await RevealNextPqCoreAsync(wallet, ct); }
        finally { _revealGate.Release(); }
    }

    private async Task<(bool Ok, string? Error)> RevealNextPqCoreAsync(LiteHdWallet wallet, CancellationToken ct)
    {
        if (_pqCount >= MaxRevealed)
            return (false, $"You've reached the maximum of {MaxRevealed} post-quantum addresses for one wallet. Use an existing address, or restore into a fresh wallet if you truly need more.");

        var unusedRun = 0;
        for (var i = _pqCount - 1; i >= 0 && unusedRun < GapLimit; i--)
        {
            if (await IsAddressVerifiablyUsedAsync(wallet.GetPqAddress(i), ct)) break;
            unusedRun++;
        }
        if (unusedRun >= GapLimit)
            return (false, $"You already have {GapLimit} fresh unused post-quantum addresses. Receive a payment on one before revealing more — a wallet restore only scans {GapLimit} unused addresses deep.");

        _pqCount++;
        await _state.SetPqAddressCountAsync(_pqCount);
        return (true, null);
    }

    /// <summary>
    /// The newest revealed BQ address, revealing the FIRST one when none is revealed yet (the
    /// first reveal is always allowed, so this never fails on a gap). Used as the destination
    /// for change and for moves into post-quantum: reusing one BQ address gives a quantum
    /// attacker nothing (an ML-DSA key is safe once seen), a restore finds it (the PQ chain is
    /// gap-scanned), and it keeps the legacy change chain free of gaps. The activation gate
    /// lives in the service, not here.
    /// </summary>
    public async Task<string> EnsurePqAddressAsync(LiteHdWallet wallet)
    {
        await _revealGate.WaitAsync();
        try
        {
            if (_pqCount == 0)
            {
                _pqCount = 1;
                await _state.SetPqAddressCountAsync(_pqCount);
            }
            return wallet.GetPqAddress(_pqCount - 1);
        }
        finally { _revealGate.Release(); }
    }

    /// <summary>A brand-new wallet starts at receive slot 0 with no change addresses used
    /// and no post-quantum addresses revealed.</summary>
    public async Task InitializeNewAsync()
    {
        _revealedCount = 1;
        _changeCount = 0;
        _pqCount = 0;
        await _state.SetRevealedAddressCountAsync(1);
        await _state.SetChangeAddressCountAsync(0);
        await _state.SetPqAddressCountAsync(0);
    }

    /// <summary>Routine unlock: trust the stored position; default to slot 0 when nothing
    /// is stored (every pre-rotation wallet) — chain rediscovery is the RESTORE flow's job,
    /// a routine unlock must not fire 20 network calls.</summary>
    public async Task LoadAsync()
    {
        _revealedCount = await _state.GetRevealedAddressCountAsync();
        if (_revealedCount < 1)
        {
            _revealedCount = 1;
            await _state.SetRevealedAddressCountAsync(1);
        }
        _changeCount = Math.Max(0, await _state.GetChangeAddressCountAsync());
        _pqCount = Math.Max(0, await _state.GetPqAddressCountAsync());
    }

    /// <summary>BIP44 discovery on restore: walk the external chain until
    /// <see cref="GapLimit"/> consecutive addresses have no on-chain activity;
    /// revealed = last used + 1 (min 1).</summary>
    /// <param name="discoverPq">Also gap-scan the post-quantum chain. The service passes
    /// true only once the fork has an activation height — before that no BQ address can
    /// have been revealed by this wallet, so the 20 extra reads would be wasted.</param>
    public async Task DiscoverFromChainAsync(LiteHdWallet wallet, CancellationToken ct = default, bool discoverPq = false)
    {
        _revealedCount = Math.Max(1, 1 + await ScanChainAsync(i => wallet.GetReceiveAddress(i), ct));
        await _state.SetRevealedAddressCountAsync(_revealedCount);

        // The internal change chain must be rediscovered too, or change coins from before the
        // restore would be unspendable (their keys unresolved) and their addresses reusable.
        // lastUsed = -1 (none) → count 0.
        _changeCount = 1 + await ScanChainAsync(i => wallet.GetChangeAddress(i), ct);
        await _state.SetChangeAddressCountAsync(_changeCount);

        // The post-quantum chain: same gap rule; none revealed → 0.
        _pqCount = discoverPq ? 1 + await ScanChainAsync(i => wallet.GetPqAddress(i), ct) : 0;
        await _state.SetPqAddressCountAsync(_pqCount);
    }

    /// <summary>
    /// The look-ahead that heals an under-discovered restore: from each chain's saved count, look
    /// <see cref="GapLimit"/> addresses further for a coin the wallet can PROVE is on the chain,
    /// and move the count past it (then look a further gap, and so on). The counts only ever move
    /// FORWARD, and only on proof — a gateway that lists made-up coins extends nothing, and one
    /// that hides coins leaves the counts where they were for the next, honest, session.
    /// Returns true when any count moved.
    /// </summary>
    public async Task<bool> ExtendFromChainAsync(LiteHdWallet wallet, bool includePq, CancellationToken ct = default)
    {
        var moved = false;

        var receive = await LookAheadAsync(i => wallet.GetReceiveAddress(i), RevealedCount, MaxRevealed, ct);
        moved |= await MoveForwardAsync(() => RevealedCount,
            async n => { _revealedCount = n; await _state.SetRevealedAddressCountAsync(n); }, receive);

        var change = await LookAheadAsync(i => wallet.GetChangeAddress(i), _changeCount, MaxScanIndex, ct);
        moved |= await MoveForwardAsync(() => _changeCount,
            async n => { _changeCount = n; await _state.SetChangeAddressCountAsync(n); }, change);

        if (includePq)
        {
            var pq = await LookAheadAsync(i => wallet.GetPqAddress(i), _pqCount, MaxRevealed, ct);
            moved |= await MoveForwardAsync(() => _pqCount,
                async n => { _pqCount = n; await _state.SetPqAddressCountAsync(n); }, pq);
        }
        return moved;
    }

    /// <summary>The count a chain should have: one past the furthest address at or beyond
    /// <paramref name="count"/> that holds a proven coin, looking <see cref="GapLimit"/> past the
    /// last find; <paramref name="count"/> itself when there is none.</summary>
    private async Task<int> LookAheadAsync(Func<int, string> address, int count, int ceiling, CancellationToken ct)
    {
        var found = count;
        var unused = 0;
        for (var i = count; i < ceiling && unused < GapLimit; i++)
        {
            if (await IsAddressVerifiablyUsedAsync(address(i), ct)) { found = i + 1; unused = 0; }
            else unused++;
        }
        return found;
    }

    /// <summary>Gap-scan a derivation chain; returns the index of the last USED address, or
    /// -1 if none. Stops after <see cref="GapLimit"/> consecutive unused.</summary>
    private async Task<int> ScanChainAsync(Func<int, string> address, CancellationToken ct)
    {
        var lastUsed = -1;
        var consecutiveUnused = 0;
        for (var i = 0; i < MaxScanIndex && consecutiveUnused < GapLimit; i++)
        {
            if (await IsAddressUsedAsync(address(i), ct)) { lastUsed = i; consecutiveUnused = 0; }
            else consecutiveUnused++;
        }
        return lastUsed;
    }

    /// <summary>
    /// Reveals the next fresh receive address. Refuses once <see cref="GapLimit"/>
    /// consecutive revealed addresses are still unused — beyond that a mnemonic-only
    /// restore would stop scanning before it and any coins sent there would look lost.
    /// </summary>
    public async Task<(bool Ok, string? Error)> RevealNextAsync(LiteHdWallet wallet, CancellationToken ct = default)
    {
        await _revealGate.WaitAsync(ct);
        try { return await RevealNextCoreAsync(wallet, ct); }
        finally { _revealGate.Release(); }
    }

    private async Task<(bool Ok, string? Error)> RevealNextCoreAsync(LiteHdWallet wallet, CancellationToken ct)
    {
        if (RevealedCount >= MaxRevealed)
            return (false, $"You've reached the maximum of {MaxRevealed} addresses for one wallet. Use an existing address, or restore into a fresh wallet if you truly need more.");

        // Walk back from the newest revealed slot counting unused addresses; one VERIFIED-used
        // address inside the window is enough to allow another reveal (H1 — a gateway's bare
        // "used" claim doesn't count).
        var unusedRun = 0;
        for (var i = RevealedCount - 1; i >= 0 && unusedRun < GapLimit; i--)
        {
            if (await IsAddressVerifiablyUsedAsync(wallet.GetReceiveAddress(i), ct)) break;
            unusedRun++;
        }
        if (unusedRun >= GapLimit)
            return (false, $"You already have {GapLimit} fresh unused addresses. Receive a payment on one before revealing more — a wallet restore only scans {GapLimit} unused addresses deep.");

        _revealedCount = RevealedCount + 1;
        await _state.SetRevealedAddressCountAsync(_revealedCount);
        return (true, null);
    }

    public async Task ResetAsync()
    {
        _revealedCount = 0;
        _changeCount = 0;
        _pqCount = 0;
        await _state.ClearAsync();
    }

    /// <summary>Discovery-grade "used": the source reports on-chain activity. Liberal in one
    /// direction by design — a false "used" only makes restore scan further. The other direction
    /// is the dangerous one (a false "unused" ends the scan early), so a source that can ask more
    /// than one gateway is asked to: the address is used if ANY of them says so.</summary>
    private async Task<bool> IsAddressUsedAsync(string address, CancellationToken ct)
    {
        if (_usageCrossCheck != null)
            return await _usageCrossCheck.IsAddressUsedByAnyGatewayAsync(address, ct);
        var summary = await _reader.GetAddressAsync(address, ct);
        return summary is { TxCount: > 0 };
    }

    /// <summary>Reveal-grade "used": in gateway mode, require a coin the wallet can PROVE is
    /// mined and owned by the address (merkle proof whose block is proven on the chain) — a
    /// lying gateway can't fabricate that, so it can't unlock reveals past a real gap (H1).
    /// With no header feed the proof stands alone and only raises the bar (a standalone header
    /// costs ~2^20 scrypt hashes to forge). Personal node → own chainstate
    /// is authoritative, so plain activity suffices. NOTE the accepted edge (documented): an
    /// address received-then-fully-spent has no UTXO to prove, so it reads as unused for
    /// reveal-gating — harmless (reveals stay conservative; restore still finds it by
    /// TxCount) and only reachable by a user revealing 20+ addresses and spending them all.</summary>
    private async Task<bool> IsAddressVerifiablyUsedAsync(string address, CancellationToken ct)
    {
        if (_verifier == null) return await IsAddressUsedAsync(address, ct);

        foreach (var u in await _reader.GetUtxosAsync(address, ct))
        {
            var v = await _verifier.VerifyAsync(u.TxId, u.OutputIndex, u.Amount,
                requireInclusionProof: true, expectedAddress: address, claimedBlockHeight: u.BlockHeight, ct: ct);
            if (v.Verified) return true;
        }
        return false;
    }
}

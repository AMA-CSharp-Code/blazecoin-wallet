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
    /// is authoritative. Discovery stays TxCount-based (liberal — a gateway lie there only
    /// makes restore scan further, never less).</param>
    public AddressRotation(Data.IChainReader reader, IWalletStateStore state, Data.LiteTxVerifier? verifier = null)
    {
        _reader = reader;
        _state = state;
        _verifier = verifier;
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

    /// <summary>Discovery-grade "used": the source reports on-chain activity. Liberal by
    /// design (used for restore scanning — a false "used" only makes restore scan further).</summary>
    private async Task<bool> IsAddressUsedAsync(string address, CancellationToken ct)
    {
        var summary = await _reader.GetAddressAsync(address, ct);
        return summary is { TxCount: > 0 };
    }

    /// <summary>Reveal-grade "used": in gateway mode, require a coin the wallet can PROVE is
    /// mined and owned by the address (merkle+PoW proof) — a lying gateway can't fabricate
    /// that, so it can't unlock reveals past a real gap (H1). Personal node → own chainstate
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
                requireInclusionProof: true, expectedAddress: address, ct);
            if (v.Verified) return true;
        }
        return false;
    }
}

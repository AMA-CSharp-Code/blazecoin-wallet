using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite;

/// <summary>What a set of listed coins comes to under the one rule for "this coin can be spent"
/// (<see cref="ReadPathVerification.ProveSpendableAsync"/>).</summary>
/// <param name="Spendable">Proven on the chain, not shown spent, and mature — largest first, each
/// carrying what the chain says of it (value, kind, height), not what the gateway's row said.</param>
/// <param name="ImmatureSatoshis">Proven coinbases still short of maturity.</param>
/// <param name="UnverifiedSatoshis">Confirmed coins that could not be proven, or that a second
/// gateway shows (or says and has not yet shown) were spent. A claim, never counted.</param>
/// <param name="UnconfirmedSatoshis">Listed with no confirmation yet: nothing to prove until mined.</param>
/// <param name="UnverifiedReason">Why the last unverified coin failed.</param>
internal sealed record SpendableCoins(List<LiteOwnedUtxo> Spendable, long ImmatureSatoshis, long UnverifiedSatoshis,
    long UnconfirmedSatoshis, string? UnverifiedReason)
{
    public long Total => Spendable.Sum(u => u.Utxo.Amount);
}

/// <summary>
/// What the wallet makes of what a gateway LISTS (audit 2026-11-09: taken out of the wallet
/// service, which keeps the orchestration). A row is a claim until checked, and this is where
/// the checks are made up and their results applied: a listed coin is proven against the chain
/// and put to a second gateway (<see cref="MarkCoinsAsync"/>), a received history row is proven
/// (<see cref="MarkHistoryAsync"/>), and the coins of a spend — the wallet's own or an old key's —
/// are held to ONE rule (<see cref="ProveSpendableAsync"/>) with ONE meaning of "mature"
/// (<see cref="IsMature"/>). With no verifier — the user's own node, whose word is
/// authoritative — every method hands back what it was given.
/// </summary>
internal sealed class ReadPathVerification
{
    /// <summary>Coinbase outputs are spendable after this many confirmations (daemon COINBASE_MATURITY).</summary>
    public const int CoinbaseMaturity = 30;

    // How much chain-checking one read may wait for. An ordinary wallet's coins are proven
    // inside its first balance read; anything beyond this runs in the paced background queue.
    internal const int InlineCoinChecks = 6;
    internal static readonly TimeSpan InlineCoinBudget = TimeSpan.FromSeconds(4);
    /// <summary>How long one read waits for a second gateway's word on coins it has not been
    /// asked about yet (one request for up to fifty coins). Past it the coins simply are not
    /// counted yet, and the answer arrives through the guard's Changed event.</summary>
    internal static readonly TimeSpan InlineSpendBudget = TimeSpan.FromSeconds(4);
    internal const int InlineHistoryChecks = 4;
    internal static readonly TimeSpan InlineHistoryBudget = TimeSpan.FromSeconds(2);
    /// <summary>How many coins of a spend are proven and asked about in one go: one spent-coin
    /// question's worth.</summary>
    internal const int ProveRound = 50;

    /// <summary>The words shown for a coin a second gateway proved was already spent.</summary>
    internal const string SpentElsewhereMessage =
        "This coin has already been spent — a second gateway proved it. The gateway you are using still lists it; switch gateway in Settings.";
    /// <summary>The words shown while a second gateway's claim that a coin was spent is still being checked.</summary>
    internal const string SpendUnsettledMessage =
        "A second gateway reports this coin as already spent and the wallet has not been able to check that yet — try again in a minute.";

    private readonly LiteTxVerifier? _verifier;
    private readonly ChainCheckQueue? _checks;   // null when the source is authoritative (no verifier)
    private readonly SpentCoinGuard? _spends;    // null when there is no verifier, or nobody else to ask
    private readonly VerifiedCoinLedger _ledger;
    private readonly HeaderChainSync? _headerSync;

    public ReadPathVerification(LiteTxVerifier? verifier, ChainCheckQueue? checks, SpentCoinGuard? spends,
        VerifiedCoinLedger ledger, HeaderChainSync? headerSync)
    {
        _verifier = verifier;
        _checks = verifier == null ? null : checks;
        _spends = verifier == null ? null : spends;
        _ledger = ledger;
        _headerSync = headerSync;
    }

    // ── maturity ─────────────────────────────────────────────────────────────────────────

    /// <summary>Confirmations to trust for maturity: capped to the PoW-verified tip when header
    /// sync is live, else the gateway's own count.</summary>
    public int TrustedConfirmations(LiteChainUtxo u) =>
        _headerSync?.EffectiveConfirmations(u.Confirmations, u.BlockHeight) ?? u.Confirmations;

    /// <summary>THE maturity rule: a coinbase needs <see cref="CoinbaseMaturity"/> trusted
    /// confirmations, anything else one. (Written once — the balance card, the send path and
    /// the old-key sweep each had their own copy.)</summary>
    public bool IsMature(LiteChainUtxo u) => TrustedConfirmations(u) >= (u.IsCoinbase ? CoinbaseMaturity : 1);

    // ── the checks ───────────────────────────────────────────────────────────────────────

    // The owning address is part of the key: the proof is "this output pays THIS address".
    internal static string CoinKey(LiteOwnedUtxo u) => $"c:{u.Utxo.TxId}:{u.Utxo.OutputIndex}:{u.Address}";

    // The claimed amount is part of the key: a gateway that changes its story is checked again.
    private static string ReceiptKey(LiteHistoryEntry e) => $"r:{e.TxId}:{e.Amount}";

    private static LiteOutpoint Outpoint(LiteOwnedUtxo u) => new(u.Utxo.TxId, u.Utxo.OutputIndex, u.Utxo.Source);

    private ChainCheckQueue.Item CoinItem(LiteOwnedUtxo u) =>
        new(CoinKey(u), async ct =>
        {
            var v = await _verifier!.VerifyAsync(u.Utxo.TxId, u.Utxo.OutputIndex, u.Utxo.Amount,
                requireInclusionProof: true, expectedAddress: u.Address, claimedBlockHeight: u.Utxo.BlockHeight, ct: ct);
            // Noted before the queue marks the coin proven.
            if (v.Verified) _ledger.Note(u.Utxo.TxId, u.Utxo.OutputIndex, u.Address, new CoinFacts(v.IsCoinbase, v.VerifiedHeight ?? 0));
            return new ChainCheckQueue.Outcome(v.Verified, v.RealSatoshis, v.Error);
        }, Priority: u.Utxo.Amount);   // the coins that matter most first

    private ChainCheckQueue.Item ReceiptItem(LiteHistoryEntry e, IReadOnlyList<string> ownedAddresses) =>
        new(ReceiptKey(e), async ct =>
        {
            var v = await _verifier!.VerifyReceiptAsync(e.TxId, ownedAddresses, e.Amount, e.BlockHeight, ct);
            return new ChainCheckQueue.Outcome(v.Verified, v.RealSatoshis, v.Error);
        }, Priority: e.BlockHeight);   // newest first, and behind every coin (a coin's priority is its value)

    /// <summary>A proven coin as the chain describes it: its real value, whether it is a coinbase
    /// (from its own transaction) and, where the header chain placed its block, its real height.
    /// The gateway's row supplied none of these — only the claim that was then checked. Before
    /// (bug-bounty report 2026-11-06, fourth round) the coinbase flag and height stayed the
    /// gateway's, so "isCoinbase: true" on an ordinary coin locked it as immature for as long
    /// as that gateway was used.</summary>
    private LiteChainUtxo WithProvenFacts(LiteOwnedUtxo u, long realSatoshis)
    {
        var utxo = u.Utxo with { Amount = realSatoshis };
        if (!_ledger.TryGetFacts(u.Utxo.TxId, u.Utxo.OutputIndex, u.Address, out var facts)) return utxo;
        return utxo with
        {
            IsCoinbase = facts.IsCoinbase,
            BlockHeight = facts.Height > 0 ? facts.Height : utxo.BlockHeight,
        };
    }

    // ── listed coins ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks each listed coin with whether the wallet has PROVEN it (<see cref="LiteOwnedUtxo.Verified"/>).
    /// A confirmed coin is verified against the chain exactly as the spend path verifies an
    /// input, a few inside this call and the rest in the background, and a proven coin carries
    /// what the chain commits to. That proves the coin was created; whether it has been SPENT
    /// since is put to a second gateway (<see cref="SpentCoinGuard"/>): it counts once nobody
    /// has shown a spend, and a coin whose spend is proven is marked <see cref="LiteOwnedUtxo.ProvenSpent"/>.
    /// The proven set is remembered for the next session (<see cref="VerifiedCoinLedger"/>).
    /// </summary>
    public async Task<IReadOnlyList<LiteOwnedUtxo>> MarkCoinsAsync(IReadOnlyList<LiteOwnedUtxo> owned, LiteHdWallet? wallet, CancellationToken ct)
    {
        if (_checks == null) return owned;                    // own node: authoritative

        foreach (var coin in await _ledger.LoadAsync(wallet))
            _checks.Seed($"c:{coin.TxId}:{coin.Vout}:{coin.Address}", coin.Satoshis);
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
            // Clear when there is no guard: a source with no second gateway to ask is exactly as
            // trusted on this point as it was.
            return (_spends?.StandingOf(u.Utxo.TxId, u.Utxo.OutputIndex) ?? SpendStanding.Clear) switch
            {
                SpendStanding.Clear => proven with { Verified = true },
                SpendStanding.Spent => proven with { Verified = false, ProvenSpent = true },
                _ => proven with { Verified = false },   // still being asked about
            };
        }).ToList();
        await _ledger.SaveAsync(wallet, marked.Where(u => u.Verified).Select(u => (u.Utxo.TxId, u.Utxo.OutputIndex, u.Address, u.Utxo.Amount)));
        return marked;
    }

    // ── history rows ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps each row with where it stands against the chain (bug-bounty report 2026-11-06: a
    /// gateway could serve a "received" row for a transaction that does not exist, and the list
    /// showed it as income). A RECEIVED row is checked: its transaction must be on the chain and
    /// pay this wallet at least the amount shown. A few rows are checked inside this read, the
    /// rest in the background; until a row is proven it says so. Sent rows are not checked — a
    /// made-up one claims no income, and the balance never comes from history.
    /// </summary>
    public async Task<IReadOnlyList<LiteHistoryEntry>> MarkHistoryAsync(
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

    // ── the coins of a spend ─────────────────────────────────────────────────────────────

    /// <summary>
    /// THE rule for "this coin can be spent", for a send from the wallet and for an old key's
    /// sweep alike (audit 2026-11-09 — the sweep used to prove coins its own way and read
    /// whatever the guard had last been told, up to ten minutes before). Largest first, each coin
    /// must be: confirmed; PROVEN on the chain — at once, whatever the background queue is
    /// doing, since the user is waiting on exactly these; not shown spent by a second gateway,
    /// asked AFRESH unless a read has just marked the coin proven and clear
    /// (<see cref="LiteOwnedUtxo.Verified"/>); and mature by what the proof says the coin is.
    /// A coin that fails is passed over and counted, so one row a gateway made up cannot fail a
    /// whole spend. Stops once <paramref name="needSatoshis"/> is covered (null = take
    /// everything) or <paramref name="maxCoins"/> are picked; coins not reached are in no total.
    /// </summary>
    public async Task<SpendableCoins> ProveSpendableAsync(IEnumerable<LiteOwnedUtxo> listed, long? needSatoshis,
        int maxCoins, CancellationToken ct)
    {
        var queue = listed.OrderByDescending(u => u.Utxo.Amount).ToList();
        var picked = new List<LiteOwnedUtxo>();
        long gathered = 0, immature = 0, unverified = 0, unconfirmed = 0;
        string? reason = null;

        var next = 0;
        while (next < queue.Count && picked.Count < maxCoins && !(needSatoshis is long need && gathered >= need))
        {
            // This round: enough coins to cover what is still needed, at most one question's worth.
            var round = new List<LiteOwnedUtxo>();
            long roundSatoshis = 0;
            while (next < queue.Count && round.Count < ProveRound && picked.Count + round.Count < maxCoins &&
                   !(needSatoshis is long stillNeeded && gathered + roundSatoshis >= stillNeeded))
            {
                var coin = queue[next++];
                if (TrustedConfirmations(coin.Utxo) <= 0) { unconfirmed += coin.Utxo.Amount; continue; }   // nothing to prove until it is mined
                round.Add(coin);
                roundSatoshis += coin.Utxo.Amount;
            }

            // 1. Proven to exist, with what the chain says of it.
            var proven = new List<(LiteOwnedUtxo Coin, bool AskAfresh)>();
            foreach (var coin in round)
            {
                if (_checks == null || coin.Verified) { proven.Add((coin, false)); continue; }
                var outcome = await _checks.CheckNowAsync(CoinItem(coin), ct);
                if (!outcome.Ok) { unverified += coin.Utxo.Amount; reason = outcome.Error; continue; }
                proven.Add((coin with { Utxo = WithProvenFacts(coin, outcome.Value) }, _spends != null));
            }

            // 2. The other half — nobody may be able to show it was spent. One question for the round.
            if (_spends != null)
                await _spends.CheckNowAsync(proven.Where(p => p.AskAfresh).Select(p => Outpoint(p.Coin)).ToList(), ct);

            foreach (var (coin, askedAfresh) in proven)
            {
                var standing = askedAfresh ? _spends!.StandingOf(coin.Utxo.TxId, coin.Utxo.OutputIndex) : SpendStanding.Clear;
                if (standing != SpendStanding.Clear)
                {
                    unverified += coin.Utxo.Amount;
                    reason = standing == SpendStanding.Spent ? SpentElsewhereMessage : SpendUnsettledMessage;
                    continue;
                }
                // 3. Mature, by what the coin has been proven to be.
                if (!IsMature(coin.Utxo)) { immature += coin.Utxo.Amount; continue; }
                picked.Add(coin with { Verified = true });
                gathered += coin.Utxo.Amount;
            }
        }
        return new SpendableCoins(picked, immature, unverified, unconfirmed, reason);
    }

    /// <summary>Forgets what was proven and remembered (wallet reset).</summary>
    public void Clear()
    {
        _checks?.Clear();
        _spends?.Clear();
        _ledger.Clear();
    }
}

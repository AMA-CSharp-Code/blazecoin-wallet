using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Services.Consolidation;

/// <summary>The spendable coins of ONE named wallet (never the active one: a consolidation is bound
/// to the wallet it was counted on).</summary>
public interface IConsolidationRpc
{
    /// <summary><c>listunspent 0</c> in <paramref name="wallet"/>: outputs the wallet can sign for and
    /// the node calls safe, INCLUDING its own that are still confirming (confirmations 0) - the
    /// card shows those and the planner leaves them out. Watch-only and unsafe rows are left out.</summary>
    Task<IReadOnlyList<UnspentOutput>> ListSpendableCoinsAsync(string wallet, CancellationToken ct = default);
}

/// <summary>What the wallet holds, as the Consolidate card shows it before anything is planned.</summary>
/// <param name="Coins">CONFIRMED coins: the ones a consolidation can merge now.</param>
/// <param name="ConfirmingCoins">Coins still waiting for their first confirmation (the outputs of an
/// earlier round, usually). They are left out of a plan; counted here so the card can say "too early"
/// before anything is planned (2026-11-12: a second round run early merged only part of the wallet).</param>
public sealed record CoinPicture(int Coins, long Satoshis, int LegacyCoins, int PostQuantumCoins, long LargestSatoshis,
    int ConfirmingCoins = 0, long ConfirmingSatoshis = 0)
{
    public static CoinPicture Of(IReadOnlyList<UnspentOutput> all)
    {
        var coins = all.Where(o => o.Confirmations >= 1).ToList();
        var confirming = all.Where(o => o.Confirmations < 1).ToList();
        var pq = coins.Count(o => BitcoinProtocol.IsPostQuantumAddress(o.Address));
        return new CoinPicture(coins.Count, coins.Sum(o => o.Satoshis), coins.Count - pq, pq,
            coins.Count == 0 ? 0 : coins.Max(o => o.Satoshis),
            confirming.Count, confirming.Sum(o => o.Satoshis));
    }

    /// <summary>What the card says while some coins are still confirming; null when none are.</summary>
    public string? ConfirmingNotice => ConfirmingCoins == 0 ? null
        : $"{ConfirmingCoins:N0} coin{(ConfirmingCoins == 1 ? " is" : "s are")} still confirming and will be left out. " +
          "To merge everything, wait a few minutes and count again.";

    /// <summary>The fewest transactions a payment of everything would need — more than one means the
    /// Send page can refuse a large payment with "inputs size exceeds the maximum weight".</summary>
    public int TransactionsToSpendEverything =>
        (int)Math.Ceiling((LegacyCoins * (double)TransactionSizes.LegacyInput
                           + PostQuantumCoins * (double)TransactionSizes.PostQuantumInput)
                          / ConsolidationPlanner.BatchByteBudget);
}

/// <summary>
/// The state and rules of the Send page's Consolidate card, kept out of the Razor view so every
/// rule is testable:
/// <list type="bullet">
/// <item>bound to ONE wallet — the one on screen when the coins are counted. The coins are read
///   there, the destination is minted there, every batch is signed there, and every step refuses
///   once that wallet is no longer the active one (the rule of the 2026-11-09 audit);</item>
/// <item>the destination is always a NEW address of that same wallet, never one typed in: a BQ…
///   address when the wallet can make one, a legacy address when it cannot. Nothing is planned
///   unless the wallet confirms the address is its own;</item>
/// <item>the coins are read again at Plan, so a plan is never built on a stale list;</item>
/// <item>broadcasting needs the "cannot be undone" acknowledgement, and the backup one for a BQ…
///   destination;</item>
/// <item>a plan is broadcast at most once — it is cleared before the first send — and each batch
///   gets exactly one send attempt (<see cref="SweepBroadcaster"/>): a timeout is UNKNOWN, never retried.</item>
/// </list>
/// </summary>
public sealed class ConsolidationSession
{
    public const string Label = "consolidated";

    private readonly IConsolidationRpc _coins;
    private readonly IBoundWalletRpc _addresses;
    private readonly ISweepBroadcaster _broadcaster;
    private readonly IWalletBinding _binding;

    public ConsolidationSession(IConsolidationRpc coins, IBoundWalletRpc addresses, ISweepBroadcaster broadcaster, IWalletBinding binding)
    {
        _coins = coins ?? throw new ArgumentNullException(nameof(coins));
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    /// <summary>The wallet this consolidation is bound to ("" = the node's default wallet endpoint); null until the coins are counted.</summary>
    public string? Wallet { get; private set; }
    public CoinPicture? Picture { get; private set; }
    /// <summary>The new address the coins go to; minted once per count and reused if the plan is redone.</summary>
    public string Destination { get; private set; } = "";
    public SweepPlan? Plan { get; private set; }
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();
    public string? Problem { get; private set; }
    public bool IsWorking { get; private set; }
    public bool IsSending { get; private set; }
    public SweepBroadcastResult? LastBroadcast { get; private set; }
    public bool RequiresBackupAck => Plan?.DestinationKind == SweepDestinationKind.PostQuantum;

    private bool Busy => IsWorking || IsSending;

    /// <summary>How many coins the planned transactions turn into how many: "1,204 coins become 4".</summary>
    public (int From, int To)? Effect => Plan is null ? null : (Plan.InputCount, Plan.Transactions.Count);

    private bool StillTheBoundWallet()
    {
        if (Wallet is null) { Problem = "Count the coins first."; return false; }
        if (_binding.IsStillActive(Wallet, out var activeNow)) return true;
        Problem = WalletBinding.MovedMessage(Wallet, activeNow);
        return false;
    }

    /// <summary>Counts the active wallet's coins and binds the consolidation to that wallet. Nothing is minted or sent.</summary>
    public async Task CountAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        ClearPlan();
        LastBroadcast = null;
        Picture = null;
        Destination = "";
        Wallet = null;
        try { Wallet = _binding.BindActiveForWrite(); }
        catch (WalletSwitchPendingException ex) { Problem = ex.Message; return; }

        IsWorking = true;
        try
        {
            var coins = await _coins.ListSpendableCoinsAsync(Wallet, ct);
            if (!StillTheBoundWallet()) { Wallet = null; return; }
            Picture = CoinPicture.Of(coins);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Problem = $"Couldn't read the wallet's coins: {ex.Message}"; Wallet = null; }
        finally { IsWorking = false; }
    }

    /// <summary>
    /// Reads the coins again and plans the merge. <paramref name="maxCoinSatoshis"/> limits it to
    /// coins no larger than that (null = every coin). The destination is minted here, on the first
    /// plan that has something to merge.
    /// </summary>
    public async Task PlanAsync(long feeRateSatPerKb, long? maxCoinSatoshis, CancellationToken ct = default)
    {
        if (Busy) return;
        ClearPlan();
        LastBroadcast = null;
        if (!StillTheBoundWallet()) return;

        IsWorking = true;
        try
        {
            var coins = await _coins.ListSpendableCoinsAsync(Wallet!, ct);
            if (!StillTheBoundWallet()) return;
            Picture = CoinPicture.Of(coins);

            // Before an address is minted: is there anything to merge at all?
            if (feeRateSatPerKb < 0) { Problem = "The fee rate cannot be negative."; return; }
            if (maxCoinSatoshis is <= 0) { Problem = "The coin size limit must be more than zero."; return; }
            var inPlay = ConsolidationPlanner.Select(coins, feeRateSatPerKb, maxCoinSatoshis).Coins.Count;
            if (inPlay < 2)
            {
                Problem = inPlay == 0 ? "There are no coins to merge." : "There is only one coin to merge, so there is nothing to consolidate.";
                return;
            }

            if (Destination.Length == 0 && !await MintDestinationAsync(ct)) return;
            // Only ever this wallet's own address: anything else is refused, not warned about.
            if (await _addresses.OwnsAddressAsync(Wallet!, Destination, ct) != true)
            {
                Problem = "Couldn't confirm that the new address belongs to this wallet, so nothing was planned.";
                Destination = "";
                return;
            }

            var result = ConsolidationPlanner.Plan(coins, Destination, feeRateSatPerKb, maxCoinSatoshis);
            if (result.Plan is null) { Problem = result.Problem; return; }
            Warnings = result.Plan.Warnings;
            Plan = result.Plan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Problem = $"Couldn't plan the consolidation: {ex.Message}"; }
        finally { IsWorking = false; }
    }

    /// <summary>A BQ… address when the wallet can make one; a legacy address when the wallet (or the
    /// node) has no post-quantum keys — such a wallet holds only legacy coins. Any other failure stops.</summary>
    private async Task<bool> MintDestinationAsync(CancellationToken ct)
    {
        var pq = await _addresses.MintPostQuantumAddressAsync(Wallet!, Label, ct);
        if (pq.IsOk) { Destination = pq.Address!; return true; }
        if (pq.Outcome is PqAddressOutcome.WalletHasNoPqKeys or PqAddressOutcome.LegacyWallet or PqAddressOutcome.DaemonTooOld)
        {
            var legacy = await _addresses.MintLegacyAddressAsync(Wallet!, Label, ct);
            if (!string.IsNullOrWhiteSpace(legacy)) { Destination = legacy.Trim(); return true; }
            Problem = "Couldn't get a new address from this wallet.";
            return false;
        }
        Problem = pq.Explain();
        return false;
    }

    public bool CanBroadcast(bool ackIrreversible, bool ackBackup) =>
        !Busy && Plan is { Balances: true } && ackIrreversible && (!RequiresBackupAck || ackBackup);

    /// <summary>Broadcasts the current plan ONCE. Returns null (and sets <see cref="Problem"/>) when it may not run.</summary>
    public async Task<SweepBroadcastResult?> BroadcastAsync(bool ackIrreversible, bool ackBackup, CancellationToken ct = default)
    {
        if (!CanBroadcast(ackIrreversible, ackBackup))
        {
            if (!Busy)
                Problem = Plan is null ? "Plan the consolidation first."
                    : !Plan.Balances ? "The plan doesn't balance. Nothing was sent."
                    : !ackIrreversible ? "Tick the acknowledgement first."
                    : "Confirm you have a current backup of this wallet first.";
            return null;
        }
        var plan = Plan!;
        ClearPlan();            // a plan goes out at most once, whatever happens next
        if (!StillTheBoundWallet()) return null;
        IsSending = true;
        try
        {
            LastBroadcast = await _broadcaster.BroadcastAsync(plan, Wallet!, ct);
            // The coin list is out of date now, and the address has been used: count again to go on.
            Picture = null;
            Destination = "";
            Wallet = null;
            return LastBroadcast;
        }
        finally { IsSending = false; }
    }

    /// <summary>What this card calls "look at the wallet again" (the Quantum page says "rescan").</summary>
    public const string RecheckAction = "count the coins again";

    /// <summary>The sentence under a finished broadcast. The broadcaster words its own result
    /// (<see cref="SweepBroadcastResult.Describe(string)"/>); this card only names its action.</summary>
    public string? Outcome => LastBroadcast is not { } r ? null
        : r.Succeeded
            ? $"Sent {r.SentTxIds.Count:N0} transaction{(r.SentTxIds.Count == 1 ? "" : "s")}. Count the coins again once {(r.SentTxIds.Count == 1 ? "it has" : "they have")} confirmed."
            : r.Describe(RecheckAction);

    /// <summary>Drops the current plan (the size limit was changed): it must be planned again.</summary>
    public void Discard()
    {
        if (!Busy) ClearPlan();
    }

    private void ClearPlan()
    {
        Plan = null; Problem = null; Warnings = Array.Empty<string>();
    }
}

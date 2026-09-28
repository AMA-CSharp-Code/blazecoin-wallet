namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>
/// The state and rules of one "move to post-quantum" on the Quantum Exposure page, kept out of the
/// Razor view so every rule is testable:
/// <list type="bullet">
/// <item>the destination is a BQ… address from this wallet by default;</item>
/// <item>a fresh LEGACY destination is offered only when the wallet cannot mint BQ… AND the coins are
///   exposed (re-hiding an exposed key is still worth something; moving unexposed coins to another
///   legacy address is not);</item>
/// <item>a destination that is not this wallet's is flagged before anything is signed;</item>
/// <item>broadcasting needs the "irreversible" acknowledgement, plus a backup acknowledgement for a
///   BQ… destination (a backup made before the wallet's post-quantum keys existed cannot recover the coins);</item>
/// <item>a plan is broadcast at most once — it is cleared before the first send.</item>
/// </list>
/// </summary>
public sealed class QuantumSweepSession
{
    public const string PostQuantumLabel = "post-quantum sweep";
    public const string LegacyLabel = "quantum-sweep";

    private readonly ISweepDestinationRpc _destinations;
    private readonly ISweepBroadcaster _broadcaster;
    private readonly IPostQuantumWalletCreator _walletCreator;
    private PqAddressResult? _pqAttempt;

    public QuantumSweepSession(ISweepDestinationRpc destinations, ISweepBroadcaster broadcaster, IPostQuantumWalletCreator walletCreator)
    {
        _destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _walletCreator = walletCreator ?? throw new ArgumentNullException(nameof(walletCreator));
    }

    /// <summary>Sending or creating a wallet: every other action waits.</summary>
    private bool Busy => IsSending || IsCreatingWallet;

    /// <summary>True when this wallet cannot hold BQ… keys at all, so the fix is a new wallet — offered
    /// right here. Not for an old NODE (a new wallet there cannot hold them either) or a transient failure.</summary>
    public bool CanCreatePostQuantumWallet =>
        Selection is not null && CreatedWallet is null &&
        _pqAttempt is { Outcome: PqAddressOutcome.WalletHasNoPqKeys or PqAddressOutcome.LegacyWallet };

    /// <summary>The post-quantum wallet made from this panel, once it exists (backed up and unloaded).</summary>
    public PqWalletCreated? CreatedWallet { get; private set; }
    public bool IsCreatingWallet { get; private set; }

    /// <summary>
    /// Creates a new encrypted wallet with BQ… keys, backs it up, unloads it, and makes its first BQ…
    /// address the destination. Nothing is sent; the coins move only on Broadcast.
    /// </summary>
    public async Task<PqWalletCreationResult?> CreatePostQuantumWalletAsync(PqWalletRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Busy) return null;
        if (!CanCreatePostQuantumWallet)
        {
            Problem = CreatedWallet is not null
                ? $"A post-quantum wallet ('{CreatedWallet.WalletName}') was already created here."
                : "This wallet can already make BQ… addresses — no new wallet is needed.";
            return null;
        }
        ClearPlan();
        IsCreatingWallet = true;
        try
        {
            var r = await _walletCreator.CreateAsync(request, ct);
            if (r.Succeeded) { CreatedWallet = r.Created; Destination = r.Created!.Address; }
            else Problem = r.Problem;
            return r;
        }
        finally { IsCreatingWallet = false; }
    }

    public SweepSelection? Selection { get; private set; }
    public string Destination { get; private set; } = "";
    public SweepDestinationKind? DestinationKind => QuantumSweepPlanner.KindOf(Destination);

    /// <summary>Why no BQ… destination could be minted (null when one was, or none was tried).</summary>
    public string? DestinationNotice => _pqAttempt is { IsOk: false } a ? a.Explain() : null;

    /// <summary>True when the wallet cannot mint BQ… but the selection holds exposed coins, so a fresh
    /// legacy address is still better than leaving the key on show.</summary>
    public bool CanUseLegacyFallback => Selection is { ContainsExposed: true } && _pqAttempt is { IsOk: false };

    public SweepPlan? Plan { get; private set; }
    public string? Problem { get; private set; }
    /// <summary>The plan's own warnings plus the session's (destination ownership).</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();
    public bool? DestinationIsMine { get; private set; }
    public bool RequiresBackupAck => Plan?.DestinationKind == SweepDestinationKind.PostQuantum;
    public bool IsSending { get; private set; }
    public SweepBroadcastResult? LastBroadcast { get; private set; }

    /// <summary>Opens a selection and tries to mint its BQ… destination straight away.</summary>
    public async Task SelectAsync(SweepSelection? selection, CancellationToken ct = default)
    {
        if (Busy) return;
        Selection = selection;
        LastBroadcast = null;
        CreatedWallet = null;
        ClearPlan();
        Destination = "";
        _pqAttempt = null;
        if (selection is not null) await NewPostQuantumDestinationAsync(ct);
    }

    public void Close()
    {
        if (Busy) return;
        Selection = null; Destination = ""; _pqAttempt = null; LastBroadcast = null; CreatedWallet = null;
        ClearPlan();
    }

    /// <summary>Mints a fresh BQ… address in the active wallet as the destination.</summary>
    public async Task NewPostQuantumDestinationAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        ClearPlan();
        PqAddressResult r;
        try { r = await _destinations.GetNewPostQuantumAddressAsync(PostQuantumLabel, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { r = PqAddressResult.Fail(PqAddressOutcome.Failed, ex.Message); }
        _pqAttempt = r;
        Destination = r.IsOk ? r.Address! : "";
    }

    /// <summary>The pre-fork mitigation: a fresh legacy address. Only for exposed coins in a wallet without BQ… keys.</summary>
    public async Task UseLegacyFallbackAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        ClearPlan();
        if (!CanUseLegacyFallback)
        {
            Problem = Selection is { ContainsExposed: false }
                ? "These coins are unexposed: moving them to another legacy address gains nothing. Use a BQ… address."
                : "A legacy destination is only offered when this wallet cannot make a BQ… address.";
            return;
        }
        try { Destination = await _destinations.GetNewLegacyAddressAsync(LegacyLabel, ct) ?? ""; }
        catch (Exception ex) when (ex is not OperationCanceledException) { Problem = $"Couldn't get a fresh address: {ex.Message}"; }
    }

    /// <summary>A destination typed or pasted by the user.</summary>
    public void SetDestination(string? destination)
    {
        if (Busy) return;
        Destination = destination?.Trim() ?? "";
        ClearPlan();
    }

    public async Task PlanAsync(long feeRateSatPerKb, CancellationToken ct = default)
    {
        if (Busy) return;
        ClearPlan();
        LastBroadcast = null;
        if (Selection is null) { Problem = "Nothing selected."; return; }
        if (DestinationKind == SweepDestinationKind.Legacy && !Selection.ContainsExposed)
        {
            Problem = "These coins are unexposed: moving them to another legacy address gains nothing. Use a BQ… address.";
            return;
        }

        var result = QuantumSweepPlanner.Plan(Selection.Outputs, Destination, feeRateSatPerKb);
        if (result.Plan is null) { Problem = result.Problem; return; }

        var warnings = new List<string>(result.Plan.Warnings);
        if (CreatedWallet is { } cw && string.Equals(cw.Address, Destination, StringComparison.Ordinal))
        {
            // Not this wallet's address by design: it belongs to the wallet created a moment ago.
            DestinationIsMine = null;
            warnings.Insert(0, $"The destination is the first BQ… address of the new wallet '{cw.WalletName}' " +
                $"({cw.EncryptionText}{(cw.Unloaded ? "; unloaded from the node" : "")}). Its backup is {cw.BackupPath}.");
            Warnings = warnings;
            Plan = result.Plan;
            return;
        }
        DestinationIsMine = await _destinations.IsMineAsync(Destination, ct);
        if (DestinationIsMine == false)
            warnings.Insert(0, "This destination is NOT an address of this wallet. Make sure you hold its keys and have a backup of the wallet that owns it.");
        else if (DestinationIsMine is null)
            warnings.Insert(0, "Couldn't confirm that the destination belongs to this wallet.");
        Warnings = warnings;
        Plan = result.Plan;
    }

    public bool CanBroadcast(bool ackIrreversible, bool ackBackup) =>
        !Busy && Plan is { Balances: true } && ackIrreversible && (!RequiresBackupAck || ackBackup);

    /// <summary>Broadcasts the current plan ONCE. Returns null (and sets <see cref="Problem"/>) when it may not run.</summary>
    public async Task<SweepBroadcastResult?> BroadcastAsync(bool ackIrreversible, bool ackBackup, CancellationToken ct = default)
    {
        if (!CanBroadcast(ackIrreversible, ackBackup))
        {
            if (!Busy)
                Problem = Plan is null ? "Plan the sweep first."
                    : !Plan.Balances ? "The plan doesn't balance — nothing was sent."
                    : !ackIrreversible ? "Tick the acknowledgement first."
                    : "Confirm you have a backup of the wallet that owns the BQ… address first.";
            return null;
        }
        var plan = Plan!;
        ClearPlan();            // a plan goes out at most once, whatever happens next
        IsSending = true;
        try
        {
            LastBroadcast = await _broadcaster.BroadcastAsync(plan, ct);
            return LastBroadcast;
        }
        finally { IsSending = false; }
    }

    private void ClearPlan()
    {
        Plan = null; Problem = null; Warnings = Array.Empty<string>(); DestinationIsMine = null;
    }
}

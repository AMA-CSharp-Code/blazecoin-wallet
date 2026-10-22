namespace BlazecoinWallet.Lite;

/// <summary>A payment the watcher noticed arriving at one of the wallet's addresses.</summary>
public sealed record IncomingPayment(string TxId, string Address, long AmountSatoshis, int Confirmations);

/// <summary>
/// Foreground incoming-payment detector: polls the wallet's UTXO set (all revealed
/// addresses, immature included — a fresh payment should announce itself at 0–1
/// confirmations, not after maturity) and raises <see cref="PaymentReceived"/> for every
/// NEW output that this session didn't create itself (change from our own sends and sweep
/// landings are excluded via <see cref="IWalletUtxoSource.SessionSentTxIds"/>).
/// Depends only on the narrow <see cref="IWalletUtxoSource"/> slice (audit F2).
/// Polling is the honest v1 on a 30-second chain; the public SignalR hub planned for the
/// VPS appliance can replace the timer later without touching subscribers.
/// Runs only while the app is open — background/push notification is a platform feature
/// deliberately out of scope here.
/// </summary>
public sealed class IncomingPaymentWatcher : IDisposable
{
    /// <summary>Default poll cadence — matches the chain's block spacing.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly IWalletUtxoSource _wallet;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private HashSet<string>? _knownOutpoints;

    /// <summary>Raised on a worker thread for each newly seen incoming transaction
    /// (one event per txid; several outputs in one tx are summed). A throwing subscriber
    /// never kills the poll loop.</summary>
    public event Action<IncomingPayment>? PaymentReceived;

    public IncomingPaymentWatcher(IWalletUtxoSource wallet, TimeSpan? interval = null)
    {
        _wallet = wallet;
        _interval = interval ?? DefaultInterval;
    }

    public bool IsRunning => _cts != null;

    /// <summary>Starts polling (idempotent). The first poll only records the baseline —
    /// pre-existing coins never fire an event.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _ = LoopAsync(_cts.Token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _knownOutpoints = null;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            await PollOnceAsync(ct); // establish the baseline immediately
            while (await timer.WaitForNextTickAsync(ct))
                await PollOnceAsync(ct);
        }
        catch (OperationCanceledException) { /* Stop() */ }
        catch { /* audit C2: nothing may kill the fire-and-forget loop silently mid-flight;
                   PollOnce already contains per-cycle failures, this is the last net */ }
    }

    /// <summary>One poll cycle — internal so tests can drive it deterministically.</summary>
    internal async Task PollOnceAsync(CancellationToken ct = default)
    {
        if (!_wallet.IsUnlocked) return;

        IReadOnlyList<LiteOwnedUtxo> utxos;
        try { utxos = await _wallet.GetAllUtxosAsync(ct); }
        catch { return; } // a flaky poll must never kill the loop; next tick retries

        var current = new HashSet<string>(utxos.Select(u => OutpointKey(u.Utxo.TxId, u.Utxo.OutputIndex)),
            StringComparer.OrdinalIgnoreCase);

        // Swap under the gate (audit C4) so a concurrent Stop/Start can't interleave a
        // stale baseline with a fresh one.
        HashSet<string>? baseline;
        lock (_gate)
        {
            baseline = _knownOutpoints;
            _knownOutpoints = current;
        }
        if (baseline == null) return; // first sight — never notify for what was already there

        var sent = _wallet.SessionSentTxIds;
        var fresh = utxos
            .Where(u => !baseline.Contains(OutpointKey(u.Utxo.TxId, u.Utxo.OutputIndex)))
            .Where(u => !sent.Contains(u.Utxo.TxId, StringComparer.OrdinalIgnoreCase))
            .GroupBy(u => u.Utxo.TxId, StringComparer.OrdinalIgnoreCase);

        foreach (var g in fresh)
        {
            var first = g.First();
            var payment = new IncomingPayment(g.Key, first.Address, g.Sum(u => u.Utxo.Amount), first.Utxo.Confirmations);
            try { PaymentReceived?.Invoke(payment); }
            catch { /* audit C2: a broken subscriber must not stop future notifications */ }
        }
    }

    private static string OutpointKey(string txId, int vout) => $"{txId}:{vout}";

    public void Dispose() => Stop();
}

using BlazecoinWallet.Core.Services;
using Microsoft.AspNetCore.Components;

namespace BlazecoinWallet.Maui.Components;

/// <summary>Base for the daemon-dependent pages, lifting the copy-pasted
/// "connection check + DaemonError + Retry" wiring out of ~8 components (SOLID
/// audit #8). A page renders <c>&lt;DaemonError Message="@_connError"
/// OnRetry="RetryConnAsync" /&gt;</c> when <see cref="_connError"/> is non-null,
/// sets it from <see cref="CheckDaemonAsync"/> during its load lifecycle, and
/// overrides <see cref="OnDaemonReconnectedAsync"/> with whatever data reload it
/// needs after a successful retry.</summary>
public abstract class DaemonAwareComponent : ComponentBase
{
    // The liveness probe lives on INodeRpc; the one BlazecoindRpcService singleton
    // backs it, so this resolves the same instance the pages use for their own RPC.
    [Inject] protected INodeRpc DaemonConn { get; set; } = default!;

    /// <summary>Human-readable offline message, or null when the daemon is
    /// reachable. Bound by the page's DaemonError markup.</summary>
    protected string? _connError;

    /// <summary>Probe the daemon, store the result in <see cref="_connError"/>, and
    /// return true when it's reachable. Pages call this where they used to assign
    /// <c>_connError = await Rpc.CheckConnectionAsync()</c>.</summary>
    protected async Task<bool> CheckDaemonAsync()
    {
        _connError = await DaemonConn.CheckConnectionAsync();
        return _connError == null;
    }

    /// <summary>Page-specific data reload run after a successful Retry (e.g. reload
    /// transactions, wallets, or restart a watcher). Default: nothing.</summary>
    protected virtual Task OnDaemonReconnectedAsync() => Task.CompletedTask;

    /// <summary>Wired to the DaemonError "Retry" button. Re-probes and, if back
    /// online, runs the page's <see cref="OnDaemonReconnectedAsync"/>.</summary>
    protected async Task RetryConnAsync()
    {
        if (await CheckDaemonAsync())
            await OnDaemonReconnectedAsync();
        StateHasChanged();
    }
}

namespace BlazecoinWallet.Core.Services.Mining;

public interface IMiningService
{
    /// <summary>Cached snapshot of current state. Safe to call from any thread.</summary>
    MiningStatus GetStatus();

    /// <summary>Fires after every internal state change (start/stop, worker tick,
    /// block found, error). Listeners should marshal to the UI thread themselves.</summary>
    event Action<MiningStatus>? StatusChanged;

    /// <summary>Start mining. Throws if already running. Validates and clamps
    /// thread count against the hard cap. If options.PayoutAddress is null,
    /// calls getnewaddress to obtain one.</summary>
    Task StartAsync(MiningOptions options, CancellationToken ct = default);

    /// <summary>Stop all workers and the template refresher. Returns when the
    /// worker threads have actually exited.</summary>
    Task StopAsync();

    /// <summary>Hard cap on thread count: Environment.ProcessorCount - 4,
    /// floored at 1. Always leaves cores for the OS, UI, and WebView2.</summary>
    int MaxAllowedThreads { get; }

    /// <summary>Default thread count the UI should suggest: ProcessorCount / 8,
    /// floored at 1. Conservative on big machines (24 of 192 cores).</summary>
    int DefaultThreadCount { get; }
}

namespace BlazecoinWallet.Lite;

public enum CometPulseKind { None, Received, Sent }

/// <summary>
/// Transient recolour of the Home balance-card comet: green after an incoming payment,
/// orange after an outgoing send, reverting to the standard white when the window lapses.
/// Shared app state rather than page state so a pulse raised while the Send page (or any
/// other page) is showing is still visible if the user lands on Home inside the window.
/// A newer pulse restarts the clock and supersedes the older pulse's pending expiry.
/// </summary>
public sealed class CometPulse
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _window;
    private readonly object _sync = new();
    private int _generation;

    public CometPulse(TimeSpan? window = null) => _window = window ?? DefaultWindow;

    public CometPulseKind Kind { get; private set; }

    /// <summary>Raised on any pulse or expiry. May fire on a background thread —
    /// Blazor subscribers must marshal via InvokeAsync.</summary>
    public event Action? Changed;

    public void Pulse(CometPulseKind kind)
    {
        int generation;
        lock (_sync)
        {
            Kind = kind;
            generation = ++_generation;
        }
        Changed?.Invoke();
        _ = ExpireAsync(generation);
    }

    private async Task ExpireAsync(int generation)
    {
        await Task.Delay(_window).ConfigureAwait(false);
        lock (_sync)
        {
            // A newer pulse owns the clock now — this expiry is stale.
            if (generation != _generation || Kind == CometPulseKind.None) return;
            Kind = CometPulseKind.None;
        }
        Changed?.Invoke();
    }
}

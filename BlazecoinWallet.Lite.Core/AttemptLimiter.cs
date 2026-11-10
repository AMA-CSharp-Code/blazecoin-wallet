namespace BlazecoinWallet.Lite;

/// <summary>
/// The one policy for guessing at the wallet's secrets: <see cref="MaxMisses"/> misses buy a
/// <see cref="Lockout"/> wait (audit round-3 C2: PBKDF2 blunts offline brute force, this bounds
/// guessing online). One instance per head, shared by the app-lock PIN gate and the phrase's
/// second look (audit 2026-11-09) — the reveal used to keep its own count, which reset on every
/// visit to the page and did nothing where the wallet could not lock.
/// </summary>
public sealed class AttemptLimiter
{
    public const int MaxMisses = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);
    public const string TrippedMessage = "Too many attempts — locked for 30 seconds.";

    private readonly object _gate = new();
    private int _misses;
    private DateTime _lockedUntil = DateTime.MinValue;

    /// <summary>The time source (tests move it).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The wait still to be served, or null when a try is allowed.</summary>
    public TimeSpan? Wait
    {
        get
        {
            var left = _lockedUntil - Clock();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    public static string WaitMessage(TimeSpan wait) => $"Too many attempts — wait {(int)wait.TotalSeconds + 1}s.";

    /// <summary>Records a miss. True when it was one too many: the wait has started.</summary>
    public bool Miss()
    {
        lock (_gate)
        {
            if (++_misses < MaxMisses) return false;
            _misses = 0;
            _lockedUntil = Clock() + Lockout;
            return true;
        }
    }

    /// <summary>A right answer: the count starts over.</summary>
    public void Reset()
    {
        lock (_gate) _misses = 0;
    }
}

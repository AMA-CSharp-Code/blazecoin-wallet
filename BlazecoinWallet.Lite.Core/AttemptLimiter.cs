namespace BlazecoinWallet.Lite;

/// <summary>What the guessing limit remembers between tries.</summary>
/// <param name="Misses">Misses since the last wait (or the last right answer).</param>
/// <param name="Trips">Waits earned in a row with no right answer in between.</param>
/// <param name="LockedUntilUtc">When the current wait ends.</param>
public sealed record AttemptState(int Misses, int Trips, DateTime LockedUntilUtc)
{
    public static readonly AttemptState None = new(0, 0, DateTime.MinValue);
}

/// <summary>Where the guessing limit keeps its count. A head with durable storage registers one
/// that survives a restart of the app, so closing and reopening it does not buy fresh tries.</summary>
public interface IAttemptLimiterState
{
    AttemptState Load();
    void Save(AttemptState state);
}

/// <summary>Process-lifetime state: the default, and what the tests use.</summary>
public sealed class InMemoryAttemptLimiterState : IAttemptLimiterState
{
    private AttemptState _state = AttemptState.None;
    public AttemptState Load() => _state;
    public void Save(AttemptState state) => _state = state;
}

/// <summary>
/// The one policy for guessing at the wallet's secrets: <see cref="MaxMisses"/> misses buy a
/// wait (audit round-3 C2: PBKDF2 blunts offline brute force, this bounds guessing online). One
/// instance per head, shared by the app-lock PIN gate and the phrase's second look (audit
/// 2026-11-09) — the reveal used to keep its own count, which reset on every visit to the page and
/// did nothing where the wallet could not lock.
/// <para>The wait GROWS with every wait earned in a row (<see cref="Lockouts"/>: 30 s, 1 min,
/// 5 min, 15 min, then 1 hour each time) and the count is kept in an
/// <see cref="IAttemptLimiterState"/>, so it survives a restart where the head stores it
/// (bug-bounty report 2026-11-18: a flat 30 s wait held only in memory let a 4-digit PIN be tried
/// through in under 17 hours, less with restarts). With the schedule, the whole 4-digit space
/// takes about 83 days of unattended trying. A right answer clears everything.</para>
/// </summary>
public sealed class AttemptLimiter
{
    public const int MaxMisses = 5;
    /// <summary>The first wait.</summary>
    public static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);
    /// <summary>The wait for the first, second, ... trip in a row; the last one repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Lockouts =
    [
        Lockout, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
    ];
    /// <summary>What the first trip says (later ones say their own length: <see cref="TripMessage"/>).</summary>
    public const string TrippedMessage = "Too many attempts — locked for 30 seconds.";

    private readonly object _gate = new();
    private readonly IAttemptLimiterState _store;

    public AttemptLimiter(IAttemptLimiterState? store = null) => _store = store ?? new InMemoryAttemptLimiterState();

    /// <summary>The time source (tests move it).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The wait for the <paramref name="trip"/>-th trip in a row (1-based).</summary>
    public static TimeSpan LockoutFor(int trip) => Lockouts[Math.Clamp(trip, 1, Lockouts.Count) - 1];

    /// <summary>The wait still to be served, or null when a try is allowed.</summary>
    public TimeSpan? Wait
    {
        get
        {
            lock (_gate)
            {
                var left = _store.Load().LockedUntilUtc - Clock();
                if (left <= TimeSpan.Zero) return null;
                // A clock that was set back must not turn a wait into one longer than any trip earns.
                var longest = Lockouts[^1];
                return left > longest ? longest : left;
            }
        }
    }

    public static string WaitMessage(TimeSpan wait) => wait.TotalSeconds < 90
        ? $"Too many attempts — wait {(int)wait.TotalSeconds + 1}s."
        : $"Too many attempts — wait {(int)Math.Ceiling(wait.TotalMinutes)} min.";

    /// <summary>What to say when <see cref="Miss"/> has just returned true.</summary>
    public string TripMessage
    {
        get
        {
            int trips;
            lock (_gate) trips = _store.Load().Trips;
            if (trips <= 1) return TrippedMessage;
            var wait = LockoutFor(trips);
            return wait.TotalMinutes < 60
                ? $"Too many attempts — locked for {(int)wait.TotalMinutes} minute{(wait.TotalMinutes >= 2 ? "s" : "")}."
                : "Too many attempts — locked for 1 hour.";
        }
    }

    /// <summary>Records a miss. True when it was one too many: the wait has started.</summary>
    public bool Miss()
    {
        lock (_gate)
        {
            var s = _store.Load();
            var misses = s.Misses + 1;
            if (misses < MaxMisses)
            {
                _store.Save(s with { Misses = misses });
                return false;
            }
            var trips = s.Trips + 1;
            _store.Save(new AttemptState(0, trips, Clock() + LockoutFor(trips)));
            return true;
        }
    }

    /// <summary>A right answer: the count and the growing wait start over.</summary>
    public void Reset()
    {
        lock (_gate) _store.Save(AttemptState.None);
    }
}

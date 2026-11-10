namespace BlazecoinWallet.Lite;

/// <summary>
/// Keeps a secret that is on screen out of everything the platform can capture a window with:
/// screenshots, screen recording, casting and the recent-apps thumbnail.
/// <para>Why (bug-bounty report 2026-11-09): the Android wallet showed the recovery phrase in a
/// window the platform was never told to protect, so a plain screen capture returned all twelve
/// words. A page that shows or takes a seed phrase or a private key holds a guard for as long as
/// it is on screen (the <c>SecretScreen</c> component does it); every other page stays
/// capturable, so a receive address or a receipt can still be screenshotted and shared.</para>
/// <para>A seam the app head fills: Android sets the window's secure flag. Where the platform has
/// no such switch (a browser tab) the default does nothing.</para>
/// </summary>
public interface IScreenCaptureGuard
{
    /// <summary>Protects the window until the returned handle is disposed. Holds nest: the
    /// window stays protected until the last one is released.</summary>
    IDisposable Protect();

    /// <summary>True while at least one hold is open.</summary>
    bool IsProtecting { get; }
}

/// <summary>
/// The counting half every platform shares: the first hold switches protection on, the last
/// release switches it off, and releasing twice is harmless. Thread-safe.
/// </summary>
public abstract class ScreenCaptureGuard : IScreenCaptureGuard
{
    private readonly object _gate = new();
    private int _holds;

    public bool IsProtecting { get { lock (_gate) return _holds > 0; } }

    public IDisposable Protect()
    {
        lock (_gate)
        {
            if (++_holds == 1) Apply(true);
        }
        return new Hold(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holds == 0) Apply(false);
        }
    }

    /// <summary>Switch the platform's protection on or off. Called only on a real change.</summary>
    protected abstract void Apply(bool protect);

    private sealed class Hold(ScreenCaptureGuard owner) : IDisposable
    {
        private ScreenCaptureGuard? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

/// <summary>No capture switch on this platform (the default; a head that has one registers its own).</summary>
public sealed class NullScreenCaptureGuard : ScreenCaptureGuard
{
    protected override void Apply(bool protect) { }
}

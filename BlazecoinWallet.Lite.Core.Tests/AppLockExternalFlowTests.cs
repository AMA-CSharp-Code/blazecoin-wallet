using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The app-lock's external-operation window: an app-initiated flow that leaves the
/// activity (the QR-scan file chooser) must not trigger the background re-lock — the
/// lock-screen swap destroys the requesting page mid-flow and loses the scan result
/// (2026-07-27 emulator smoke finding).
/// </summary>
public class AppLockExternalFlowTests
{
    [Fact]
    public void Background_lock_fires_normally()
    {
        var s = new AppLockSession();
        s.Unlock();
        s.LockForBackgrounding();
        Assert.False(s.IsUnlocked);
    }

    [Fact]
    public void Background_lock_is_suppressed_during_an_external_operation()
    {
        var s = new AppLockSession(TimeSpan.Zero);   // no grace — pin the window itself
        s.Unlock();
        using (s.BeginExternalOperation())
        {
            s.LockForBackgrounding();
            Assert.True(s.IsUnlocked);   // the file-chooser bounce must not re-lock
        }
        s.LockForBackgrounding();        // window closed — normal behaviour returns
        Assert.False(s.IsUnlocked);
    }

    [Fact]
    public void Grace_period_absorbs_a_late_stop_after_the_operation_ends()
    {
        // Android can deliver the covered activity's OnStop AFTER the flow completed —
        // the decoded address appeared, then the late stop re-locked the wallet.
        var s = new AppLockSession(TimeSpan.FromSeconds(30));
        s.Unlock();
        s.BeginExternalOperation().Dispose();
        s.LockForBackgrounding();        // the late OnStop, inside the grace window
        Assert.True(s.IsUnlocked);
    }

    [Fact]
    public void Explicit_lock_still_wins_inside_an_external_operation()
    {
        var s = new AppLockSession();
        s.Unlock();
        using var _ = s.BeginExternalOperation();
        s.Lock();
        Assert.False(s.IsUnlocked);
    }

    [Fact]
    public void Double_dispose_cannot_underflow_the_window()
    {
        var s = new AppLockSession(TimeSpan.Zero);
        s.Unlock();
        var op = s.BeginExternalOperation();
        op.Dispose();
        op.Dispose();                    // second dispose is a no-op
        s.LockForBackgrounding();        // no active op, no grace — locks
        Assert.False(s.IsUnlocked);

        s.Unlock();
        using var _ = s.BeginExternalOperation();
        s.LockForBackgrounding();        // the fresh window still suppresses
        Assert.True(s.IsUnlocked);
    }
}

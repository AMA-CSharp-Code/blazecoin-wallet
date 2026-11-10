namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The counting behind screen-capture protection (bug-bounty report 2026-11-09): the window is
/// protected from the first hold to the last release, however the holds overlap.
/// </summary>
public class ScreenCaptureGuardTests
{
    private sealed class RecordingGuard : ScreenCaptureGuard
    {
        public List<bool> Applied { get; } = [];
        protected override void Apply(bool protect) => Applied.Add(protect);
    }

    [Fact]
    public void Protection_is_on_from_the_first_hold_to_the_last_release()
    {
        var guard = new RecordingGuard();
        Assert.False(guard.IsProtecting);

        var first = guard.Protect();
        var second = guard.Protect();                          // a second secret page opens before the first has gone
        Assert.Equal(new[] { true }, guard.Applied);           // switched on once

        first.Dispose();
        Assert.True(guard.IsProtecting);                       // still one secret on screen
        Assert.Equal(new[] { true }, guard.Applied);

        second.Dispose();
        Assert.False(guard.IsProtecting);
        Assert.Equal(new[] { true, false }, guard.Applied);
    }

    [Fact]
    public void Releasing_a_hold_twice_does_not_release_someone_elses()
    {
        var guard = new RecordingGuard();
        var first = guard.Protect();
        var second = guard.Protect();

        first.Dispose();
        first.Dispose();                                       // a page disposed twice

        Assert.True(guard.IsProtecting);                       // the second hold still stands
        second.Dispose();
        Assert.False(guard.IsProtecting);
    }

    [Fact]
    public void Protection_can_be_taken_again_after_it_was_released()
    {
        var guard = new RecordingGuard();
        guard.Protect().Dispose();
        using (guard.Protect())
            Assert.True(guard.IsProtecting);

        Assert.Equal(new[] { true, false, true, false }, guard.Applied);
    }

    [Fact]
    public void The_default_guard_counts_but_switches_nothing()
    {
        var guard = new NullScreenCaptureGuard();
        using (guard.Protect())
            Assert.True(guard.IsProtecting);
        Assert.False(guard.IsProtecting);
    }
}

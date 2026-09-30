using BlazecoinWallet.Lite;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The transient Home-comet recolour state: a pulse holds its kind for the window then
/// reverts to None, and a newer pulse restarts the clock (the older pending expiry must
/// not clear the newer colour early).
/// </summary>
public class CometPulseTests
{
    [Fact]
    public void Pulse_sets_kind_and_raises_changed()
    {
        var pulse = new CometPulse(TimeSpan.FromMinutes(5)); // never expires inside the test
        var events = 0;
        pulse.Changed += () => events++;

        pulse.Pulse(CometPulseKind.Received);

        Assert.Equal(CometPulseKind.Received, pulse.Kind);
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Pulse_reverts_to_none_after_the_window()
    {
        var pulse = new CometPulse(TimeSpan.FromMilliseconds(50));
        var reverted = new TaskCompletionSource();
        pulse.Changed += () => { if (pulse.Kind == CometPulseKind.None) reverted.TrySetResult(); };

        pulse.Pulse(CometPulseKind.Sent);
        Assert.Equal(CometPulseKind.Sent, pulse.Kind);

        await reverted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CometPulseKind.None, pulse.Kind);
    }

    [Fact]
    public async Task A_newer_pulse_survives_the_older_pulses_expiry()
    {
        var pulse = new CometPulse(TimeSpan.FromMilliseconds(80));

        pulse.Pulse(CometPulseKind.Received);
        await Task.Delay(40);
        pulse.Pulse(CometPulseKind.Sent);       // restarts the clock

        // Past the FIRST pulse's expiry: its stale timer must not have cleared the newer kind.
        await Task.Delay(60);
        Assert.Equal(CometPulseKind.Sent, pulse.Kind);

        // And the newer pulse still expires on its own clock.
        var reverted = new TaskCompletionSource();
        pulse.Changed += () => { if (pulse.Kind == CometPulseKind.None) reverted.TrySetResult(); };
        if (pulse.Kind != CometPulseKind.None)
            await reverted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CometPulseKind.None, pulse.Kind);
    }
}

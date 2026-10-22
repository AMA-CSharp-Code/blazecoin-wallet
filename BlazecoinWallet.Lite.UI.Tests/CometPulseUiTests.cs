using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The balance-card comet's transient event recolour: Home renders the plain comet by
/// default, turns it green (comet-received) / orange (comet-sent) when the shared
/// CometPulse service fires, and follows a newer pulse.
/// </summary>
public class CometPulseUiTests : LiteUiTestBase
{
    [Fact]
    public void A_pulse_refreshes_the_balance_figures()
    {
        var reader = new FakeReader { SpendableSats = 0 };
        Wire(new LoadedVault(), reader, new FakeRelay());
        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => Assert.Contains("lite-card-balance", cut.Markup));

        reader.SpendableSats = 2_500_000;   // a payment lands; the watcher would pulse
        Services.GetRequiredService<CometPulse>().Pulse(CometPulseKind.Received);

        // The balance figure updates without the manual ↻.
        cut.WaitForAssertion(() => Assert.Contains("0.025", cut.Markup));
    }

    [Fact]
    public void Home_comet_recolours_on_pulses()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => Assert.Contains("lite-card-comet", cut.Markup));
        Assert.DoesNotContain("comet-received", cut.Markup);
        Assert.DoesNotContain("comet-sent", cut.Markup);

        var pulse = Services.GetRequiredService<CometPulse>();

        pulse.Pulse(CometPulseKind.Received);
        cut.WaitForAssertion(() => Assert.Contains("comet-received", cut.Markup));

        // A send supersedes the receive colour.
        pulse.Pulse(CometPulseKind.Sent);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("comet-sent", cut.Markup);
            Assert.DoesNotContain("comet-received", cut.Markup);
        });
    }
}

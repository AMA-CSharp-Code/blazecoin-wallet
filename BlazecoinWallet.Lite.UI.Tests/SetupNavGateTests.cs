using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI.Shared;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The setup ceremony must not offer an exit: once Create runs the seed already exists,
/// so tapping Home would unlock and bypass the write-down + quiz gates (user-found on the
/// 2026-07-27 emulator smoke). The layout hides the top nav while /setup is showing.
/// </summary>
public class SetupNavGateTests : LiteUiTestBase
{
    [Fact]
    public void Nav_is_hidden_on_the_setup_page_and_shown_elsewhere()
    {
        var wallet = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        Services.AddSingleton(new IncomingPaymentWatcher(wallet)); // LiteLayout hosts it

        var nav = Services.GetRequiredService<Bunit.TestDoubles.FakeNavigationManager>();
        var cut = RenderComponent<LiteLayout>();
        Assert.Contains("lite-nav", cut.Markup);          // normal pages keep the nav

        nav.NavigateTo("setup");
        cut.WaitForAssertion(() => Assert.DoesNotContain("lite-nav", cut.Markup));

        nav.NavigateTo("");
        cut.WaitForAssertion(() => Assert.Contains("lite-nav", cut.Markup));
    }
}

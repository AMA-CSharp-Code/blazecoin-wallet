using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI.Pages;
using BlazecoinWallet.Lite.UI.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The header 🔒 (2026-08-23): shown only when the head's locker can actually lock, and it
/// calls that locker — what locking means is the head's business. Plus the Settings auto-lock
/// card, which only appears on heads that support idle locking (the web head).
/// </summary>
public class LockButtonAndAutoLockTests : LiteUiTestBase
{
    private sealed class FakeLocker : IWalletLocker
    {
        public bool CanLock { get; set; }
        public int Locks;
        public Task LockAsync() { Locks++; return Task.CompletedTask; }
    }

    private sealed class SupportedAutoLock : IAutoLockSettings
    {
        public bool Supported => true;
        public int IdleMinutes { get; private set; } = 15;
        public void SetIdleMinutes(int minutes) => IdleMinutes = minutes;
    }

    [Fact]
    public void Lock_button_is_hidden_when_the_locker_cannot_lock()
    {
        var wallet = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        Services.AddSingleton(new IncomingPaymentWatcher(wallet));
        Services.AddSingleton<IWalletLocker>(new FakeLocker { CanLock = false }); // last wins

        var cut = RenderComponent<LiteLayout>();

        Assert.DoesNotContain("lite-lock", cut.Markup);
    }

    [Fact]
    public void Lock_button_shows_and_calls_the_locker_when_it_can_lock()
    {
        var wallet = Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        Services.AddSingleton(new IncomingPaymentWatcher(wallet));
        var locker = new FakeLocker { CanLock = true };
        Services.AddSingleton<IWalletLocker>(locker);

        var cut = RenderComponent<LiteLayout>();
        var btn = cut.Find("button.lite-lock");
        btn.Click();

        cut.WaitForAssertion(() => Assert.Equal(1, locker.Locks));
    }

    /// <summary>The Settings page injects the connection settings too — mirror SettingsPageTests.Wire.</summary>
    private void WireSettingsExtras()
    {
        Services.AddSingleton<IGatewaySettings>(new InMemoryGatewaySettings(["https://blazecoin.co.uk/"]));
        Services.AddSingleton<IPersonalNodeSettings>(new InMemoryPersonalNodeSettings());
        Services.AddSingleton<IP2PNodeSettings>(new InMemoryP2PNodeSettings(["85.15.179.171:55414"]));
    }

    [Fact]
    public void Auto_lock_card_is_hidden_on_heads_without_idle_lock_support()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        WireSettingsExtras();
        var cut = RenderComponent<Settings>();
        Assert.DoesNotContain("Auto-Lock", cut.Markup);
    }

    [Fact]
    public void Auto_lock_card_shows_and_persists_the_choice_when_supported()
    {
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        WireSettingsExtras();
        var settings = new SupportedAutoLock();
        Services.AddSingleton<IAutoLockSettings>(settings);

        var cut = RenderComponent<Settings>();
        Assert.Contains("Auto-Lock", cut.Markup);

        cut.Find("#autolock-select").Change("5");
        Assert.Equal(5, settings.IdleMinutes);
        cut.Find("#autolock-select").Change("0");
        Assert.Equal(0, settings.IdleMinutes);
    }
}

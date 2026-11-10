using System.Text.RegularExpressions;
using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// A page that shows or takes a recovery phrase or a private key keeps the window out of
/// screen capture for as long as it is on screen (bug-bounty report 2026-11-09: the Android
/// wallet's backup page printed all twelve words in a window any screenshot, screen recorder,
/// cast or recent-apps thumbnail could read). Pages with nothing secret stay capturable.
/// </summary>
public class SecretScreenTests : LiteUiTestBase
{
    private sealed class RecordingGuard : ScreenCaptureGuard
    {
        public List<bool> Applied { get; } = [];
        protected override void Apply(bool protect) => Applied.Add(protect);
    }

    private RecordingGuard WireWithGuard(ISeedVault vault)
    {
        var guard = new RecordingGuard();
        Services.AddSingleton<IScreenCaptureGuard>(guard);
        Wire(vault, new FakeReader(), new FakeRelay());
        return guard;
    }

    [Fact]
    public async Task The_backup_page_is_protected_before_the_phrase_can_be_shown_and_released_when_it_closes()
    {
        var guard = WireWithGuard(new LoadedVault { Words = null });
        await Services.GetRequiredService<LiteWalletService>().CreateNewWalletAsync();   // a new wallet: backup not yet verified

        var cut = RenderComponent<VerifyBackup>();
        cut.WaitForAssertion(() => Assert.Contains("Show my recovery phrase", cut.Markup));

        Assert.True(guard.IsProtecting);                       // was: no protection at any point
        cut.FindAll("button").First(b => b.TextContent.Contains("Show my recovery phrase")).Click();
        Assert.NotEmpty(cut.FindAll(".lite-word"));            // the words are on screen…
        Assert.True(guard.IsProtecting);                       // …inside a protected window

        DisposeComponents();
        Assert.False(guard.IsProtecting);
        Assert.Equal(new[] { true, false }, guard.Applied);
    }

    [Fact]
    public void The_setup_ceremony_is_protected()
    {
        var guard = WireWithGuard(new LoadedVault { Words = null });

        RenderComponent<Setup>();

        Assert.True(guard.IsProtecting);                       // a new phrase is shown here, an old one typed here
        DisposeComponents();
        Assert.False(guard.IsProtecting);
    }

    [Fact]
    public void The_old_key_sweep_is_protected()
    {
        var guard = WireWithGuard(new LoadedVault());

        RenderComponent<Sweep>();

        Assert.True(guard.IsProtecting);                       // a private key is typed or scanned here
        DisposeComponents();
        Assert.False(guard.IsProtecting);
    }

    [Fact]
    public void Pages_with_nothing_secret_stay_capturable()
    {
        var guard = WireWithGuard(new LoadedVault());

        RenderComponent<Home>();
        RenderComponent<Receive>();                            // an address and its QR are meant to be shared
        RenderComponent<Send>();

        Assert.False(guard.IsProtecting);
        Assert.Empty(guard.Applied);
    }

    [Fact]
    public void Every_page_that_handles_a_secret_asks_for_protection()
    {
        // So that a page added later cannot quietly show a phrase or a key in an open window:
        // anything in Pages/ or Shared/ that reveals, creates, restores or sweeps must carry
        // <SecretScreen />.
        var handlesASecret = new Regex(
            @"RevealMnemonicAsync|ISecretRevealGate|CreateNewWalletAsync|RestoreWalletAsync|SweepWifAsync|CheckWifAsync|GetWif\b|MnemonicWords");
        var ui = Path.Combine(RepoRoot(), "BlazecoinWallet.Lite.UI");
        var pages = Directory.GetFiles(Path.Combine(ui, "Pages"), "*.razor")
            .Concat(Directory.GetFiles(Path.Combine(ui, "Shared"), "*.razor"))
            .ToList();
        Assert.True(pages.Count > 10, "the page sources were not found");

        var secret = pages.Where(p => handlesASecret.IsMatch(File.ReadAllText(p))).ToList();
        var unprotected = secret.Where(p => !File.ReadAllText(p).Contains("<SecretScreen />")).Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { "Setup.razor", "Sweep.razor", "VerifyBackup.razor" },
            secret.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Empty(unprotected);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "BlazecoinWallet.Lite.UI", "Pages")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}

using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The unverified-backup loop: a created-but-unproven wallet shows the Home warning chip,
/// the verify-backup page reveals the phrase and re-runs the quiz, and passing clears the
/// chip. Restored wallets never enter the loop — possession is proof.
/// </summary>
public class VerifyBackupTests : LiteUiTestBase
{
    [Fact]
    public void Home_shows_the_backup_chip_only_while_unverified()
    {
        // Restored wallet (LoadedVault + UnlockAsync path) → verified → no chip.
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());
        var cut = RenderComponent<Home>();
        cut.WaitForAssertion(() => Assert.Contains("lite-card-balance", cut.Markup));
        Assert.DoesNotContain("lite-backup-chip", cut.Markup);
    }

    [Fact]
    public void A_wallet_with_no_record_of_the_flag_is_warned_and_quizzed_not_told_it_is_proven()
    {
        // Bug-bounty report 2026-11-06: the vault holds a wallet but the state store has no record of
        // the backup flag (state cleared, a write that never landed). That used to read as verified.
        var reader = new FakeReader();
        Wire(new LoadedVault(), reader, new FakeRelay());
        Services.AddSingleton(new LiteWalletService(new LoadedVault(), reader, new FakeRelay()));   // last one wins: a fresh store

        var home = RenderComponent<Home>();
        home.WaitForAssertion(() => Assert.Contains("lite-backup-chip", home.Markup));               // was: no chip

        var verify = RenderComponent<VerifyBackup>();
        verify.WaitForAssertion(() => Assert.Contains("Show my recovery phrase", verify.Markup));    // the quiz is offered
        Assert.DoesNotContain("has been", verify.Markup);                                            // was: "…the written copy has been proven"
    }

    [Fact]
    public async Task Created_wallet_shows_the_chip_and_the_verify_quiz_clears_it()
    {
        var vault = new LoadedVault { Words = null };
        var svc = Wire(vault, new FakeReader(), new FakeRelay());
        var words = (await svc.CreateNewWalletAsync()).Split(' ');

        var home = RenderComponent<Home>();
        home.WaitForAssertion(() => Assert.Contains("lite-backup-chip", home.Markup));

        // The verify page: reveal words, answer the two asked words from the real phrase.
        var cut = RenderComponent<VerifyBackup>();
        cut.WaitForAssertion(() => Assert.Contains("Verify Your Backup", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Show my recovery phrase")).Click();
        Assert.Equal(12, cut.FindAll(".lite-word").Count);

        // Answer the asked word numbers using the created mnemonic. Re-find elements per
        // change — each Change() re-renders and stales previously found references.
        var labels = cut.FindAll("label").Select(l => l.TextContent).Where(t => t.Contains("Word #")).ToList();
        for (var i = 0; i < labels.Count; i++)
        {
            var wordNo = int.Parse(labels[i].Replace("Word #", "").Trim());
            cut.FindAll("input.lite-input")[i].Change(words[wordNo - 1]);
        }
        cut.FindAll("button").First(b => b.TextContent.Contains("Verify My Backup")).Click();
        cut.WaitForAssertion(() => Assert.Contains("verified", cut.Markup, StringComparison.OrdinalIgnoreCase));

        Assert.True(await svc.IsBackupVerifiedAsync());
    }
}

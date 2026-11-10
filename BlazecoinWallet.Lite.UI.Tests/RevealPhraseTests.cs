using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// "Show my recovery phrase" asks for the wallet password (or the PIN) before the words go on
/// screen (bug-bounty report 2026-11-09: one click showed all twelve to anyone holding an
/// unlocked wallet, password or not). Asked every time; nothing to ask where the wallet has
/// neither.
/// </summary>
public class RevealPhraseTests : LiteUiTestBase
{
    private sealed class FakePassword(string password) : IWalletPassword
    {
        public Task<bool> IsSetAsync() => Task.FromResult(true);
        public Task<bool> CheckAsync(string candidate) => Task.FromResult(candidate == password);
    }

    private LoadedVault _vault = null!;

    /// <summary>The backup page of a new, not yet verified wallet.</summary>
    private async Task<IRenderedComponent<VerifyBackup>> BackupPageAsync(string? password = null, string? pin = null)
    {
        if (password != null) Services.AddSingleton<IWalletPassword>(new FakePassword(password));
        _vault = new LoadedVault { Words = null };
        Wire(_vault, new FakeReader(), new FakeRelay());
        if (pin != null) Services.GetRequiredService<IPinLock>().Set(pin);
        await Services.GetRequiredService<LiteWalletService>().CreateNewWalletAsync();

        var cut = RenderComponent<VerifyBackup>();
        cut.WaitForAssertion(() => Assert.Contains("Show my recovery phrase", cut.Markup));
        return cut;
    }

    private static void Tap(IRenderedComponent<VerifyBackup> cut, string label) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(label)).Click();

    private static void Answer(IRenderedComponent<VerifyBackup> cut, string answer)
    {
        cut.Find("input.lite-reveal-answer").Input(answer);
        Tap(cut, "Show the phrase");
    }

    [Fact]
    public async Task With_a_wallet_password_the_phrase_stays_hidden_until_the_password_is_given()
    {
        var cut = await BackupPageAsync(password: "correct horse");

        Tap(cut, "Show my recovery phrase");
        Assert.Empty(cut.FindAll(".lite-word"));                      // was: all twelve words, at once
        Assert.Contains("wallet password", cut.Markup);

        Answer(cut, "wrong");
        Assert.Empty(cut.FindAll(".lite-word"));
        Assert.Contains("That password is not right.", cut.Markup);

        Answer(cut, "correct horse");
        Assert.Equal(12, cut.FindAll(".lite-word").Count);
        Assert.Empty(cut.FindAll("input.lite-reveal-answer"));        // the question is gone
    }

    [Fact]
    public async Task Hiding_the_phrase_and_showing_it_again_asks_again()
    {
        var cut = await BackupPageAsync(password: "correct horse");
        Tap(cut, "Show my recovery phrase");
        Answer(cut, "correct horse");

        Tap(cut, "Hide my recovery phrase");
        Assert.Empty(cut.FindAll(".lite-word"));
        Tap(cut, "Show my recovery phrase");

        Assert.Empty(cut.FindAll(".lite-word"));
        Assert.Single(cut.FindAll("input.lite-reveal-answer"));
        Assert.Equal(string.Empty, cut.Find("input.lite-reveal-answer").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task With_only_a_pin_the_pin_is_asked_for()
    {
        var cut = await BackupPageAsync(pin: "4821");

        Tap(cut, "Show my recovery phrase");
        Assert.Empty(cut.FindAll(".lite-word"));
        Assert.Contains("Enter your PIN", cut.Markup);

        Answer(cut, "0000");
        Assert.Contains("That PIN is not right.", cut.Markup);
        Answer(cut, "4821");
        Assert.Equal(12, cut.FindAll(".lite-word").Count);
    }

    [Fact]
    public async Task Five_wrong_answers_lock_the_wallet()
    {
        var cut = await BackupPageAsync(pin: "4821");
        var session = Services.GetRequiredService<AppLockSession>();
        session.Unlock();
        Tap(cut, "Show my recovery phrase");

        for (var i = 0; i < 4; i++) Answer(cut, "000" + i);
        Assert.True(session.IsUnlocked);

        Answer(cut, "9999");

        Assert.False(session.IsUnlocked);
        Assert.Empty(cut.FindAll(".lite-word"));
        Assert.Empty(cut.FindAll("input.lite-reveal-answer"));
    }

    [Fact]
    public async Task The_words_leave_the_vault_only_when_the_gate_releases_them()
    {
        // Audit 2026-11-09: the page used to read the whole phrase when it opened and keep it
        // behind a flag. Now the gate reads it, once the answer is right; the page's own needs
        // (how many words to quiz on) cost one read at opening and no more.
        var cut = await BackupPageAsync(pin: "4821");
        var opened = _vault.Loads;

        Tap(cut, "Show my recovery phrase");
        Answer(cut, "0000");
        Assert.Equal(opened, _vault.Loads);                        // a wrong answer fetches nothing
        Assert.Empty(cut.FindAll(".lite-word"));

        Answer(cut, "4821");
        Assert.Equal(opened + 1, _vault.Loads);
        Assert.Equal(12, cut.FindAll(".lite-word").Count);
    }

    [Fact]
    public async Task Wrong_answers_are_remembered_across_visits_to_the_page_and_the_pin_gate_shares_them()
    {
        var cut = await BackupPageAsync(pin: "4821");
        Tap(cut, "Show my recovery phrase");
        for (var i = 0; i < 3; i++) Answer(cut, "000" + i);

        // Leave and come back: the count did not start over (it is the head's one limiter).
        var again = RenderComponent<VerifyBackup>();
        again.WaitForAssertion(() => Assert.Contains("Show my recovery phrase", again.Markup));
        var attempts = Services.GetRequiredService<AttemptLimiter>();
        Assert.False(attempts.Miss());                             // the fourth, at the lock screen say
        Tap(again, "Show my recovery phrase");
        Answer(again, "9999");                                     // the fifth

        Assert.NotNull(attempts.Wait);                             // the lock screen now says "wait" too
        Assert.False(Services.GetRequiredService<AppLockSession>().IsUnlocked);
        Assert.Empty(again.FindAll("input.lite-reveal-answer"));   // the question closed with the wallet
    }

    [Fact]
    public async Task With_neither_a_password_nor_a_pin_the_phrase_shows_at_once()
    {
        var cut = await BackupPageAsync();

        Tap(cut, "Show my recovery phrase");

        Assert.Equal(12, cut.FindAll(".lite-word").Count);
        Assert.Empty(cut.FindAll("input.lite-reveal-answer"));
    }
}

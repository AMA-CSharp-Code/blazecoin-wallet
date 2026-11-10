namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// What a wallet asks for before it shows the recovery phrase again (bug-bounty report
/// 2026-11-09: one click showed it to anyone holding an unlocked wallet): the wallet password
/// where there is one, else the PIN where one is set, else nothing.
/// </summary>
public class SecretRevealGateTests
{
    private sealed class FakePassword(string? password) : IWalletPassword
    {
        public int Checks { get; private set; }
        public Task<bool> IsSetAsync() => Task.FromResult(password != null);
        public Task<bool> CheckAsync(string candidate) { Checks++; return Task.FromResult(candidate == password); }
    }

    private static InMemoryPinLock Pin(string? pin)
    {
        var p = new InMemoryPinLock();
        if (pin != null) p.Set(pin);
        return p;
    }

    [Fact]
    public async Task With_neither_a_password_nor_a_pin_there_is_nothing_to_ask()
    {
        var gate = new SecretRevealGate(Pin(null));

        Assert.Equal(RevealChallenge.None, await gate.ChallengeAsync());
        Assert.True(await gate.ConfirmAsync(null));
    }

    [Fact]
    public async Task A_pin_is_asked_for_and_only_the_right_one_passes()
    {
        var gate = new SecretRevealGate(Pin("4821"));

        Assert.Equal(RevealChallenge.Pin, await gate.ChallengeAsync());
        Assert.True(await gate.ConfirmAsync("4821"));
        Assert.False(await gate.ConfirmAsync("4822"));
        Assert.False(await gate.ConfirmAsync(""));
        Assert.False(await gate.ConfirmAsync(null));
    }

    [Fact]
    public async Task A_wallet_password_is_asked_for_ahead_of_a_pin()
    {
        var password = new FakePassword("correct horse");
        var gate = new SecretRevealGate(Pin("4821"), password);

        Assert.Equal(RevealChallenge.Password, await gate.ChallengeAsync());
        Assert.True(await gate.ConfirmAsync("correct horse"));
        Assert.False(await gate.ConfirmAsync("4821"));          // the PIN is not the password
        Assert.False(await gate.ConfirmAsync(""));
        Assert.Equal(2, password.Checks);                        // an empty answer is never tried
    }

    [Fact]
    public async Task A_head_with_a_password_vault_but_no_password_set_falls_back_to_the_pin()
    {
        var gate = new SecretRevealGate(Pin("4821"), new FakePassword(null));

        Assert.Equal(RevealChallenge.Pin, await gate.ChallengeAsync());
        Assert.True(await gate.ConfirmAsync("4821"));
    }

    // ── audit 2026-11-09: the gate hands out the phrase, under the one guessing limit ──

    private sealed class Locker : IWalletLocker
    {
        public bool CanLock { get; init; } = true;
        public int Locks;
        public Task LockAsync() { Locks++; return Task.CompletedTask; }
    }

    private const string Phrase = "legal winner thank year wave sausage worth useful legal winner thank yellow";
    private int _phraseReads;
    private Task<string> ReadPhrase() { _phraseReads++; return Task.FromResult(Phrase); }

    [Fact]
    public async Task The_phrase_is_given_only_for_the_right_answer_and_read_only_then()
    {
        var gate = new SecretRevealGate(Pin("4821"), phrase: ReadPhrase);

        var wrong = await gate.RevealAsync("0000");
        Assert.Null(wrong.Phrase);
        Assert.Equal("That PIN is not right.", wrong.Error);
        Assert.False(wrong.WalletLocked);
        Assert.Equal(0, _phraseReads);                            // the words were never fetched

        var right = await gate.RevealAsync("4821");
        Assert.Equal(Phrase, right.Phrase);
        Assert.Null(right.Error);
        Assert.Equal(1, _phraseReads);

        var passwordGate = new SecretRevealGate(Pin(null), new FakePassword("correct horse"), ReadPhrase);
        Assert.Equal("That password is not right.", (await passwordGate.RevealAsync("horse")).Error);
        Assert.Equal(Phrase, (await passwordGate.RevealAsync("correct horse")).Phrase);
    }

    [Fact]
    public async Task With_nothing_to_ask_the_phrase_is_given_at_once()
    {
        var gate = new SecretRevealGate(Pin(null), phrase: ReadPhrase);
        Assert.Equal(Phrase, (await gate.RevealAsync(null)).Phrase);
    }

    [Fact]
    public async Task One_wrong_answer_too_many_locks_the_wallet_and_then_no_answer_is_taken_for_thirty_seconds()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var attempts = new AttemptLimiter { Clock = () => now };
        var locker = new Locker();
        var gate = new SecretRevealGate(Pin("4821"), phrase: ReadPhrase, attempts: attempts, locker: locker);

        for (var i = 1; i < AttemptLimiter.MaxMisses; i++)
            Assert.False((await gate.RevealAsync("0000")).WalletLocked);
        Assert.Equal(0, locker.Locks);

        var tripped = await gate.RevealAsync("0000");
        Assert.True(tripped.WalletLocked);
        Assert.Equal(AttemptLimiter.TrippedMessage, tripped.Error);
        Assert.Equal(1, locker.Locks);

        // Not even the right answer is taken while the wait runs — and the words are not fetched.
        now += TimeSpan.FromSeconds(10);
        var waiting = await gate.RevealAsync("4821");
        Assert.Null(waiting.Phrase);
        Assert.Equal("Too many attempts — wait 21s.", waiting.Error);
        Assert.Equal(0, _phraseReads);

        now += TimeSpan.FromSeconds(21);
        Assert.Equal(Phrase, (await gate.RevealAsync("4821")).Phrase);
    }

    [Fact]
    public async Task Where_the_wallet_cannot_lock_the_wait_still_applies()
    {
        var attempts = new AttemptLimiter();
        var gate = new SecretRevealGate(Pin("4821"), phrase: ReadPhrase, attempts: attempts, locker: new Locker { CanLock = false });

        for (var i = 0; i < AttemptLimiter.MaxMisses; i++) await gate.RevealAsync("0000");

        Assert.NotNull(attempts.Wait);                            // was: five misses did nothing at all here
        Assert.StartsWith("Too many attempts — wait", (await gate.RevealAsync("4821")).Error);
    }

    [Fact]
    public async Task The_reveal_and_the_pin_gate_count_misses_together()
    {
        // The layout's PIN gate records its misses on the same limiter: three guesses at the phrase
        // and two at the lock screen are five.
        var attempts = new AttemptLimiter();
        var gate = new SecretRevealGate(Pin("4821"), phrase: ReadPhrase, attempts: attempts, locker: new Locker());

        for (var i = 0; i < 3; i++) await gate.RevealAsync("0000");
        Assert.False(attempts.Miss());
        Assert.True(attempts.Miss());                             // the lock screen's fifth: the wait starts for both
        Assert.NotNull(attempts.Wait);
        Assert.StartsWith("Too many attempts — wait", (await gate.RevealAsync("4821")).Error);

        // And a right answer anywhere starts the count over.
        var fresh = new AttemptLimiter();
        var gate2 = new SecretRevealGate(Pin("4821"), phrase: ReadPhrase, attempts: fresh);
        for (var i = 0; i < 4; i++) await gate2.RevealAsync("0000");
        Assert.NotNull((await gate2.RevealAsync("4821")).Phrase);
        for (var i = 0; i < 4; i++) Assert.False(fresh.Miss());
    }

    [Fact]
    public async Task The_default_registration_asks_for_the_pin()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var pin = Pin("4821");
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IPinLock>(services, pin);
        LiteWalletServiceCollectionExtensions.AddSecretRevealGate(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var gate = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ISecretRevealGate>(provider);

        Assert.Equal(RevealChallenge.Pin, await gate.ChallengeAsync());
    }
}

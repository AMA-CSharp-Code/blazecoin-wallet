namespace BlazecoinWallet.Lite;

/// <summary>What has to be entered again before a recovery phrase is shown in an open session.</summary>
public enum RevealChallenge
{
    /// <summary>Nothing to ask: this wallet has neither a password nor a PIN.</summary>
    None,
    /// <summary>The app-lock PIN.</summary>
    Pin,
    /// <summary>The wallet password the keys are encrypted under (the web wallet).</summary>
    Password,
}

/// <summary>
/// The second look before a recovery phrase goes on screen.
/// <para>Why (bug-bounty report 2026-11-09): on the backup page one click on "Show my recovery
/// phrase" printed all twelve words to whoever was holding an unlocked wallet — a phone handed
/// over, a browser left open — even where the wallet had a password. Sending coins from an open
/// session at least leaves a transaction to see; a copied phrase leaves nothing, and is the
/// whole wallet for good. So the page asks for the password (or the PIN) each time the phrase
/// is to be shown, where there is one to ask for.</para>
/// </summary>
public interface ISecretRevealGate
{
    /// <summary>What this wallet can ask for right now.</summary>
    Task<RevealChallenge> ChallengeAsync();

    /// <summary>True when <paramref name="answer"/> is right for the current challenge.
    /// Always true when there is nothing to ask.</summary>
    Task<bool> ConfirmAsync(string? answer);

    /// <summary>
    /// The phrase itself — and the ONLY way a page gets it (audit 2026-11-09: the page used to
    /// hold the words from the moment it opened, behind a flag). Given when
    /// <paramref name="answer"/> is right for the current challenge, or at once when there is
    /// nothing to ask; otherwise why not. A wrong answer counts against the attempt limit the
    /// app-lock PIN gate shares (<see cref="AttemptLimiter"/>): one too many locks the wallet,
    /// where it can lock, and no answer is taken for the next thirty seconds.
    /// </summary>
    Task<RevealOutcome> RevealAsync(string? answer);
}

/// <param name="Phrase">The recovery phrase; null when it was not given.</param>
/// <param name="Error">Why not, in the words to show.</param>
/// <param name="WalletLocked">True when this answer was one too many and the wallet was locked.</param>
public sealed record RevealOutcome(string? Phrase, string? Error, bool WalletLocked = false);

/// <summary>A head whose keys are encrypted at rest under a wallet password (the web wallet's
/// vault). Heads without one do not register this.</summary>
public interface IWalletPassword
{
    /// <summary>True when a password protects this wallet.</summary>
    Task<bool> IsSetAsync();

    /// <summary>True when <paramref name="password"/> is that password. Changes nothing.</summary>
    Task<bool> CheckAsync(string password);
}

/// <summary>
/// The one rule every head shares: the wallet password where there is one, otherwise the
/// app-lock PIN where one is set, otherwise nothing.
/// </summary>
/// <param name="phrase">Where the phrase comes from once the challenge is passed (the wallet service).</param>
/// <param name="attempts">The guessing limit shared with the PIN gate; a fresh one when none is given.</param>
/// <param name="locker">What locks the wallet when the limit trips; null (or one that cannot lock) → the wait alone.</param>
public sealed class SecretRevealGate(IPinLock pin, IWalletPassword? password = null, Func<Task<string>>? phrase = null,
    AttemptLimiter? attempts = null, IWalletLocker? locker = null) : ISecretRevealGate
{
    private readonly AttemptLimiter _attempts = attempts ?? new AttemptLimiter();

    public async Task<RevealChallenge> ChallengeAsync()
    {
        if (password != null && await password.IsSetAsync()) return RevealChallenge.Password;
        return pin.IsSet ? RevealChallenge.Pin : RevealChallenge.None;
    }

    public async Task<RevealOutcome> RevealAsync(string? answer)
    {
        if (phrase == null) throw new InvalidOperationException("This gate has no phrase to reveal.");
        if (_attempts.Wait is { } wait) return new(null, AttemptLimiter.WaitMessage(wait));

        var challenge = await ChallengeAsync();
        if (challenge != RevealChallenge.None && !await ConfirmAsync(answer))
        {
            if (_attempts.Miss())
            {
                // Guessing: lock the wallet, as leaving it idle would, where this head can.
                var locked = locker is { CanLock: true };
                if (locked) await locker!.LockAsync();
                return new(null, AttemptLimiter.TrippedMessage, locked);
            }
            return new(null, challenge == RevealChallenge.Password ? "That password is not right." : "That PIN is not right.");
        }
        _attempts.Reset();
        return new(await phrase(), null);
    }

    public async Task<bool> ConfirmAsync(string? answer)
    {
        switch (await ChallengeAsync())
        {
            case RevealChallenge.Password:
                return !string.IsNullOrEmpty(answer) && await password!.CheckAsync(answer);
            case RevealChallenge.Pin:
                return !string.IsNullOrEmpty(answer) && pin.Verify(answer);
            default:
                return true;
        }
    }
}

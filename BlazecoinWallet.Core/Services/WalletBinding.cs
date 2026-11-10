namespace BlazecoinWallet.Core.Services;

/// <summary>
/// The one place "the wallet on screen" becomes the wallet a write is bound to (audit 2026-11-09).
/// <para>A read can re-point the app at another wallet when the selected one is no longer loaded
/// on the node; the user is told by a notice they have to acknowledge
/// (<see cref="IWalletSwitchNotice"/>). Calls that FOLLOW the active wallet were held back until
/// then (bug-bounty report 2026-11-09) - but the Send page and auto-payout Start take the active
/// wallet as a PIN and send to it by name, and they took it with no look at the notice. So
/// Review, Send Max, a straight send or Start, pressed in the moment after the switch, bound
/// themselves to a wallet the user had not been shown yet. Every write now gets its wallet here:
/// the Send page, auto-payout, the Console, and the RPC layer's own active-wallet writes.</para>
/// </summary>
public interface IWalletBinding
{
    /// <summary>
    /// The active wallet, as the wallet a write is about to be bound to ("" = none chosen: the
    /// node's default wallet endpoint). Throws <see cref="WalletSwitchPendingException"/> while a
    /// switch the app made itself has not been acknowledged - its message names both wallets and
    /// says that nothing was done, and is fit to show as it is.
    /// </summary>
    string BindActiveForWrite();

    /// <summary>
    /// True while <paramref name="bound"/> is still the active wallet. <paramref name="activeNow"/>
    /// is the active wallet either way ("" = none chosen), for the caller's own wording.
    /// </summary>
    bool IsStillActive(string bound, out string activeNow);
}

public sealed class WalletBinding(IWalletContext context, IWalletSwitchNotice notice) : IWalletBinding
{
    /// <summary>
    /// What a flow says when the wallet it was bound to is no longer the active one — the Send
    /// page's words, used by the Quantum sweep and the vintage send too, so "the wallet moved
    /// under this payment" reads the same wherever it happens.
    /// </summary>
    public static string MovedMessage(string bound, string activeNow) =>
        MovedSentence(bound, activeNow) + NothingSent;

    /// <summary>The second half of <see cref="MovedMessage"/> (it begins with a space).</summary>
    public const string NothingSent = " Nothing was sent — check which wallet is active, then review the payment again.";

    /// <summary>The first sentence of <see cref="MovedMessage"/> alone, for a flow that has to say
    /// something else about what was and was not sent (a sweep stopped between two batches).</summary>
    public static string MovedSentence(string bound, string activeNow) =>
        $"The active wallet changed from {Name(bound)} to {Name(activeNow)} while this payment was being prepared.";

    private static string Name(string? wallet) => string.IsNullOrEmpty(wallet) ? "the default wallet" : $"\"{wallet}\"";

    public string BindActiveForWrite()
    {
        var active = context.Active ?? string.Empty;
        // Looked at AFTER the wallet is read: a healing read raises the notice first and moves the
        // context second, so whoever picked up the new wallet always finds the notice.
        if (notice.Current is { } pending) throw new WalletSwitchPendingException(pending);
        return active;
    }

    public bool IsStillActive(string bound, out string activeNow)
    {
        activeNow = context.Active ?? string.Empty;
        return string.Equals(activeNow, bound ?? string.Empty, StringComparison.Ordinal);
    }
}

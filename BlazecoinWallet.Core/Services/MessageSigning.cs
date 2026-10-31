using System.Text.RegularExpressions;

namespace BlazecoinWallet.Core.Services;

/// <summary>
/// Signed-message support for the desktop wallet's Sign Message page (2026-10-08). The page exists so
/// that proving an address to the website is one paste and one click, not an RPC typed into the console:
/// the daemon demands <c>/wallet/&lt;name&gt;</c> routing once more than one wallet is loaded, and the
/// address being proved may live in a wallet other than the window's active one. Every method here names
/// its wallet explicitly, so the page can find the owning wallet, unlock it in place if it is encrypted,
/// sign, and lock it again — the window's active wallet is never changed.
/// </summary>
public interface IMessageSigningRpc
{
    /// <summary>The wallets the daemon currently has loaded (<c>listwallets</c>).</summary>
    Task<IReadOnlyList<string>> ListLoadedWalletsAsync(CancellationToken ct = default);

    /// <summary>The loaded wallet that holds the key for <paramref name="address"/>, or null when none does
    /// (or the daemon could not say). Checks <c>getaddressinfo.ismine</c> wallet by wallet, active one first.</summary>
    Task<string?> FindWalletOwningAsync(string address, CancellationToken ct = default);

    /// <summary>True when <paramref name="wallet"/> is encrypted AND currently locked; false when unencrypted or
    /// unlocked; null when the daemon could not say.</summary>
    Task<bool?> IsLockedInAsync(string wallet, CancellationToken ct = default);

    Task UnlockInAsync(string wallet, string passphrase, int seconds, CancellationToken ct = default);
    Task LockInAsync(string wallet, CancellationToken ct = default);

    /// <summary><c>signmessage</c> through <paramref name="wallet"/>. For a legacy B… address the result is the
    /// 88-char compact ECDSA signature; for a post-quantum BQ… address it is base64 of the ML-DSA-44 key blob
    /// followed by the signature (≈ 4,980 chars) — paste it whole.</summary>
    Task<string> SignMessageInAsync(string wallet, string address, string message, CancellationToken ct = default);

    /// <summary><c>verifymessage</c> (node-level, no wallet needed). Null when the daemon answered with an error
    /// rather than a verdict — the fork reports a malformed post-quantum signature as an error, not false.</summary>
    Task<bool?> VerifyMessageAsync(string address, string signature, string message, CancellationToken ct = default);
}

/// <summary>What the user pasted into the Sign Message page, taken apart.</summary>
public sealed record PastedSigningRequest(string? Address, string Message);

/// <summary>
/// Understands the two things a user is likely to paste from the website's Wallet Address card: the ready-made
/// console line <c>signmessage "B…" "blazecoin-verify:…"</c> (address + message in one go) or the bare
/// challenge text. Anything else is treated as a bare message. Pure, so it is unit-tested without a daemon.
/// </summary>
public static class SigningPasteParser
{
    // signmessage "<address>" "<message>"  — tolerant of surrounding whitespace and single quotes.
    private static readonly Regex CommandLine = new(
        @"^\s*signmessage\s+[""']?(?<addr>[1-9A-HJ-NP-Za-km-z]{25,60})[""']?\s+[""'](?<msg>.*?)[""']\s*$",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static PastedSigningRequest Parse(string? pasted)
    {
        var text = (pasted ?? string.Empty).Trim();
        if (text.Length == 0) return new PastedSigningRequest(null, string.Empty);

        var m = CommandLine.Match(text);
        if (m.Success)
            return new PastedSigningRequest(m.Groups["addr"].Value, m.Groups["msg"].Value);

        return new PastedSigningRequest(null, text);
    }

    /// <summary>A Blazecoin address as the website hands it out: base58, starting with B (BQ… for post-quantum).</summary>
    public static bool LooksLikeAddress(string? s)
        => !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim(), @"^B[1-9A-HJ-NP-Za-km-z]{25,60}$");
}

namespace BlazecoinWallet.Core.Services;

public enum SignOutcome
{
    /// <summary>Signed (and checked node-level — see <see cref="SignResult.Verified"/>).</summary>
    Signed,
    /// <summary>No loaded wallet holds the key for the address.</summary>
    NoWalletOwnsAddress,
    /// <summary>The owning wallet is encrypted and locked; call again with its passphrase.</summary>
    PassphraseNeeded,
    /// <summary>The daemon refused the passphrase (RPC −14). Nothing was signed.</summary>
    WrongPassphrase
}

/// <param name="Wallet">The wallet that holds the address (null only for <see cref="SignOutcome.NoWalletOwnsAddress"/>).</param>
/// <param name="Verified">The daemon's own verdict on the signature; null when it answered with an error rather than a verdict.</param>
public sealed record SignResult(SignOutcome Outcome, string? Wallet, string? Signature = null, bool? Verified = null);

public interface IMessageSigner
{
    /// <summary>
    /// Prove <paramref name="address"/> by signing <paramref name="message"/> through whichever loaded wallet holds its
    /// key. An encrypted wallet is unlocked for 60 s with <paramref name="passphrase"/> and locked again whatever
    /// happens; without a passphrase the result says one is needed. The passphrase is never stored.
    /// </summary>
    Task<SignResult> SignAsync(string address, string message, string? passphrase, CancellationToken ct = default);
}

/// <summary>
/// The Sign Message sequence as one tested unit (2026-10-22, SOLID audit A6). It used to live in the Razor panel's
/// <c>@code</c>, which cached the owning wallet across address edits — after a "passphrase needed" stop, signing a
/// different address unlocked the FIRST wallet and the daemon then refused the sign (wrong key). The owning wallet is
/// resolved afresh on every call here, so there is nothing to go stale.
/// </summary>
public sealed class MessageSigner : IMessageSigner
{
    public const int UnlockSeconds = 60;

    private readonly IMessageSigningRpc _rpc;

    public MessageSigner(IMessageSigningRpc rpc) => _rpc = rpc;

    public async Task<SignResult> SignAsync(string address, string message, string? passphrase, CancellationToken ct = default)
    {
        // Always from scratch — the address may have changed since the last call.
        var wallet = await _rpc.FindWalletOwningAsync(address, ct);
        if (wallet is null)
            return new SignResult(SignOutcome.NoWalletOwnsAddress, null);

        var locked = await _rpc.IsLockedInAsync(wallet, ct);
        if (locked == true)
        {
            if (string.IsNullOrEmpty(passphrase))
                return new SignResult(SignOutcome.PassphraseNeeded, wallet);
            try
            {
                await _rpc.UnlockInAsync(wallet, passphrase, UnlockSeconds, ct);
            }
            catch (RpcException rex) when (rex.Code == -14)
            {
                return new SignResult(SignOutcome.WrongPassphrase, wallet);
            }
        }

        try
        {
            var signature = await _rpc.SignMessageInAsync(wallet, address, message, ct);
            var verified = await _rpc.VerifyMessageAsync(address, signature, message, ct);
            return new SignResult(SignOutcome.Signed, wallet, signature, verified);
        }
        finally
        {
            // Relock what we unlocked, even when the sign threw; the 60 s timeout is the backstop if this fails.
            if (locked == true)
            {
                try { await _rpc.LockInAsync(wallet, ct); } catch { /* timeout relocks it anyway */ }
            }
        }
    }
}

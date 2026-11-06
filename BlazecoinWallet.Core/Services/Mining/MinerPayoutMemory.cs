namespace BlazecoinWallet.Core.Services.Mining;

/// <summary>
/// The address the solo miner last paid to, and the wallet it was made for. The miner keeps its
/// payout address across a stop and start so that restarting does not mint a new address every
/// time — but the address belongs to the wallet that was active when it was made. It used to be
/// remembered with no owner (bug-bounty report 2026-11-06, fourth round): stop, switch wallet,
/// start, and the new blocks paid the wallet the user had just left.
/// </summary>
public sealed class MinerPayoutMemory
{
    /// <summary>The remembered payout address, or null.</summary>
    public string? Address { get; private set; }

    /// <summary>The active wallet when <see cref="Address"/> was made (null = none chosen).</summary>
    public string? Wallet { get; private set; }

    public void Remember(string address, string? wallet)
    {
        Address = address;
        Wallet = wallet;
        if (string.Equals(_dropped, address, StringComparison.Ordinal)) _dropped = null;   // in use again, by choice of the resolver
    }

    public void Forget()
    {
        Address = null;
        Wallet = null;
    }

    // The address most recently dropped for a wallet change. A page that had not yet caught up
    // may still hand it back as if it were configured; it is this miner's old memory, not a choice.
    private string? _dropped;

    /// <summary>
    /// The address to start with. An address the caller chose stands. One that is only the
    /// miner's own memory handed back is used while the active wallet is still the wallet it was
    /// made for, and dropped (null = make a fresh one from the active wallet) once it is not.
    /// </summary>
    public string? ForStart(string? requested, string? activeWallet)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var asked = requested.Trim();
        if (_dropped != null && string.Equals(asked, _dropped, StringComparison.Ordinal)) return null;
        var isMemory = Address != null && string.Equals(asked, Address, StringComparison.Ordinal);
        return isMemory && !SameWallet(activeWallet) ? null : requested;
    }

    /// <summary>Forgets the address when the active wallet is no longer the one it was made for.
    /// True when something was forgotten.</summary>
    public bool DropIfWalletChanged(string? activeWallet)
    {
        if (Address == null || SameWallet(activeWallet)) return false;
        _dropped = Address;
        Forget();
        return true;
    }

    private bool SameWallet(string? activeWallet) => string.Equals(Wallet, activeWallet, StringComparison.Ordinal);
}

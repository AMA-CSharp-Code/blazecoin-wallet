using BlazecoinWallet.Core.Services.Mining;

namespace BlazecoinWallet.Core.Services;

/// <summary>
/// The one answer to "can this address be paid?" — for the Send page, Auto Payout, the vintage
/// planners and the Quantum sweep alike: a legacy B… address or a post-quantum BQ… address
/// (<see cref="BitcoinProtocol.KindOf"/>), and nothing else.
/// <para>Why one place, and why an allow-list (audit 2026-11-09): the rule was written three times.
/// The vintage planners and Auto Payout refused ONE prefix — blz1…, the SegWit address this chain
/// accepts and nobody can ever spend from — and so did the Send page, in its own copy; anything
/// else that was not blank went through: a mistyped address, a Bitcoin address, a script-hash
/// address (which can hide a witness program behind an ordinary-looking address). The sweep
/// planner already asked the right question — is it a B… or a BQ… address? — and that is now the
/// question everywhere.</para>
/// </summary>
public static class PayableAddress
{
    /// <summary>The second half of every refusal: what can be paid, and why a blz1… address above all cannot.</summary>
    public const string Rule =
        "only a legacy B… or a post-quantum BQ… address can be paid. (A blz1… SegWit address is accepted by this " +
        "chain and spendable by nobody — SegWit is not active here — so coins sent to one would be lost.)";

    /// <summary>True for a legacy B… or a post-quantum BQ… mainnet address; false for everything else.</summary>
    public static bool CanBePaid(string? address) => BitcoinProtocol.KindOf(address) is not null;

    /// <summary>Why <paramref name="address"/> cannot receive an output (<paramref name="role"/> names it:
    /// "recipient", "change", "destination"), or null when it can.</summary>
    public static string? Problem(string? address, string role)
    {
        if (string.IsNullOrWhiteSpace(address)) return $"A {role} address is required.";
        return CanBePaid(address) ? null : $"The {role} address is not a valid Blazecoin address: {Rule}";
    }
}

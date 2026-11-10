using BlazecoinWallet.Core.Services.Mining;

namespace BlazecoinWallet.Core.Services;

/// <summary>
/// The smallest output the network relays, by the kind of address it pays — the one place the two
/// figures live (audit 2026-11-09: the desktop planners and the lite wallet's transaction builder
/// each carried their own copy of both).
/// <para><b>546</b> for a legacy output. <c>minrelaytxfee=0</c> makes sends FREE on this chain, but
/// dust is a SEPARATE policy (<c>dustrelayfee</c>, left at Core's default) and it bites regardless
/// of fee. Verified against the live daemon 2026-08-01 with <c>testmempoolaccept</c>: 545 satoshis
/// → "dust", 546 → accepted.</para>
/// <para><b>11,475</b> for a post-quantum (BQ…) output. The node prices an output by what it takes
/// to create AND spend, and a P2PQH output is spent by a 3,739-byte signature-and-key: 43 bytes of
/// output + a 3,782-byte input, at 3,000 sat/kvB. Nodes up to 2.1.1 priced it like a legacy output;
/// 2.1.2 applies this figure (bug-bounty report 2026-11-06).</para>
/// </summary>
public static class DustPolicy
{
    public const long LegacyFloorSatoshis = 546;
    public const long PostQuantumFloorSatoshis = 11_475;

    /// <summary>The floor for an output of the given kind.</summary>
    public static long FloorFor(bool postQuantum) => postQuantum ? PostQuantumFloorSatoshis : LegacyFloorSatoshis;

    /// <summary>The floor for an output to <paramref name="address"/> (anything that is not a BQ… address is held to the legacy figure).</summary>
    public static long FloorFor(string? address) => FloorFor(BitcoinProtocol.KindOf(address) == BlazecoinAddressKind.PostQuantum);

    /// <summary>
    /// The same with the legacy figure supplied by the caller — the planners take it as a parameter,
    /// and 0 switches the check off, as it always has. A BQ… output is never held to less than its own floor.
    /// </summary>
    public static long FloorFor(string? address, long legacyFloor) =>
        legacyFloor <= 0 ? 0
        : BitcoinProtocol.KindOf(address) == BlazecoinAddressKind.PostQuantum
            ? Math.Max(legacyFloor, PostQuantumFloorSatoshis)
            : legacyFloor;
}

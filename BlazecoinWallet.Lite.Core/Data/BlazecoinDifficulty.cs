using System.Globalization;
using System.Numerics;

namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// The slice of Blazecoin's proof-of-work difficulty rules a LITE client needs to VERIFY
/// (not mine) a forward run of block headers. Every post-checkpoint height is in the chain's
/// "Era 2" (height ≥ 600,000), whose retarget is ±10% every 120 blocks; Era-1's ±400% band is
/// deliberately not ported because a shipped checkpoint is always deep in Era 2. This ports
/// src/pow.cpp — the arith_uint256 compact codec and PermittedDifficultyTransition — exactly,
/// so a genuine mainnet header run validates and a forged one cannot.
/// </summary>
public static class BlazecoinDifficulty
{
    /// <summary>Consensus::DifficultyAdjustmentInterval() — retarget every 120 blocks.</summary>
    public const int RetargetInterval = 120;

    /// <summary>Consensus nPowTargetTimespan — 1 hour.</summary>
    public const long TargetTimespan = 60 * 60;

    // Era-2 ±10% timespan bounds (src/pow.cpp GetRetargetTimespanBounds) — the arithmetic
    // byte-matches the daemon's (timespan ± timespan/10).
    private const long LowerTimespan = TargetTimespan - TargetTimespan / 10; // 3240
    private const long UpperTimespan = TargetTimespan + TargetTimespan / 10; // 3960

    /// <summary>powLimit — the easiest allowed target (00000fffff…ff). A header claiming a
    /// target above this is invalid regardless of the PoW it shows.</summary>
    public static readonly BigInteger PowLimit = BigInteger.Parse(
        "00000fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", NumberStyles.HexNumber);

    /// <summary>arith_uint256::SetCompact — decode nBits to a 256-bit target. The sign/overflow
    /// bits Core also decodes are irrelevant for real chain nBits (all positive, in range).</summary>
    public static BigInteger CompactToTarget(uint compact)
    {
        int size = (int)(compact >> 24);
        BigInteger word = compact & 0x007fffff;
        return size <= 3 ? word >> (8 * (3 - size)) : word << (8 * (size - 3));
    }

    /// <summary>arith_uint256::GetCompact — encode a target back to canonical nBits. Byte-matches
    /// Core so the SetCompact(GetCompact(x)) round-trip PermittedDifficultyTransition relies on
    /// truncates precision identically.</summary>
    public static uint TargetToCompact(BigInteger target)
    {
        if (target.Sign <= 0) return 0;
        int size = target.ToByteArray(isUnsigned: true, isBigEndian: true).Length; // == (bits+7)/8
        uint compact = size <= 3
            ? (uint)(target << (8 * (3 - size)))
            : (uint)(target >> (8 * (size - 3)));
        compact &= 0x00ffffff;
        // A set 0x00800000 bit would read as the sign bit — shift down one byte, bump the size.
        if ((compact & 0x00800000) != 0) { compact >>= 8; size += 1; }
        compact |= (uint)size << 24;
        return compact;
    }

    /// <summary>True when the target is in range (0 &lt; target ≤ powLimit) — the range half of
    /// CheckProofOfWork, before the scrypt hash is compared.</summary>
    public static bool TargetInRange(uint bits)
    {
        var target = CompactToTarget(bits);
        return target > BigInteger.Zero && target <= PowLimit;
    }

    /// <summary>
    /// Ports src/pow.cpp PermittedDifficultyTransition (Era 2). Off a retarget boundary the
    /// bits must be UNCHANGED; on a boundary the new target must sit within ±10% of the old
    /// (clamped to powLimit), matching the daemon's own transition guard. Because difficulty
    /// can therefore fall only 10% per 120 blocks, an attacker faking a long forward run must
    /// mine real PoW near the checkpoint's difficulty for a very long stretch before it could
    /// ever drop to a laptop-mineable level — this is the SPV work guarantee that makes a
    /// checkpoint-anchored header chain trustworthy.
    /// </summary>
    public static bool PermittedTransition(long height, uint oldBits, uint newBits)
    {
        if (height % RetargetInterval != 0)
            return newBits == oldBits;

        var old = CompactToTarget(oldBits);
        var observed = CompactToTarget(newBits);

        var largest = old * UpperTimespan / TargetTimespan;
        if (largest > PowLimit) largest = PowLimit;
        if (CompactToTarget(TargetToCompact(largest)) < observed) return false; // too EASY (diff dropped >10%)

        var smallest = old * LowerTimespan / TargetTimespan;
        if (smallest > PowLimit) smallest = PowLimit;
        if (CompactToTarget(TargetToCompact(smallest)) > observed) return false; // too HARD (diff rose >10%)

        return true;
    }

    // ── Era 3: Phoenix-413 (per-block ASERT; spec PHOENIX_413.md in the V2 repo) ──

    /// <summary>Phoenix-413 activation height H_A — chosen 2026-08-26 (tip 4,193,990 with
    /// mining deliberately stopped). Blocks at height &gt; H_A carry an EXACTLY computable
    /// target, which this lite client verifies bit-for-bit — a STRONGER guarantee than the
    /// old ±10% band, because the header stream itself supplies every input the rule needs
    /// (the anchor at H_A and each parent's timestamp).</summary>
    public const long PhoenixActivationHeight = 4_194_000;

    /// <summary>Target spacing (30 s) — Phoenix's schedule unit.</summary>
    public const long PhoenixTargetSpacing = 30;

    /// <summary>Half-life: 413 blocks = 12,390 s. The chain's own number as its time constant.</summary>
    public const long PhoenixHalfLife = 413 * PhoenixTargetSpacing;

    /// <summary>
    /// The exact nBits Phoenix-413 requires for the block at <paramref name="evalHeight"/>
    /// (&gt; H_A), given the anchor (block H_A's nBits + block H_A−1's timestamp) and the
    /// parent (evalHeight−1) timestamp. Ports the canonical aserti3-2d fixed-point form —
    /// semantics pinned by test/phoenix413/generate_phoenix_vectors.py in the V2 repo and
    /// shared bit-exactly with the V2 daemon and the V1.5.2 client: C-truncating division,
    /// arithmetic-shift floor, two's-complement low-16 fraction, right-shift floors,
    /// zero→1, clamp to powLimit. BigInteger is arbitrary-precision, so no overflow
    /// handling is needed here (unlike the daemon's 256-bit arithmetic).
    /// </summary>
    public static uint PhoenixNextBits(uint anchorBits, long anchorParentTime, long anchorHeight, long evalHeight, long parentTime)
    {
        var timeDiff = parentTime - anchorParentTime;
        var heightDiff = (evalHeight - 1) - anchorHeight;

        // C# long division truncates toward zero — identical to the pinned C semantics.
        var num = (timeDiff - PhoenixTargetSpacing * (heightDiff + 1)) * 65536;
        var exponent = num / PhoenixHalfLife;

        var shifts = exponent >> 16;                 // arithmetic shift on long: floor
        var frac = (ulong)exponent & 0xffff;         // two's-complement low 16 bits

        var factor = 65536UL
            + ((195766423245049UL * frac
                + 971821376UL * frac * frac
                + 5127UL * frac * frac * frac
                + (1UL << 47)) >> 48);

        var target = CompactToTarget(anchorBits) * factor;
        var net = shifts - 16;
        target = net < 0 ? target >> (int)-net : target << (int)net; // >> floors (target ≥ 0)

        if (target.IsZero) target = BigInteger.One;
        if (target > PowLimit) target = PowLimit;
        return TargetToCompact(target);
    }
}

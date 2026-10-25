using System.Numerics;
using NBitcoin;
using Org.BouncyCastle.Crypto.Generators;

namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Blazecoin proof-of-work verification. The chain's PoW hash is SCRYPT(1024,1,1) over the
/// 80-byte block header (the same function the miner computes) — NOT the SHA256d block id.
/// NBitcoin's built-in <c>BlockHeader.CheckProofOfWork()</c> checks SHA256d and is therefore
/// WRONG for this chain (it would reject every real mainnet header), so the merkle-proof
/// anchor must use this instead.
/// </summary>
/// <remarks>
/// What this does NOT prove on its own: that the header is ON the chain. Even at powLimit a
/// header that clears its own target costs only ~2^20 scrypt hashes (seconds on one core), so a
/// standalone header is cheap to forge. Chain membership comes from the verified header chain
/// (<see cref="HeaderChainSync"/>), whose difficulty rules make a forged run expensive.
/// </remarks>
public static class BlazecoinPoW
{
    /// <summary>The whole of Core's CheckProofOfWork: the header's nBits must be in range
    /// (<see cref="BlazecoinDifficulty.TargetInRange"/>) AND its scrypt hash must meet that
    /// target. Without the range half a header can claim an absurdly easy target and "meet" it
    /// in a couple of tries (bug-bounty report 2026-10-25).</summary>
    public static bool MeetsTarget(BlockHeader header)
    {
        var bits = header.Bits.ToCompact();
        if (!BlazecoinDifficulty.TargetInRange(bits)) return false;

        var headerBytes = header.ToBytes();
        // Litecoin-style scrypt: the header is both password and salt.
        var scrypt = SCrypt.Generate(headerBytes, headerBytes, N: 1024, r: 1, p: 1, dkLen: 32);

        // The 32-byte scrypt output is a little-endian 256-bit number; it must be <= target.
        // Decoded with the same codec as the range check, so both halves judge one target.
        var powHash = new BigInteger(scrypt, isUnsigned: true, isBigEndian: false);
        return powHash <= BlazecoinDifficulty.CompactToTarget(bits);
    }
}

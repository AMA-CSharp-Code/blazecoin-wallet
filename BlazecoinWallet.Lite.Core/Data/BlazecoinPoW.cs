using NBitcoin;
using Org.BouncyCastle.Crypto.Generators;

namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Blazecoin proof-of-work verification. The chain's PoW hash is SCRYPT(1024,1,1) over the
/// 80-byte block header (the same function the miner computes) — NOT the SHA256d block id.
/// NBitcoin's built-in <c>BlockHeader.CheckProofOfWork()</c> checks SHA256d and is therefore
/// WRONG for this chain (it would reject every real mainnet header), so the merkle-proof
/// anchor must use this instead. A valid PoW is the thing a semi-trusted indexer cannot
/// forge, so this is what makes an inclusion proof trustworthy.
/// </summary>
public static class BlazecoinPoW
{
    /// <summary>True when the header's scrypt hash meets the difficulty target encoded in its nBits.</summary>
    public static bool MeetsTarget(BlockHeader header)
    {
        var headerBytes = header.ToBytes();
        // Litecoin-style scrypt: the header is both password and salt.
        var scrypt = SCrypt.Generate(headerBytes, headerBytes, N: 1024, r: 1, p: 1, dkLen: 32);

        // The 32-byte scrypt output is a little-endian 256-bit number; it must be <= target.
        var powHash = new uint256(scrypt); // uint256(byte[]) is little-endian
        var target = header.Bits.ToUInt256();
        return powHash <= target;
    }
}

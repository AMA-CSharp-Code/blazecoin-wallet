using System.Security.Cryptography;
using System.Text;

namespace BlazecoinWallet.PqBench;

/// <summary>
/// The BIP-340 tagged hash PQ_SIGNATURES.md uses everywhere:
/// <c>SHA256(SHA256(tag) || SHA256(tag) || m)</c>. Here only for the seed-derivation benchmark
/// (spec §6); the consensus-side twin lives with the fork, not in this library.
/// </summary>
public static class TaggedHash
{
    public static byte[] Compute(string tag, ReadOnlySpan<byte> message)
    {
        var tagHash = SHA256.HashData(Encoding.ASCII.GetBytes(tag));
        var buffer = new byte[64 + message.Length];
        tagHash.CopyTo(buffer, 0);
        tagHash.CopyTo(buffer, 32);
        message.CopyTo(buffer.AsSpan(64));
        return SHA256.HashData(buffer);
    }

    /// <summary>Spec §6: ξ_i = TaggedHash("Blazecoin/MLDSA44/seed", master_seed || uint32_le(i)).</summary>
    public static byte[] MlDsa44Seed(ReadOnlySpan<byte> masterSeed, uint index)
    {
        if (masterSeed.Length != 32) throw new ArgumentException("master seed must be 32 bytes", nameof(masterSeed));
        var m = new byte[36];
        masterSeed.CopyTo(m);
        BitConverter.TryWriteBytes(m.AsSpan(32), index);
        if (!BitConverter.IsLittleEndian) Array.Reverse(m, 32, 4);
        return Compute("Blazecoin/MLDSA44/seed", m);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace BlazecoinWallet.Lite.Pq;

/// <summary>
/// The BIP-340 tagged hash PQ_SIGNATURES.md uses for every commitment:
/// <c>TaggedHash(tag, m) = SHA256(SHA256(tag) || SHA256(tag) || m)</c> (spec §3.1).
/// The tag pre-hash is cached per tag; the three consensus tags live here as constants so
/// the daemon's C++ twin and this file can be diffed line-for-line.
/// </summary>
public static class TaggedHash
{
    /// <summary>Commitment of a key blob → pqkh (§3.1).</summary>
    public const string PqkhTag = "Blazecoin/PQKH/v1";

    /// <summary>The transaction digest envelope (§3.5).</summary>
    public const string SigHashTag = "Blazecoin/PQSig/v1";

    /// <summary>Per-index ML-DSA-44 seed derivation (§6).</summary>
    public const string SeedTag = "Blazecoin/MLDSA44/seed";

    private static readonly Dictionary<string, byte[]> TagHashes = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static byte[] Compute(string tag, ReadOnlySpan<byte> message)
    {
        var tagHash = TagHash(tag);
        var buffer = new byte[64 + message.Length];
        tagHash.CopyTo(buffer, 0);
        tagHash.CopyTo(buffer, 32);
        message.CopyTo(buffer.AsSpan(64));
        return SHA256.HashData(buffer);
    }

    private static byte[] TagHash(string tag)
    {
        lock (Gate)
        {
            if (!TagHashes.TryGetValue(tag, out var h))
            {
                h = SHA256.HashData(Encoding.ASCII.GetBytes(tag));
                TagHashes[tag] = h;
            }
            return h;
        }
    }
}

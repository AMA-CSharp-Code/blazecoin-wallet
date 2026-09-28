using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using Org.BouncyCastle.Crypto.Parameters;

namespace BlazecoinWallet.Lite.Pq;

/// <summary>One derived post-quantum key: index i under the wallet's PQ master seed.</summary>
/// <param name="Index">The derivation index i.</param>
/// <param name="Seed">ξ_i — the 32-byte ML-DSA-44 key seed (secret).</param>
/// <param name="PublicKey">The 1,312-byte public key.</param>
/// <param name="Private">The signing handle (secret).</param>
/// <param name="KeyBlob">0x01 || pubkey — what a spend reveals.</param>
/// <param name="KeyHash">pqkh — what the chain sees until then.</param>
public sealed record PqKey(int Index, byte[] Seed, byte[] PublicKey, MLDsaPrivateKeyParameters Private, byte[] KeyBlob, byte[] KeyHash)
{
    /// <summary>The 34-byte P2PQH scriptPubKey paying this key.</summary>
    public byte[] ScriptPubKey => PqScript.BuildP2pqh(KeyHash);

    /// <summary>The BQ (mainnet) / TQ (regtest) address of this key.</summary>
    public string Address(Network? network = null) => PqAddress.Encode(KeyHash, network);
}

/// <summary>
/// The lite wallet's PQ key derivation (PQ_SIGNATURES.md §6): everything descends from the
/// BIP39 seed the user already backs up, so the mnemonic stays the only backup artifact.
/// <code>
/// master_seed = HMAC-SHA512(key = "Blazecoin PQ", data = BIP39 seed (64 bytes))[0:32]
/// ξ_i         = TaggedHash("Blazecoin/MLDSA44/seed", master_seed || uint32_le(i))
/// (pk_i, sk_i) = ML-DSA-44.KeyGen_internal(ξ_i)
/// </code>
/// The HMAC follows the BIP32 convention (the constant is the KEY, the seed is the data).
/// The shared cross-implementation vectors start from master_seed; the HMAC step is pinned
/// by this project's own test with a fixed mnemonic.
/// </summary>
public static class PqKeyDerivation
{
    /// <summary>The HMAC-SHA512 key for the master-seed step.</summary>
    public const string MasterSeedHmacKey = "Blazecoin PQ";

    private static readonly byte[] MasterSeedHmacKeyBytes = Encoding.ASCII.GetBytes(MasterSeedHmacKey);

    /// <summary>master_seed from the 64-byte BIP39 seed.</summary>
    public static byte[] MasterSeedFromBip39Seed(ReadOnlySpan<byte> bip39Seed)
    {
        if (bip39Seed.Length != 64)
            throw new ArgumentException("The BIP39 seed is 64 bytes.", nameof(bip39Seed));
        var full = HMACSHA512.HashData(MasterSeedHmacKeyBytes, bip39Seed);
        var master = full[..32];
        CryptographicOperations.ZeroMemory(full);
        return master;
    }

    /// <summary>ξ_i = TaggedHash("Blazecoin/MLDSA44/seed", master_seed || uint32_le(i)).</summary>
    public static byte[] KeySeed(ReadOnlySpan<byte> masterSeed, uint index)
    {
        if (masterSeed.Length != 32)
            throw new ArgumentException("master_seed must be 32 bytes.", nameof(masterSeed));
        Span<byte> m = stackalloc byte[36];
        masterSeed.CopyTo(m);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(m[32..], index);
        return TaggedHash.Compute(TaggedHash.SeedTag, m);
    }

    /// <summary>The full key at index i: seed, key pair, blob, hash.</summary>
    public static PqKey Derive(ReadOnlySpan<byte> masterSeed, int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var xi = KeySeed(masterSeed, (uint)index);
        var pair = MlDsa44.KeyPairFromSeed(xi);
        var blob = PqScript.BuildKeyBlob(pair.PublicKey);
        return new PqKey(index, xi, pair.PublicKey, pair.Private, blob, PqScript.ComputeKeyHash(blob));
    }
}

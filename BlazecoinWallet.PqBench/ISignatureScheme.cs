namespace BlazecoinWallet.PqBench;

/// <summary>
/// One signature scheme under test. Every implementation is a thin, allocation-honest wrapper over a
/// library the wallet already ships (NBitcoin for the ECDSA baseline, BouncyCastle for the FIPS
/// schemes) or the .NET 10 BCL, so the timings measure what a wallet would actually run.
/// </summary>
public interface ISignatureScheme
{
    /// <summary>Display name, e.g. "ML-DSA-44".</summary>
    string Name { get; }

    /// <summary>"ECDSA", "ML-DSA", "SLH-DSA" — the family the PQ_SIGNATURES algorithm-ID byte distinguishes.</summary>
    string Family { get; }

    /// <summary>Which library produced the numbers ("NBitcoin", "BouncyCastle 2.6.2", ".NET 10 BCL").</summary>
    string Provider { get; }

    /// <summary>NIST security category (1–5); 0 for the classical baseline.</summary>
    int SecurityCategory { get; }

    /// <summary>Encoded public key size in bytes (fixed per parameter set).</summary>
    int PublicKeyBytes { get; }

    /// <summary>Maximum encoded signature size in bytes (fixed for ML-DSA/SLH-DSA; DER ECDSA varies up to this).</summary>
    int SignatureBytes { get; }

    /// <summary>True when this scheme can run on the current platform (the BCL type needs OS support).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Generate a key pair. <paramref name="seed"/> (32 bytes) makes generation deterministic where the
    /// scheme supports it (ML-DSA: FIPS 204 KeyGen_internal from ξ; ECDSA: the seed IS the private
    /// scalar); null = fresh randomness. SLH-DSA in BouncyCastle has no seed entry point — null only.
    /// </summary>
    SchemeKeyPair GenerateKeyPair(byte[]? seed = null);

    /// <summary>Sign <paramref name="message"/>. <paramref name="context"/> = the FIPS 204/205 ctx string (ignored by ECDSA).</summary>
    byte[] Sign(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[]? context = null);

    /// <summary>Verify; a wrong context must fail for the FIPS schemes.</summary>
    bool Verify(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[] signature, byte[]? context = null);
}

/// <summary>Opaque key material for one scheme: the encoded public key plus a provider-private handle.</summary>
public sealed class SchemeKeyPair
{
    public SchemeKeyPair(byte[] publicKey, object privateHandle, byte[]? seed)
    {
        PublicKey = publicKey;
        PrivateHandle = privateHandle;
        Seed = seed;
    }

    public byte[] PublicKey { get; }
    internal object PrivateHandle { get; }
    /// <summary>The 32-byte seed the pair was derived from, when deterministic.</summary>
    public byte[]? Seed { get; }
}

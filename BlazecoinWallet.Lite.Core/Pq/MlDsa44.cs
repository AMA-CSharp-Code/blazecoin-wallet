using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace BlazecoinWallet.Lite.Pq;

/// <summary>An ML-DSA-44 key pair generated from a 32-byte seed ξ (FIPS 204 KeyGen_internal).</summary>
/// <param name="PublicKey">The 1,312-byte encoded public key.</param>
/// <param name="Private">The BouncyCastle private-key handle (seed-derived; never persisted).</param>
public sealed record MlDsa44KeyPair(byte[] PublicKey, MLDsaPrivateKeyParameters Private)
{
    /// <summary>The 2,560-byte FIPS 204 expanded secret key (ρ‖K‖tr‖s1‖s2‖t0).</summary>
    public byte[] SecretKey => Private.GetEncoded();
}

/// <summary>
/// ML-DSA-44 (FIPS 204) exactly as PQ_SIGNATURES.md §3.4 step 6 and §6 use it, via
/// BouncyCastle 2.6.2: seed-deterministic key generation, DETERMINISTIC signing (no
/// per-signature randomness — a bad RNG can never leak the key), and the FIPS 204 context
/// string <see cref="TxContext"/> as the algorithm-level domain separation. The signed
/// message is always the 32-byte <see cref="PqSigHash"/> digest.
/// </summary>
public static class MlDsa44
{
    public const int SeedLength = 32;
    public const int PublicKeyLength = 1312;
    public const int SecretKeyLength = 2560;
    public const int SignatureLength = 2420;

    /// <summary>FIPS 204 §5.3 context string for transaction signatures (§3.4 step 6).</summary>
    public const string TxContext = "blazecoin-tx-v1";

    private static readonly byte[] TxContextBytes = Encoding.ASCII.GetBytes(TxContext);

    /// <summary>KeyGen_internal(ξ): the same seed always yields the same key pair.</summary>
    public static MlDsa44KeyPair KeyPairFromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength)
            throw new ArgumentException($"ML-DSA-44 seed ξ must be {SeedLength} bytes.", nameof(seed));
        var priv = MLDsaPrivateKeyParameters.FromSeed(MLDsaParameters.ml_dsa_44, seed.ToArray());
        return new MlDsa44KeyPair(priv.GetPublicKeyEncoded(), priv);
    }

    /// <summary>Deterministic ML-DSA-44 signature over <paramref name="message"/> under the
    /// transaction context. Always <see cref="SignatureLength"/> bytes.</summary>
    public static byte[] Sign(MLDsaPrivateKeyParameters privateKey, ReadOnlySpan<byte> message)
    {
        var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_44, deterministic: true);
        signer.Init(true, new ParametersWithContext(privateKey, TxContextBytes));
        signer.BlockUpdate(message);
        return signer.GenerateSignature();
    }

    /// <summary>Verifies under the transaction context. Never throws: a malformed public key
    /// or signature is simply "invalid" (consensus fails closed).</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength) return false;
        try
        {
            var pub = MLDsaPublicKeyParameters.FromEncoding(MLDsaParameters.ml_dsa_44, publicKey.ToArray());
            var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_44, deterministic: true);
            signer.Init(false, new ParametersWithContext(pub, TxContextBytes));
            signer.BlockUpdate(message);
            return signer.VerifySignature(signature.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or CryptoException or InvalidOperationException)
        {
            return false;
        }
    }
}

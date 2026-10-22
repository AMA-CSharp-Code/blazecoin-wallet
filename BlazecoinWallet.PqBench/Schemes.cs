using System.Security.Cryptography;
using NBitcoin;
using NBitcoin.Crypto;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace BlazecoinWallet.PqBench;

/// <summary>
/// The baseline: secp256k1 ECDSA exactly as the lite wallet signs today (NBitcoin <c>Key.Sign</c>,
/// DER-encoded low-S signature, compressed 33-byte public key). Sizes are the wallet's real scriptSig
/// material: a DER signature is 70–72 bytes + 1 sighash byte; 72 is the usual maximum.
/// </summary>
public sealed class EcdsaSecp256k1Scheme : ISignatureScheme
{
    public string Name => "ECDSA secp256k1";
    public string Family => "ECDSA";
    public string Provider => "NBitcoin 10.0.7";
    public int SecurityCategory => 0;
    public int PublicKeyBytes => 33;
    public int SignatureBytes => 72; // DER (≤ 71) + sighash type byte, as it sits in a P2PKH scriptSig
    public bool IsSupported => true;

    public SchemeKeyPair GenerateKeyPair(byte[]? seed = null)
    {
        var key = seed == null ? new Key() : new Key(seed);
        return new SchemeKeyPair(key.PubKey.ToBytes(), key, seed);
    }

    public byte[] Sign(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[]? context = null)
    {
        var k = (Key)key.PrivateHandle;
        // The wallet signs a 32-byte transaction digest; the benchmark message is hashed the same way.
        var sig = k.Sign(new uint256(SHA256.HashData(message)));
        var der = sig.ToDER();
        var withHashType = new byte[der.Length + 1];
        der.CopyTo(withHashType, 0);
        withHashType[^1] = 0x01; // SIGHASH_ALL
        return withHashType;
    }

    public bool Verify(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[] signature, byte[]? context = null)
    {
        var pub = new PubKey(key.PublicKey);
        var der = signature.AsSpan(0, signature.Length - 1).ToArray();
        try
        {
            var sig = ECDSASignature.FromDER(der);
            return pub.Verify(new uint256(SHA256.HashData(message)), sig);
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>ML-DSA (FIPS 204) via BouncyCastle: seed-deterministic key generation (KeyGen_internal from ξ), context string honoured.</summary>
public sealed class MlDsaScheme : ISignatureScheme
{
    private readonly MLDsaParameters _parameters;

    public MlDsaScheme(MLDsaParameters parameters, string name, int category, int publicKeyBytes, int signatureBytes)
    {
        _parameters = parameters;
        Name = name;
        SecurityCategory = category;
        PublicKeyBytes = publicKeyBytes;
        SignatureBytes = signatureBytes;
    }

    public static MlDsaScheme Ml44() => new(MLDsaParameters.ml_dsa_44, "ML-DSA-44", 2, 1312, 2420);
    public static MlDsaScheme Ml65() => new(MLDsaParameters.ml_dsa_65, "ML-DSA-65", 3, 1952, 3309);
    public static MlDsaScheme Ml87() => new(MLDsaParameters.ml_dsa_87, "ML-DSA-87", 5, 2592, 4627);

    public string Name { get; }
    public string Family => "ML-DSA";
    public string Provider => "BouncyCastle 2.6.2";
    public int SecurityCategory { get; }
    public int PublicKeyBytes { get; }
    public int SignatureBytes { get; }
    public bool IsSupported => true;

    public SchemeKeyPair GenerateKeyPair(byte[]? seed = null)
    {
        MLDsaPrivateKeyParameters priv;
        if (seed != null)
        {
            if (seed.Length != 32) throw new ArgumentException("ML-DSA seed ξ must be 32 bytes", nameof(seed));
            priv = MLDsaPrivateKeyParameters.FromSeed(_parameters, seed);
        }
        else
        {
            var gen = new MLDsaKeyPairGenerator();
            gen.Init(new MLDsaKeyGenerationParameters(new SecureRandom(), _parameters));
            priv = (MLDsaPrivateKeyParameters)gen.GenerateKeyPair().Private;
        }
        return new SchemeKeyPair(priv.GetPublicKeyEncoded(), priv, seed);
    }

    public byte[] Sign(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[]? context = null)
    {
        // deterministic: true = FIPS 204 deterministic variant (no per-signature randomness) — the
        // wallet-appropriate choice (RFC 6979 spirit: a bad RNG can never leak the key).
        var signer = new MLDsaSigner(_parameters, deterministic: true);
        signer.Init(true, WithContext((ICipherParameters)key.PrivateHandle, context));
        signer.BlockUpdate(message);
        return signer.GenerateSignature();
    }

    public bool Verify(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[] signature, byte[]? context = null)
    {
        var pub = MLDsaPublicKeyParameters.FromEncoding(_parameters, key.PublicKey);
        var signer = new MLDsaSigner(_parameters, deterministic: true);
        signer.Init(false, WithContext(pub, context));
        signer.BlockUpdate(message);
        return signer.VerifySignature(signature);
    }

    internal static ICipherParameters WithContext(ICipherParameters key, byte[]? context)
        => context == null || context.Length == 0 ? key : new ParametersWithContext(key, context);
}

/// <summary>SLH-DSA (FIPS 205) via BouncyCastle — the hash-based fallback family; no seed entry point in this library.</summary>
public sealed class SlhDsaScheme : ISignatureScheme
{
    private readonly SlhDsaParameters _parameters;

    public SlhDsaScheme(SlhDsaParameters parameters, string name, int category, int publicKeyBytes, int signatureBytes)
    {
        _parameters = parameters;
        Name = name;
        SecurityCategory = category;
        PublicKeyBytes = publicKeyBytes;
        SignatureBytes = signatureBytes;
    }

    public static SlhDsaScheme Sha2_128s() => new(SlhDsaParameters.slh_dsa_sha2_128s, "SLH-DSA-SHA2-128s", 1, 32, 7856);
    public static SlhDsaScheme Sha2_128f() => new(SlhDsaParameters.slh_dsa_sha2_128f, "SLH-DSA-SHA2-128f", 1, 32, 17088);

    public string Name { get; }
    public string Family => "SLH-DSA";
    public string Provider => "BouncyCastle 2.6.2";
    public int SecurityCategory { get; }
    public int PublicKeyBytes { get; }
    public int SignatureBytes { get; }
    public bool IsSupported => true;

    public SchemeKeyPair GenerateKeyPair(byte[]? seed = null)
    {
        if (seed != null) throw new NotSupportedException("BouncyCastle 2.6.2 exposes no seeded SLH-DSA key generation");
        var gen = new SlhDsaKeyPairGenerator();
        gen.Init(new SlhDsaKeyGenerationParameters(new SecureRandom(), _parameters));
        var priv = (SlhDsaPrivateKeyParameters)gen.GenerateKeyPair().Private;
        return new SchemeKeyPair(priv.GetPublicKeyEncoded(), priv, null);
    }

    public byte[] Sign(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[]? context = null)
    {
        var signer = new SlhDsaSigner(_parameters, deterministic: true);
        signer.Init(true, MlDsaScheme.WithContext((ICipherParameters)key.PrivateHandle, context));
        signer.BlockUpdate(message);
        return signer.GenerateSignature();
    }

    public bool Verify(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[] signature, byte[]? context = null)
    {
        var pub = SlhDsaPublicKeyParameters.FromEncoding(_parameters, key.PublicKey);
        var signer = new SlhDsaSigner(_parameters, deterministic: true);
        signer.Init(false, MlDsaScheme.WithContext(pub, context));
        signer.BlockUpdate(message);
        return signer.VerifySignature(signature);
    }
}

/// <summary>
/// ML-DSA-44 through the .NET 10 BCL (<c>System.Security.Cryptography.MLDsa</c>, [Experimental]
/// SYSLIB5006): backed by Windows CNG PQC (Windows 11 24H2+ / Server 2025) or OpenSSL 3.5+. On a box
/// without either, <see cref="IsSupported"/> is false and the report says so — that is itself a
/// finding for the lite-wallet platform matrix.
/// </summary>
public sealed class BclMlDsa44Scheme : ISignatureScheme
{
    public string Name => "ML-DSA-44";
    public string Family => "ML-DSA";
    public string Provider => ".NET 10 BCL (OS-backed)";
    public int SecurityCategory => 2;
    public int PublicKeyBytes => 1312;
    public int SignatureBytes => 2420;
    public bool IsSupported => MLDsa.IsSupported;

    public SchemeKeyPair GenerateKeyPair(byte[]? seed = null)
    {
        var key = seed == null
            ? MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa44)
            : MLDsa.ImportMLDsaPrivateSeed(MLDsaAlgorithm.MLDsa44, seed);
        return new SchemeKeyPair(key.ExportMLDsaPublicKey(), key, seed);
    }

    public byte[] Sign(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[]? context = null)
        => ((MLDsa)key.PrivateHandle).SignData(message.ToArray(), context);

    public bool Verify(SchemeKeyPair key, ReadOnlySpan<byte> message, byte[] signature, byte[]? context = null)
    {
        using var pub = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa44, key.PublicKey);
        return pub.VerifyData(message, signature, context);
    }
}

/// <summary>The catalogue the benchmark and the spec table iterate, in the spec's order.</summary>
public static class SchemeCatalogue
{
    public static IReadOnlyList<ISignatureScheme> All() =>
    [
        new EcdsaSecp256k1Scheme(),
        MlDsaScheme.Ml44(),
        MlDsaScheme.Ml65(),
        MlDsaScheme.Ml87(),
        SlhDsaScheme.Sha2_128s(),
        SlhDsaScheme.Sha2_128f(),
        new BclMlDsa44Scheme(),
    ];
}

using System.Security.Cryptography;
using System.Text;
using BlazecoinWallet.PqBench;

namespace BlazecoinWallet.PqBench.Tests;

/// <summary>
/// Post-quantum plan item 4 (2026-09-14): the benchmark library's correctness pins. Sizes must be the
/// FIPS 204/205 constants (the spec's §2 table is copied from these, so a wrong constant would mislead
/// the fork design); every scheme must round-trip, reject a tampered message and — for the FIPS
/// schemes — reject the wrong context string (the spec leans on ctx for domain separation); ML-DSA
/// key generation must be deterministic from the spec's seed derivation (the C++ twin will be held to
/// the same bytes); and the size model must reproduce the spec's §4 numbers.
/// </summary>
public class SchemeTests
{
    private static readonly byte[] Message = SHA256.HashData(Encoding.ASCII.GetBytes("a transaction digest"));
    private static readonly byte[] Ctx = Encoding.ASCII.GetBytes("blazecoin-tx-v1");

    public static IEnumerable<object[]> Supported() =>
        SchemeCatalogue.All().Where(s => s.IsSupported).Select(s => new object[] { s });

    [Theory, MemberData(nameof(Supported))]
    public void Sizes_match_the_parameter_set_constants(ISignatureScheme scheme)
    {
        var key = scheme.GenerateKeyPair();
        var sig = scheme.Sign(key, Message, scheme.Family == "ECDSA" ? null : Ctx);
        Assert.Equal(scheme.PublicKeyBytes, key.PublicKey.Length);
        if (scheme.Family == "ECDSA") Assert.InRange(sig.Length, 70, scheme.SignatureBytes);   // DER varies
        else Assert.Equal(scheme.SignatureBytes, sig.Length);
    }

    [Theory, MemberData(nameof(Supported))]
    public void Round_trip_verifies_and_a_tampered_message_does_not(ISignatureScheme scheme)
    {
        var ctx = scheme.Family == "ECDSA" ? null : Ctx;
        var key = scheme.GenerateKeyPair();
        var sig = scheme.Sign(key, Message, ctx);
        Assert.True(scheme.Verify(key, Message, sig, ctx));

        var tampered = (byte[])Message.Clone();
        tampered[0] ^= 0x01;
        Assert.False(scheme.Verify(key, tampered, sig, ctx));

        var other = scheme.GenerateKeyPair();
        Assert.False(scheme.Verify(other, Message, sig, ctx));
    }

    [Theory, MemberData(nameof(Supported))]
    public void The_context_string_is_part_of_what_is_signed(ISignatureScheme scheme)
    {
        if (scheme.Family == "ECDSA") return; // no ctx in ECDSA; the spec's domain separation is the tagged digest there
        var key = scheme.GenerateKeyPair();
        var sig = scheme.Sign(key, Message, Ctx);
        Assert.True(scheme.Verify(key, Message, sig, Ctx));
        Assert.False(scheme.Verify(key, Message, sig, Encoding.ASCII.GetBytes("blazecoin-msg-v1")));
        Assert.False(scheme.Verify(key, Message, sig, null));
    }

    [Fact]
    public void Ml_dsa_keys_are_deterministic_from_the_spec_seed_derivation()
    {
        var master = SHA256.HashData(Encoding.ASCII.GetBytes("master seed under test"));
        var seed0 = TaggedHash.MlDsa44Seed(master, 0);
        var seed1 = TaggedHash.MlDsa44Seed(master, 1);
        Assert.NotEqual(seed0, seed1);

        var scheme = MlDsaScheme.Ml44();
        var a = scheme.GenerateKeyPair(seed0);
        var b = scheme.GenerateKeyPair(seed0);
        var c = scheme.GenerateKeyPair(seed1);
        Assert.Equal(a.PublicKey, b.PublicKey);
        Assert.NotEqual(a.PublicKey, c.PublicKey);

        // Deterministic signing too: same key, same message, same ctx ⇒ identical bytes.
        Assert.Equal(scheme.Sign(a, Message, Ctx), scheme.Sign(b, Message, Ctx));
    }

    [Fact]
    public void Tagged_hash_matches_the_bip340_construction()
    {
        var tag = "Blazecoin/PQKH/v1";
        var m = Encoding.ASCII.GetBytes("keyblob");
        var tagHash = SHA256.HashData(Encoding.ASCII.GetBytes(tag));
        var expected = SHA256.HashData(tagHash.Concat(tagHash).Concat(m).ToArray());
        Assert.Equal(expected, TaggedHash.Compute(tag, m));
    }

    [Fact]
    public void Size_model_reproduces_the_spec_numbers()
    {
        var legacy = InputSizeEstimate.ForScheme(new EcdsaSecp256k1Scheme());
        Assert.Equal(25, legacy.ScriptPubKeyBytes);
        Assert.Equal(148, legacy.InputBytes);

        var ml44 = InputSizeEstimate.ForScheme(MlDsaScheme.Ml44());
        Assert.Equal(34, ml44.ScriptPubKeyBytes);
        Assert.Equal(3_739, ml44.ScriptSigBytes);
        Assert.Equal(3_782, ml44.InputBytes);
        Assert.Equal(264, ml44.InputsPerBlock);
        Assert.Equal(26, ml44.InputsPerStandardTx);

        var ml87 = InputSizeEstimate.ForScheme(MlDsaScheme.Ml87());
        Assert.Equal(7_269, ml87.InputBytes);   // 36 + 3 + (3 + 4,627 + 3 + 2,593) + 4
        Assert.Equal(137, ml87.InputsPerBlock);
    }

    [Fact]
    public void Quick_benchmark_runs_every_scheme_and_renders_a_report()
    {
        var results = Benchmark.Run(SchemeCatalogue.All(), BenchmarkProfile.Quick);
        Assert.Equal(SchemeCatalogue.All().Count, results.Count);
        foreach (var r in results.Where(r => r.Supported))
        {
            Assert.NotNull(r.Verify);
            Assert.True(r.Verify!.MedianUs > 0);
        }
        var md = BenchmarkReport.ToMarkdown(results, BenchmarkProfile.Quick, new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("| ML-DSA-44 | BouncyCastle 2.6.2 | 2 | 1,312 B | 2,420 B |", md);
        Assert.Contains("**3,782 B**", md);
        Assert.Contains("ECDSA secp256k1", md);
    }
}

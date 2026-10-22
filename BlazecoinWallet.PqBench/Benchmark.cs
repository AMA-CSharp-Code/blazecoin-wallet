using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BlazecoinWallet.PqBench;

/// <summary>Timing summary for one operation: median / mean / p90 in microseconds over N iterations.</summary>
public sealed record Timing(int Iterations, double MedianUs, double MeanUs, double P90Us)
{
    public static Timing Of(IReadOnlyList<double> samplesUs)
    {
        var sorted = samplesUs.OrderBy(x => x).ToArray();
        double Pct(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(p * (sorted.Length - 1)))];
        return new Timing(sorted.Length, Pct(0.5), sorted.Average(), Pct(0.9));
    }
}

/// <summary>Everything the report prints for one scheme.</summary>
public sealed record SchemeResult(
    ISignatureScheme Scheme, bool Supported, string? Note,
    int MeasuredPublicKeyBytes, int MeasuredSignatureBytes,
    Timing? KeyGen, Timing? Sign, Timing? Verify, InputSizeEstimate Sizes);

/// <summary>Iteration counts; SLH-DSA signing is orders of magnitude slower, so it gets fewer.</summary>
public sealed record BenchmarkProfile(int KeyGen, int Sign, int Verify, int SlowSign, int WarmUp)
{
    // 30 warm-up rounds: with fewer, the first BouncyCastle scheme measured pays the tiered-JIT cost of the
    // shared lattice code and reads slower than the larger parameter sets (observed 2026-09-14).
    public static BenchmarkProfile Full => new(KeyGen: 100, Sign: 200, Verify: 500, SlowSign: 10, WarmUp: 30);
    public static BenchmarkProfile Quick => new(KeyGen: 3, Sign: 3, Verify: 5, SlowSign: 1, WarmUp: 1);
}

/// <summary>
/// Runs every scheme through keygen / sign / verify on a 32-byte message (a transaction digest, which is
/// what a wallet signs) with the spec's FIPS 204 context string, and reports sizes as MEASURED from the
/// library, cross-checked against the parameter-set constants. Single-threaded, Stopwatch-timed,
/// warm-up excluded; good to one or two significant figures, which is what the spec table needs.
/// </summary>
public static class Benchmark
{
    public static readonly byte[] SpecContext = Encoding.ASCII.GetBytes("blazecoin-tx-v1");

    public static IReadOnlyList<SchemeResult> Run(IEnumerable<ISignatureScheme> schemes, BenchmarkProfile profile)
        => schemes.Select(s => RunOne(s, profile)).ToList();

    public static SchemeResult RunOne(ISignatureScheme scheme, BenchmarkProfile profile)
    {
        var sizes = InputSizeEstimate.ForScheme(scheme);
        if (!scheme.IsSupported)
            return new SchemeResult(scheme, false, "not supported on this platform", 0, 0, null, null, null, sizes);

        var message = SHA256.HashData(Encoding.ASCII.GetBytes("Blazecoin PQ benchmark message"));
        var ctx = scheme.Family == "ECDSA" ? null : SpecContext;
        var slow = scheme.Family == "SLH-DSA";
        var signIterations = slow ? profile.SlowSign : profile.Sign;
        var keyGenIterations = slow ? Math.Max(1, profile.KeyGen / 10) : profile.KeyGen;
        var warmUp = slow ? Math.Max(1, profile.WarmUp / 10) : profile.WarmUp;

        // Warm-up (JIT, first-use table generation) is excluded from every sample.
        var key = scheme.GenerateKeyPair();
        var sig = scheme.Sign(key, message, ctx);
        for (var i = 0; i < warmUp; i++) { key = scheme.GenerateKeyPair(); sig = scheme.Sign(key, message, ctx); scheme.Verify(key, message, sig, ctx); }
        if (!scheme.Verify(key, message, sig, ctx)) throw new InvalidOperationException($"{scheme.Name}: warm-up signature failed to verify");

        var keyGen = Time(keyGenIterations, () => scheme.GenerateKeyPair());
        var sign = Time(signIterations, () => scheme.Sign(key, message, ctx));
        var verify = Time(profile.Verify, () => scheme.Verify(key, message, sig, ctx));

        return new SchemeResult(scheme, true, null, key.PublicKey.Length, sig.Length, keyGen, sign, verify, sizes);
    }

    private static Timing Time(int iterations, Action op)
    {
        var samples = new List<double>(iterations);
        var sw = new Stopwatch();
        for (var i = 0; i < iterations; i++)
        {
            sw.Restart();
            op();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMicroseconds);
        }
        return Timing.Of(samples);
    }
}

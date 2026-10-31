using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BlazecoinWallet.PqBench;

/// <summary>Renders results as the two Markdown tables PQ_SIGNATURES.md §2 and §4 are built from.</summary>
public static class BenchmarkReport
{
    public static string ToMarkdown(IReadOnlyList<SchemeResult> results, BenchmarkProfile profile, DateTime? whenUtc = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var when = (whenUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm 'UTC'", inv);
        sb.AppendLine($"Measured {when} on {RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical cores, .NET {Environment.Version}; single thread, Stopwatch, {profile.WarmUp} warm-up rounds excluded. Message = a 32-byte digest; FIPS schemes signed with ctx `blazecoin-tx-v1`.");
        sb.AppendLine();
        sb.AppendLine("| Scheme | Provider | Cat. | Public key | Signature | KeyGen median | Sign median | Verify median | Verify p90 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            if (!r.Supported)
            {
                sb.AppendLine($"| {r.Scheme.Name} | {r.Scheme.Provider} | {Cat(r.Scheme)} | {r.Scheme.PublicKeyBytes:N0} B | {r.Scheme.SignatureBytes:N0} B | — | — | — | *{r.Note}* |");
                continue;
            }
            sb.AppendLine($"| {r.Scheme.Name} | {r.Scheme.Provider} | {Cat(r.Scheme)} | {r.MeasuredPublicKeyBytes:N0} B | {r.MeasuredSignatureBytes:N0} B | {Us(r.KeyGen!.MedianUs)} | {Us(r.Sign!.MedianUs)} | {Us(r.Verify!.MedianUs)} | {Us(r.Verify.P90Us)} |");
        }
        sb.AppendLine();
        sb.AppendLine("| Scheme | scriptPubKey | scriptSig | Input | Inputs / 1 MB block | Inputs / standard tx (100 KB) | Inputs / day (2,880 blocks) | Verify time for a full block |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results.Where(r => r.Supported))
        {
            var s = r.Sizes;
            var block = Ms(s.InputsPerBlock * r.Verify!.MedianUs / 1000.0);
            sb.AppendLine($"| {s.Scheme} | {s.ScriptPubKeyBytes} B | {s.ScriptSigBytes:N0} B | **{s.InputBytes:N0} B** | {s.InputsPerBlock:N0} | {s.InputsPerStandardTx:N0} | {s.InputsPerDay:N0} | {block} |");
        }
        return sb.ToString();

        static string Cat(ISignatureScheme s) => s.SecurityCategory == 0 ? "—" : s.SecurityCategory.ToString(CultureInfo.InvariantCulture);
        static string Us(double us) => us >= 10_000 ? $"{us / 1000:N1} ms" : us >= 1000 ? $"{us / 1000:N2} ms" : $"{us:N0} µs";
        static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:N1} s" : $"{ms:N0} ms";
    }
}

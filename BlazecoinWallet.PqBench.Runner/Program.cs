using BlazecoinWallet.PqBench;

var profile = args.Any(a => a.Equals("quick", StringComparison.OrdinalIgnoreCase)) ? BenchmarkProfile.Quick : BenchmarkProfile.Full;
Console.Error.WriteLine($"Blazecoin PQ benchmark — profile {(profile == BenchmarkProfile.Quick ? "quick" : "full")}, {SchemeCatalogue.All().Count} schemes …");
// Two full passes; only the second is reported — the first absorbs tiered-JIT promotion of the
// shared BouncyCastle lattice code, which otherwise penalises whichever scheme runs first.
if (profile == BenchmarkProfile.Full) Benchmark.Run(SchemeCatalogue.All(), BenchmarkProfile.Quick with { WarmUp = 30 });
var results = Benchmark.Run(SchemeCatalogue.All(), profile);
Console.Write(BenchmarkReport.ToMarkdown(results, profile));

// Live check of the provenance tracer through the real RPC service: one wallet, one or
// all outputs, against a running daemon. Read-only. Usage:
//   TraceHarness <datadir> <port> <wallet> [txid] [vout]
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Provenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var dataDir = args[0]; var port = args[1]; var wallet = args[2];
var cookie = File.ReadAllText(Path.Combine(dataDir, ".cookie")).Trim();

var services = new ServiceCollection();
services.AddHttpClient("blazecoind", c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
        Convert.ToBase64String(Encoding.ASCII.GetBytes(cookie)));
});
var sp = services.BuildServiceProvider();
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Blazecoind:RpcUrl"] = $"http://127.0.0.1:{port}"
}).Build();

var rpc = new BlazecoindRpcService(config, new Ctx(wallet), sp.GetRequiredService<IHttpClientFactory>());
IAncestryRpc ancestry = rpc;

var utxos = await ancestry.ListUnspentAsync();
Console.WriteLine($"{wallet}: {utxos.Count} unspent");
if (args.Length >= 7 && args[3] == "outpoint")
    utxos = new List<UnspentOutput> { new(args[4], int.Parse(args[5]), long.Parse(args[6]), null, 0) };
else if (args.Length >= 4 && args[3] == "largest")
    utxos = utxos.OrderByDescending(u => u.Satoshis).Take(1).ToList();
else if (args.Length >= 5)
    utxos = utxos.Where(u => u.TxId == args[3] && u.Vout == int.Parse(args[4])).ToList();
Console.WriteLine($"tracing {utxos.Count} output(s), {utxos.Sum(u => u.Satoshis) / 1e8:N8} BLZ");

var sw = Stopwatch.StartNew();
var last = 0;
var ticks = 0;
var progress = new Sync(p =>
{
    ticks++;
    if (p.TransactionsRead - last >= 10_000 || p.OutputsDone == p.OutputsTotal)
    {
        last = p.TransactionsRead;
        Console.WriteLine($"  {sw.Elapsed:mm\\:ss}  {p.OutputsDone}/{p.OutputsTotal} outputs  {p.TransactionsRead:N0} reads  mem {GC.GetTotalMemory(false) / 1_048_576:N0} MB");
    }
});

var cap = int.TryParse(Environment.GetEnvironmentVariable("SEGCAP"), out var c) ? c : 4096;
var tracer = new AncestryTracer(ancestry, new AncestryTracerOptions { MaxSegmentsPerOutput = cap });
var report = await tracer.TraceOutputsAsync(utxos, progress);
sw.Stop();

Console.WriteLine($"done in {sw.Elapsed:mm\\:ss}: {report.TransactionsRead:N0} reads, {ticks} progress ticks, budget reached = {report.ReadBudgetReached}");
Console.WriteLine($"  held {report.TotalSatoshis / 1e8:N8} BLZ; haircut attributed {report.Haircut.AttributedSatoshis / 1e8:N8}; fifo attributed {report.Fifo.AttributedSatoshis / 1e8:N8}");
Console.WriteLine($"  origins {report.Origins.Count:N0} blocks; peak managed memory {GC.GetTotalMemory(true) / 1_048_576:N0} MB after collect");
foreach (var y in report.Haircut.Years)
    Console.WriteLine($"  haircut {y.Label,-6} {y.MonthsLabel,-20} {y.OutputCount,7:N0} blocks  {y.Satoshis / 1e8,18:N8} BLZ");
foreach (var s in report.Haircut.Specials)
    Console.WriteLine($"  haircut {s.Label,-32} block {s.FirstHeight,10:N0}  {s.Satoshis / 1e8,18:N8} BLZ");
foreach (var y in report.Fifo.Years)
    Console.WriteLine($"  fifo    {y.Label,-6} {y.MonthsLabel,-20} {y.OutputCount,7:N0} blocks  {y.Satoshis / 1e8,18:N8} BLZ");
foreach (var s in report.Fifo.Specials)
    Console.WriteLine($"  fifo    {s.Label,-32} block {s.FirstHeight,10:N0}  {s.Satoshis / 1e8,18:N8} BLZ");
if (report.Fifo.Specials.Count == 0) Console.WriteLine("  fifo    (no special vintages under FIFO)");

sealed class Ctx(string? name) : IWalletContext
{
    public string? Active { get; private set; } = name;
    public event Action? Changed;
    public void SetActive(string? n) { Active = n; Changed?.Invoke(); }
}

sealed class Sync(Action<TraceProgress> on) : IProgress<TraceProgress>
{
    public void Report(TraceProgress value) => on(value);
}

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Generators;

namespace BlazecoinWallet.Core.Services.Mining;

public class MiningService : IMiningService
{
    private readonly IMiningRpc _rpc;
    private readonly ILogger<MiningService>? _logger;

    private readonly Lock _stateLock = new();
    private CancellationTokenSource? _cts;
    private List<Thread> _workers = new();
    private Thread? _refresher;

    // Shared state read by workers without locking — kept atomic via volatile.
    private volatile MiningContext? _ctx;

    private long _totalHashes;
    private long _blocksFound;
    private DateTime _startedUtc;
    private int _activeThreads;
    private int _configuredThreads;
    private int _throttlePercent;
    private string? _payoutAddress;
    private string? _lastError;

    // Sliding-window hashrate: capture (timestamp, totalHashes) every ~1s.
    private readonly Queue<(DateTime t, long hashes)> _rateSamples = new();
    private readonly Lock _rateLock = new();

    public MiningService(IMiningRpc rpc, ILogger<MiningService>? logger = null)
    {
        _rpc = rpc;
        _logger = logger;
    }

    public event Action<MiningStatus>? StatusChanged;

    public int MaxAllowedThreads => Math.Max(1, Environment.ProcessorCount - 4);
    public int DefaultThreadCount => Math.Max(1, Environment.ProcessorCount / 8);

    public MiningStatus GetStatus()
    {
        double hps;
        lock (_rateLock)
        {
            hps = ComputeHashesPerSecond();
        }

        return new MiningStatus
        {
            IsRunning = _cts is { IsCancellationRequested: false } && _workers.Count > 0,
            ActiveThreads = _activeThreads,
            ConfiguredThreads = _configuredThreads,
            ThrottlePercent = _throttlePercent,
            HashesPerSecond = hps,
            TotalHashes = Interlocked.Read(ref _totalHashes),
            Elapsed = _startedUtc == default ? TimeSpan.Zero : DateTime.UtcNow - _startedUtc,
            BlocksFound = (int)Interlocked.Read(ref _blocksFound),
            CurrentBlockHeight = _ctx?.Template.Height,
            CurrentTemplateRefreshUnix = _ctx == null ? null : new DateTimeOffset(_ctx.LoadedUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
            PayoutAddress = _payoutAddress,
            LastError = _lastError,
            MaxAllowedThreads = MaxAllowedThreads,
        };
    }

    private double ComputeHashesPerSecond()
    {
        if (_rateSamples.Count < 2) return 0;
        var first = _rateSamples.Peek();
        var last = _rateSamples.Last();
        var dt = (last.t - first.t).TotalSeconds;
        if (dt <= 0) return 0;
        return (last.hashes - first.hashes) / dt;
    }

    public async Task StartAsync(MiningOptions options, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
                throw new InvalidOperationException("Mining is already running");
        }

        // Clamp thread count to the hard cap.
        int threads = Math.Clamp(options.ThreadCount, 1, MaxAllowedThreads);
        int throttle = Math.Clamp(options.ThrottlePercent, 0, 90);

        // Resolve payout address.
        string? address = options.PayoutAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            address = await _rpc.GetNewAddressAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(address))
                throw new InvalidOperationException("getnewaddress returned no address");
        }

        // Pre-validate the address by decoding it — better to fail here than per-worker.
        byte[] payoutScript;
        try
        {
            payoutScript = BitcoinProtocol.AddressToP2PKH(address);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid payout address \"{address}\": {ex.Message}");
        }

        // Get the first block template before starting workers.
        var template = await _rpc.GetBlockTemplateAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("getblocktemplate returned null");

        var ctx = new MiningContext(template, payoutScript);
        _ctx = ctx;
        _payoutAddress = address;
        _configuredThreads = threads;
        _throttlePercent = throttle;
        _lastError = null;
        Interlocked.Exchange(ref _totalHashes, 0);
        Interlocked.Exchange(ref _blocksFound, 0);
        _startedUtc = DateTime.UtcNow;

        lock (_rateLock) { _rateSamples.Clear(); _rateSamples.Enqueue((DateTime.UtcNow, 0)); }

        var cts = new CancellationTokenSource();
        _cts = cts;

        // Spawn workers.
        _workers = new List<Thread>(threads);
        for (int i = 0; i < threads; i++)
        {
            int workerId = i;
            var t = new Thread(() => WorkerLoop(workerId, cts.Token))
            {
                IsBackground = true,
                Name = $"BlazeMiner-{workerId}",
                Priority = ThreadPriority.BelowNormal,
            };
            _workers.Add(t);
            t.Start();
        }
        Interlocked.Exchange(ref _activeThreads, threads);

        // Template refresher + rate sampler in one thread.
        _refresher = new Thread(() => RefresherLoop(cts.Token))
        {
            IsBackground = true,
            Name = "BlazeMiner-Refresher",
            Priority = ThreadPriority.BelowNormal,
        };
        _refresher.Start();

        EmitStatus();
        _logger?.LogInformation("Mining started: {Threads} threads, {Throttle}% throttle, paying {Address}",
            threads, throttle, address);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        List<Thread> workers;
        Thread? refresher;
        lock (_stateLock)
        {
            cts = _cts;
            workers = _workers;
            refresher = _refresher;
        }
        if (cts == null) return;

        cts.Cancel();

        // Workers will exit at the next CancellationToken check (frequent — every hash).
        await Task.Run(() =>
        {
            foreach (var w in workers)
            {
                try { w.Join(TimeSpan.FromSeconds(10)); } catch { /* swallow */ }
            }
            refresher?.Join(TimeSpan.FromSeconds(2));
        }).ConfigureAwait(false);

        lock (_stateLock)
        {
            _cts = null;
            _workers = new List<Thread>();
            _refresher = null;
        }
        Interlocked.Exchange(ref _activeThreads, 0);
        EmitStatus();
        _logger?.LogInformation("Mining stopped");
    }

    private void EmitStatus()
    {
        try { StatusChanged?.Invoke(GetStatus()); } catch { /* listener errors are not our problem */ }
    }

    private void WorkerLoop(int workerId, CancellationToken ct)
    {
        // Per-worker counter — combined with workerId to make extraNonce unique
        // across workers without coordination.
        uint rebuildCount = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ctx = _ctx;
                if (ctx == null)
                {
                    Thread.Sleep(50);
                    continue;
                }

                var tmpl = ctx.Template;

                // Build coinbase for this rebuild.
                var extraNonce = new byte[8];
                BitConverter.TryWriteBytes(extraNonce.AsSpan(0, 4), workerId);
                BitConverter.TryWriteBytes(extraNonce.AsSpan(4, 4), rebuildCount++);

                var coinbase = BitcoinProtocol.BuildCoinbaseTx(tmpl.CoinbaseValue, ctx.PayoutScript, extraNonce);
                var coinbaseHash = BitcoinProtocol.DoubleSha256(coinbase);

                // Merkle leaves: coinbase first, then template txs in order. Template
                // txids come as display-hex (reversed); we need internal byte order.
                var leaves = new List<byte[]> { coinbaseHash };
                if (tmpl.Transactions != null)
                {
                    foreach (var tx in tmpl.Transactions)
                    {
                        if (tx.TxId == null) continue;
                        leaves.Add(BitcoinProtocol.Reverse(BitcoinProtocol.HexToBytes(tx.TxId)));
                    }
                }
                var merkleRoot = BitcoinProtocol.ComputeMerkleRoot(leaves);

                var prevHash = BitcoinProtocol.Reverse(BitcoinProtocol.HexToBytes(tmpl.PreviousBlockHash!));
                uint time = (uint)tmpl.CurTime;
                uint bits = uint.Parse(tmpl.Bits!, NumberStyles.HexNumber);
                // Template target is BE hex; reverse to LE for comparison.
                var target = BitcoinProtocol.Reverse(BitcoinProtocol.HexToBytes(tmpl.Target!));

                int sleepMs = _throttlePercent / 10; // 0..9 ms

                // Sweep the 4-byte nonce. Bail early if cancellation or template change.
                for (uint nonce = 0; ; nonce++)
                {
                    if (ct.IsCancellationRequested) return;

                    // Cheap periodic check: every 8192 hashes, see if template was swapped out.
                    if ((nonce & 0x1FFF) == 0 && !ReferenceEquals(_ctx, ctx)) break;

                    var header = BitcoinProtocol.SerializeHeader(tmpl.Version, prevHash, merkleRoot, time, bits, nonce);
                    var hash = SCrypt.Generate(header, header, N: 1024, r: 1, p: 1, dkLen: 32);
                    Interlocked.Increment(ref _totalHashes);

                    if (BitcoinProtocol.MeetsTarget(hash, target))
                    {
                        TrySubmitFoundBlock(ctx, coinbase, header);
                        // Bump our rebuild count so we don't re-find the same hash.
                        break;
                    }

                    if (sleepMs > 0) Thread.Sleep(sleepMs);

                    if (nonce == uint.MaxValue) break; // exhausted; rebuild with new extraNonce
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = $"Worker {workerId}: {ex.Message}";
            _logger?.LogError(ex, "Mining worker {WorkerId} crashed", workerId);
            EmitStatus();
            // Worker thread exits, but other workers continue.
        }
        finally
        {
            Interlocked.Decrement(ref _activeThreads);
        }
    }

    private void TrySubmitFoundBlock(MiningContext ctx, byte[] coinbase, byte[] header)
    {
        try
        {
            // Serialize the full block: 80-byte header + tx count varint + coinbase + other txs (as raw hex).
            using var ms = new MemoryStream();
            ms.Write(header);
            int txCount = 1 + (ctx.Template.Transactions?.Count ?? 0);
            ms.Write(BitcoinProtocol.VarInt((ulong)txCount));
            ms.Write(coinbase);
            if (ctx.Template.Transactions != null)
            {
                foreach (var tx in ctx.Template.Transactions)
                {
                    if (tx.Data == null) continue;
                    var bytes = BitcoinProtocol.HexToBytes(tx.Data);
                    ms.Write(bytes);
                }
            }
            var hexBlock = BitcoinProtocol.BytesToHex(ms.ToArray());

            // Submit synchronously (we're already on a worker thread).
            var rejectReason = _rpc.SubmitBlockAsync(hexBlock).GetAwaiter().GetResult();

            if (string.IsNullOrEmpty(rejectReason))
            {
                Interlocked.Increment(ref _blocksFound);
                _logger?.LogWarning("BLOCK FOUND! Height {Height}, paid {Address}",
                    ctx.Template.Height, _payoutAddress);
                EmitStatus();
            }
            else
            {
                _lastError = $"Block rejected: {rejectReason}";
                _logger?.LogWarning("Block submit rejected: {Reason}", rejectReason);
                EmitStatus();
            }
        }
        catch (Exception ex)
        {
            _lastError = $"Submit failed: {ex.Message}";
            _logger?.LogError(ex, "Block submit threw");
            EmitStatus();
        }
    }

    private void RefresherLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastSampleHashes = 0;
        var lastSampleAt = DateTime.UtcNow;
        var lastTemplateAt = DateTime.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Sample hashrate every ~1s.
                var now = DateTime.UtcNow;
                if ((now - lastSampleAt).TotalMilliseconds >= 1000)
                {
                    long total = Interlocked.Read(ref _totalHashes);
                    lock (_rateLock)
                    {
                        _rateSamples.Enqueue((now, total));
                        // Keep a 5-sample / ~5s rolling window.
                        while (_rateSamples.Count > 5) _rateSamples.Dequeue();
                    }
                    lastSampleAt = now;
                    lastSampleHashes = total;
                    EmitStatus();
                }

                // Refresh block template every 5s.
                if ((now - lastTemplateAt).TotalSeconds >= 5)
                {
                    try
                    {
                        var newTmpl = _rpc.GetBlockTemplateAsync().GetAwaiter().GetResult();
                        if (newTmpl != null)
                        {
                            var prev = _ctx;
                            // Only swap if previousblockhash changed (new tip) or curtime advanced > 30s.
                            // Re-using the same template across many rounds is fine; we just want fresh
                            // info when the network moves on.
                            if (prev == null ||
                                prev.Template.PreviousBlockHash != newTmpl.PreviousBlockHash ||
                                Math.Abs(newTmpl.CurTime - prev.Template.CurTime) > 30)
                            {
                                _ctx = new MiningContext(newTmpl, prev?.PayoutScript ?? Array.Empty<byte>());
                            }
                        }
                        lastTemplateAt = now;
                    }
                    catch (Exception ex)
                    {
                        _lastError = $"Template refresh failed: {ex.Message}";
                        _logger?.LogWarning(ex, "Template refresh failed");
                    }
                }

                Thread.Sleep(200);
            }
        }
        catch (Exception ex)
        {
            _lastError = $"Refresher crashed: {ex.Message}";
            _logger?.LogError(ex, "Refresher thread crashed");
            EmitStatus();
        }
    }

    private sealed class MiningContext
    {
        public BlockTemplate Template { get; }
        public byte[] PayoutScript { get; }
        public DateTime LoadedUtc { get; } = DateTime.UtcNow;

        public MiningContext(BlockTemplate template, byte[] payoutScript)
        {
            Template = template;
            PayoutScript = payoutScript;
        }
    }
}

using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The wallet-side mint-ancestry tracer. The blend cases deliberately reuse the WEBSITE
/// engine's own test vectors (Blazecoin_Indexer_API VintageAncestryTests) — the two must
/// agree coin-for-coin or wallet provenance and the site's collector board would tell the
/// user different stories about the same satoshis. Also pins the conservative contract:
/// anything unreadable or cut off contributes NOTHING, so a trace under-claims and can
/// never invent ancestry.
/// </summary>
public class AncestryTracerTests
{
    private static readonly DateTime Y2014 = new(2014, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Y2026 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>An in-memory chain graph — no daemon, no network.</summary>
    private sealed class FakeChain : IAncestryRpc
    {
        private readonly Dictionary<string, RawTransaction> _txs = new();
        private readonly Dictionary<string, BlockRef> _blocks = new();
        public readonly HashSet<string> Unreadable = new();
        public readonly List<UnspentOutput> Unspent = new();
        public int TxReads;

        public FakeChain Coinbase(string txid, long height, DateTime time, params long[] outputs)
        {
            _txs[txid] = new RawTransaction(txid, true, $"hash-{height}", height, time,
                Array.Empty<RawTxInput>(),
                outputs.Select((v, i) => new RawTxOutput(i, v)).ToList());
            _blocks[$"hash-{height}"] = new BlockRef(height, time);
            return this;
        }

        /// <summary>A coinbase whose transaction carries only the block HASH — forces the
        /// tracer through the getblockheader hop (the getrawtransaction shape).</summary>
        public FakeChain CoinbaseHashOnly(string txid, long height, DateTime time, params long[] outputs)
        {
            _txs[txid] = new RawTransaction(txid, true, $"hash-{height}", null, null,
                Array.Empty<RawTxInput>(),
                outputs.Select((v, i) => new RawTxOutput(i, v)).ToList());
            _blocks[$"hash-{height}"] = new BlockRef(height, time);
            return this;
        }

        public FakeChain Spend(string txid, (string TxId, int Vout)[] inputs, params long[] outputs)
        {
            _txs[txid] = new RawTransaction(txid, false, "hash-spend", 9_000_000, Y2026,
                inputs.Select(i => new RawTxInput(i.TxId, i.Vout)).ToList(),
                outputs.Select((v, i) => new RawTxOutput(i, v)).ToList());
            return this;
        }

        public FakeChain Utxo(string txid, int vout, long sats)
        {
            Unspent.Add(new UnspentOutput(txid, vout, sats, "Btest", 100));
            return this;
        }

        public Task<IReadOnlyList<UnspentOutput>> ListUnspentAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UnspentOutput>>(Unspent);

        public Task<RawTransaction?> GetTransactionAsync(string txid, CancellationToken ct = default)
        {
            TxReads++;
            if (Unreadable.Contains(txid)) return Task.FromResult<RawTransaction?>(null);
            return Task.FromResult(_txs.GetValueOrDefault(txid));
        }

        public Task<BlockRef?> GetBlockRefAsync(string blockHash, CancellationToken ct = default)
            => Task.FromResult(_blocks.GetValueOrDefault(blockHash));
    }

    // ── coinbases terminate a path ──

    [Fact]
    public async Task a_coinbase_output_is_pure_lineage_of_its_own_block()
    {
        var chain = new FakeChain().Coinbase("cb", 2_500_000, Y2014, 41_300_000_000).Utxo("cb", 0, 41_300_000_000);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        var utxo = Assert.Single(report.Outputs);
        Assert.Equal(new Dictionary<long, long> { [2_500_000] = 41_300_000_000 }, utxo.ByHeight);
        Assert.True(utxo.FullyTraced);
        Assert.Equal(1.0, utxo.PurityOf(2_500_000));       // the collector's pure coin
        Assert.True(report.FullyTraced);
    }

    [Fact]
    public async Task a_coinbase_with_only_a_block_hash_resolves_via_the_header()
    {
        var chain = new FakeChain().CoinbaseHashOnly("cb", 1_000_000, Y2014, 500).Utxo("cb", 0, 500);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        Assert.Equal(new Dictionary<long, long> { [1_000_000] = 500 }, report.Outputs[0].ByHeight);
    }

    // ── the blend (website engine's vectors) ──

    [Fact]
    public async Task a_spend_blends_inputs_proportionally_and_floors_conservatively()
    {
        // Mirrors the site engine's test: inputs 1000 (2014) + 500 (2026), outputs 900 +
        // 590, fee 10 → denominator 1500 (the true input total).
        var chain = new FakeChain()
            .Coinbase("cbA", 500_001, Y2014, 1000)
            .Coinbase("cbB", 4_000_001, Y2026, 500)
            .Spend("spend", new[] { ("cbA", 0), ("cbB", 0) }, 900, 590)
            .Utxo("spend", 0, 900).Utxo("spend", 1, 590);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        Assert.Equal(new Dictionary<long, long> { [500_001] = 600, [4_000_001] = 300 }, report.Outputs[0].ByHeight);
        Assert.Equal(new Dictionary<long, long> { [500_001] = 393, [4_000_001] = 196 }, report.Outputs[1].ByHeight);

        // Conservation: the floor loses a little, and never the other way.
        Assert.True(600 + 393 <= 1000);
        Assert.True(300 + 196 <= 500);
        Assert.All(report.Outputs, o => Assert.True(o.Attributed <= o.Satoshis));
    }

    [Fact]
    public async Task an_unreadable_parent_dilutes_but_never_fabricates()
    {
        // One readable 500-sat 2014 coinbase + one input the node can't read (a parent
        // outside a wallet on a node with no tx index), output 1000.
        var chain = new FakeChain()
            .Coinbase("cb", 600_000, Y2014, 500)
            .Spend("spend", new[] { ("cb", 0), ("mystery", 0) }, 1000)
            .Utxo("spend", 0, 1000);
        chain.Unreadable.Add("mystery");

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        // Denominator falls back to the output total (1000), so the known 2014 ancestry
        // passes through at its true share and nothing is invented for the mystery input.
        Assert.Equal(new Dictionary<long, long> { [600_000] = 500 }, report.Outputs[0].ByHeight);
        Assert.False(report.Outputs[0].FullyTraced);
        Assert.False(report.FullyTraced);
    }

    [Fact]
    public async Task chained_spends_share_the_cache_and_read_each_transaction_once()
    {
        var chain = new FakeChain()
            .Coinbase("cb", 700_000, Y2014, 1000)
            .Spend("tx1", new[] { ("cb", 0) }, 995)
            .Spend("tx2", new[] { ("tx1", 0) }, 990)
            .Utxo("tx2", 0, 990).Utxo("tx1", 0, 995);

        var tracer = new AncestryTracer(chain);
        var report = await tracer.TraceUnspentAsync();

        Assert.Equal(new Dictionary<long, long> { [700_000] = 990 }, report.Outputs[0].ByHeight);
        Assert.Equal(new Dictionary<long, long> { [700_000] = 995 }, report.Outputs[1].ByHeight);
        Assert.Equal(3, chain.TxReads);          // cb, tx1, tx2 — each exactly once
        Assert.Equal(3, report.TransactionsRead);
    }

    [Fact]
    public async Task the_depth_cap_yields_nothing_rather_than_a_guess()
    {
        var chain = new FakeChain().Coinbase("cb", 800_000, Y2014, 1000)
            .Spend("tx1", new[] { ("cb", 0) }, 1000)
            .Spend("tx2", new[] { ("tx1", 0) }, 1000)
            .Utxo("tx2", 0, 1000);

        var shallow = new AncestryTracer(chain, new AncestryTracerOptions { MaxDepth = 1 });
        var report = await shallow.TraceUnspentAsync();

        Assert.Empty(report.Outputs[0].ByHeight);   // cut off → no ancestry claimed
        Assert.Equal(0, report.Haircut.AttributedSatoshis);
        Assert.Equal(0, report.Fifo.AttributedSatoshis);
        Assert.False(report.FullyTraced);
    }

    [Fact]
    public async Task a_payout_style_change_chain_hundreds_deep_traces_fully_under_defaults()
    {
        // Regression for 2026-08-24: the payout wallet chains change→change every 15
        // minutes (~96 hops/day), so real paid-out coins sit hundreds of hops above
        // their coinbases. The old MaxDepth=100 default abandoned every such path and
        // traced live coins to NOTHING; the default must comfortably clear a chain
        // like this (the read budget, not depth, is the real bound).
        var chain = new FakeChain().Coinbase("cb", 700_000, Y2014, 1_000);
        var prev = "cb";
        for (var i = 0; i < 600; i++)
        {
            var name = $"hop{i}";
            chain.Spend(name, new[] { (prev, 0) }, 1_000);
            prev = name;
        }
        chain.Utxo(prev, 0, 1_000);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        Assert.Equal(new Dictionary<long, long> { [700_000] = 1_000 }, report.Outputs[0].ByHeight);
        Assert.True(report.FullyTraced);
        Assert.Equal(1_000, report.Fifo.AttributedSatoshis);
    }

    [Fact]
    public async Task past_the_segment_cap_the_remainder_folds_untraced_instead_of_nulling_the_coin()
    {
        // Regression for 2026-08-24 (part 2): heavily-blended payout ancestry fragments
        // past MaxSegmentsPerOutput; the old code returned null for the WHOLE output, so
        // FIFO claimed nothing at all. The contract is a fold: pedigree up to the cap
        // survives, everything after occupies its exact position as an untraced run.
        var chain = new FakeChain()
            .Coinbase("cb1", 600_000, Y2014, 500)
            .Coinbase("cb2", 700_000, Y2014, 300)
            .Coinbase("cb3", 800_000, Y2014, 200)
            .Spend("mix", new[] { ("cb1", 0), ("cb2", 0), ("cb3", 0) }, 1_000)
            .Utxo("mix", 0, 1_000);

        var tracer = new AncestryTracer(chain, new AncestryTracerOptions { MaxSegmentsPerOutput = 2 });
        var report = await tracer.TraceUnspentAsync();

        // First two inputs keep their pedigree; the third folds to an untraced run.
        Assert.Equal(800, report.Fifo.AttributedSatoshis);
        Assert.False(report.FullyTraced);
        var segments = report.Outputs[0].Segments;
        Assert.Equal(500, segments.First(s => s.MintHeight == 600_000).Length);
        Assert.Equal(300, segments.First(s => s.MintHeight == 700_000).Length);
        Assert.Equal(200, segments.Where(s => s.MintHeight is null).Sum(s => s.Length));
    }

    // ── the catalogue ──

    [Fact]
    public async Task the_report_groups_years_and_specials_with_spans()
    {
        var chain = new FakeChain()
            .Coinbase("a", 600_000, new DateTime(2014, 5, 24, 1, 0, 0, DateTimeKind.Utc), 100)
            .Coinbase("b", 640_000, new DateTime(2014, 12, 2, 1, 0, 0, DateTimeKind.Utc), 200)
            .Coinbase("c", 413, new DateTime(2014, 5, 24, 1, 51, 0, DateTimeKind.Utc), 300)
            .Coinbase("d", 1_051_200, new DateTime(2015, 8, 14, 8, 3, 0, DateTimeKind.Utc), 400)
            .Utxo("a", 0, 100).Utxo("b", 0, 200).Utxo("c", 0, 300).Utxo("d", 0, 400);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        // Untouched coinbases: both models agree exactly (no blending has happened).
        foreach (var model in new[] { report.Fifo, report.Haircut })
        {
            var year = Assert.Single(model.Years);
            Assert.Equal("2014", year.Key);
            Assert.Equal(300, year.Satoshis);               // the two ordinary 2014 blocks
            Assert.Equal("May–Dec 2014", year.MonthsLabel); // real months, straight off the blocks

            Assert.Collection(model.Specials,               // chain order, not key order
                s => { Assert.Equal("block-413", s.Key); Assert.Equal("Block 413", s.Label); Assert.Equal(300, s.Satoshis); },
                s => { Assert.Equal("halving-1", s.Key); Assert.Equal("Halving I", s.Label); Assert.Equal(400, s.Satoshis); });

            Assert.Equal(1000, model.AttributedSatoshis);
        }

        Assert.Equal(1000, report.TotalSatoshis);
        Assert.True(report.FullyTraced);
    }

    // ── FIFO sat-ranges (the pedigree model) ──

    [Fact]
    public async Task a_coinbase_is_one_fifo_run_owned_by_its_block()
    {
        var chain = new FakeChain().Coinbase("cb", 413, Y2014, 41_300_000_000).Utxo("cb", 0, 41_300_000_000);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        var seg = Assert.Single(report.Outputs[0].Segments);
        Assert.Equal(new SatSegment(0, 41_300_000_000, 413), seg);
        Assert.Equal(1.0, report.Outputs[0].FifoPurityOf(413));
    }

    [Fact]
    public async Task fifo_lays_inputs_end_to_end_and_the_fee_eats_the_tail()
    {
        // Inputs 1000 (block 500,001) then 500 (block 4,000,001) = a 1500-sat stream.
        // Outputs 900 + 590 take the FIRST 1490; the 10-sat fee is the tail nobody claims.
        var chain = new FakeChain()
            .Coinbase("cbA", 500_001, Y2014, 1000)
            .Coinbase("cbB", 4_000_001, Y2026, 500)
            .Spend("spend", new[] { ("cbA", 0), ("cbB", 0) }, 900, 590)
            .Utxo("spend", 0, 900).Utxo("spend", 1, 590);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        // Output 0 = stream[0..900) → entirely the first coinbase. PURE, unlike the haircut.
        var first = Assert.Single(report.Outputs[0].Segments);
        Assert.Equal(new SatSegment(0, 900, 500_001), first);
        Assert.Equal(1.0, report.Outputs[0].FifoPurityOf(500_001));

        // Output 1 = stream[900..1490) → the last 100 of input A, then 490 of input B.
        Assert.Collection(report.Outputs[1].Segments,
            s => Assert.Equal(new SatSegment(0, 100, 500_001), s),
            s => Assert.Equal(new SatSegment(100, 490, 4_000_001), s));

        // The two models disagree by design — FIFO gives whole sats, the haircut fractions.
        Assert.Equal(new Dictionary<long, long> { [500_001] = 600, [4_000_001] = 300 },
            report.Outputs[0].ByHeight);
        Assert.Equal(new Dictionary<long, long> { [500_001] = 900 },
            report.Outputs[0].FifoByHeight());
    }

    [Fact]
    public async Task an_untraceable_input_keeps_its_place_in_the_fifo_stream()
    {
        // The mystery parent is READABLE (so its 500-sat width is known) but its own
        // ancestry isn't — later offsets must still line up.
        var chain = new FakeChain()
            .Coinbase("mystery", 900_000, Y2014, 500)
            .Coinbase("known", 950_000, Y2026, 500)
            .Spend("spend", new[] { ("mystery", 0), ("known", 0) }, 1000)
            .Utxo("spend", 0, 1000);
        chain.Unreadable.Add("mystery-parent");   // (not wired — 'mystery' resolves fine)

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        Assert.Collection(report.Outputs[0].Segments,
            s => Assert.Equal(new SatSegment(0, 500, 900_000), s),
            s => Assert.Equal(new SatSegment(500, 500, 950_000), s));
    }

    [Fact]
    public async Task an_unreadable_parent_leaves_fifo_untraced_rather_than_misaligned()
    {
        // The parent's VALUE can't be read, so every offset after it would be a guess.
        var chain = new FakeChain()
            .Coinbase("known", 950_000, Y2026, 500)
            .Spend("spend", new[] { ("gone", 0), ("known", 0) }, 1000)
            .Utxo("spend", 0, 1000);
        chain.Unreadable.Add("gone");

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        var seg = Assert.Single(report.Outputs[0].Segments);
        Assert.Null(seg.MintHeight);                       // explicitly unknown
        Assert.Equal(1000, seg.Length);
        Assert.Equal(0, report.Outputs[0].FifoAttributed);
        Assert.False(report.FullyTraced);
    }

    [Fact]
    public async Task segments_are_contiguous_and_cover_the_whole_output()
    {
        var chain = new FakeChain()
            .Coinbase("a", 413, Y2014, 300)
            .Coinbase("b", 1_051_200, Y2014, 300)
            .Coinbase("c", 4_000_000, Y2026, 400)
            .Spend("mix", new[] { ("a", 0), ("b", 0), ("c", 0) }, 1000)
            .Utxo("mix", 0, 1000);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        long expected = 0;
        foreach (var s in report.Outputs[0].Segments)
        {
            Assert.Equal(expected, s.Offset);   // no gaps, no overlaps
            expected += s.Length;
        }
        Assert.Equal(1000, expected);           // covers the full value
        Assert.Equal(1000, report.Outputs[0].FifoAttributed);
    }

    [Fact]
    public async Task the_report_carries_both_models_and_they_can_disagree()
    {
        var chain = new FakeChain()
            .Coinbase("old", 413, new DateTime(2014, 5, 24, 1, 51, 0, DateTimeKind.Utc), 400)
            .Coinbase("new", 4_000_000, Y2026, 600)
            .Spend("mix", new[] { ("old", 0), ("new", 0) }, 400, 590)
            .Utxo("mix", 0, 400);   // the FIRST 400 sats — pure Block 413 under FIFO

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        // FIFO: the first 400 sats of the stream came wholly from input A, so this UTXO
        // is 100% Block-413 lineage — ONE vintage, whole satoshis.
        var fifoSpecial = Assert.Single(report.Fifo.Specials);
        Assert.Equal("block-413", fifoSpecial.Key);
        Assert.Equal(400, fifoSpecial.Satoshis);
        Assert.Equal(1.0, report.Outputs[0].FifoPurityOf(413));

        // The haircut smears the SAME UTXO across both inputs' vintages in proportion to
        // value (400×400÷1000 and 600×400÷1000) — two partial vintages, no pure coin.
        Assert.Collection(report.Haircut.Specials,
            s => { Assert.Equal("block-413", s.Key); Assert.Equal(160, s.Satoshis); },
            s => { Assert.Equal("block-4000000", s.Key); Assert.Equal(240, s.Satoshis); });

        Assert.Equal(400, report.Fifo.AttributedSatoshis);
        Assert.Equal(400, report.Haircut.AttributedSatoshis);
        Assert.True(report.FullyTraced);                  // FIFO drives the headline
    }

    [Fact]
    public async Task years_break_down_into_months_off_the_block_timestamps()
    {
        var chain = new FakeChain()
            .Coinbase("may", 600_000, new DateTime(2014, 5, 24, 1, 0, 0, DateTimeKind.Utc), 100)
            .Coinbase("may2", 610_000, new DateTime(2014, 5, 30, 1, 0, 0, DateTimeKind.Utc), 50)
            .Coinbase("dec", 640_000, new DateTime(2014, 12, 2, 1, 0, 0, DateTimeKind.Utc), 200)
            .Coinbase("jan", 700_000, new DateTime(2015, 1, 9, 1, 0, 0, DateTimeKind.Utc), 400)
            .Utxo("may", 0, 100).Utxo("may2", 0, 50).Utxo("dec", 0, 200).Utxo("jan", 0, 400);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        // Years still total as before …
        Assert.Collection(report.Fifo.Years,
            y => { Assert.Equal("2014", y.Key); Assert.Equal(350, y.Satoshis); },
            y => { Assert.Equal("2015", y.Key); Assert.Equal(400, y.Satoshis); });

        // … and each is now broken down by calendar month, in date order.
        Assert.Collection(report.Fifo.Months,
            m => { Assert.Equal("2014-05", m.Key); Assert.Equal("May 2014", m.Label); Assert.Equal(150, m.Satoshis); },
            m => { Assert.Equal("2014-12", m.Key); Assert.Equal(200, m.Satoshis); },
            m => { Assert.Equal("2015-01", m.Key); Assert.Equal(400, m.Satoshis); });

        // The UI pairs months to their year by key prefix.
        var y2014 = report.Fifo.Years.First(y => y.Key == "2014");
        Assert.Equal(2, report.Fifo.MonthsOf(y2014).Count());
        Assert.Equal(350, report.Fifo.MonthsOf(y2014).Sum(m => m.Satoshis));
    }

    [Fact]
    public async Task specials_never_appear_in_the_month_breakdown()
    {
        // A special IS a single block, so it has no month rollup to give.
        var chain = new FakeChain()
            .Coinbase("cb413", 413, new DateTime(2014, 5, 24, 1, 51, 0, DateTimeKind.Utc), 300)
            .Utxo("cb413", 0, 300);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        Assert.Single(report.Fifo.Specials);
        Assert.Empty(report.Fifo.Months);
        Assert.Empty(report.Fifo.Years);
    }

    [Fact]
    public async Task blended_holdings_report_partial_purity_per_block()
    {
        var chain = new FakeChain()
            .Coinbase("old", 413, new DateTime(2014, 5, 24, 1, 51, 0, DateTimeKind.Utc), 400)
            .Coinbase("new", 4_000_000, Y2026, 600)
            .Spend("mix", new[] { ("old", 0), ("new", 0) }, 1000)
            .Utxo("mix", 0, 1000);

        var report = await new AncestryTracer(chain).TraceUnspentAsync();

        var o = report.Outputs[0];
        Assert.Equal(0.4, o.PurityOf(413), 3);          // 40% Block-413 lineage
        Assert.Equal(0.6, o.PurityOf(4_000_000), 3);
        Assert.True(o.FullyTraced);
    }
}

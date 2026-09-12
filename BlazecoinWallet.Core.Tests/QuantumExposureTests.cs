using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Provenance;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Quantum exposure — which of the wallet's keys the chain has already seen, and the sweep
/// that moves exposed coins behind a fresh key. The pubkey → address derivation and the
/// scriptSig parse are pinned against a REAL mainnet spend (block 4,235,528, txid
/// f7135f34…84dd, input 0 funded by BnKVYKojUkgynKnD43ZHzSfGTw3apinDMM), so a wrong
/// version byte, hash or push parse cannot pass.
/// </summary>
public class QuantumExposureTests
{
    // Real chain vector (relay node, 2026-09-12): the scriptSig of input 0 of f7135f34…84dd.
    private const string RealScriptSigHex =
        "473044022062effaba725a78f0f3fdf02c16385aaea677949a848bfb98a15779477ad9d83d0220642f3db91f2cd89fabe4453fa438a34f26381de55ce398c23090bff467080325" +
        "01" +
        "210351594e4c08569655e3a6a6b3732e96a7de1a08e08682da05cfe7c5439974389a";
    private const string RealPubKeyHex = "0351594e4c08569655e3a6a6b3732e96a7de1a08e08682da05cfe7c5439974389a";
    private const string RealHash160Hex = "cb45ed1d2cd7363b06fdb559e997ef92f521e6b4";
    private const string RealAddress = "BnKVYKojUkgynKnD43ZHzSfGTw3apinDMM";

    // Pinned address vectors from BitcoinProtocolTests (hash160 → address, version 26).
    private const string ZeroHashAddress = "BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z";
    private const string SeqHashAddress = "BTt1goArr9xPyiJmuCVqZ3DpTVMKkBT1dL";

    // ── protocol helpers ──

    [Fact]
    public void Hash160_of_real_pubkey_matches_chain()
    {
        var h = BitcoinProtocol.Hash160(BitcoinProtocol.HexToBytes(RealPubKeyHex));
        Assert.Equal(RealHash160Hex, BitcoinProtocol.BytesToHex(h));
    }

    [Fact]
    public void PubKeyToAddress_matches_the_funding_output_on_chain()
    {
        Assert.Equal(RealAddress, BitcoinProtocol.PubKeyToAddress(BitcoinProtocol.HexToBytes(RealPubKeyHex)));
    }

    [Fact]
    public void Base58CheckEncode_round_trips_the_pinned_address_vectors()
    {
        var zero = new byte[21]; zero[0] = BitcoinProtocol.MainnetPubKeyVersion;
        Assert.Equal(ZeroHashAddress, BitcoinProtocol.Base58CheckEncode(zero));

        var seq = new byte[21]; seq[0] = BitcoinProtocol.MainnetPubKeyVersion;
        for (int i = 0; i < 20; i++) seq[i + 1] = (byte)(i + 1);
        Assert.Equal(SeqHashAddress, BitcoinProtocol.Base58CheckEncode(seq));

        // and the decoder already in Core agrees with the encoder
        Assert.Equal("76a914" + RealHash160Hex + "88ac", BitcoinProtocol.BytesToHex(BitcoinProtocol.AddressToP2PKH(RealAddress)));
    }

    // ── scriptSig parsing ──

    [Fact]
    public void Parser_extracts_the_compressed_pubkey_from_a_real_p2pkh_spend()
    {
        var pk = ScriptSigParser.TryExtractPubKey(RealScriptSigHex);
        Assert.NotNull(pk);
        Assert.Equal(RealPubKeyHex, BitcoinProtocol.BytesToHex(pk!));
        Assert.Equal(RealAddress, ScriptSigParser.PubKeyToAddress(pk!));
    }

    [Fact]
    public void Parser_accepts_an_uncompressed_key()
    {
        var sig = new string('a', 140);                       // 70-byte fake DER
        var pk = "04" + new string('b', 128);                 // 65-byte uncompressed
        var hex = "46" + sig + "41" + pk;
        var got = ScriptSigParser.TryExtractPubKey(hex);
        Assert.NotNull(got);
        Assert.Equal(65, got!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]                                        // OP_0: not a push
    [InlineData("abc")]                                       // odd length
    [InlineData("zz")]                                        // not hex
    [InlineData("210351594e4c08569655e3a6a6b3732e96a7de1a08e08682da05cfe7c5439974389a")]   // one push only (P2PK-style)
    [InlineData("0201020102ac")]                              // trailing opcode after two pushes
    [InlineData("4730440220")]                                // push runs off the end
    public void Parser_rejects_anything_that_is_not_a_plain_p2pkh_scriptsig(string? hex)
    {
        Assert.Null(ScriptSigParser.TryExtractPubKey(hex));
    }

    [Fact]
    public void Parser_rejects_a_second_push_that_is_not_a_pubkey()
    {
        // two pushes, but the second is 20 bytes — a hash, not a key
        var hex = "02" + "0102" + "14" + RealHash160Hex;
        Assert.Null(ScriptSigParser.TryExtractPubKey(hex));
    }

    // ── scanner ──

    private sealed class FakeRpc : IExposureRpc
    {
        public List<UnspentOutput> Unspent { get; } = new();
        public Dictionary<string, List<string>> ScriptSigsByTx { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);
        public int Reads;

        public Task<IReadOnlyList<UnspentOutput>> ListUnspentAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UnspentOutput>>(Unspent);
        public Task<IReadOnlyList<string>> ListSpendingTxIdsAsync(int maxTransactions = 100_000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(ScriptSigsByTx.Keys.ToList());
        public Task<IReadOnlyList<string>> GetInputScriptSigHexAsync(string txid, CancellationToken ct = default)
        {
            Reads++;
            if (Unreadable.Contains(txid)) throw new InvalidOperationException("unreadable");
            return Task.FromResult<IReadOnlyList<string>>(ScriptSigsByTx[txid]);
        }
    }

    [Fact]
    public async Task Scanner_flags_the_address_whose_key_appeared_in_a_send_and_leaves_the_rest_unexposed()
    {
        var rpc = new FakeRpc();
        rpc.Unspent.Add(new UnspentOutput("t1", 0, 5_000_000_000, RealAddress, 50));
        rpc.Unspent.Add(new UnspentOutput("t2", 1, 1_000_000_000, RealAddress, 12));   // second output → reused
        rpc.Unspent.Add(new UnspentOutput("t3", 0, 2_000_000_000, SeqHashAddress, 90));
        rpc.Unspent.Add(new UnspentOutput("t4", 0, 100, null, 1));                      // no address → ignored
        rpc.ScriptSigsByTx["s1"] = new List<string> { RealScriptSigHex, "00" };        // key of RealAddress + a coinbase-ish junk sig
        rpc.ScriptSigsByTx["s2"] = new List<string> { RealScriptSigHex };              // same key again — counted once

        var progress = new List<ExposureProgress>();
        var report = await new QuantumExposureScanner(rpc).ScanAsync(progress: new SyncProgress(progress));

        Assert.Equal(2, report.Addresses.Count);
        var exposed = report.Addresses.Single(a => a.Address == RealAddress);
        Assert.Equal(ExposureStatus.Exposed, exposed.Status);
        Assert.Equal("s1", exposed.ExposingTxId);
        Assert.Equal(6_000_000_000, exposed.Satoshis);
        Assert.True(exposed.IsReused);
        Assert.Equal(2, exposed.OutputCount);

        var safe = report.Addresses.Single(a => a.Address == SeqHashAddress);
        Assert.Equal(ExposureStatus.Unexposed, safe.Status);
        Assert.Null(safe.ExposingTxId);
        Assert.False(safe.IsReused);

        Assert.Equal(8_000_000_000, report.TotalSatoshis);
        Assert.Equal(6_000_000_000, report.ExposedSatoshis);
        Assert.Equal(2_000_000_000, report.UnexposedSatoshis);
        Assert.Equal(1, report.ExposedKeyCount);
        Assert.Equal(1, report.ExposedAddressCount);
        Assert.Equal(1, report.UnexposedAddressCount);
        Assert.Equal(2, report.TransactionsScanned);
        Assert.Equal(ExposureStatus.Exposed, report.Addresses[0].Status);   // exposed rows sort first
        Assert.Contains(progress, p => p.TransactionsDone == 2 && p.TransactionsTotal == 2 && p.ExposedKeysFound == 1);
    }

    [Fact]
    public async Task Scanner_treats_an_unreadable_transaction_as_proving_nothing()
    {
        var rpc = new FakeRpc();
        rpc.Unspent.Add(new UnspentOutput("t1", 0, 1_000, RealAddress, 5));
        rpc.ScriptSigsByTx["bad"] = new List<string> { RealScriptSigHex };
        rpc.Unreadable.Add("bad");

        var report = await new QuantumExposureScanner(rpc).ScanAsync();

        Assert.Equal(ExposureStatus.Unexposed, report.Addresses.Single().Status);
        Assert.Equal(0, report.ExposedKeyCount);
        Assert.Equal(1, report.TransactionsScanned);
    }

    [Fact]
    public async Task Scanner_honours_cancellation()
    {
        var rpc = new FakeRpc();
        rpc.ScriptSigsByTx["s1"] = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new QuantumExposureScanner(rpc).ScanAsync(ct: cts.Token));
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; tests want the reports inline.</summary>
    private sealed class SyncProgress : IProgress<ExposureProgress>
    {
        private readonly List<ExposureProgress> _sink;
        public SyncProgress(List<ExposureProgress> sink) => _sink = sink;
        public void Report(ExposureProgress value) => _sink.Add(value);
    }

    // ── sweep planner ──

    private static UnspentOutput U(string txid, long sats, int conf = 10) => new(txid, 0, sats, RealAddress, conf);

    [Fact]
    public void Planner_moves_everything_to_the_destination_minus_a_fee_that_matches_the_size_model()
    {
        var utxos = new[] { U("a", 100_000_000), U("b", 50_000_000) };

        var r = QuantumSweepPlanner.Plan(utxos, SeqHashAddress);

        Assert.Null(r.Problem);
        var tx = Assert.Single(r.Plan!.Transactions);
        Assert.Equal(2, tx.Inputs.Count);
        Assert.Equal(SeqHashAddress, tx.Destination);
        var bytes = QuantumSweepPlanner.EstimateBytes(2);
        Assert.Equal(10 + 148 * 2 + 34, bytes);
        var fee = QuantumSweepPlanner.FeeFor(bytes, QuantumSweepPlanner.DefaultFeeRateSatPerKb);
        Assert.Equal((bytes * 100_000L + 999) / 1000, fee);
        Assert.Equal(fee, tx.FeeSatoshis);
        Assert.Equal(150_000_000 - fee, tx.OutputSatoshis);
        Assert.True(tx.Balances);
        Assert.True(r.Plan.Balances);
        Assert.Empty(r.Plan.Warnings);
    }

    [Fact]
    public void Planner_batches_by_input_count_and_warns_about_the_reused_destination()
    {
        var utxos = Enumerable.Range(0, 5).Select(i => U($"u{i}", 10_000_000)).ToList();

        var r = QuantumSweepPlanner.Plan(utxos, SeqHashAddress, maxInputsPerTx: 2);

        Assert.Null(r.Problem);
        Assert.Equal(3, r.Plan!.Transactions.Count);
        Assert.Equal(new[] { 2, 2, 1 }, r.Plan.Transactions.Select(t => t.Inputs.Count).ToArray());
        Assert.Equal(5, r.Plan.InputCount);
        Assert.Equal(50_000_000, r.Plan.InputSatoshis);
        Assert.Equal(r.Plan.InputSatoshis, r.Plan.OutputSatoshis + r.Plan.FeeSatoshis);
        Assert.Contains(r.Plan.Warnings, w => w.Contains("3 transactions"));
    }

    [Fact]
    public void Planner_leaves_unconfirmed_outputs_behind_and_says_so()
    {
        var utxos = new[] { U("a", 100_000_000, conf: 3), U("b", 100_000_000, conf: 0) };

        var r = QuantumSweepPlanner.Plan(utxos, SeqHashAddress);

        Assert.Null(r.Problem);
        Assert.Single(r.Plan!.Transactions[0].Inputs);
        Assert.Contains(r.Plan.Warnings, w => w.Contains("1 unconfirmed"));
    }

    [Fact]
    public void Planner_refuses_a_sweep_that_would_be_dust_after_the_fee()
    {
        var utxos = new[] { U("a", 1_000) };                  // fee at the default rate is ~19,200 sats

        var r = QuantumSweepPlanner.Plan(utxos, SeqHashAddress);

        Assert.Null(r.Plan);
        Assert.Contains("dust", r.Problem!);
    }

    [Fact]
    public void Planner_at_zero_fee_moves_every_satoshi()
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 546) }, SeqHashAddress, feeRateSatPerKb: 0);

        Assert.Null(r.Problem);
        Assert.Equal(546, r.Plan!.OutputSatoshis);
        Assert.Equal(0, r.Plan.FeeSatoshis);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BnKVYKojUkgynKnD43ZHzSfGTw3apinDMX")]       // bad checksum
    [InlineData("blz1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4")]
    public void Planner_rejects_a_destination_that_is_not_a_legacy_address(string destination)
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 100_000_000) }, destination);
        Assert.Null(r.Plan);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void Planner_refuses_when_nothing_is_confirmed()
    {
        var r = QuantumSweepPlanner.Plan(new[] { U("a", 100_000_000, conf: 0) }, SeqHashAddress);
        Assert.Null(r.Plan);
        Assert.Contains("Nothing confirmed", r.Problem!);
    }
}

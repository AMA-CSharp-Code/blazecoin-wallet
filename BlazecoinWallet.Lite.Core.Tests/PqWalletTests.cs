using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.Pq;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Core.Tests;

/// <summary>
/// The post-quantum output type at the WALLET layer (PQ_SIGNATURES.md §3.6, §6, §13 decision 8):
/// the hand-assembled mixed legacy + P2PQH send path, the activation gate on paying a BQ
/// address / spending a P2PQH coin / revealing a BQ receive address, ownership of PQ coins
/// once the fork is active, and the persisted PQ reveal position. The activation height is
/// injected per test (the chain constant is still null).
/// </summary>
public class PqWalletTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static readonly Network Net = BlazecoinNetwork.Instance;

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored = TestMnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    /// <summary>Per-address coins (any address not listed has none); every listed address
    /// counts as used on-chain. Confirmed coins sit at height 100 with 10 confirmations, so
    /// the implied tip is 109.</summary>
    private sealed class AddressReader : IChainReader
    {
        public readonly Dictionary<string, List<LiteChainUtxo>> Utxos = new(StringComparer.Ordinal);
        public readonly HashSet<string> Used = new(StringComparer.Ordinal);
        public bool SupportsHistory => true;
        public bool SupportsChainVerification => false;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default)
            => Task.FromResult(Used.Contains(a) || Utxos.ContainsKey(a) ? new LiteAddressSummary(0, 0, 0, 1) : null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteChainUtxo>>(Utxos.TryGetValue(a, out var l) ? l : []);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class CapturingRelay : ITxRelay
    {
        public readonly List<string> Sent = [];
        public Task<LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default)
        { Sent.Add(hex); return Task.FromResult(LiteBroadcastResult.Ok(Transaction.Parse(hex, Net).GetHash().ToString())); }
    }

    private static LiteChainUtxo Coin(char tag, int vout, long sats, string? scriptHex = null)
        => new(new string(tag, 64), vout, sats, 10, 100, false, scriptHex);

    private static string Hex(byte[] b) => Encoders.Hex.EncodeData(b);

    // ── The builder's hand-assembled path ─────────────────────────────────────────────────

    [Fact]
    public void Mixed_legacy_and_pq_inputs_pay_a_BQ_destination_with_legacy_change()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var legacyAddr = wallet.GetReceiveAddress(0);
        var pq0 = wallet.GetPqKey(0);
        var pq1 = wallet.GetPqKey(1);
        var change = wallet.GetChangeAddress(0);

        var utxos = new List<LiteUtxo>
        {
            new(new string('a', 64), 0, 300_000_000, legacyAddr),
            new(new string('b', 64), 1, 200_000_000, pq0.Address(), Hex(pq0.ScriptPubKey)),
        };
        var signed = LiteTransactionBuilder.BuildAndSign(utxos, pq1.Address(), 400_000_000, change,
            keyForAddress: _ => wallet.GetPrivateKey(0), pqKeyForAddress: _ => pq0);

        var tx = Transaction.Parse(signed.Hex, Net);
        Assert.Equal(signed.TxId, tx.GetHash().ToString());
        Assert.True(signed.ChangeCreated);
        Assert.Equal(2, tx.Inputs.Count);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(pq1.ScriptPubKey, tx.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal(400_000_000, tx.Outputs[0].Value.Satoshi);
        Assert.Equal(new BitcoinPubKeyAddress(change, Net).ScriptPubKey, tx.Outputs[1].ScriptPubKey);
        Assert.Equal(100_000_000, tx.Outputs[1].Value.Satoshi);

        // Input 0 is a standard P2PKH spend NBitcoin's interpreter accepts.
        var coin0 = new Coin(tx.Inputs[0].PrevOut, new TxOut(Money.Satoshis(300_000_000), new BitcoinPubKeyAddress(legacyAddr, Net).ScriptPubKey));
        var ctx = new ScriptEvaluationContext { ScriptVerify = ScriptVerify.Standard };
        Assert.True(ctx.VerifyScript(tx.Inputs[0].ScriptSig, coin0.TxOut.ScriptPubKey, new TransactionChecker(tx, 0, coin0.TxOut)), ctx.Error.ToString());

        // Input 1 is a P2PQH spend the daemon's rules accept after the fork and reject before it.
        Assert.Equal(PqScript.ScriptSigLength, tx.Inputs[1].ScriptSig.Length);
        Assert.Equal(PqInputVerdict.OK, PqInputVerifier.Verify(signed.Hex, 1, 200_000_000, pq0.ScriptPubKey, activated: true));
        Assert.Equal(PqInputVerdict.PRE_ACTIVATION, PqInputVerifier.Verify(signed.Hex, 1, 200_000_000, pq0.ScriptPubKey, activated: false));
        // … and it is bound to THIS transaction: a changed output invalidates it.
        var tampered = tx.Clone();
        tampered.Outputs[1].Value = Money.Satoshis(100_000_001);
        Assert.Equal(PqInputVerdict.PQ_SIG_INVALID, PqInputVerifier.Verify(tampered, 1, 200_000_000, pq0.ScriptPubKey, activated: true));

        // Deterministic: the same inputs sign to the same bytes.
        var again = LiteTransactionBuilder.BuildAndSign(utxos, pq1.Address(), 400_000_000, change,
            keyForAddress: _ => wallet.GetPrivateKey(0), pqKeyForAddress: _ => pq0);
        Assert.Equal(signed.Hex, again.Hex);
    }

    [Fact]
    public void Sweeping_a_pq_coin_to_a_legacy_address_has_no_change_and_verifies()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var pq0 = wallet.GetPqKey(0);
        var utxos = new List<LiteUtxo> { new(new string('c', 64), 0, 50_000_000, pq0.Address()) };
        var signed = LiteTransactionBuilder.BuildAndSign(utxos, wallet.GetReceiveAddress(1), 50_000_000, wallet.GetChangeAddress(0),
            keyForAddress: _ => throw new InvalidOperationException("no legacy inputs here"), pqKeyForAddress: _ => pq0);
        var tx = Transaction.Parse(signed.Hex, Net);
        Assert.False(signed.ChangeCreated);
        Assert.Single(tx.Outputs);
        Assert.Equal(PqInputVerdict.OK, PqInputVerifier.Verify(tx, 0, 50_000_000, pq0.ScriptPubKey, activated: true));
        Assert.Equal(PqScript.InputLength, tx.Inputs[0].ToBytes().Length);
    }

    [Fact]
    public void The_pq_path_refuses_too_many_pq_inputs_bad_scripts_and_missing_keys()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var pq0 = wallet.GetPqKey(0);
        var dest = wallet.GetReceiveAddress(1);
        var change = wallet.GetChangeAddress(0);
        Key NoKey(string _) => throw new InvalidOperationException("unused");

        // 27 post-quantum inputs exceed the standard-size limit (§4: 26 per transaction).
        var many = Enumerable.Range(0, 27).Select(i => new LiteUtxo(new string('d', 64), i, 1_000_000, pq0.Address())).ToList();
        var tooMany = Assert.Throws<InvalidOperationException>(() =>
            LiteTransactionBuilder.BuildAndSign(many, dest, 27_000_000, change, NoKey, pqKeyForAddress: _ => pq0));
        Assert.Contains("26", tooMany.Message);
        // 26 is fine (and stays under 100 KB).
        var ok = LiteTransactionBuilder.BuildAndSign(many.Take(26).ToList(), dest, 26_000_000, change, NoKey, pqKeyForAddress: _ => pq0);
        Assert.True(Transaction.Parse(ok.Hex, Net).GetSerializedSize() <= PqScript.MaxStandardTxBytes);

        // A stated script that disagrees with the address is refused before signing.
        var lying = new List<LiteUtxo> { new(new string('e', 64), 0, 1_000_000, pq0.Address(), Hex(wallet.GetPqKey(1).ScriptPubKey)) };
        Assert.Contains("does not match", Assert.Throws<InvalidOperationException>(() =>
            LiteTransactionBuilder.BuildAndSign(lying, dest, 1_000_000, change, NoKey, pqKeyForAddress: _ => pq0)).Message);

        // A key that doesn't own the input address is refused; no resolver at all is refused.
        var one = new List<LiteUtxo> { new(new string('f', 64), 0, 1_000_000, pq0.Address()) };
        Assert.Contains("does not own", Assert.Throws<InvalidOperationException>(() =>
            LiteTransactionBuilder.BuildAndSign(one, dest, 1_000_000, change, NoKey, pqKeyForAddress: _ => wallet.GetPqKey(1))).Message);
        Assert.Throws<InvalidOperationException>(() => LiteTransactionBuilder.BuildAndSign(one, dest, 1_000_000, change, NoKey));

        // A BQ change address is not supported (change stays legacy).
        Assert.Throws<ArgumentException>(() =>
            LiteTransactionBuilder.BuildAndSign(one, dest, 500_000, pq0.Address(), NoKey, pqKeyForAddress: _ => pq0));
    }

    [Fact]
    public void An_all_legacy_spend_still_takes_the_legacy_path_unchanged()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var utxos = new List<LiteUtxo> { new(new string('a', 64), 0, 100_000_000, wallet.GetReceiveAddress(0)) };
        var dest = wallet.GetReceiveAddress(1);
        var change = wallet.GetChangeAddress(0);
        // The PQ resolver is never consulted on a legacy spend (it would throw), and the
        // result is the TransactionBuilder's usual shape: P2PKH in, P2PKH out, builder-verified.
        // (Output order is the builder's own shuffle, so the two builds aren't compared as hex.)
        foreach (var signed in new[]
        {
            LiteTransactionBuilder.BuildAndSign(utxos, dest, 40_000_000, change, _ => wallet.GetPrivateKey(0)),
            LiteTransactionBuilder.BuildAndSign(utxos, dest, 40_000_000, change, _ => wallet.GetPrivateKey(0),
                pqKeyForAddress: _ => throw new InvalidOperationException("must not be consulted")),
        })
        {
            var tx = Transaction.Parse(signed.Hex, Net);
            Assert.True(signed.ChangeCreated);
            Assert.NotNull(PayToPubkeyHashTemplate.Instance.ExtractScriptSigParameters(tx.Inputs[0].ScriptSig));
            Assert.All(tx.Outputs, o => Assert.True(PayToPubkeyHashTemplate.Instance.CheckScriptPubKey(o.ScriptPubKey)));
            Assert.Equal([40_000_000L, 60_000_000L], tx.Outputs.Select(o => o.Value.Satoshi).OrderBy(v => v));
        }
    }

    // ── The wallet service: gate, ownership, reveal, persistence ──────────────────────────

    private static (LiteWalletService svc, AddressReader reader, CapturingRelay relay, InMemoryWalletStateStore state) Build(long? activation)
    {
        var reader = new AddressReader();
        var relay = new CapturingRelay();
        var state = new InMemoryWalletStateStore();
        var svc = new LiteWalletService(new MemoryVault(), reader, relay, stateStore: state) { PqActivationHeight = activation };
        return (svc, reader, relay, state);
    }

    [Fact]
    public async Task Paying_a_BQ_address_is_refused_until_the_fork_is_chosen_and_reached()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var bq = wallet.GetPqKey(3).Address();

        // No height chosen (today's chain constant).
        var (svc, reader, relay, _) = Build(activation: null);
        reader.Utxos[wallet.GetReceiveAddress(0)] = [Coin('a', 0, 100_000_000)];
        await svc.UnlockAsync();
        Assert.False(svc.PqActivated);
        var r = await svc.SendAsync(bq, 10_000_000);
        Assert.False(r.Success);
        Assert.Contains("post-quantum", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hasn't been set", r.Error);
        Assert.Empty(relay.Sent);
        Assert.Contains("hasn't been set", (await svc.RevealNextPqAddressAsync()).Error);

        // Height chosen but the known tip (109) is below it.
        var (svc2, reader2, relay2, _) = Build(activation: 5_000_000);
        reader2.Utxos[wallet.GetReceiveAddress(0)] = [Coin('a', 0, 100_000_000)];
        await svc2.UnlockAsync();
        var r2 = await svc2.SendAsync(bq, 10_000_000);
        Assert.False(r2.Success);
        Assert.Contains("5,000,000", r2.Error);
        Assert.Empty(relay2.Sent);
        Assert.Equal(109, svc2.KnownTipHeight);
        Assert.False(await svc2.IsPqActivatedAsync());
        Assert.False((await svc2.RevealNextPqAddressAsync()).Ok);
        Assert.Null(svc2.PqAddress);
    }

    [Fact]
    public async Task Once_activated_a_BQ_payment_broadcasts_a_P2PQH_output()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var bq = wallet.GetPqKey(3).Address();
        var (svc, reader, relay, _) = Build(activation: 100);
        reader.Utxos[wallet.GetReceiveAddress(0)] = [Coin('a', 0, 100_000_000)];
        await svc.UnlockAsync();

        var r = await svc.SendAsync(bq, 30_000_000);
        Assert.True(r.Success, r.Error);
        Assert.True(svc.PqActivated);

        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.Equal(PqScript.BuildP2pqh(PqAddress.Decode(bq)), tx.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal(30_000_000, tx.Outputs[0].Value.Satoshi);
        Assert.Equal(wallet.GetChangeAddress(0), tx.Outputs[1].ScriptPubKey.GetDestinationAddress(Net)!.ToString());
        Assert.Contains(bq, LiteTxDecoder.Decode(tx.ToHex())!.Outputs.Select(o => o.Address)); // the detail view names it
    }

    [Fact]
    public async Task Pq_coins_are_owned_only_after_activation_and_are_spent_with_ML_DSA()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var pq0 = wallet.GetPqKey(0);
        var legacy = wallet.GetReceiveAddress(0);

        // The wallet has one legacy coin and one coin on its first BQ address (revealed earlier).
        var (svc, reader, relay, state) = Build(activation: null);
        await state.SetPqAddressCountAsync(1);
        reader.Utxos[legacy] = [Coin('a', 0, 10_000_000)];
        reader.Utxos[pq0.Address()] = [Coin('b', 0, 40_000_000, Hex(pq0.ScriptPubKey))];
        await svc.UnlockAsync();
        Assert.Equal(1, svc.PqRevealedCount);
        Assert.Equal(pq0.Address(), svc.PqAddress);

        // Before the fork the PQ coin is invisible to the balance (it can't be moved).
        Assert.Equal(10_000_000, await svc.GetSpendableAsync());

        // After it, the coin is owned and a sweep spends it with a P2PQH scriptSig.
        svc.PqActivationHeight = 100;
        Assert.Equal(50_000_000, await svc.GetSpendableAsync());
        var r = await svc.SendMaxAsync(wallet.GetReceiveAddress(2));
        Assert.True(r.Success, r.Error);
        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.Equal(2, tx.Inputs.Count);
        var pqIn = tx.Inputs.Select((i, n) => (i, n)).Single(p => p.i.PrevOut.Hash == uint256.Parse(new string('b', 64))).n;
        Assert.Equal(PqInputVerdict.OK, PqInputVerifier.Verify(tx, pqIn, 40_000_000, pq0.ScriptPubKey, activated: true));
        Assert.Equal(50_000_000, tx.Outputs.Sum(o => o.Value.Satoshi)); // zero fee, no change on a sweep
    }

    [Fact]
    public async Task Revealing_BQ_addresses_is_gated_gap_limited_and_persisted()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var (svc, reader, _, state) = Build(activation: 100);
        reader.Utxos[wallet.GetReceiveAddress(0)] = [Coin('a', 0, 1_000_000)];
        await svc.UnlockAsync();
        Assert.Null(svc.PqAddress);
        Assert.Empty(svc.PqAddresses);

        var (ok, err) = await svc.RevealNextPqAddressAsync();
        Assert.True(ok, err);
        Assert.Equal(wallet.GetPqAddress(0), svc.PqAddress);
        Assert.StartsWith("BQ", svc.PqAddress);
        Assert.Equal(1, await state.GetPqAddressCountAsync());

        // Slot 0 unused → a second reveal is still fine (gap limit is 20), and it persists.
        Assert.True((await svc.RevealNextPqAddressAsync()).Ok);
        Assert.Equal([wallet.GetPqAddress(0), wallet.GetPqAddress(1)], svc.PqAddresses);
        Assert.Equal(2, await state.GetPqAddressCountAsync());

        // A fresh session over the same store comes back at the same position.
        var svc2 = new LiteWalletService(new MemoryVault(), reader, new CapturingRelay(), stateStore: state) { PqActivationHeight = 100 };
        await svc2.UnlockAsync();
        Assert.Equal(2, svc2.PqRevealedCount);
        await svc2.ResetAsync();
        Assert.Equal(0, await state.GetPqAddressCountAsync());
    }

    [Fact]
    public async Task Restore_rediscovers_used_BQ_addresses_once_the_fork_has_a_height()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var (svc, reader, _, state) = Build(activation: 100);
        reader.Used.Add(wallet.GetPqAddress(0));
        reader.Used.Add(wallet.GetPqAddress(2)); // an interior gap is scanned past
        await svc.RestoreWalletAsync(TestMnemonic);
        Assert.Equal(3, svc.PqRevealedCount);
        Assert.Equal(3, await state.GetPqAddressCountAsync());

        // Without a height nothing can have been revealed, so the PQ chain isn't scanned.
        var (svc2, reader2, _, state2) = Build(activation: null);
        reader2.Used.Add(wallet.GetPqAddress(0));
        await svc2.RestoreWalletAsync(TestMnemonic);
        Assert.Equal(0, svc2.PqRevealedCount);
        Assert.Equal(0, await state2.GetPqAddressCountAsync());
    }

    [Fact]
    public void Address_helpers_recognise_both_types()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var b = wallet.GetReceiveAddress(0);
        var bq = wallet.GetPqAddress(0);
        Assert.True(BlazecoinAddress.IsValid(b));
        Assert.True(BlazecoinAddress.IsValid(bq));
        Assert.True(BlazecoinAddress.IsLegacy(b));
        Assert.False(BlazecoinAddress.IsLegacy(bq));
        Assert.True(BlazecoinAddress.IsPostQuantum(bq));
        Assert.False(BlazecoinAddress.IsPostQuantum(b));
        Assert.False(BlazecoinAddress.IsValid("blz1qnothere"));
        Assert.Equal(wallet.GetPqKey(0).ScriptPubKey, BlazecoinAddress.ScriptPubKeyFor(bq));
        Assert.Null(BlazecoinAddress.ScriptPubKeyFor(bq, BlazecoinNetwork.Regtest));
        Assert.True(new LiteUtxo("t", 0, 1, bq).IsPostQuantum);
        Assert.False(new LiteUtxo("t", 0, 1, b).IsPostQuantum);
    }
}

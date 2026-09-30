using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The C1 fix's core: a txid is the double-SHA256 of its whole serialized tx, so it commits
/// to every output value. The verifier fetches the raw funding tx, checks it hashes to the
/// claimed txid, and returns the REAL output value — a lying indexer cannot make a tx that
/// hashes to txId yet carries a different value. (Merkle/PoW inclusion is exercised against
/// the real daemon in the regtest cross-check; here we test the value binding with locally
/// built txs and no proof requirement.)
/// </summary>
public class LiteTxVerifierTests
{
    /// <summary>A reader that serves a fixed raw tx for one txid and nothing else.</summary>
    private sealed class RawTxReader : IChainReader
    {
        private readonly string _txId;
        private readonly string _hex;
        public RawTxReader(Transaction tx) { _txId = tx.GetHash().ToString(); _hex = tx.ToHex(); }

        public bool SupportsChainVerification => true;
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) =>
            Task.FromResult<string?>(string.Equals(txId, _txId, StringComparison.OrdinalIgnoreCase) ? _hex : null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public bool SupportsHistory => true;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
    }

    /// <summary>A reader that lies: serves a DIFFERENT tx than the txid asked for.</summary>
    private sealed class SubstitutingReader : IChainReader
    {
        private readonly string _hex;
        public SubstitutingReader(Transaction actuallyServes) { _hex = actuallyServes.ToHex(); }
        public bool SupportsChainVerification => true;
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(_hex);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public bool SupportsHistory => true;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
    }

    /// <summary>Builds a simple funding tx paying <paramref name="sats"/> to output 0.</summary>
    private static Transaction FundingTx(long sats)
    {
        var tx = Transaction.Create(BlazecoinNetwork.Instance);
        var key = new Key();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(new TxOut(Money.Satoshis(sats), key.PubKey.GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(123), key.PubKey.Hash)); // a second output at index 1
        return tx;
    }

    [Fact]
    public async Task Verifies_the_real_value_from_the_txid_committed_hex()
    {
        var tx = FundingTx(5_000_000);
        var verifier = new LiteTxVerifier(new RawTxReader(tx));

        var v = await verifier.VerifyAsync(tx.GetHash().ToString(), 0, 5_000_000, requireInclusionProof: false);

        Assert.True(v.Verified);
        Assert.Equal(5_000_000, v.RealSatoshis);
    }

    [Fact]
    public async Task A_lying_claim_is_overridden_by_the_real_committed_value()
    {
        // THE C1 defence: indexer claims 1 BLZ, the txid-committed tx really pays 100 BLZ.
        // The verifier returns the REAL 100 BLZ so the wallet builds correct change and no
        // excess leaks to fee.
        var tx = FundingTx(100_000_000_00); // 100 BLZ
        var verifier = new LiteTxVerifier(new RawTxReader(tx));

        var v = await verifier.VerifyAsync(tx.GetHash().ToString(), 0, claimedSatoshis: 100_000_000, requireInclusionProof: false);

        Assert.True(v.Verified);
        Assert.Equal(100_000_000_00, v.RealSatoshis); // the real value, not the 1-BLZ claim
    }

    [Fact]
    public async Task Output_ownership_is_enforced_when_an_expected_address_is_given()
    {
        // M1: the funding output pays some random key; a send verifying it as one of OUR
        // addresses must be rejected (a lying indexer injected a third-party coin).
        var tx = FundingTx(5_000_000);
        var verifier = new LiteTxVerifier(new RawTxReader(tx));
        var trueOwner = tx.Outputs[0].ScriptPubKey.GetDestinationAddress(BlazecoinNetwork.Instance)!.ToString();
        var notOurs = LiteHdWallet.CreateNew().GetReceiveAddress(0);

        // Correct owner → passes.
        var ok = await verifier.VerifyAsync(tx.GetHash().ToString(), 0, 5_000_000, requireInclusionProof: false, expectedAddress: trueOwner);
        Assert.True(ok.Verified);

        // Wrong owner → rejected even though value + txid are genuine.
        var bad = await verifier.VerifyAsync(tx.GetHash().ToString(), 0, 5_000_000, requireInclusionProof: false, expectedAddress: notOurs);
        Assert.False(bad.Verified);
        Assert.Contains("not owned by your wallet", bad.Error);
    }

    [Fact]
    public async Task Ownership_is_a_script_byte_comparison_so_post_quantum_outputs_verify_too()
    {
        // A P2PQH output (0x20 <pqkh> OP_CHECKPQSIG) has no NBitcoin destination — ownership
        // is judged by comparing the output's script bytes with the script the wallet's own
        // BQ address commits to. Legacy "B…" addresses go through the same path.
        var key = Pq.PqKeyDerivation.Derive(new byte[32], 0);
        var otherKey = Pq.PqKeyDerivation.Derive(new byte[32], 1);
        var tx = Transaction.Create(BlazecoinNetwork.Instance);
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(new TxOut(Money.Satoshis(4_000_000), new Script(key.ScriptPubKey)));
        var verifier = new LiteTxVerifier(new RawTxReader(tx));
        var txId = tx.GetHash().ToString();

        var ok = await verifier.VerifyAsync(txId, 0, 4_000_000, requireInclusionProof: false, expectedAddress: key.Address());
        Assert.True(ok.Verified);
        Assert.Equal(4_000_000, ok.RealSatoshis);

        var wrongPq = await verifier.VerifyAsync(txId, 0, 4_000_000, requireInclusionProof: false, expectedAddress: otherKey.Address());
        Assert.False(wrongPq.Verified);
        var legacyClaim = await verifier.VerifyAsync(txId, 0, 4_000_000, requireInclusionProof: false,
            expectedAddress: LiteHdWallet.CreateNew().GetReceiveAddress(0));
        Assert.False(legacyClaim.Verified);
        // A regtest TQ address never owns a mainnet-verified output (and vice versa).
        var tq = await verifier.VerifyAsync(txId, 0, 4_000_000, requireInclusionProof: false,
            expectedAddress: key.Address(BlazecoinNetwork.Regtest));
        Assert.False(tq.Verified);

        // The address → script map itself.
        Assert.Equal(key.ScriptPubKey, LiteTxVerifier.ScriptForAddress(key.Address()));
        Assert.Null(LiteTxVerifier.ScriptForAddress("not an address"));
        var legacy = LiteHdWallet.Restore("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about").GetReceiveAddress(0);
        Assert.Equal(new BitcoinPubKeyAddress(legacy, BlazecoinNetwork.Instance).ScriptPubKey.ToBytes(), LiteTxVerifier.ScriptForAddress(legacy));
    }

    [Fact]
    public async Task A_substituted_transaction_that_doesnt_hash_to_the_txid_is_rejected()
    {
        // The reader serves a real tx, but we ask for a DIFFERENT txid — the hash won't match.
        var served = FundingTx(9_000_000);
        var verifier = new LiteTxVerifier(new SubstitutingReader(served));

        var v = await verifier.VerifyAsync(new string('b', 64), 0, 9_000_000, requireInclusionProof: false);

        Assert.False(v.Verified);
        Assert.Contains("does not hash", v.Error);
    }

    [Fact]
    public async Task A_missing_funding_tx_fails_closed()
    {
        var tx = FundingTx(1_000_000);
        var verifier = new LiteTxVerifier(new RawTxReader(tx));

        // Ask for a txid the reader doesn't have → null hex → fail (never silently proceed).
        var v = await verifier.VerifyAsync(new string('c', 64), 0, 1_000_000, requireInclusionProof: false);
        Assert.False(v.Verified);
    }

    [Fact]
    public async Task An_out_of_range_output_index_is_rejected()
    {
        var tx = FundingTx(1_000_000); // has outputs 0 and 1
        var verifier = new LiteTxVerifier(new RawTxReader(tx));

        var v = await verifier.VerifyAsync(tx.GetHash().ToString(), 9, 1_000_000, requireInclusionProof: false);
        Assert.False(v.Verified);
        Assert.Contains("output index", v.Error);
    }

    /// <summary>A reader with a spendable UTXO but NO funding tx — verification fails.</summary>
    private sealed class UnverifiableReader : IChainReader
    {
        public bool SupportsChainVerification => true;
        public bool SupportsHistory => true;
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiteChainUtxo>>([new LiteChainUtxo(new string('d', 64), 0, 10_000_000, 5, 100, false)]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
    }

    private sealed class RecordingRelay : ITxRelay
    {
        public int Calls;
        public Task<LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default)
        { Calls++; return Task.FromResult(LiteBroadcastResult.Ok("shouldnothappen")); }
    }

    private sealed class MemVault : ISeedVault
    {
        public string? S;
        public Task<bool> HasWalletAsync() => Task.FromResult(S != null);
        public Task SaveMnemonicAsync(string m) { S = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(S);
        public Task ClearAsync() { S = null; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Send_fails_closed_and_never_broadcasts_when_verification_fails()
    {
        // The wiring guarantee: if a selected input can't be verified against the chain, the
        // send is refused and NOTHING is broadcast — a lying/broken indexer cannot get an
        // unverified spend onto the wire.
        const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var reader = new UnverifiableReader();
        var relay = new RecordingRelay();
        var verifier = new LiteTxVerifier(reader);
        var svc = new LiteWalletService(new MemVault(), reader, relay, verifier: verifier);
        await svc.RestoreWalletAsync(mnemonic);

        var result = await svc.SendAsync(LiteHdWallet.Restore(mnemonic).GetReceiveAddress(5), 1_000_000);

        Assert.False(result.Success);
        Assert.Contains("verify", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, relay.Calls); // fail-closed — never broadcast
    }
}

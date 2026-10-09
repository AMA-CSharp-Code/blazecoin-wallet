using BlazecoinWallet.Lite;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The key/signing layer's teeth: the NBitcoin network definition is cross-checked against
/// the REAL chain (embedded genesis hashes to the chainparams genesis id), key derivation is
/// deterministic and pinned to a golden vector (a shipped wallet's restore path must never
/// drift), version bytes match the daemon, and a signed transaction actually satisfies its
/// input scripts.
/// </summary>
public class LiteKeySigningTests
{
    // The standard BIP39 test mnemonic — every wallet vendor's cross-check seed.
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void Network_genesis_hashes_to_the_chainparams_genesis_id()
    {
        var genesis = BlazecoinNetwork.Instance.GetGenesis();
        Assert.Equal(BlazecoinNetwork.GenesisHash, genesis.GetHash().ToString());
    }

    [Fact]
    public void Regtest_network_genesis_and_prefixes_match_the_daemon()
    {
        Assert.Equal(BlazecoinNetwork.RegtestGenesisHash, BlazecoinNetwork.Regtest.GetGenesis().GetHash().ToString());

        // Regtest P2PKH prefix 111 → 'm'/'n' addresses, distinct from mainnet 'B'.
        // Derived through the wallet class itself (audit F6): same keys, regtest encoding.
        var mainnet = LiteHdWallet.Restore(TestMnemonic);
        var regtest = LiteHdWallet.Restore(TestMnemonic, network: BlazecoinNetwork.Regtest);
        var payload = NBitcoin.DataEncoders.Encoders.Base58Check.DecodeData(regtest.GetReceiveAddress(0));
        Assert.Equal(111, payload[0]);
        // Identical underlying key — only the encoding differs.
        Assert.Equal(mainnet.GetPrivateKey(0).ToHex(), regtest.GetPrivateKey(0).ToHex());
    }

    [Fact]
    public void Addresses_are_p2pkh_version_26_starting_with_B()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var address = wallet.GetReceiveAddress(0);

        Assert.StartsWith("B", address);
        var payload = Encoders.Base58Check.DecodeData(address);
        Assert.Equal(26, payload[0]);          // daemon PUBKEY_ADDRESS prefix
        Assert.Equal(21, payload.Length);      // version + HASH160
    }

    [Fact]
    public void Wif_export_carries_version_154()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var wif = wallet.GetWif(0);

        var payload = Encoders.Base58Check.DecodeData(wif);
        Assert.Equal(154, payload[0]);         // daemon SECRET_KEY prefix (128 + 26)
        // Compressed-key WIF: version + 32-byte key + 0x01 compression flag.
        Assert.Equal(34, payload.Length);
        Assert.Equal(1, payload[^1]);
    }

    [Fact]
    public void Derivation_is_deterministic_and_pinned_to_the_golden_vector()
    {
        // m/44'/413'/0'/0/0 for the standard test mnemonic. PINNED: if this ever changes,
        // restores of shipped wallets would land on different addresses — a breaking event.
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var again = LiteHdWallet.Restore(TestMnemonic);

        Assert.Equal(wallet.GetReceiveAddress(0), again.GetReceiveAddress(0));
        Assert.Equal("Bgn3jBCv6MiVaKWyMHsgUfBi266fkZs5sM", wallet.GetReceiveAddress(0));
        Assert.NotEqual(wallet.GetReceiveAddress(0), wallet.GetReceiveAddress(1));
        Assert.NotEqual(wallet.GetReceiveAddress(0), wallet.GetChangeAddress(0));
        // A passphrase is a different wallet entirely (BIP39 semantics).
        Assert.NotEqual(wallet.GetReceiveAddress(0), LiteHdWallet.Restore(TestMnemonic, "x").GetReceiveAddress(0));
    }

    [Fact]
    public void New_wallets_roundtrip_through_their_mnemonic()
    {
        foreach (var words in new[] { 12, 24 })
        {
            var created = LiteHdWallet.CreateNew(words);
            Assert.Equal(words, created.MnemonicWords.Split(' ').Length);
            var restored = LiteHdWallet.Restore(created.MnemonicWords);
            Assert.Equal(created.GetReceiveAddress(0), restored.GetReceiveAddress(0));
        }
    }

    [Fact]
    public void Restore_rejects_a_bad_checksum()
    {
        var corrupted = TestMnemonic.Replace("about", "abandon");
        Assert.ThrowsAny<Exception>(() => LiteHdWallet.Restore(corrupted));
    }

    [Fact]
    public void Signed_transaction_satisfies_its_input_scripts_at_zero_fee()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var from = wallet.GetReceiveAddress(0);
        var dest = wallet.GetReceiveAddress(1);
        var change = wallet.GetChangeAddress(0);

        var utxo = new LiteUtxo(
            TxId: "7fc67b71000000000000000000000000000000000000000000000000000000aa",
            Vout: 0, Satoshis: 5_000_000, Address: from);

        var signed = LiteTransactionBuilder.BuildAndSign(
            new[] { utxo }, dest, amountSatoshis: 3_000_000, change,
            keyForAddress: _ => wallet.GetPrivateKey(0), feeSatoshis: 0);

        // BuildAndSign already ran TransactionBuilder.Verify (script execution over the
        // inputs) — reaching here means the signatures satisfy the P2PKH scripts. Check the
        // money conservation independently.
        var tx = Transaction.Parse(signed.Hex, BlazecoinNetwork.Instance);
        Assert.Equal(64, signed.TxId.Length);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(5_000_000, tx.Outputs.Sum(o => o.Value.Satoshi)); // zero fee: in == out
        Assert.Contains(tx.Outputs, o => o.Value.Satoshi == 3_000_000);
        Assert.Contains(tx.Outputs, o => o.Value.Satoshi == 2_000_000);
    }

    [Fact]
    public void Change_below_dust_is_folded_into_the_fee_not_an_output()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var from = wallet.GetReceiveAddress(0);

        var utxo = new LiteUtxo(new string('1', 64), 0, 1_000_400, from);
        var signed = LiteTransactionBuilder.BuildAndSign(
            new[] { utxo }, wallet.GetReceiveAddress(1), 1_000_000, wallet.GetChangeAddress(0),
            _ => wallet.GetPrivateKey(0));

        var tx = Transaction.Parse(signed.Hex, BlazecoinNetwork.Instance);
        Assert.Single(tx.Outputs);             // 400-sat change would be dust — became fee
        Assert.Equal(1_000_000, tx.Outputs[0].Value.Satoshi);
    }

    [Fact]
    public void Builder_refuses_a_fee_above_the_ceiling()
    {
        // Defence in depth (audit C1): a build whose fee would exceed MaxFeeSatoshis is
        // refused rather than burning coins to miners.
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var from = wallet.GetReceiveAddress(0);
        var utxo = new LiteUtxo(new string('9', 64), 0, 1_000_000_000, from);

        var ex = Assert.Throws<InvalidOperationException>(() => LiteTransactionBuilder.BuildAndSign(
            new[] { utxo }, wallet.GetReceiveAddress(1), 1_000_000, wallet.GetChangeAddress(0),
            _ => wallet.GetPrivateKey(0), feeSatoshis: LiteTransactionBuilder.MaxFeeSatoshis + 1));
        Assert.Contains("fee would be", ex.Message);
    }

    [Fact]
    public void Builder_rejects_bad_inputs()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var from = wallet.GetReceiveAddress(0);
        var dest = wallet.GetReceiveAddress(1);
        var change = wallet.GetChangeAddress(0);
        var utxo = new LiteUtxo(new string('2', 64), 0, 1_000_000, from);
        Key KeyFor(string _) => wallet.GetPrivateKey(0);

        // Insufficient funds.
        Assert.Throws<InvalidOperationException>(() => LiteTransactionBuilder.BuildAndSign(
            new[] { utxo }, dest, 2_000_000, change, KeyFor));
        // Dust send.
        Assert.Throws<ArgumentOutOfRangeException>(() => LiteTransactionBuilder.BuildAndSign(
            new[] { utxo }, dest, 100, change, KeyFor));
        // Foreign address (Bitcoin '1…') must be rejected, not paid.
        Assert.Throws<ArgumentException>(() => LiteTransactionBuilder.BuildAndSign(
            new[] { utxo }, "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa", 500_000, change, KeyFor));
        // No inputs.
        Assert.Throws<ArgumentException>(() => LiteTransactionBuilder.BuildAndSign(
            Array.Empty<LiteUtxo>(), dest, 500_000, change, KeyFor));
    }
}

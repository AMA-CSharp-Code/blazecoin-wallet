using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>A spendable output as served by the Indexer's per-address UTXO endpoint.</summary>
/// <param name="TxId">Funding transaction id (big-endian hex, as the explorer shows it).</param>
/// <param name="Vout">Output index in the funding transaction.</param>
/// <param name="Satoshis">Value in satoshis.</param>
/// <param name="Address">The P2PKH address that owns the output (its scriptPubKey is reconstructed from this).</param>
public sealed record LiteUtxo(string TxId, int Vout, long Satoshis, string Address);

/// <summary>The finished product of a client-side build: ready to POST to the broadcast relay.</summary>
/// <param name="TxId">The transaction id the network will know it by.</param>
/// <param name="Hex">Raw transaction hex for `sendrawtransaction`.</param>
/// <param name="ChangeCreated">True when a change output was actually paid to the change
/// address (false for a sweep, or when change folded into the fee as dust) — the wallet
/// advances its change-chain index only when this is true, so the chain has no gaps.</param>
public sealed record SignedTransaction(string TxId, string Hex, bool ChangeCreated);

/// <summary>
/// Builds and signs legacy-P2PKH transactions entirely client-side — the lite wallet has no
/// daemon to delegate to. Zero fees are the CHAIN'S normal case (minrelay 0 + block-min 0
/// shipped in the daemon binary), so fee defaults to 0 and any change below dust is added
/// to the fee instead of creating an unspendable output.
/// </summary>
public static class LiteTransactionBuilder
{
    /// <summary>Outputs below this many satoshis are treated as dust (daemon DUST_RELAY_TX_FEE derived floor).</summary>
    public const long DustSatoshis = 546;

    /// <summary>
    /// Hard ceiling on the fee any single transaction may pay (0.001 BLZ). Blazecoin is a
    /// zero-fee chain, so a build whose CLAIMED-value fee exceeds this is a bug (mis-computed
    /// change, or a runaway sub-dust fold) — refuse rather than burn coins to miners.
    /// NOTE the limit: this bounds the fee the builder can SEE (claimed inputs − outputs).
    /// It does NOT stop a coordinated lying indexer that UNDER-reports an input's value —
    /// that excess becomes real on-chain fee the builder can't observe. The only defences
    /// against a value lie are chain-verified input values (the merkle-proof roadmap item,
    /// security-gated before real-coin launch) or personal-node mode (own-chainstate UTXOs).
    /// </summary>
    public const long MaxFeeSatoshis = 100_000;

    /// <summary>
    /// Builds and signs a transaction spending <paramref name="utxos"/>: pays
    /// <paramref name="amountSatoshis"/> to <paramref name="destinationAddress"/>, returns the
    /// remainder (minus <paramref name="feeSatoshis"/>) to <paramref name="changeAddress"/>.
    /// <paramref name="keyForAddress"/> supplies the private key owning each input's address
    /// (the HD wallet knows which derivation slot an address came from).
    /// </summary>
    public static SignedTransaction BuildAndSign(
        IReadOnlyCollection<LiteUtxo> utxos,
        string destinationAddress,
        long amountSatoshis,
        string changeAddress,
        Func<string, Key> keyForAddress,
        long feeSatoshis = 0,
        Network? network = null)
    {
        if (utxos.Count == 0) throw new ArgumentException("No inputs to spend.", nameof(utxos));
        if (amountSatoshis <= 0) throw new ArgumentOutOfRangeException(nameof(amountSatoshis));
        if (amountSatoshis < DustSatoshis)
            throw new ArgumentOutOfRangeException(nameof(amountSatoshis), $"Send amount is below the {DustSatoshis}-satoshi dust floor.");
        if (feeSatoshis < 0) throw new ArgumentOutOfRangeException(nameof(feeSatoshis));

        // Mainnet unless the daemon cross-check harness passes regtest.
        network ??= BlazecoinNetwork.Instance;
        var dest = ParseP2pkh(destinationAddress, network, nameof(destinationAddress));
        var change = ParseP2pkh(changeAddress, network, nameof(changeAddress));

        var totalIn = utxos.Sum(u => u.Satoshis);
        var required = amountSatoshis + feeSatoshis;
        if (totalIn < required)
            throw new InvalidOperationException($"Inputs total {totalIn} sat but {required} sat are required.");

        var coins = new List<Coin>(utxos.Count);
        var keys = new List<Key>(utxos.Count);
        foreach (var u in utxos)
        {
            var owner = ParseP2pkh(u.Address, network, nameof(utxos));
            coins.Add(new Coin(
                new OutPoint(uint256.Parse(u.TxId), (uint)u.Vout),
                new TxOut(Money.Satoshis(u.Satoshis), owner.ScriptPubKey)));
            keys.Add(keyForAddress(u.Address)
                ?? throw new InvalidOperationException($"No private key supplied for input address {u.Address}."));
        }

        // Change below dust would be unspendable — fold it into the fee instead.
        var changeValue = totalIn - required;

        // Fee ceiling (defence in depth): the actual fee this tx pays is feeSatoshis, plus
        // any sub-dust change we're about to fold in. Refuse rather than burn coins to
        // miners if that exceeds MaxFeeSatoshis — catches change-calc bugs and a lying
        // indexer under-reporting an input value (whose excess would otherwise be fee).
        var effectiveFee = feeSatoshis + (changeValue < DustSatoshis ? Math.Max(0, changeValue) : 0);
        if (effectiveFee > MaxFeeSatoshis)
            throw new InvalidOperationException(
                $"Refusing to send: the fee would be {effectiveFee} sat (max {MaxFeeSatoshis}). " +
                "This can indicate stale or incorrect coin data — refresh and try again.");

        var builder = network.CreateTransactionBuilder();
        // Zero fee is Blazecoin's NORMAL case (daemon ships minrelay 0 + block-min 0) —
        // null (not FeeRate(0), which still floors at 1 sat) disables NBitcoin's
        // Bitcoin-policy fee check so Verify doesn't reject free sends.
        builder.StandardTransactionPolicy.MinRelayTxFee = null;
        builder.AddCoins(coins);
        builder.AddKeys(keys.ToArray());
        builder.Send(dest, Money.Satoshis(amountSatoshis));
        var changeCreated = changeValue >= DustSatoshis;
        if (changeCreated)
        {
            builder.Send(change, Money.Satoshis(changeValue));
            builder.SendFees(Money.Satoshis(feeSatoshis));
        }
        else
        {
            builder.SendFees(Money.Satoshis(feeSatoshis + changeValue));
        }

        var tx = builder.BuildTransaction(sign: true);
        if (!builder.Verify(tx, out var errors))
            throw new InvalidOperationException("Signed transaction failed verification: " +
                string.Join("; ", errors.Select(e => e.ToString())));

        return new SignedTransaction(tx.GetHash().ToString(), tx.ToHex(), changeCreated);
    }

    private static BitcoinPubKeyAddress ParseP2pkh(string address, Network network, string paramName)
    {
        try
        {
            // BitcoinPubKeyAddress enforces the P2PKH version byte (26) — a P2SH or foreign
            // address fails here rather than producing an output nobody can spend.
            return new BitcoinPubKeyAddress(address, network);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"'{address}' is not a valid Blazecoin P2PKH address.", paramName, ex);
        }
    }
}

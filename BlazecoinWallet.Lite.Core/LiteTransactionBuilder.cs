using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite;

/// <summary>A spendable output as served by the Indexer's per-address UTXO endpoint.</summary>
/// <param name="TxId">Funding transaction id (big-endian hex, as the explorer shows it).</param>
/// <param name="Vout">Output index in the funding transaction.</param>
/// <param name="Satoshis">Value in satoshis.</param>
/// <param name="Address">The address that owns the output — legacy P2PKH "B…" or post-quantum
/// "BQ…" (its scriptPubKey is reconstructed from this).</param>
/// <param name="ScriptPubKey">The output's scriptPubKey hex when the data source served it;
/// null → derived from <paramref name="Address"/>. When present it MUST agree with the address
/// (the builder checks), so a gateway can't steer a signature at the wrong template.</param>
public sealed record LiteUtxo(string TxId, int Vout, long Satoshis, string Address, string? ScriptPubKey = null)
{
    /// <summary>True when this coin sits on a P2PQH output (spending it needs an ML-DSA signature).</summary>
    public bool IsPostQuantum => Pq.PqAddress.TryDecode(Address) != null;
}

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
    /// A post-quantum destination (BQ…) or any post-quantum input routes to the hand-assembled
    /// path (<see cref="BuildAndSignWithPq"/>); an all-legacy spend takes the NBitcoin
    /// TransactionBuilder path below, byte-for-byte as before the fork work.
    /// </summary>
    /// <param name="pqKeyForAddress">Supplies the ML-DSA-44 key owning a BQ… input address;
    /// required only when a PQ input is present.</param>
    public static SignedTransaction BuildAndSign(
        IReadOnlyCollection<LiteUtxo> utxos,
        string destinationAddress,
        long amountSatoshis,
        string changeAddress,
        Func<string, Key> keyForAddress,
        long feeSatoshis = 0,
        Network? network = null,
        Func<string, Pq.PqKey>? pqKeyForAddress = null)
    {
        if (utxos.Count == 0) throw new ArgumentException("No inputs to spend.", nameof(utxos));

        if (Pq.PqAddress.TryDecode(destinationAddress) != null || utxos.Any(u => u.IsPostQuantum))
            return BuildAndSignWithPq(utxos, destinationAddress, amountSatoshis, changeAddress,
                keyForAddress, pqKeyForAddress, feeSatoshis, network ?? BlazecoinNetwork.Instance);

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

    // ── The post-quantum path (PQ_SIGNATURES.md §3) ─────────────────────────────────────

    /// <summary>
    /// Hand-assembles and signs a transaction that pays a P2PQH output and/or spends P2PQH
    /// coins — NBitcoin's TransactionBuilder knows neither OP_CHECKPQSIG nor PQSigHash, so
    /// this path builds the <see cref="Transaction"/> field by field: every legacy input is
    /// signed per-input with the legacy SIGHASH_ALL P2PKH signature (RFC 6979, low-S), every
    /// PQ input with the tagged BIP-143-shaped digest and a deterministic ML-DSA-44 signature
    /// under the "blazecoin-tx-v1" context. Legacy inputs are checked with NBitcoin's script
    /// interpreter, PQ inputs with <see cref="Pq.PqInputVerifier"/> (the daemon's rules), so
    /// nothing leaves here that a post-fork node would refuse. Change always returns to a
    /// legacy address. Same dust / fee-ceiling policy as the legacy path.
    /// </summary>
    internal static SignedTransaction BuildAndSignWithPq(
        IReadOnlyCollection<LiteUtxo> utxos,
        string destinationAddress,
        long amountSatoshis,
        string changeAddress,
        Func<string, Key> keyForAddress,
        Func<string, Pq.PqKey>? pqKeyForAddress,
        long feeSatoshis,
        Network network)
    {
        if (utxos.Count == 0) throw new ArgumentException("No inputs to spend.", nameof(utxos));
        if (amountSatoshis <= 0) throw new ArgumentOutOfRangeException(nameof(amountSatoshis));
        if (amountSatoshis < DustSatoshis)
            throw new ArgumentOutOfRangeException(nameof(amountSatoshis), $"Send amount is below the {DustSatoshis}-satoshi dust floor.");
        if (feeSatoshis < 0) throw new ArgumentOutOfRangeException(nameof(feeSatoshis));

        var destScript = BlazecoinAddress.ScriptPubKeyFor(destinationAddress, network)
            ?? throw new ArgumentException($"'{destinationAddress}' is not a valid Blazecoin address (B… or BQ…).", nameof(destinationAddress));
        var change = ParseP2pkh(changeAddress, network, nameof(changeAddress));

        var pqInputs = utxos.Count(u => u.IsPostQuantum);
        if (pqInputs > Pq.PqScript.MaxPqInputsPerStandardTx)
            throw new InvalidOperationException(
                $"A transaction can carry at most {Pq.PqScript.MaxPqInputsPerStandardTx} post-quantum inputs " +
                $"(each is about {Pq.PqScript.InputLength:N0} bytes); this one would need {pqInputs}. " +
                "Send a smaller amount, or move the coins in several transactions.");
        if (pqInputs > 0 && pqKeyForAddress == null)
            throw new InvalidOperationException("No post-quantum key resolver was supplied for the post-quantum inputs.");

        var totalIn = utxos.Sum(u => u.Satoshis);
        var required = amountSatoshis + feeSatoshis;
        if (totalIn < required)
            throw new InvalidOperationException($"Inputs total {totalIn} sat but {required} sat are required.");

        var changeValue = totalIn - required;
        var effectiveFee = feeSatoshis + (changeValue < DustSatoshis ? Math.Max(0, changeValue) : 0);
        if (effectiveFee > MaxFeeSatoshis)
            throw new InvalidOperationException(
                $"Refusing to send: the fee would be {effectiveFee} sat (max {MaxFeeSatoshis}). " +
                "This can indicate stale or incorrect coin data — refresh and try again.");
        var changeCreated = changeValue >= DustSatoshis;

        // ── Assemble ────────────────────────────────────────────────────────────────────
        var inputs = utxos.ToList();
        var prevScripts = new byte[inputs.Count][];
        var tx = network.CreateTransaction();
        for (var i = 0; i < inputs.Count; i++)
        {
            var u = inputs[i];
            var derived = BlazecoinAddress.ScriptPubKeyFor(u.Address, network)
                ?? throw new ArgumentException($"'{u.Address}' is not a valid Blazecoin address.", nameof(utxos));
            // The data source may state the script explicitly; it must be the address's own —
            // a signature commits to the script, so a wrong one would be an unspendable spend.
            if (u.ScriptPubKey != null && !Encoders.Hex.DecodeData(u.ScriptPubKey).AsSpan().SequenceEqual(derived))
                throw new InvalidOperationException($"The coin data's script for {u.Address} does not match the address — refusing to sign.");
            prevScripts[i] = derived;
            tx.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(u.TxId), (uint)u.Vout)));
        }
        tx.Outputs.Add(new TxOut(Money.Satoshis(amountSatoshis), new Script(destScript)));
        if (changeCreated) tx.Outputs.Add(new TxOut(Money.Satoshis(changeValue), change.ScriptPubKey));

        // ── Sign ────────────────────────────────────────────────────────────────────────
        // PQSigHash never covers scriptSigs and the legacy digest blanks every OTHER input's,
        // so the two kinds can be signed in any order into the same transaction.
        for (var i = 0; i < inputs.Count; i++)
        {
            var u = inputs[i];
            if (u.IsPostQuantum)
            {
                var key = pqKeyForAddress!(u.Address)
                    ?? throw new InvalidOperationException($"No post-quantum key supplied for input address {u.Address}.");
                if (!key.ScriptPubKey.AsSpan().SequenceEqual(prevScripts[i]))
                    throw new InvalidOperationException($"The post-quantum key supplied for {u.Address} does not own that address.");
                var digest = Pq.PqSigHash.Digest(tx, i, u.Satoshis, prevScripts[i]);
                var sig = Pq.MlDsa44.Sign(key.Private, digest);
                tx.Inputs[i].ScriptSig = new Script(Pq.PqScript.BuildScriptSig(sig, key.KeyBlob));
            }
            else
            {
                var key = keyForAddress(u.Address)
                    ?? throw new InvalidOperationException($"No private key supplied for input address {u.Address}.");
                var scriptCode = new Script(prevScripts[i]);
                var spent = new TxOut(Money.Satoshis(u.Satoshis), scriptCode);
                var hash = tx.GetSignatureHash(scriptCode, i, SigHash.All, spent, HashVersion.Original);
                var sig = new TransactionSignature(key.Sign(hash), SigHash.All);
                tx.Inputs[i].ScriptSig = PayToPubkeyHashTemplate.Instance.GenerateScriptSig(sig, key.PubKey);
            }
        }

        // ── Verify (fail closed) ────────────────────────────────────────────────────────
        for (var i = 0; i < inputs.Count; i++)
        {
            var u = inputs[i];
            if (u.IsPostQuantum)
            {
                var verdict = Pq.PqInputVerifier.Verify(tx, i, u.Satoshis, prevScripts[i], activated: true);
                if (verdict != Pq.PqInputVerdict.OK)
                    throw new InvalidOperationException($"Signed transaction failed verification: post-quantum input {i} {verdict}.");
            }
            else
            {
                var coin = new Coin(tx.Inputs[i].PrevOut, new TxOut(Money.Satoshis(u.Satoshis), new Script(prevScripts[i])));
                var checker = new TransactionChecker(tx, i, coin.TxOut);
                var ctx = new ScriptEvaluationContext { ScriptVerify = ScriptVerify.Standard };
                if (!ctx.VerifyScript(tx.Inputs[i].ScriptSig, coin.TxOut.ScriptPubKey, checker))
                    throw new InvalidOperationException($"Signed transaction failed verification: input {i} {ctx.Error}.");
            }
        }
        var size = tx.GetSerializedSize();
        if (size > Pq.PqScript.MaxStandardTxBytes)
            throw new InvalidOperationException($"The transaction would be {size:N0} bytes, above the {Pq.PqScript.MaxStandardTxBytes:N0}-byte standard limit — move the coins in several transactions.");

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

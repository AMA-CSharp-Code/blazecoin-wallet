using BlazecoinWallet.Lite.Data;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// A block of genuine-looking payments to the standard test wallet, MINED at the chain's
/// minimum difficulty — so the wallet's real verifier accepts its merkle proofs with no test
/// seam in the verifier (the read-path verification tests need coins that actually verify).
/// <para>The transactions and header are built deterministically; only the nonce was found by
/// work, once (about a million scrypt hashes), and is pinned here. Changing ANY transaction
/// changes the merkle root and needs a new nonce: set BLZ_GRIND=1 and run
/// <see cref="ReadPathVerificationTests.Regrind_the_fixture_nonce"/>, which prints it.</para>
/// <para>Without a header feed (the web wallet's shape) such a proof is all the verifier asks
/// for; with one, the block must also be on the verified chain, which this block is not.</para>
/// </summary>
internal static class MinedBlockFixture
{
    public const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    public const long BlockHeight = 4_300_000;
    public const uint Bits = 0x1e0fffff;                  // powLimit — the easiest target the chain allows

    /// <summary>The nonce that makes this block's scrypt hash meet <see cref="Bits"/>.</summary>
    public const uint Nonce = 2_035_639_739;

    public static readonly string Receive0 = LiteHdWallet.Restore(Mnemonic).GetReceiveAddress(0);
    public static readonly string Receive1 = LiteHdWallet.Restore(Mnemonic).GetReceiveAddress(1);

    /// <summary>Someone else's address (a fixed key) — a real payment that is not ours.</summary>
    public static readonly string Foreign =
        new Key(Encoders.Hex.DecodeData("11".PadLeft(64, '1')))
            .GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString();

    /// <summary>What each transaction pays: (address, satoshis) per output.</summary>
    private static readonly (string Address, long Sats)[][] Plan =
    [
        [(Receive0, 1_000_000_000)],                       // 0: 10 BLZ to receive[0]
        [(Receive0, 900_000_000)],                         // 1
        [(Receive0, 800_000_000)],                         // 2
        [(Receive0, 700_000_000)],                         // 3
        [(Receive0, 600_000_000)],                         // 4
        [(Receive0, 500_000_000)],                         // 5
        [(Receive0, 400_000_000)],                         // 6
        [(Receive0, 300_000_000)],                         // 7
        [(Receive0, 200_000_000)],                         // 8
        [(Receive0, 100_000_000)],                         // 9: 1 BLZ
        [(Receive1, 250_000_000)],                         // 10: to receive[1]
        [(Foreign, 5_000_000_000)],                        // 11: a real payment to someone else
        [(Receive0, 30_000_000), (Receive0, 20_000_000)],  // 12: two outputs to us (0.5 BLZ in all)
    ];

    private static readonly Lazy<(Block Block, Transaction[] Txs)> Built = new(Build);

    private static (Block, Transaction[]) Build()
    {
        var txs = Plan.Select((outputs, i) =>
        {
            var tx = Transaction.Create(BlazecoinNetwork.Instance);
            tx.Inputs.Add(new TxIn(new OutPoint(new uint256((ulong)(i + 1)), 0)));
            foreach (var (address, sats) in outputs)
                tx.Outputs.Add(new TxOut(Money.Satoshis(sats), new Script(LiteTxVerifier.ScriptForAddress(address)!)));
            return tx;
        }).ToArray();

        var block = BlazecoinNetwork.Instance.Consensus.ConsensusFactory.CreateBlock();
        foreach (var tx in txs) block.Transactions.Add(tx);
        block.UpdateMerkleRoot();
        block.Header.Bits = new Target(Bits);
        block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        block.Header.Nonce = Nonce;
        return (block, txs);
    }

    public static Block Block => Built.Value.Block;

    /// <summary>The i-th transaction of the plan.</summary>
    public static Transaction Tx(int index) => Built.Value.Txs[index];
    public static string TxId(int index) => Tx(index).GetHash().ToString();
    public static int Count => Plan.Length;

    /// <summary>The raw hex of a transaction in the block; null for any other txid.</summary>
    public static string? RawHex(string txId) =>
        Built.Value.Txs.FirstOrDefault(t => string.Equals(t.GetHash().ToString(), txId, StringComparison.OrdinalIgnoreCase))?.ToHex();

    /// <summary>The merkle inclusion proof of a transaction in the block; null for any other txid.</summary>
    public static string? ProofHex(string txId)
    {
        var tx = Built.Value.Txs.FirstOrDefault(t => string.Equals(t.GetHash().ToString(), txId, StringComparison.OrdinalIgnoreCase));
        return tx == null ? null : Encoders.Hex.EncodeData(new MerkleBlock(Block, [tx.GetHash()]).ToBytes());
    }
}

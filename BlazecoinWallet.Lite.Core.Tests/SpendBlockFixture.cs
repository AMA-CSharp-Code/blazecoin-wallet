using BlazecoinWallet.Lite.Data;
using NBitcoin;
using NBitcoin.DataEncoders;
using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// A LATER block, also genuinely mined at the chain's minimum difficulty, whose transactions
/// SPEND coins of <see cref="MinedBlockFixture"/> — so the spent-coin tests (bug-bounty report
/// 2026-11-06, sixth round) can hand the wallet's real verifier a real proof that a coin is gone,
/// with no test seam in the verifier.
/// <para>Built deterministically; only the nonce was found by work and is pinned here. Changing
/// any transaction needs a new nonce: set BLZ_GRIND=spend and run
/// <see cref="ReadPathVerificationTests.Regrind_the_spend_fixture_nonce"/>, which prints it.</para>
/// <para>The inputs are unsigned: the wallet judges a spend by the block that mined it, not by
/// evaluating the script (see <see cref="LiteTxVerifier.VerifySpendAsync"/>).</para>
/// </summary>
internal static class SpendBlockFixture
{
    public const long BlockHeight = F.BlockHeight + 50;

    /// <summary>The nonce that makes this block's scrypt hash meet <see cref="F.Bits"/>.</summary>
    public const uint Nonce = 2304072627;

    /// <summary>Which fixture output each transaction consumes: (fixture tx index, vout).</summary>
    private static readonly (int Tx, int Vout)[] Plan =
    [
        (0, 0),     // 0: spends the 10 BLZ coin paid to receive[0]
        (12, 1),    // 1: spends the SECOND output of the two-output payment
        (11, 0),    // 2: spends the 50 BLZ coin on the foreign (old-key) address
        (10, 0),    // 3: spends the coin on receive[1]
    ];

    private static readonly Lazy<(Block Block, Transaction[] Txs)> Built = new(Build);

    private static (Block, Transaction[]) Build()
    {
        var sink = new Script(LiteTxVerifier.ScriptForAddress(
            new Key(Encoders.Hex.DecodeData("22".PadLeft(64, '2'))).GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString())!);
        var txs = Plan.Select(p =>
        {
            var tx = Transaction.Create(BlazecoinNetwork.Instance);
            tx.Inputs.Add(new TxIn(new OutPoint(F.Tx(p.Tx).GetHash(), p.Vout)));
            tx.Outputs.Add(new TxOut(F.Tx(p.Tx).Outputs[p.Vout].Value, sink));
            return tx;
        }).ToArray();

        var block = BlazecoinNetwork.Instance.Consensus.ConsensusFactory.CreateBlock();
        foreach (var tx in txs) block.Transactions.Add(tx);
        block.UpdateMerkleRoot();
        block.Header.HashPrevBlock = F.Block.Header.GetHash();
        block.Header.Bits = new Target(F.Bits);
        block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_790_001_500);
        block.Header.Nonce = Nonce;
        return (block, txs);
    }

    public static Block Block => Built.Value.Block;

    /// <summary>The i-th spending transaction of the plan.</summary>
    public static Transaction Tx(int index) => Built.Value.Txs[index];
    public static string TxId(int index) => Tx(index).GetHash().ToString();

    /// <summary>The raw hex of a transaction in this block; null for any other txid.</summary>
    public static string? RawHex(string txId) =>
        Built.Value.Txs.FirstOrDefault(t => string.Equals(t.GetHash().ToString(), txId, StringComparison.OrdinalIgnoreCase))?.ToHex();

    /// <summary>The merkle inclusion proof of a transaction in this block; null for any other txid.</summary>
    public static string? ProofHex(string txId)
    {
        var tx = Built.Value.Txs.FirstOrDefault(t => string.Equals(t.GetHash().ToString(), txId, StringComparison.OrdinalIgnoreCase));
        return tx == null ? null : Encoders.Hex.EncodeData(new MerkleBlock(Block, [tx.GetHash()]).ToBytes());
    }
}

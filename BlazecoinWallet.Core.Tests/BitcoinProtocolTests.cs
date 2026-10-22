using BlazecoinWallet.Core.Services.Mining;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Vector tests for <see cref="BitcoinProtocol"/> — the solo-miner's stateless protocol
/// primitives (varint, double-SHA256, merkle, nBits→target, header serialization, coinbase
/// builder, base58check address decode). Several vectors are cross-checked against the REAL
/// Blazecoin mainnet genesis from the daemon's <c>chainparams.cpp</c>, so this suite pins the
/// wallet's header serialization + hashing against the live chain (a regression here would mean
/// the miner assembles blocks the daemon rejects).
/// </summary>
public class BitcoinProtocolTests
{
    // ---- VarInt ----
    [Theory]
    [InlineData(0x0UL, "00")]
    [InlineData(0xfcUL, "fc")]              // last single-byte value
    [InlineData(0xfdUL, "fdfd00")]          // first 3-byte (0xFD prefix) value, LE
    [InlineData(0xffffUL, "fdffff")]        // last 3-byte value
    [InlineData(0x10000UL, "fe00000100")]   // first 5-byte (0xFE) value
    [InlineData(0xffffffffUL, "feffffffff")]// last 5-byte value
    [InlineData(0x100000000UL, "ff0000000001000000")] // first 9-byte (0xFF)
    public void VarInt_encodes_each_size_class(ulong n, string expectedHex)
        => Assert.Equal(expectedHex, BitcoinProtocol.BytesToHex(BitcoinProtocol.VarInt(n)));

    // ---- Hex round-trip ----
    [Fact]
    public void Hex_round_trips_and_lowercases()
    {
        var bytes = new byte[] { 0x00, 0x0f, 0xab, 0xFF };
        Assert.Equal("000fabff", BitcoinProtocol.BytesToHex(bytes));
        Assert.Equal(bytes, BitcoinProtocol.HexToBytes("000FABFF")); // case-insensitive in
    }

    [Fact]
    public void HexToBytes_empty_is_empty_and_odd_length_throws()
    {
        Assert.Empty(BitcoinProtocol.HexToBytes(""));
        Assert.Throws<ArgumentException>(() => BitcoinProtocol.HexToBytes("abc"));
    }

    [Fact]
    public void Reverse_does_not_mutate_the_input()
    {
        var input = new byte[] { 1, 2, 3, 4 };
        var reversed = BitcoinProtocol.Reverse(input);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, reversed);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, input); // original untouched (clone semantics)
    }

    // ---- Double-SHA256 (known vectors) ----
    [Fact]
    public void DoubleSha256_matches_known_vectors()
    {
        Assert.Equal("5df6e0e2761359d30a8275058e299fcc0381534545f55cf43e41983f5d4c9456",
            BitcoinProtocol.BytesToHex(BitcoinProtocol.DoubleSha256(Array.Empty<byte>())));
        Assert.Equal("4f8b42c22dd3729b519ba6f68d2da7cc5b2d606d05daed5ad5128cc03e6c6358",
            BitcoinProtocol.BytesToHex(BitcoinProtocol.DoubleSha256(System.Text.Encoding.ASCII.GetBytes("abc"))));
    }

    // ---- Merkle root ----
    [Fact]
    public void Merkle_single_leaf_is_the_leaf()
    {
        var leaf = Enumerable.Repeat((byte)0x11, 32).ToArray();
        Assert.Equal(leaf, BitcoinProtocol.ComputeMerkleRoot(new List<byte[]> { leaf }));
    }

    [Fact]
    public void Merkle_two_leaves_is_double_sha_of_concat()
    {
        var a = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var b = Enumerable.Repeat((byte)0x22, 32).ToArray();
        Assert.Equal("1140b574afee3cb89a4db3dc8037acfa856f5112e68a954e3ca0a908082c98ba",
            BitcoinProtocol.BytesToHex(BitcoinProtocol.ComputeMerkleRoot(new List<byte[]> { a, b })));
    }

    [Fact]
    public void Merkle_odd_count_duplicates_the_last_leaf()
    {
        var a = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var b = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var c = Enumerable.Repeat((byte)0x33, 32).ToArray();
        // level0 [a,b,c] -> pad to [a,b,c,c] -> [dsha(ab), dsha(cc)] -> dsha(...)
        Assert.Equal("cacd895c5e82f37a37b6f4923c214ca6089e5f7b075b9fca7e11e782a0f3f5e6",
            BitcoinProtocol.BytesToHex(BitcoinProtocol.ComputeMerkleRoot(new List<byte[]> { a, b, c })));
    }

    [Fact]
    public void Merkle_empty_throws()
        => Assert.Throws<ArgumentException>(() => BitcoinProtocol.ComputeMerkleRoot(new List<byte[]>()));

    // ---- nBits -> target ----
    [Fact]
    public void TargetFromBits_decodes_genesis_bits()
    {
        // 0x1e0ffff0 is the Blazecoin mainnet genesis nBits (chainparams.cpp).
        Assert.Equal("000000000000000000000000000000000000000000000000000000f0ff0f0000",
            BitcoinProtocol.BytesToHex(BitcoinProtocol.TargetFromBits(0x1e0ffff0)));
    }

    [Theory]
    [InlineData(0x03123456u, 0, (byte)0x56)] // exponent 3: mantissa lands at LSB
    [InlineData(0x03123456u, 1, (byte)0x34)]
    [InlineData(0x03123456u, 2, (byte)0x12)]
    public void TargetFromBits_low_exponent_places_mantissa_at_lsb(uint bits, int index, byte expected)
        => Assert.Equal(expected, BitcoinProtocol.TargetFromBits(bits)[index]);

    // ---- MeetsTarget ----
    [Fact]
    public void MeetsTarget_true_when_equal_or_below_false_when_above()
    {
        var target = new byte[32]; target[31] = 0x10;          // high byte = 0x10
        var equal = (byte[])target.Clone();
        var below = new byte[32]; below[31] = 0x0f;            // smaller high byte
        var above = new byte[32]; above[31] = 0x11;            // larger high byte

        Assert.True(BitcoinProtocol.MeetsTarget(equal, target));
        Assert.True(BitcoinProtocol.MeetsTarget(below, target));
        Assert.False(BitcoinProtocol.MeetsTarget(above, target));
    }

    [Fact]
    public void MeetsTarget_compares_from_the_high_order_byte_down()
    {
        // Hash has a bigger low byte but a smaller high byte -> still meets (high byte wins).
        var target = new byte[32]; target[31] = 0x05; target[0] = 0x00;
        var hash = new byte[32];   hash[31] = 0x04;   hash[0] = 0xff;
        Assert.True(BitcoinProtocol.MeetsTarget(hash, target));
    }

    // ---- Header serialization (cross-checked vs the daemon genesis) ----
    [Fact]
    public void SerializeHeader_reproduces_the_mainnet_genesis_header_and_block_id()
    {
        // Values straight out of chainparams.cpp CreateGenesisBlock(1400442576, 595109, 0x1e0ffff0, 1, ...).
        var prev = new byte[32]; // all zero
        var merkleInternal = BitcoinProtocol.Reverse(BitcoinProtocol.HexToBytes(
            "76dac14606eb13388518e7581062b3d4aec84feb58d015a83c532bc5349618b8"));

        var header = BitcoinProtocol.SerializeHeader(
            version: 1, prevHash: prev, merkleRoot: merkleInternal,
            time: 1400442576, bits: 0x1e0ffff0, nonce: 595109);

        Assert.Equal(80, header.Length);
        Assert.Equal(
            "010000000000000000000000000000000000000000000000000000000000000000000000" +
            "b8189634c52b533ca815d058eb4fc8aed4b3621058e718853813eb0646c1da76d00e7953f0ff0f1ea5140900",
            BitcoinProtocol.BytesToHex(header));

        // The block id is DoubleSHA256(header) shown in reverse (display) byte order.
        var blockId = BitcoinProtocol.BytesToHex(BitcoinProtocol.Reverse(BitcoinProtocol.DoubleSha256(header)));
        Assert.Equal("5d871c1b6ea542c2bb8a3b3ac70028a591bbf81369e90c2446c1a2bbfb89459b", blockId);
    }

    [Fact]
    public void SerializeHeader_rejects_wrong_length_hashes()
    {
        Assert.Throws<ArgumentException>(() =>
            BitcoinProtocol.SerializeHeader(1, new byte[31], new byte[32], 0, 0, 0));
        Assert.Throws<ArgumentException>(() =>
            BitcoinProtocol.SerializeHeader(1, new byte[32], new byte[33], 0, 0, 0));
    }

    // ---- Coinbase builder ----
    [Fact]
    public void BuildCoinbaseTx_produces_the_expected_serialization()
    {
        var spk = BitcoinProtocol.HexToBytes("76a914" + new string('0', 40) + "88ac"); // P2PKH to zeros
        var tx = BitcoinProtocol.BuildCoinbaseTx(5000000000L, spk, new byte[] { 0xAA, 0xBB });
        Assert.Equal(
            "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff" +
            "0302aabbffffffff0100f2052a010000001976a914000000000000000000000000000000000000000088ac00000000",
            BitcoinProtocol.BytesToHex(tx));
    }

    [Fact]
    public void BuildCoinbaseTx_rejects_an_oversize_extranonce()
        => Assert.Throws<ArgumentException>(() =>
            BitcoinProtocol.BuildCoinbaseTx(1, new byte[] { 0x00 }, new byte[101]));

    // ---- Address decode ----
    [Theory]
    [InlineData("BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31z", "76a914000000000000000000000000000000000000000088ac")]
    [InlineData("BTt1goArr9xPyiJmuCVqZ3DpTVMKkBT1dL", "76a9140102030405060708090a0b0c0d0e0f101112131488ac")]
    public void AddressToP2PKH_builds_the_25_byte_script(string address, string expectedScriptHex)
    {
        var script = BitcoinProtocol.AddressToP2PKH(address);
        Assert.Equal(25, script.Length);
        Assert.Equal(expectedScriptHex, BitcoinProtocol.BytesToHex(script));
    }

    [Fact]
    public void AddressToP2PKH_rejects_a_bad_checksum()
    {
        // Same address with the last char mangled -> checksum mismatch.
        Assert.Throws<FormatException>(() =>
            BitcoinProtocol.AddressToP2PKH("BTngbpkVTh3nGGdFdufHcG5TN7hXYuX31y"));
    }

    [Fact]
    public void AddressToP2PKH_rejects_an_invalid_base58_character()
    {
        // '0' (zero) is not in the base58 alphabet.
        Assert.Throws<FormatException>(() =>
            BitcoinProtocol.AddressToP2PKH("B0ngbpkVTh3nGGdFdufHcG5TN7hXYuX31z"));
    }
}

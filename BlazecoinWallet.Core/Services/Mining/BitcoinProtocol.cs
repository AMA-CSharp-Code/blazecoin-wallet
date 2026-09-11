using System.Security.Cryptography;

namespace BlazecoinWallet.Core.Services.Mining;

/// <summary>
/// Bitcoin/Blazecoin protocol primitives needed for solo mining: varint
/// encoding, double-SHA256, base58check, merkle root, target decoding,
/// header serialization, and a minimal coinbase-tx builder. All routines
/// are stateless and side-effect-free.
/// </summary>
public static class BitcoinProtocol
{
    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    public static byte[] DoubleSha256(byte[] data) => SHA256.HashData(SHA256.HashData(data));

    public static byte[] VarInt(ulong n)
    {
        if (n < 0xFD) return new[] { (byte)n };
        if (n <= 0xFFFF)
        {
            var b = new byte[3];
            b[0] = 0xFD;
            BitConverter.TryWriteBytes(b.AsSpan(1), (ushort)n);
            return b;
        }
        if (n <= 0xFFFFFFFF)
        {
            var b = new byte[5];
            b[0] = 0xFE;
            BitConverter.TryWriteBytes(b.AsSpan(1), (uint)n);
            return b;
        }
        var bb = new byte[9];
        bb[0] = 0xFF;
        BitConverter.TryWriteBytes(bb.AsSpan(1), n);
        return bb;
    }

    /// <summary>Parse a hex string into bytes (case-insensitive, no spaces).</summary>
    public static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) throw new ArgumentException("Hex length must be even", nameof(hex));
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return result;
    }

    public static string BytesToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Reverse a byte array (used for txid display-order ↔ internal-order conversion).</summary>
    public static byte[] Reverse(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        Array.Reverse(copy);
        return copy;
    }

    /// <summary>
    /// Compute the merkle root from a list of leaf hashes (in internal byte
    /// order). If the count is odd at any level, duplicates the last hash —
    /// this is the Bitcoin convention and the CVE-2012-2459 quirk that all
    /// derived chains inherit.
    /// </summary>
    public static byte[] ComputeMerkleRoot(List<byte[]> leaves)
    {
        if (leaves.Count == 0) throw new ArgumentException("Need at least one leaf", nameof(leaves));
        var level = new List<byte[]>(leaves);
        while (level.Count > 1)
        {
            if (level.Count % 2 == 1) level.Add(level[^1]);
            var next = new List<byte[]>(level.Count / 2);
            for (int i = 0; i < level.Count; i += 2)
            {
                var pair = new byte[64];
                Buffer.BlockCopy(level[i], 0, pair, 0, 32);
                Buffer.BlockCopy(level[i + 1], 0, pair, 32, 32);
                next.Add(DoubleSha256(pair));
            }
            level = next;
        }
        return level[0];
    }

    /// <summary>
    /// Decode a "nBits" compact-form target (4 bytes, big-endian in the
    /// integer sense — high byte = exponent) into a 32-byte target value
    /// in internal (little-endian) byte order, ready for direct comparison
    /// with a hashed result.
    /// </summary>
    public static byte[] TargetFromBits(uint bits)
    {
        int exponent = (int)(bits >> 24);
        uint mantissa = bits & 0x00FFFFFF;
        var target = new byte[32];
        // Place the 3-byte mantissa at position (exponent - 3) from the LSB end.
        // Bitcoin's compact form is big-endian in concept; the result we want
        // here is little-endian (LSB at index 0).
        if (exponent <= 3)
        {
            mantissa >>= 8 * (3 - exponent);
            target[0] = (byte)mantissa;
            target[1] = (byte)(mantissa >> 8);
            target[2] = (byte)(mantissa >> 16);
        }
        else
        {
            int shift = exponent - 3;
            target[shift]     = (byte)mantissa;
            target[shift + 1] = (byte)(mantissa >> 8);
            target[shift + 2] = (byte)(mantissa >> 16);
        }
        return target;
    }

    /// <summary>
    /// Compare a hash (32 bytes, little-endian internal order) to a target
    /// (same encoding). Returns true if hash &lt;= target as a 256-bit
    /// unsigned integer.
    /// </summary>
    public static bool MeetsTarget(byte[] hash, byte[] target)
    {
        // Compare from the high-order byte (index 31) down. First mismatch wins.
        for (int i = 31; i >= 0; i--)
        {
            if (hash[i] < target[i]) return true;
            if (hash[i] > target[i]) return false;
        }
        return true; // exactly equal
    }

    /// <summary>
    /// Serialize a Bitcoin/Blazecoin block header to 80 bytes:
    ///   version(4 LE) | prevHash(32) | merkleRoot(32) | time(4 LE) | bits(4 LE) | nonce(4 LE)
    /// prevHash and merkleRoot are passed in internal byte order (already reversed
    /// from the display-hex representation).
    /// </summary>
    public static byte[] SerializeHeader(int version, byte[] prevHash, byte[] merkleRoot, uint time, uint bits, uint nonce)
    {
        if (prevHash.Length != 32) throw new ArgumentException("prevHash must be 32 bytes");
        if (merkleRoot.Length != 32) throw new ArgumentException("merkleRoot must be 32 bytes");
        var header = new byte[80];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), version);
        Buffer.BlockCopy(prevHash, 0, header, 4, 32);
        Buffer.BlockCopy(merkleRoot, 0, header, 36, 32);
        BitConverter.TryWriteBytes(header.AsSpan(68, 4), time);
        BitConverter.TryWriteBytes(header.AsSpan(72, 4), bits);
        BitConverter.TryWriteBytes(header.AsSpan(76, 4), nonce);
        return header;
    }

    /// <summary>
    /// Build a coinbase transaction paying the given output script. The
    /// scriptSig holds an arbitrary "extraNonce" buffer — bumping this is
    /// how miners get fresh merkle roots once they exhaust the 4-byte
    /// header nonce. Returns the serialized tx bytes.
    ///
    /// Format:
    ///   version(4 LE) | inputCount(varint=1)
    ///   | prevoutHash(32 zeros) | prevoutIndex(4 LE = 0xFFFFFFFF)
    ///   | scriptSigLen(varint) | scriptSig(...)
    ///   | sequence(4 LE = 0xFFFFFFFF)
    ///   | outputCount(varint=1)
    ///   | value(8 LE) | scriptPubKeyLen(varint) | scriptPubKey
    ///   | lockTime(4 LE = 0)
    /// </summary>
    public static byte[] BuildCoinbaseTx(long valueSat, byte[] scriptPubKey, byte[] extraNonce)
    {
        if (extraNonce.Length > 100) throw new ArgumentException("extraNonce too large");
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // Version
        w.Write((int)1);
        // Input count
        w.Write(VarInt(1));
        // Prevout: all-zero hash + index 0xFFFFFFFF
        w.Write(new byte[32]);
        w.Write((uint)0xFFFFFFFF);
        // scriptSig: push-length byte + raw extranonce bytes (height encoding skipped
        // because Blazecoin V2 has BIP34 NEVER_ACTIVE).
        var scriptSig = new byte[extraNonce.Length + 1];
        scriptSig[0] = (byte)extraNonce.Length;
        Buffer.BlockCopy(extraNonce, 0, scriptSig, 1, extraNonce.Length);
        w.Write(VarInt((ulong)scriptSig.Length));
        w.Write(scriptSig);
        // Sequence
        w.Write((uint)0xFFFFFFFF);
        // Output count
        w.Write(VarInt(1));
        // Value (satoshis, 8 bytes LE)
        w.Write(valueSat);
        // scriptPubKey
        w.Write(VarInt((ulong)scriptPubKey.Length));
        w.Write(scriptPubKey);
        // LockTime
        w.Write((uint)0);

        return ms.ToArray();
    }

    /// <summary>
    /// Decode a Blazecoin legacy address (base58check, version byte 26 for 'B'
    /// addresses) into the 20-byte HASH160 pubkey hash, and return the
    /// 25-byte P2PKH scriptPubKey: OP_DUP OP_HASH160 &lt;20&gt; ... OP_EQUALVERIFY OP_CHECKSIG.
    /// </summary>
    public static byte[] AddressToP2PKH(string address)
    {
        var decoded = Base58CheckDecode(address);
        if (decoded.Length != 21) throw new FormatException($"Expected 21-byte address payload, got {decoded.Length}");
        // First byte is the version (Blazecoin mainnet = 26); next 20 are the HASH160.
        var hash160 = new byte[20];
        Buffer.BlockCopy(decoded, 1, hash160, 0, 20);

        var script = new byte[25];
        script[0] = 0x76; // OP_DUP
        script[1] = 0xA9; // OP_HASH160
        script[2] = 0x14; // push 20 bytes
        Buffer.BlockCopy(hash160, 0, script, 3, 20);
        script[23] = 0x88; // OP_EQUALVERIFY
        script[24] = 0xAC; // OP_CHECKSIG
        return script;
    }

    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static byte[] Base58Decode(string s)
    {
        var bigInt = System.Numerics.BigInteger.Zero;
        foreach (var c in s)
        {
            int idx = Base58Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"Invalid base58 character: {c}");
            bigInt = bigInt * 58 + idx;
        }
        // BigInteger.ToByteArray returns little-endian; we want big-endian.
        var leadingZeros = 0;
        foreach (var c in s)
        {
            if (c == '1') leadingZeros++;
            else break;
        }
        var bytes = bigInt.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[leadingZeros + bytes.Length];
        Buffer.BlockCopy(bytes, 0, result, leadingZeros, bytes.Length);
        return result;
    }

    private static byte[] Base58CheckDecode(string s)
    {
        var decoded = Base58Decode(s);
        if (decoded.Length < 4) throw new FormatException("Base58check payload too short");
        var payload = new byte[decoded.Length - 4];
        Buffer.BlockCopy(decoded, 0, payload, 0, payload.Length);
        var checksum = new byte[4];
        Buffer.BlockCopy(decoded, payload.Length, checksum, 0, 4);
        var computed = DoubleSha256(payload);
        for (int i = 0; i < 4; i++)
        {
            if (computed[i] != checksum[i]) throw new FormatException("Base58check checksum mismatch");
        }
        return payload;
    }
}

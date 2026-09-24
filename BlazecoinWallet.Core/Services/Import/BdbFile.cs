using System.Buffers.Binary;

namespace BlazecoinWallet.Core.Services.Import;

/// <summary>Just enough Berkeley DB btree header handling to import legacy wallets on a
/// daemon built without BDB. Core 28 can migrate an UNLOADED legacy wallet through its
/// read-only BerkeleyRO reader, but that reader accepts only data-file version 9 (what
/// BDB 4.8 writes). A V1.5 built against a newer BDB — the macOS port links Homebrew's
/// 18.1 — rewrites <c>wallet.dat</c> as version 10, which BerkeleyRO refuses with
/// "Unsupported BDB data file version number". The 9→10 bump came with BLOB support and
/// only appended meta-page fields; the page layout a wallet actually uses is unchanged,
/// so rewriting the version field on every btree meta page (page 0 AND each
/// subdatabase's) yields a file BDB 4.8's own <c>db_verify</c> accepts and whose
/// <c>db_dump</c> matches the original record-for-record (verified 2026-09-23).</summary>
public static class BdbFile
{
    public const uint BtreeMagic = 0x00053162;
    public const uint SupportedVersion = 9;
    private const int BtreeMetaPageType = 9;

    /// <summary>The btree version recorded on page 0, or 0 if the file isn't a readable
    /// Berkeley DB btree (missing, too short, wrong magic).</summary>
    public static uint ReadMetaVersion(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[32];
            var read = fs.Read(header, 0, header.Length);
            if (read < header.Length) return 0;
            return ParseMeta(header, 0, out _) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Rewrites the version field of every btree meta page in <paramref name="file"/>
    /// to <see cref="SupportedVersion"/>, in place. Returns how many pages were changed;
    /// 0 when the buffer isn't a BDB btree or already reports version 9 everywhere.</summary>
    public static int NormaliseMetaVersions(byte[] file)
    {
        if (ParseMeta(file, 0, out var bigEndian) is null) return 0;
        var pageSize = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(20))
            : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0) return 0;

        var patched = 0;
        for (long offset = 0; offset + 26 <= file.Length; offset += pageSize)
        {
            var off = (int)offset;
            var version = ParseMeta(file, off, out _);
            if (version is null || file[off + 25] != BtreeMetaPageType || version == SupportedVersion) continue;
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(off + 16), SupportedVersion);
            else BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(off + 16), SupportedVersion);
            patched++;
        }
        return patched;
    }

    /// <summary>restorewallet on a daemon built --without-bdb: RPC -18 with this text
    /// (walletdb.cpp). The macOS and Linux daemons are built that way.</summary>
    public static bool LooksLikeNoBdbSupport(string? msg)
        => msg != null
        && msg.Contains("Berkeley DB", StringComparison.OrdinalIgnoreCase)
        && (msg.Contains("not support", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("not compiled", StringComparison.OrdinalIgnoreCase));

    // Meta page: lsn(8) pgno(4) magic(4)@12 version(4)@16 pagesize(4)@20 encrypt(1) type(1)@25 …
    private static uint? ParseMeta(byte[] b, int off, out bool bigEndian)
    {
        bigEndian = false;
        if (b.Length < off + 26) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off + 12)) == BtreeMagic)
            return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off + 16));
        if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 12)) == BtreeMagic)
        {
            bigEndian = true;
            return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 16));
        }
        return null;
    }
}

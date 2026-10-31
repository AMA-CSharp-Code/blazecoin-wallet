namespace BlazecoinWallet.Lite.Data;

/// <summary>
/// Sparse trusted points on the chain: the block hash at every <see cref="Interval"/>-th height
/// from genesis up to <see cref="MaxHeight"/>, shipped in the binary (harvested from a synced
/// node by <c>Tools/Build-HeaderAnchors.ps1</c>). Trusted exactly like the shipped checkpoint.
/// They let the wallet prove ANY block is on the chain cheaply: fetch the headers from the block
/// up to the next anchor and check each one's prev-hash links to the one before — a SHA256d chain
/// ending on a trusted hash cannot be forged, whatever the headers' difficulty (bug-bounty report
/// 2026-10-25: a standalone proof's own PoW is cheap to fake). At most one interval of headers
/// (~80 KB) per check, and no per-block storage.
/// </summary>
public sealed class HeaderAnchors
{
    /// <summary>Spacing of the shipped mainnet table — must match the harvest's -Interval.</summary>
    public const int MainnetInterval = 1000;

    private const string MainnetResource = "BlazecoinWallet.Lite.Data.mainnet-header-anchors.bin";
    private static readonly Lazy<HeaderAnchors> _mainnet = new(LoadMainnet);

    private readonly byte[] _hashes; // 32 bytes per anchor, display (RPC) byte order; anchor i = height i × Interval

    private HeaderAnchors(byte[] hashes, int interval)
    {
        if (interval <= 0) throw new ArgumentOutOfRangeException(nameof(interval));
        if (hashes.Length % 32 != 0) throw new ArgumentException("Anchor data must be whole 32-byte hashes.", nameof(hashes));
        _hashes = hashes;
        Interval = interval;
    }

    /// <summary>The shipped mainnet table (embedded resource).</summary>
    public static HeaderAnchors Mainnet => _mainnet.Value;

    /// <summary>A table from raw 32-byte hashes (heights 0, interval, 2×interval, …).</summary>
    public static HeaderAnchors FromBytes(byte[] hashes, int interval = MainnetInterval) => new((byte[])hashes.Clone(), interval);

    /// <summary>Anchor spacing in blocks.</summary>
    public int Interval { get; }

    /// <summary>Number of anchors in the table.</summary>
    public int Count => _hashes.Length / 32;

    /// <summary>The highest anchored height; −1 when the table is empty.</summary>
    public long MaxHeight => Count == 0 ? -1 : (Count - 1L) * Interval;

    /// <summary>The anchored hash at <paramref name="height"/>, or null when that height isn't an anchor.</summary>
    public string? HashAt(long height)
    {
        if (height < 0 || height % Interval != 0 || height > MaxHeight) return null;
        return Convert.ToHexString(_hashes, (int)(height / Interval) * 32, 32).ToLowerInvariant();
    }

    /// <summary>The lowest anchored height ≥ <paramref name="height"/>, or null past <see cref="MaxHeight"/>.</summary>
    public long? NextAtOrAbove(long height)
    {
        if (height > MaxHeight) return null;
        if (height <= 0) return 0;
        return (height + Interval - 1) / Interval * Interval;
    }

    /// <summary>A copy truncated to anchors at or below <paramref name="maxHeight"/> (tests, and
    /// rehearsing a table that predates the wallet's verified tip).</summary>
    public HeaderAnchors UpTo(long maxHeight)
    {
        var keep = maxHeight < 0 ? 0 : (int)Math.Min(Count, maxHeight / Interval + 1);
        return new HeaderAnchors(_hashes[..(keep * 32)], Interval);
    }

    private static HeaderAnchors LoadMainnet()
    {
        using var stream = typeof(HeaderAnchors).Assembly.GetManifestResourceStream(MainnetResource)
            ?? throw new InvalidOperationException($"Embedded resource {MainnetResource} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new HeaderAnchors(buffer.ToArray(), MainnetInterval);
    }
}

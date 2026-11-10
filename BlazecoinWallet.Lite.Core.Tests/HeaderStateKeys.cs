using System.Security.Cryptography;
using System.Text;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Core.Tests;

/// <summary>
/// Seal keys for a <see cref="HeaderChainSync"/> built by hand in a test. In the app the wallet
/// service attaches its own (<see cref="HeaderChainSync.StateKeys"/>); a chain with none attached
/// neither adopts nor writes stored state (audit 2026-11-09), so a test about what survives a
/// restart attaches these and reads the store through them.
/// </summary>
internal static class HeaderStateKeys
{
    /// <summary>A fixed key per purpose — "this wallet", for every chain in a test.</summary>
    public static byte[]? Keys(string purpose) => SHA256.HashData(Encoding.UTF8.GetBytes("test wallet/" + purpose));

    /// <summary>A stored tip as this wallet would have written it.</summary>
    public static string SealedTip(string checkpoint) => SealedValue.Seal(Keys(HeaderChainSync.CheckpointSealPurpose)!, checkpoint);

    /// <summary>The stored tip when this wallet wrote it; null otherwise.</summary>
    public static string? OpenTip(string? stored) =>
        SealedValue.TryOpen(Keys(HeaderChainSync.CheckpointSealPurpose)!, stored, out var checkpoint) ? checkpoint : null;

    /// <summary>The stored anchors when this wallet wrote them; null otherwise.</summary>
    public static string? OpenAnchors(string? stored) =>
        SealedValue.TryOpen(Keys(HeaderChainSync.AnchorsSealPurpose)!, stored, out var anchors) ? anchors : null;
}

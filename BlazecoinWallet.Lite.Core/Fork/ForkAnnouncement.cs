using System.Text;
using System.Text.RegularExpressions;
using BlazecoinWallet.Lite.Data;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Fork;

/// <summary>
/// What the wallet will accept from a gateway's fork announcement, and how an announcement
/// proves it comes from the Blazecoin project (bug-bounty report 2026-11-06).
/// <para>The announcement arrives in an unauthenticated status response from a gateway the
/// wallet does not trust. Before this, that response alone could (a) stop every send and
/// (b) put text of the gateway's choosing into the wallet's own alert, next to "restore from
/// your recovery phrase". So:</para>
/// <list type="bullet">
/// <item>every field is held to a narrow shape, or dropped — no free text reaches the banner;</item>
/// <item>a fork this build does not follow cannot be announced at or below the build's own
/// shipped checkpoint (the build was made after that block and follows the chain there);</item>
/// <item>only an announcement SIGNED by a project key pinned in the build can stop sends. An
/// unsigned one is shown as a warning, labelled as the gateway's word.</item>
/// </list>
/// </summary>
public static partial class ForkAnnouncement
{
    /// <summary>
    /// The project's fork-announcement public keys (compressed secp256k1, hex), pinned in the
    /// build. An announcement signed by EITHER key is authentic. Two keys, created 2026-11-12:
    /// the first is the one used; the second is a spare kept apart from it, so losing the first
    /// does not leave the project unable to stop sends in builds already installed.
    /// <para>The private keys are held offline by the project and are in no repository. Sign
    /// with <c>Tools/ForkAnnouncementSigner</c>. A build only trusts the keys it shipped with:
    /// changing this list changes nothing for wallets already installed.</para>
    /// </summary>
    public static IReadOnlyList<string> PinnedKeys { get; } =
    [
        "028231e51223f74606b9545305bad166d498c358f46c9e9bd204146e87eae55efb",   // key 1, primary
        "03d81f8b4006c1f897ba1af3ae6c381cc432daddd8ead8036a21237c8c69e992b7",   // key 2, spare
    ];

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,32}\z")]
    private static partial Regex NameShape();

    [GeneratedRegex(@"\A[0-9]{1,5}(\.[0-9]{1,5}){0,3}\z")]
    private static partial Regex VersionShape();

    /// <summary>The fork codename when it is a plain token (letters, digits, - and _, at most
    /// 32); null otherwise — an announcement without a usable name is no announcement.</summary>
    public static string? CleanName(string? name)
    {
        var s = name?.Trim();
        return s != null && NameShape().IsMatch(s) ? s : null;
    }

    /// <summary>The minimum client version when it is digits and dots (an optional leading "v"
    /// is dropped); null otherwise, so the banner says "the latest version" instead of printing
    /// whatever the gateway sent.</summary>
    public static string? CleanVersion(string? version)
    {
        var s = version?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s[0] is 'v' or 'V') s = s[1..];
        return VersionShape().IsMatch(s) ? s : null;
    }

    /// <summary>
    /// True when <paramref name="forkHeight"/> is a height a fork could still be announced at for
    /// this build: positive, and — for a fork the build does not follow — above the build's
    /// shipped checkpoint. A fork the build DOES follow may sit anywhere (it is history).
    /// </summary>
    public static bool IsPlausibleHeight(long? forkHeight, string forkName, IReadOnlySet<string> supportedForks,
        long shippedCheckpointHeight)
    {
        if (forkHeight is not long h || h <= 0) return false;
        if (supportedForks.Any(f => string.Equals(f, forkName, StringComparison.OrdinalIgnoreCase))) return true;
        return h > shippedCheckpointHeight;
    }

    /// <summary>The height of the checkpoint this build ships — the last block the build is
    /// known to follow the chain to, whatever anyone tells it later.</summary>
    public static long ShippedCheckpointHeight => HeaderChainSync.MainnetCheckpoint.Height;

    // ── Signature ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The exact bytes a signature covers: a fixed domain line, then the three announced values in
    /// their canonical form (name lower-cased, version cleaned, a missing version as the empty
    /// string). Line-based and versioned so a different message can never be confused with it.
    /// </summary>
    public static byte[] MessageBytes(string forkName, long forkHeight, string? minClientVersion) =>
        Encoding.UTF8.GetBytes(
            "Blazecoin fork announcement v1\n" +
            $"name={forkName.Trim().ToLowerInvariant()}\n" +
            $"height={forkHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
            $"minClientVersion={CleanVersion(minClientVersion) ?? string.Empty}");

    private static uint256 MessageHash(string forkName, long forkHeight, string? minClientVersion) =>
        Hashes.DoubleSHA256(MessageBytes(forkName, forkHeight, minClientVersion));

    /// <summary>
    /// True when <paramref name="signatureHex"/> (DER, hex) is a valid signature of the announcement
    /// by one of <paramref name="pinnedKeys"/>. False for a missing or malformed signature, an
    /// unknown signer, an empty key list, or a signature over different values — any change to the
    /// name, the height or the version invalidates it.
    /// </summary>
    public static bool IsSigned(string? forkName, long? forkHeight, string? minClientVersion,
        string? signatureHex, IReadOnlyList<string> pinnedKeys)
    {
        if (pinnedKeys.Count == 0 || string.IsNullOrWhiteSpace(signatureHex)) return false;
        if (CleanName(forkName) is not string name || forkHeight is not long height || height <= 0) return false;
        var sigText = signatureHex.Trim();
        if (sigText.Length > 160) return false;           // a DER ECDSA signature is at most 72 bytes

        ECDSASignature signature;
        try
        {
            if (!ECDSASignature.IsValidDER(Encoders.Hex.DecodeData(sigText))) return false;
            signature = ECDSASignature.FromDER(Encoders.Hex.DecodeData(sigText));
        }
        catch { return false; }

        var hash = MessageHash(name, height, minClientVersion);
        foreach (var keyHex in pinnedKeys)
        {
            try
            {
                if (new PubKey(keyHex).Verify(hash, signature)) return true;
            }
            catch { /* a malformed pinned key verifies nothing */ }
        }
        return false;
    }

    /// <summary>Signs an announcement with the project's announcement key (the operator's side —
    /// here so the wallet and the signer can never disagree about the message). Returns the DER
    /// signature as hex, the value a gateway serves as <c>forkSignature</c>.</summary>
    public static string Sign(Key key, string forkName, long forkHeight, string? minClientVersion)
    {
        var name = CleanName(forkName) ?? throw new ArgumentException("The fork name must be a plain token.", nameof(forkName));
        if (forkHeight <= 0) throw new ArgumentOutOfRangeException(nameof(forkHeight));
        if (!string.IsNullOrWhiteSpace(minClientVersion) && CleanVersion(minClientVersion) == null)
            throw new ArgumentException("The minimum client version must be digits and dots.", nameof(minClientVersion));
        return Encoders.Hex.EncodeData(key.Sign(MessageHash(name, forkHeight, minClientVersion)).ToDER());
    }
}

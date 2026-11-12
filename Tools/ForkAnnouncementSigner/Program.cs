using BlazecoinWallet.Lite.Fork;
using NBitcoin;
using NBitcoin.DataEncoders;

// The operator's side of the fork-announcement channel. The wallet's side, and the message
// format both sides share, is BlazecoinWallet.Lite.Core/Fork/ForkAnnouncement.cs.
//
// A private key never appears on the command line or on the screen: it is written to, and read
// from, a file. Only public keys and signatures are printed.

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) return Usage();
    try
    {
        var opts = Options(args.Skip(1).ToArray());
        return args[0].ToLowerInvariant() switch
        {
            "newkey" => NewKey(opts),
            "pubkey" => PubKeyOf(opts),
            "sign" => Sign(opts),
            "verify" => Verify(opts),
            "pinned" => Pinned(),
            _ => Usage(),
        };
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or FormatException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 2;
    }
}

static int Usage()
{
    Console.Error.WriteLine("""
        ForkAnnouncementSigner - the Blazecoin project's fork-announcement key tool

          newkey --out <file>
              Create a key. The private key goes to <file> (which must not exist); the public
              key is printed. Pin the public key in ForkAnnouncement.PinnedKeys.

          pubkey --key-file <file>
              Print the public key of the private key in <file>.

          sign --key-file <file> --name <fork> --height <n> [--min-version <x.y.z>]
              Sign an announcement. Prints the signature a gateway serves as Fork:Signature,
              and says whether THIS BUILD's pinned keys accept it.

          verify --name <fork> --height <n> [--min-version <x.y.z>] --signature <hex>
              Check a signature against this build's pinned keys.

          pinned
              List the public keys pinned in this build.
        """);
    return 1;
}

static Dictionary<string, string> Options(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            throw new ArgumentException($"Expected --option value pairs, got '{args[i]}'.");
        map[args[i][2..]] = args[++i];
    }
    return map;
}

static string Need(Dictionary<string, string> o, string name) =>
    o.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
        ? v
        : throw new ArgumentException($"--{name} is required.");

static Key LoadKey(string path)
{
    var hex = File.ReadAllText(path).Trim();
    if (hex.Length != 64) throw new FormatException("The key file must hold 64 hex characters and nothing else.");
    return new Key(Encoders.Hex.DecodeData(hex));
}

static (string Name, long Height, string? MinVersion) Announcement(Dictionary<string, string> o)
{
    if (!long.TryParse(Need(o, "height"), out var height))
        throw new ArgumentException("--height must be a whole number.");
    o.TryGetValue("min-version", out var minVersion);
    return (Need(o, "name"), height, string.IsNullOrWhiteSpace(minVersion) ? null : minVersion);
}

static int NewKey(Dictionary<string, string> o)
{
    var path = Path.GetFullPath(Need(o, "out"));
    using var key = new Key();

    // Prove the pair works before anything is written: sign a throwaway announcement and check
    // it the way the wallet will. The signature is discarded - it is never printed or stored.
    var pub = key.PubKey.ToHex();
    var probe = ForkAnnouncement.Sign(key, "keycheck", 1, null);
    if (!ForkAnnouncement.IsSigned("keycheck", 1, null, probe, [pub]))
        throw new ArgumentException("The new key failed its own sign-and-verify check; nothing was written.");

    // CreateNew: never overwrite a key that already exists.
    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    using (var writer = new StreamWriter(file))
        writer.Write(Encoders.Hex.EncodeData(key.ToBytes()));

    // Read it back, so a bad write is found now and not on the day of a fork.
    using var reread = LoadKey(path);
    if (reread.PubKey.ToHex() != pub)
        throw new IOException("The key file did not read back as the key that was written.");

    Console.WriteLine("private key file : " + path);
    Console.WriteLine("public key       : " + pub);
    return 0;
}

static int PubKeyOf(Dictionary<string, string> o)
{
    using var key = LoadKey(Need(o, "key-file"));
    Console.WriteLine(key.PubKey.ToHex());
    return 0;
}

static int Sign(Dictionary<string, string> o)
{
    var (name, height, minVersion) = Announcement(o);
    using var key = LoadKey(Need(o, "key-file"));
    var signature = ForkAnnouncement.Sign(key, name, height, minVersion);
    var accepted = ForkAnnouncement.IsSigned(name, height, minVersion, signature, ForkAnnouncement.PinnedKeys);

    Console.WriteLine("signer public key : " + key.PubKey.ToHex());
    Console.WriteLine("Fork:Name         : " + ForkAnnouncement.CleanName(name));
    Console.WriteLine("Fork:Height       : " + height);
    Console.WriteLine("Fork:MinVersion   : " + (ForkAnnouncement.CleanVersion(minVersion) ?? "(none)"));
    Console.WriteLine("Fork:Signature    : " + signature);
    Console.WriteLine(accepted
        ? "accepted by this build's pinned keys: YES"
        : "accepted by this build's pinned keys: NO - this key is not pinned; wallets will ignore the signature");
    return accepted ? 0 : 3;
}

static int Verify(Dictionary<string, string> o)
{
    var (name, height, minVersion) = Announcement(o);
    var ok = ForkAnnouncement.IsSigned(name, height, minVersion, Need(o, "signature"), ForkAnnouncement.PinnedKeys);
    Console.WriteLine(ok ? "VALID - signed by a pinned key" : "NOT VALID for this build's pinned keys");
    return ok ? 0 : 3;
}

static int Pinned()
{
    if (ForkAnnouncement.PinnedKeys.Count == 0) Console.WriteLine("(no key is pinned in this build)");
    foreach (var k in ForkAnnouncement.PinnedKeys) Console.WriteLine(k);
    return 0;
}

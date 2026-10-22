using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>
/// The node calls behind "Create a post-quantum wallet". Every call names its wallet: the new wallet
/// is never the window's active one, and it is unloaded again as soon as its first BQ… address and
/// its backup exist.
/// </summary>
public interface IPostQuantumWalletRpc
{
    /// <summary><c>listwalletdir</c> — every wallet the node could load, so a name is never reused.</summary>
    Task<IReadOnlyList<string>> ListWalletNamesAsync(CancellationToken ct = default);
    /// <summary><c>createwallet</c>: descriptor, not blank, not loaded at startup; encrypted at creation
    /// when <paramref name="passphrase"/> is given, unencrypted when it is null.</summary>
    Task CreatePostQuantumWalletAsync(string name, string? passphrase, CancellationToken ct = default);
    /// <summary><c>getwalletinfo</c> of the named wallet: true when it is encrypted (it reports
    /// <c>unlocked_until</c>), false when not, null when the node could not say.</summary>
    Task<bool?> IsEncryptedInAsync(string wallet, CancellationToken ct = default);
    /// <summary><c>getnewaddress &lt;label&gt; "pq"</c> in the named wallet.</summary>
    Task<PqAddressResult> GetNewPostQuantumAddressInAsync(string wallet, string label, CancellationToken ct = default);
    /// <summary><c>getaddressinfo</c> → <c>ismine</c> in the named wallet; null when the node could not say.</summary>
    Task<bool?> IsMineInAsync(string wallet, string address, CancellationToken ct = default);
    /// <summary><c>backupwallet</c> of the named wallet to a file on this machine.</summary>
    Task BackupWalletToAsync(string wallet, string destination, CancellationToken ct = default);
    /// <summary><c>unloadwallet</c>.</summary>
    Task UnloadWalletByNameAsync(string wallet, CancellationToken ct = default);
}

/// <summary>The filesystem the creator checks the backup against — a seam so every outcome is testable.</summary>
public interface IBackupFileInspector
{
    bool Exists(string path);
    void EnsureDirectoryFor(string path);
    /// <summary>Size and SHA-256 (upper-case hex) of a written backup, or null when it is missing or empty.</summary>
    (long Bytes, string Sha256)? Inspect(string path);
}

public sealed class BackupFileInspector : IBackupFileInspector
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public void EnsureDirectoryFor(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public (long Bytes, string Sha256)? Inspect(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return null;
        using var stream = info.OpenRead();
        return (info.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }
}

/// <summary>What the user typed into the "Create a post-quantum wallet" form. <paramref name="Encrypt"/>
/// is on by default; turning it off is a deliberate choice for a wallet that must sign unattended
/// (like a payout hot wallet) — the passphrase fields are then ignored and never sent.</summary>
public sealed record PqWalletRequest(string Name, string Passphrase, string ConfirmPassphrase, string BackupPath, bool Encrypt = true)
{
    /// <summary>The passphrase <c>createwallet</c> receives: null for an unencrypted wallet.</summary>
    public string? EffectivePassphrase => Encrypt ? Passphrase : null;

    public const int MinPassphraseLength = 12;
    public const int MaxNameLength = 64;
    public const string DefaultName = "post-quantum";
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    /// <summary><c>&lt;documents&gt;\Blazecoin Wallet Backups\&lt;name&gt;-&lt;yyyyMMdd-HHmmss&gt;.dat</c>.</summary>
    public static string DefaultBackupPath(string name, DateTime now, string documentsDir)
    {
        var safe = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        return Path.Combine(documentsDir, "Blazecoin Wallet Backups", $"{safe}-{now:yyyyMMdd-HHmmss}.dat");
    }

    /// <summary>
    /// Every rule, one message each, checked before anything touches the node. The passphrase rules
    /// come from this project's own history: printable ASCII only (a 2019 wallet passphrase was
    /// lost for years partly to an encoding question), no double quote (PowerShell 5.1 and the
    /// Console page both mangle it on the way to <c>walletpassphrase</c>), no leading/trailing space
    /// (invisible on paper), at least 12 characters (the project's vault rule).
    /// </summary>
    public string? Validate(IReadOnlyCollection<string> existingWalletNames, IBackupFileInspector files)
    {
        var name = Name?.Trim() ?? "";
        if (name.Length == 0) return "Give the new wallet a name.";
        if (name.Length > MaxNameLength) return $"Keep the wallet name to {MaxNameLength} characters.";
        if (!NamePattern.IsMatch(name)) return "Use only letters, digits, dot, dash and underscore in the wallet name, starting with a letter or digit.";
        if (existingWalletNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return $"A wallet called '{name}' already exists on this node. Choose another name.";

        if (Encrypt)
        {
            var p = Passphrase ?? "";
            if (p.Length < MinPassphraseLength) return $"Use a passphrase of at least {MinPassphraseLength} characters.";
            if (p != (ConfirmPassphrase ?? "")) return "The two passphrases do not match.";
            if (p.Trim() != p) return "The passphrase starts or ends with a space — easy to lose on paper. Remove it.";
            if (p.Any(c => c < 0x20 || c > 0x7E)) return "Use only plain keyboard characters (A–Z, 0–9 and ASCII symbols) in the passphrase — no £, €, accents or emoji.";
            if (p.Contains('"')) return "Leave the double quote (\") out of the passphrase: command lines mangle it when you unlock the wallet later.";
        }

        var path = BackupPath?.Trim() ?? "";
        if (path.Length == 0) return "Choose where the backup file goes.";
        if (!Path.IsPathFullyQualified(path)) return $"Give the backup file as a full path, e.g. {UserFolders.ExampleDocumentsPath("wallet.dat")}.";
        if (!path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) return "Name the backup file with a .dat ending.";
        if (files.Exists(path)) return "A file already exists at that backup path. Choose another name — a backup never overwrites.";
        return null;
    }
}

/// <summary>Which step of the creation stopped it.</summary>
public enum PqWalletStep { None, Validate, Create, MintAddress, Verify, Backup }

/// <summary>A wallet that now exists, has a BQ… address, and has a verified backup.</summary>
public sealed record PqWalletCreated(string WalletName, string Address, string BackupPath, long BackupBytes, string BackupSha256, bool Unloaded,
    bool Encrypted = true)
{
    /// <summary>The words the page and the plan use, so "unencrypted" is never shown more softly in one place than another.</summary>
    public string EncryptionText => Encrypted
        ? "encrypted"
        : "UNENCRYPTED — anyone with the wallet file or this computer can spend it";
}

public sealed record PqWalletCreationResult(PqWalletCreated? Created, PqWalletStep FailedStep, string? Problem)
{
    public bool Succeeded => Created is not null;
    public static PqWalletCreationResult Ok(PqWalletCreated c) => new(c, PqWalletStep.None, null);
    public static PqWalletCreationResult Fail(PqWalletStep step, string problem) => new(null, step, problem);
}

public interface IPostQuantumWalletCreator
{
    Task<PqWalletCreationResult> CreateAsync(PqWalletRequest request, CancellationToken ct = default);
}

/// <summary>
/// Validate → <c>createwallet</c> (descriptor, PQ chains at birth, encrypted unless deliberately not) →
/// confirm on the node that the encryption is as asked → mint the first BQ… address → confirm the
/// wallet owns it → <c>backupwallet</c> → confirm the file is there and not
/// empty → <c>unloadwallet</c>. The backup is taken AFTER the PQ keys exist, which is the whole point
/// (a backup from before them cannot recover coins sent to BQ…). The wallet is only ever unloaded
/// when this run created it — an existing wallet of the same name is refused before the node is touched.
/// </summary>
public sealed class PostQuantumWalletCreator : IPostQuantumWalletCreator
{
    public const string AddressLabel = "post-quantum sweep";

    private readonly IPostQuantumWalletRpc _rpc;
    private readonly IBackupFileInspector _files;

    public PostQuantumWalletCreator(IPostQuantumWalletRpc rpc, IBackupFileInspector files)
    {
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<PqWalletCreationResult> CreateAsync(PqWalletRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> existing;
        try { existing = await _rpc.ListWalletNamesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return PqWalletCreationResult.Fail(PqWalletStep.Validate, $"Couldn't list this node's wallets: {ex.Message}"); }

        var invalid = request.Validate(existing, _files);
        if (invalid is not null) return PqWalletCreationResult.Fail(PqWalletStep.Validate, invalid);

        var name = request.Name.Trim();
        var backup = request.BackupPath.Trim();
        try { _files.EnsureDirectoryFor(backup); }
        catch (Exception ex) { return PqWalletCreationResult.Fail(PqWalletStep.Validate, $"Couldn't create the backup folder: {ex.Message}"); }

        try { await _rpc.CreatePostQuantumWalletAsync(name, request.EffectivePassphrase, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return PqWalletCreationResult.Fail(PqWalletStep.Create, $"The node couldn't create the wallet: {ex.Message}"); }

        // From here the wallet exists and is loaded: every failure unloads it (it holds no coins yet).
        // First: is it encrypted exactly as asked? Checked on the node, not assumed from the request.
        var encrypted = await Guard(() => _rpc.IsEncryptedInAsync(name, ct));
        if (encrypted != request.Encrypt)
            return await FailAfterCreate(name, PqWalletStep.Verify, encrypted is null
                ? "The node did not say whether the new wallet is encrypted."
                : request.Encrypt
                    ? "The node created the wallet WITHOUT encryption although a passphrase was given."
                    : "The node created the wallet encrypted although no passphrase was given.");

        var pq = await Guard(() => _rpc.GetNewPostQuantumAddressInAsync(name, AddressLabel, ct));
        if (pq is null || !pq.IsOk)
            return await FailAfterCreate(name, PqWalletStep.MintAddress,
                $"The new wallet couldn't make a BQ… address: {pq?.Explain() ?? "no answer"}");
        var address = pq.Address!;

        var mine = await Guard(() => _rpc.IsMineInAsync(name, address, ct));
        if (mine != true)
            return await FailAfterCreate(name, PqWalletStep.Verify, $"The new wallet did not confirm it owns {address}.");

        try { await _rpc.BackupWalletToAsync(name, backup, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return await FailAfterCreate(name, PqWalletStep.Backup, $"The backup failed: {ex.Message}"); }
        var file = _files.Inspect(backup);
        if (file is null)
            return await FailAfterCreate(name, PqWalletStep.Backup, $"The backup file at {backup} is missing or empty.");

        var unloaded = await Unload(name);
        return PqWalletCreationResult.Ok(new PqWalletCreated(name, address, backup, file.Value.Bytes, file.Value.Sha256, unloaded, request.Encrypt));
    }

    private async Task<PqWalletCreationResult> FailAfterCreate(string name, PqWalletStep step, string problem)
    {
        var unloaded = await Unload(name);
        var tail = unloaded
            ? $" The wallet '{name}' was created but holds no coins; it has been unloaded and can be deleted."
            : $" The wallet '{name}' was created but holds no coins; it is still loaded on the node.";
        return PqWalletCreationResult.Fail(step, problem + tail);
    }

    private async Task<bool> Unload(string name)
    {
        try { await _rpc.UnloadWalletByNameAsync(name); return true; }
        catch { return false; }
    }

    private static async Task<T?> Guard<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return default; }
    }
}

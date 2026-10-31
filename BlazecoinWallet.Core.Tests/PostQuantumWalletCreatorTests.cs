using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Mining;
using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// "Create a post-quantum wallet" on the Quantum page (2026-09-28, asked for from the Pool window, whose
/// Primary wallet predates 2.1.0 and cannot hold BQ keys). Pins the form's rules, and the creator's
/// sequence: validate → createwallet (encrypted, descriptor) → first BQ… address → ismine → backupwallet
/// → the file exists and is not empty → unloadwallet; every failure after createwallet unloads the new
/// (empty) wallet, and a wallet this run did not create is never touched.
/// </summary>
public class PostQuantumWalletCreatorTests
{
    private const string Pass = "correct horse battery";
    // Fully qualified on whichever OS runs the suite (C:\ on Windows, / elsewhere): the validator
    // uses Path.IsPathFullyQualified, and a C:\ literal is relative on macOS/Linux.
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";
    private static string Abs(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
    private static readonly string Backup = Abs("Users", "Andrew", "Documents", "Blazecoin Wallet Backups", "post-quantum-20260928-200000.dat");
    private static readonly string Bq = BqAddress();

    private static string BqAddress()
    {
        var payload = new byte[34]; payload[0] = 0x46; payload[1] = 0x50;
        for (int i = 0; i < 32; i++) payload[2 + i] = (byte)(40 + i);
        return BitcoinProtocol.Base58CheckEncode(payload);
    }

    private static PqWalletRequest Req(string name = "post-quantum", string pass = Pass, string? confirm = null, string? backup = null)
        => new(name, pass, confirm ?? pass, backup ?? Backup);

    private sealed class FakeFiles : IBackupFileInspector
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (long, string)? Written { get; set; } = (4_280_320, new string('C', 64));
        public List<string> EnsuredFor { get; } = new();
        public bool ThrowOnEnsure { get; set; }
        public bool Exists(string path) => Existing.Contains(path);
        public void EnsureDirectoryFor(string path) { if (ThrowOnEnsure) throw new UnauthorizedAccessException("denied"); EnsuredFor.Add(path); }
        public (long Bytes, string Sha256)? Inspect(string path) => Written;
    }

    private sealed class FakeRpc : IPostQuantumWalletRpc
    {
        public List<string> Calls { get; } = new();
        public List<string> WalletNames { get; } = new() { "Primary" };
        public Exception? ListThrows, CreateThrows, BackupThrows, UnloadThrows, MintThrows;
        public PqAddressResult Mint { get; set; } = PqAddressResult.Ok(Bq);
        public bool? Mine { get; set; } = true;
        public string? CreatedPassphrase, BackupDestination, MintLabel;

        public Task<IReadOnlyList<string>> ListWalletNamesAsync(CancellationToken ct = default)
        { Calls.Add("list"); if (ListThrows is not null) throw ListThrows; return Task.FromResult<IReadOnlyList<string>>(WalletNames); }
        public bool PassphraseWasNull;
        /// <summary>What getwalletinfo reports; null = whatever createwallet was asked for (the honest node).</summary>
        public bool? EncryptedOverride { get; set; }
        public bool ReportNothing { get; set; }
        private bool _createdEncrypted;
        public Task CreatePostQuantumWalletAsync(string name, string? passphrase, CancellationToken ct = default)
        {
            Calls.Add("create:" + name); CreatedPassphrase = passphrase; PassphraseWasNull = passphrase is null;
            _createdEncrypted = passphrase is not null;
            if (CreateThrows is not null) throw CreateThrows; return Task.CompletedTask;
        }
        public Task<bool?> IsEncryptedInAsync(string wallet, CancellationToken ct = default)
        { Calls.Add("encrypted?:" + wallet); return Task.FromResult<bool?>(ReportNothing ? null : EncryptedOverride ?? _createdEncrypted); }
        public Task<PqAddressResult> GetNewPostQuantumAddressInAsync(string wallet, string label, CancellationToken ct = default)
        { Calls.Add("mint:" + wallet); MintLabel = label; if (MintThrows is not null) throw MintThrows; return Task.FromResult(Mint); }
        public Task<bool?> IsMineInAsync(string wallet, string address, CancellationToken ct = default)
        { Calls.Add("ismine:" + wallet + ":" + address); return Task.FromResult(Mine); }
        public Task BackupWalletToAsync(string wallet, string destination, CancellationToken ct = default)
        { Calls.Add("backup:" + wallet); BackupDestination = destination; if (BackupThrows is not null) throw BackupThrows; return Task.CompletedTask; }
        public Task UnloadWalletByNameAsync(string wallet, CancellationToken ct = default)
        { Calls.Add("unload:" + wallet); if (UnloadThrows is not null) throw UnloadThrows; return Task.CompletedTask; }
    }

    private static (PostQuantumWalletCreator c, FakeRpc rpc, FakeFiles files) Make()
    {
        var rpc = new FakeRpc(); var files = new FakeFiles();
        return (new PostQuantumWalletCreator(rpc, files), rpc, files);
    }

    // ════════════════════════════════════════════════════════════ the form's rules

    [Fact]
    public void A_sensible_request_passes()
        => Assert.Null(Req().Validate(new[] { "Primary" }, new FakeFiles()));

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("-leading-dash", "letters, digits")]
    [InlineData("has space", "letters, digits")]
    [InlineData(@"..\escape", "letters, digits")]
    [InlineData("wallet/sub", "letters, digits")]
    [InlineData("PRIMARY", "already exists")]                 // case-insensitive: Windows file names
    public void Bad_wallet_names_are_refused(string name, string expected)
    {
        var problem = Req(name: name).Validate(new[] { "Primary" }, new FakeFiles());
        Assert.Contains(expected, problem!);
    }

    [Fact]
    public void A_too_long_name_is_refused()
        => Assert.Contains("64 characters", Req(name: new string('a', 65)).Validate(Array.Empty<string>(), new FakeFiles())!);

    [Fact]
    public void Names_may_use_dot_dash_underscore_and_digits()
        => Assert.Null(Req(name: "pq_savings-2026.09").Validate(Array.Empty<string>(), new FakeFiles()));

    [Theory]
    [InlineData("short", null, "at least 12")]
    [InlineData("correct horse battery", "correct horse batterY", "do not match")]
    [InlineData(" correct horse battery", null, "space")]
    [InlineData("correct horse battery ", null, "space")]
    [InlineData("pounds£1234567890", null, "plain keyboard")]  // the 2019 lesson: non-ASCII
    [InlineData("naïve passphrase", null, "plain keyboard")]
    [InlineData("tab\tinside passphrase", null, "plain keyboard")]
    [InlineData("say \"hello\" loudly", null, "double quote")]
    public void Risky_passphrases_are_refused(string pass, string? confirm, string expected)
    {
        var problem = Req(pass: pass, confirm: confirm).Validate(Array.Empty<string>(), new FakeFiles());
        Assert.Contains(expected, problem!);
    }

    [Fact]
    public void Plain_ASCII_symbols_are_fine_in_a_passphrase()
        => Assert.Null(Req(pass: "Tr0ub4dor&3-x$y!", confirm: "Tr0ub4dor&3-x$y!").Validate(Array.Empty<string>(), new FakeFiles()));

    [Theory]
    [InlineData("", "where the backup")]
    [InlineData(@"relative\wallet.dat", "full path")]
    [InlineData("ROOT:backups/wallet.txt", ".dat")]
    public void Bad_backup_paths_are_refused(string path, string expected)
        => Assert.Contains(expected, Req(backup: path.StartsWith("ROOT:") ? Abs(path[5..].Split('/')) : path).Validate(Array.Empty<string>(), new FakeFiles())!);

    [Fact]
    public void A_backup_never_overwrites_an_existing_file()
    {
        var files = new FakeFiles(); files.Existing.Add(Backup);
        Assert.Contains("never overwrites", Req().Validate(Array.Empty<string>(), files)!);
    }

    [Fact]
    public void The_default_backup_path_sits_in_Documents_with_the_name_and_a_timestamp()
    {
        var p = PqWalletRequest.DefaultBackupPath("pq-savings", new DateTime(2026, 9, 28, 20, 5, 9), Abs("Users", "Andrew", "Documents"));
        Assert.Equal(Abs("Users", "Andrew", "Documents", "Blazecoin Wallet Backups", "pq-savings-20260928-200509.dat"), p);
        Assert.Contains(PqWalletRequest.DefaultName, PqWalletRequest.DefaultBackupPath("  ", DateTime.Now, Abs("d")));
    }

    // ════════════════════════════════════════════════════════════ the creator

    [Fact]
    public async Task The_happy_path_creates_mints_verifies_backs_up_and_unloads_in_that_order()
    {
        var (c, rpc, files) = Make();

        var r = await c.CreateAsync(Req(name: "  post-quantum  "));

        Assert.True(r.Succeeded);
        Assert.Equal(new[]
        {
            "list", "create:post-quantum", "encrypted?:post-quantum", "mint:post-quantum", "ismine:post-quantum:" + Bq, "backup:post-quantum", "unload:post-quantum",
        }, rpc.Calls);
        Assert.Equal(Pass, rpc.CreatedPassphrase);
        Assert.Equal(PostQuantumWalletCreator.AddressLabel, rpc.MintLabel);
        Assert.Equal(Backup, rpc.BackupDestination);
        Assert.Equal(new[] { Backup }, files.EnsuredFor);
        var w = r.Created!;
        Assert.Equal("post-quantum", w.WalletName);
        Assert.Equal(Bq, w.Address);
        Assert.Equal(Backup, w.BackupPath);
        Assert.Equal(4_280_320, w.BackupBytes);
        Assert.Equal(new string('C', 64), w.BackupSha256);
        Assert.True(w.Unloaded);
    }

    [Fact]
    public async Task An_invalid_request_never_touches_the_node_beyond_listing_names()
    {
        var (c, rpc, files) = Make();

        var r = await c.CreateAsync(Req(confirm: "something else entirely"));

        Assert.False(r.Succeeded);
        Assert.Equal(PqWalletStep.Validate, r.FailedStep);
        Assert.Equal(new[] { "list" }, rpc.Calls);
        Assert.Empty(files.EnsuredFor);
    }

    [Fact]
    public async Task An_existing_wallet_name_is_refused_and_that_wallet_is_never_unloaded()
    {
        var (c, rpc, _) = Make();
        rpc.WalletNames.Add("post-quantum");

        var r = await c.CreateAsync(Req());

        Assert.Equal(PqWalletStep.Validate, r.FailedStep);
        Assert.DoesNotContain(rpc.Calls, x => x.StartsWith("unload"));
        Assert.DoesNotContain(rpc.Calls, x => x.StartsWith("create"));
    }

    [Fact]
    public async Task A_node_that_cannot_list_its_wallets_stops_before_creating()
    {
        var (c, rpc, _) = Make();
        rpc.ListThrows = new HttpRequestException("down");
        var r = await c.CreateAsync(Req());
        Assert.Equal(PqWalletStep.Validate, r.FailedStep);
        Assert.Contains("down", r.Problem!);
        Assert.Equal(new[] { "list" }, rpc.Calls);
    }

    [Fact]
    public async Task A_backup_folder_that_cannot_be_made_stops_before_creating()
    {
        var (c, rpc, files) = Make();
        files.ThrowOnEnsure = true;
        var r = await c.CreateAsync(Req());
        Assert.Equal(PqWalletStep.Validate, r.FailedStep);
        Assert.DoesNotContain(rpc.Calls, x => x.StartsWith("create"));
    }

    [Fact]
    public async Task A_create_failure_unloads_nothing_because_nothing_was_created()
    {
        var (c, rpc, _) = Make();
        rpc.CreateThrows = new RpcException(-4, "Wallet file verification failed");

        var r = await c.CreateAsync(Req());

        Assert.Equal(PqWalletStep.Create, r.FailedStep);
        Assert.Contains("verification failed", r.Problem!);
        Assert.DoesNotContain(rpc.Calls, x => x.StartsWith("unload"));
    }

    [Fact]
    public async Task A_new_wallet_that_cannot_mint_BQ_is_unloaded_and_the_reason_given()
    {
        var (c, rpc, _) = Make();
        rpc.Mint = PqAddressResult.Fail(PqAddressOutcome.DaemonTooOld, "Unknown address type 'pq'");

        var r = await c.CreateAsync(Req());

        Assert.Equal(PqWalletStep.MintAddress, r.FailedStep);
        Assert.Contains("2.1.0", r.Problem!);
        Assert.Contains("holds no coins", r.Problem!);
        Assert.Equal("unload:post-quantum", rpc.Calls[^1]);
        Assert.DoesNotContain(rpc.Calls, x => x.StartsWith("backup"));
    }

    [Fact]
    public async Task A_mint_that_throws_is_a_mint_failure_and_unloads()
    {
        var (c, rpc, _) = Make();
        rpc.MintThrows = new TimeoutException("slow");
        var r = await c.CreateAsync(Req());
        Assert.Equal(PqWalletStep.MintAddress, r.FailedStep);
        Assert.Equal("unload:post-quantum", rpc.Calls[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_address_the_new_wallet_does_not_confirm_is_never_used(bool? mine)
    {
        var (c, rpc, _) = Make();
        rpc.Mine = mine;

        var r = await c.CreateAsync(Req());

        Assert.Equal(PqWalletStep.Verify, r.FailedStep);
        Assert.Null(r.Created);
        Assert.DoesNotContain(rpc.Calls, x => x.StartsWith("backup"));
        Assert.Equal("unload:post-quantum", rpc.Calls[^1]);
    }

    [Fact]
    public async Task A_failed_backup_means_no_destination()
    {
        var (c, rpc, _) = Make();
        rpc.BackupThrows = new RpcException(-4, "Error: Wallet backup failed!");

        var r = await c.CreateAsync(Req());

        Assert.Equal(PqWalletStep.Backup, r.FailedStep);
        Assert.Null(r.Created);
        Assert.Contains("backup failed", r.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("unload:post-quantum", rpc.Calls[^1]);
    }

    [Fact]
    public async Task A_backup_file_that_is_missing_or_empty_means_no_destination()
    {
        var (c, rpc, files) = Make();
        files.Written = null;

        var r = await c.CreateAsync(Req());

        Assert.Equal(PqWalletStep.Backup, r.FailedStep);
        Assert.Contains("missing or empty", r.Problem!);
        Assert.Equal("unload:post-quantum", rpc.Calls[^1]);
    }

    [Fact]
    public async Task A_failed_unload_after_success_is_reported_not_fatal()
    {
        var (c, rpc, _) = Make();
        rpc.UnloadThrows = new RpcException(-18, "gone");

        var r = await c.CreateAsync(Req());

        Assert.True(r.Succeeded);
        Assert.False(r.Created!.Unloaded);
    }

    [Fact]
    public async Task A_failed_unload_after_a_failure_says_the_wallet_is_still_loaded()
    {
        var (c, rpc, _) = Make();
        rpc.Mine = false;
        rpc.UnloadThrows = new RpcException(-18, "gone");

        var r = await c.CreateAsync(Req());

        Assert.Contains("still loaded", r.Problem!);
    }

    // ════════════════════════════════════════════════════════════ encryption is the default, not a requirement

    [Fact]
    public void Encryption_is_on_unless_turned_off()
    {
        Assert.True(Req().Encrypt);
        Assert.Equal(Pass, Req().EffectivePassphrase);
        Assert.Null((Req() with { Encrypt = false }).EffectivePassphrase);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("short", "different")]
    [InlineData("say \"hi\"", "say \"hi\"")]
    public void An_unencrypted_request_ignores_the_passphrase_fields(string pass, string confirm)
        => Assert.Null(new PqWalletRequest("post-quantum", pass, confirm, Backup, Encrypt: false).Validate(Array.Empty<string>(), new FakeFiles()));

    [Fact]
    public void An_unencrypted_request_still_needs_a_good_name_and_backup_path()
    {
        Assert.Contains("already exists", new PqWalletRequest("Primary", "", "", Backup, false).Validate(new[] { "Primary" }, new FakeFiles())!);
        Assert.Contains(".dat", new PqWalletRequest("pq", "", "", Abs("b", "w.txt"), false).Validate(Array.Empty<string>(), new FakeFiles())!);
    }

    [Fact]
    public async Task An_unencrypted_wallet_is_created_without_any_passphrase_reaching_the_node()
    {
        var (c, rpc, _) = Make();

        var r = await c.CreateAsync(new PqWalletRequest("hot-pq", "typed then untick", "typed then untick", Backup, Encrypt: false));

        Assert.True(r.Succeeded);
        Assert.True(rpc.PassphraseWasNull);
        Assert.Null(rpc.CreatedPassphrase);
        Assert.False(r.Created!.Encrypted);
        Assert.StartsWith("UNENCRYPTED", r.Created.EncryptionText);
        Assert.Equal("unload:hot-pq", rpc.Calls[^1]);
    }

    [Fact]
    public async Task An_encrypted_wallet_reports_encrypted()
    {
        var (c, _, _) = Make();
        var r = await c.CreateAsync(Req());
        Assert.True(r.Created!.Encrypted);
        Assert.Equal("encrypted", r.Created.EncryptionText);
    }

    [Theory]
    [InlineData(true, false, "WITHOUT encryption")]      // asked for encryption, node says unencrypted
    [InlineData(false, true, "encrypted although no")]   // asked for none, node says encrypted
    public async Task A_wallet_whose_encryption_is_not_what_was_asked_is_never_used(bool encrypt, bool nodeSays, string expected)
    {
        var (c, rpc, _) = Make();
        rpc.EncryptedOverride = nodeSays;

        var r = await c.CreateAsync(Req() with { Encrypt = encrypt });

        Assert.Equal(PqWalletStep.Verify, r.FailedStep);
        Assert.Contains(expected, r.Problem!);
        Assert.DoesNotContain(rpc.Calls, x => x.StartsWith("mint"));
        Assert.Equal("unload:post-quantum", rpc.Calls[^1]);
    }

    [Fact]
    public async Task A_node_that_cannot_say_whether_the_wallet_is_encrypted_stops_the_run()
    {
        var (c, rpc, _) = Make();
        rpc.ReportNothing = true;

        var r = await c.CreateAsync(Req());

        Assert.Equal(PqWalletStep.Verify, r.FailedStep);
        Assert.Contains("did not say", r.Problem!);
        Assert.Equal("unload:post-quantum", rpc.Calls[^1]);
    }

    [Fact]
    public void The_creator_needs_both_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new PostQuantumWalletCreator(null!, new FakeFiles()));
        Assert.Throws<ArgumentNullException>(() => new PostQuantumWalletCreator(new FakeRpc(), null!));
    }

    // ════════════════════════════════════════════════════════════ the real file inspector

    [Fact]
    public void The_file_inspector_hashes_a_real_file_and_rejects_missing_or_empty_ones()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pqwc-" + Guid.NewGuid().ToString("N"));
        var inspector = new BackupFileInspector();
        var file = Path.Combine(dir, "nested", "w.dat");
        try
        {
            inspector.EnsureDirectoryFor(file);
            Assert.True(Directory.Exists(Path.GetDirectoryName(file)));
            Assert.False(inspector.Exists(file));
            Assert.Null(inspector.Inspect(file));

            File.WriteAllBytes(file, Array.Empty<byte>());
            Assert.True(inspector.Exists(file));
            Assert.Null(inspector.Inspect(file));             // empty = not a backup

            File.WriteAllText(file, "abc");
            var r = inspector.Inspect(file)!.Value;
            Assert.Equal(3, r.Bytes);
            Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", r.Sha256);   // SHA-256("abc")
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

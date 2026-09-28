using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Import;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>The direct-migration path: a daemon built --without-bdb (macOS/Linux) can't load a
/// legacy wallet (restorewallet → -18), and Core's BDB-less reader accepts only Berkeley DB
/// version 9 — a V1.5 written by BDB 18 (the macOS port) is version 10. Both cases must
/// end in migratewallet on an unloaded copy whose meta pages read version 9.</summary>
public class BdbDirectMigrationTests : IDisposable
{
    private const string NoBdbMsg =
        "Wallet file verification failed. Failed to open database path '/x/wallets/w'. Build does not support Berkeley DB database format.";
    private const int Page = 4096;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "blz-import-" + Guid.NewGuid().ToString("N"));
    private string WalletDir => Path.Combine(_tmp, "wallets");

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    // A 3-page btree: meta (page 0), a leaf (page 1), and a subdatabase meta (page 2).
    private static byte[] FakeBdb(uint version)
    {
        var f = new byte[Page * 3];
        void Meta(int off)
        {
            BitConverter.GetBytes(BdbFile.BtreeMagic).CopyTo(f, off + 12);
            BitConverter.GetBytes(version).CopyTo(f, off + 16);
            BitConverter.GetBytes((uint)Page).CopyTo(f, off + 20);
            f[off + 25] = 9;      // BTREE_META
            f[off + 26] = 0x20;   // SUBDB
        }
        Meta(0); Meta(Page * 2);
        f[Page + 25] = 5;         // page 1: a leaf, must be left alone
        for (var i = 64; i < Page * 3; i += 97) f[i] = (byte)(i % 251);   // "data"
        return f;
    }

    private string Source(uint version)
    {
        Directory.CreateDirectory(_tmp);
        var p = Path.Combine(_tmp, "wallet.dat");
        File.WriteAllBytes(p, FakeBdb(version));
        return p;
    }

    private (WalletImportService svc, IWalletRpc rpc, IWalletContext ctx) Make()
    {
        var rpc = Substitute.For<IWalletRpc>();
        rpc.ListWalletDirAsync().Returns(Task.FromResult(new List<string>()));
        var ctx = Substitute.For<IWalletContext>();
        return (new WalletImportService(rpc, ctx, () => WalletDir), rpc, ctx);
    }

    [Fact]
    public void normaliser_rewrites_every_meta_page_and_nothing_else()
    {
        var original = FakeBdb(10);
        var copy = (byte[])original.Clone();

        var patched = BdbFile.NormaliseMetaVersions(copy);

        Assert.Equal(2, patched);
        Assert.Equal(9u, BitConverter.ToUInt32(copy, 16));
        Assert.Equal(9u, BitConverter.ToUInt32(copy, Page * 2 + 16));
        for (var i = 0; i < copy.Length; i++)
            if (i is >= 16 and < 20 || i is >= Page * 2 + 16 and < Page * 2 + 20) continue;
            else Assert.Equal(original[i], copy[i]);
        Assert.Equal(0, BdbFile.NormaliseMetaVersions(copy));   // idempotent
    }

    [Fact]
    public void read_meta_version_reports_10_for_bdb18_files_and_0_for_non_bdb()
    {
        Assert.Equal(10u, BdbFile.ReadMetaVersion(Source(10)));
        File.WriteAllText(Path.Combine(_tmp, "x.txt"), "SQLite format 3\0 nope");
        Assert.Equal(0u, BdbFile.ReadMetaVersion(Path.Combine(_tmp, "x.txt")));
        Assert.Equal(0u, BdbFile.ReadMetaVersion(Path.Combine(_tmp, "missing.dat")));
    }

    [Fact]
    public async Task version_10_file_skips_restorewallet_and_migrates_a_normalised_copy()
    {
        var (svc, rpc, ctx) = Make();
        rpc.MigrateWalletAsync("w", null).Returns(Task.CompletedTask);

        var r = await svc.ImportAsync("w", Source(10), WalletFormat.LegacyBdb, migrate: true, passphrase: null);

        Assert.True(r.Success); Assert.True(r.Migrated);
        await rpc.DidNotReceive().RestoreWalletAsync(Arg.Any<string>(), Arg.Any<string>());
        await rpc.Received(1).MigrateWalletAsync("w", null);
        var placed = File.ReadAllBytes(Path.Combine(WalletDir, "w", "wallet.dat"));
        Assert.Equal(9u, BitConverter.ToUInt32(placed, 16));
        Assert.Equal(9u, BitConverter.ToUInt32(placed, Page * 2 + 16));
        ctx.Received(1).SetActive("w");
    }

    [Fact]
    public async Task daemon_without_bdb_falls_back_from_restorewallet_to_direct_migration()
    {
        var (svc, rpc, ctx) = Make();
        rpc.RestoreWalletAsync("w", Arg.Any<string>()).Returns(Task.FromException(new RpcException(-18, NoBdbMsg)));
        rpc.MigrateWalletAsync("w", null).Returns(Task.CompletedTask);

        var r = await svc.ImportAsync("w", Source(9), WalletFormat.LegacyBdb, migrate: true, passphrase: null);

        Assert.True(r.Success); Assert.True(r.Migrated);
        await rpc.Received(1).RestoreWalletAsync("w", Arg.Any<string>());   // tried the normal path first
        await rpc.Received(1).MigrateWalletAsync("w", null);
        Assert.True(File.Exists(Path.Combine(WalletDir, "w", "wallet.dat")));
        ctx.Received(1).SetActive("w");
    }

    [Fact]
    public async Task daemon_without_bdb_and_migration_off_explains_instead_of_corrupt()
    {
        var (svc, rpc, _) = Make();
        rpc.RestoreWalletAsync("w", Arg.Any<string>()).Returns(Task.FromException(new RpcException(-18, NoBdbMsg)));

        var r = await svc.ImportAsync("w", Source(9), WalletFormat.LegacyBdb, migrate: false, passphrase: null);

        Assert.False(r.Success);
        Assert.Contains("migration", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corrupt", r.Error!, StringComparison.OrdinalIgnoreCase);
        await rpc.DidNotReceive().MigrateWalletAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task direct_path_name_collision_is_reported_and_touches_nothing()
    {
        var (svc, rpc, _) = Make();
        Directory.CreateDirectory(Path.Combine(WalletDir, "w"));

        var r = await svc.ImportAsync("w", Source(10), WalletFormat.LegacyBdb, migrate: true, passphrase: null);

        Assert.False(r.Success);
        Assert.Contains("already exists", r.Error!, StringComparison.OrdinalIgnoreCase);
        await rpc.DidNotReceive().MigrateWalletAsync(Arg.Any<string>(), Arg.Any<string?>());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(WalletDir, "w")));
    }

    [Fact]
    public async Task encrypted_wallet_on_direct_path_keeps_the_copy_for_the_passphrase_retry()
    {
        var (svc, rpc, ctx) = Make();
        rpc.MigrateWalletAsync("w", null).Returns(Task.FromException(new RpcException(-4,
            "Error: Wallet decryption failed, the wallet passphrase was not provided or was incorrect.")));
        rpc.MigrateWalletAsync("w", "right").Returns(Task.CompletedTask);

        var first = await svc.ImportAsync("w", Source(10), WalletFormat.LegacyBdb, migrate: true, passphrase: null);
        Assert.False(first.Success); Assert.True(first.NeedsPassphrase);
        Assert.True(File.Exists(Path.Combine(WalletDir, "w", "wallet.dat")));   // kept

        var retry = await svc.RetryMigrateAsync("w", "right");
        Assert.True(retry.Success);
        ctx.Received(1).SetActive("w");
    }

    [Fact]
    public async Task other_migration_failures_on_direct_path_remove_the_copy()
    {
        var (svc, rpc, _) = Make();
        rpc.MigrateWalletAsync("w", null).Returns(Task.FromException(new RpcException(-4,
            "Wallet file verification failed. LSNs are not reset, this database is not completely flushed.")));

        var r = await svc.ImportAsync("w", Source(10), WalletFormat.LegacyBdb, migrate: true, passphrase: null);

        Assert.False(r.Success); Assert.False(r.NeedsPassphrase);
        Assert.False(Directory.Exists(Path.Combine(WalletDir, "w")));
    }
}

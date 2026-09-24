using BlazecoinWallet.Core.Services;          // IWalletRpc, IWalletContext, RpcException
using BlazecoinWallet.Core.Services.Import;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>Unit tests for the wallet-import classification — especially the
/// encrypted-wallet path: migratewallet can't unlock an encrypted legacy wallet
/// without the passphrase, and that must surface as a retryable "needs passphrase"
/// state rather than a dead-end (the wallet is already restored, so re-importing the
/// same name would collide). Daemon error strings are the verbatim ones from
/// src/wallet/wallet.cpp.</summary>
public class WalletImportServiceTests
{
    // The exact message MigrateLegacyToDescriptor returns when it can't unlock an
    // encrypted wallet (wallet.cpp), thrown by migratewallet as RPC_WALLET_ERROR (-4).
    private const string EncryptedMsg =
        "Error: Wallet decryption failed, the wallet passphrase was not provided or was incorrect.";
    private const string EncryptedMsg2 =
        "Error: Unable to produce descriptors for this legacy wallet. Make sure to provide the wallet's passphrase if it is encrypted.";
    private const string UnflushedMsg =
        "Wallet file verification failed. LSNs are not reset, this database is not completely flushed. Please reopen then close the database with a version that has BDB support.";

    private static (WalletImportService svc, IWalletRpc rpc, IWalletContext ctx) Make()
    {
        var rpc = Substitute.For<IWalletRpc>();
        var ctx = Substitute.For<IWalletContext>();
        return (new WalletImportService(rpc, ctx), rpc, ctx);
    }

    private static Task Throws(int code, string msg) => Task.FromException(new RpcException(code, msg));

    [Theory]
    [InlineData(EncryptedMsg)]
    [InlineData(EncryptedMsg2)]
    public async Task encrypted_legacy_wallet_surfaces_needs_passphrase(string daemonMsg)
    {
        var (svc, rpc, ctx) = Make();
        rpc.MigrateWalletAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(Throws(-4, daemonMsg));

        var r = await svc.ImportAsync("w", "path", WalletFormat.LegacyBdb, migrate: true, passphrase: null);

        Assert.False(r.Success);
        Assert.True(r.NeedsPassphrase);
        Assert.Contains("encrypted", r.Error!, StringComparison.OrdinalIgnoreCase);
        ctx.DidNotReceive().SetActive(Arg.Any<string>());   // not active until it actually migrates
    }

    [Fact]
    public async Task retry_with_correct_passphrase_migrates_and_activates()
    {
        var (svc, rpc, ctx) = Make();
        rpc.MigrateWalletAsync("w", "right").Returns(Task.CompletedTask);

        var r = await svc.RetryMigrateAsync("w", "right");

        Assert.True(r.Success);
        Assert.True(r.Migrated);
        ctx.Received(1).SetActive("w");
        await rpc.DidNotReceive().RestoreWalletAsync(Arg.Any<string>(), Arg.Any<string>()); // no re-restore
    }

    [Fact]
    public async Task retry_with_still_wrong_passphrase_asks_again()
    {
        var (svc, rpc, ctx) = Make();
        rpc.MigrateWalletAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(Throws(-4, EncryptedMsg));

        var r = await svc.RetryMigrateAsync("w", "wrong");

        Assert.True(r.NeedsPassphrase);
        ctx.DidNotReceive().SetActive(Arg.Any<string>());
    }

    [Fact]
    public async Task full_flow_encrypted_then_correct_passphrase_succeeds()
    {
        var (svc, rpc, ctx) = Make();
        rpc.MigrateWalletAsync("w", Arg.Is<string?>(p => p != "right")).Returns(Throws(-4, EncryptedMsg));
        rpc.MigrateWalletAsync("w", "right").Returns(Task.CompletedTask);

        var first = await svc.ImportAsync("w", "path", WalletFormat.LegacyBdb, migrate: true, passphrase: null);
        Assert.True(first.NeedsPassphrase);

        var second = await svc.RetryMigrateAsync("w", "right");
        Assert.True(second.Success);
        Assert.True(second.Migrated);
        ctx.Received(1).SetActive("w");
    }

    [Fact]
    public async Task unflushed_bdb_is_not_mistaken_for_a_passphrase_problem()
    {
        var (svc, rpc, _) = Make();
        rpc.MigrateWalletAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(Throws(-4, UnflushedMsg));

        var r = await svc.ImportAsync("w", "path", WalletFormat.LegacyBdb, migrate: true, passphrase: "x");

        Assert.False(r.Success);
        Assert.False(r.NeedsPassphrase);
        Assert.Contains("cleanly closed", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task restore_name_collision_maps_to_already_exists_not_passphrase()
    {
        var (svc, rpc, _) = Make();
        rpc.RestoreWalletAsync(Arg.Any<string>(), Arg.Any<string>())
           .Returns(Throws(-4, "Wallet \"w\" already exists."));

        var r = await svc.ImportAsync("w", "path", WalletFormat.LegacyBdb, migrate: true, passphrase: null);

        Assert.False(r.Success);
        Assert.False(r.NeedsPassphrase);
        Assert.Contains("already exists", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task descriptor_wallet_imports_without_migration()
    {
        var (svc, rpc, ctx) = Make();

        var r = await svc.ImportAsync("w", "path", WalletFormat.Sqlite, migrate: false, passphrase: null);

        Assert.True(r.Success);
        Assert.False(r.Migrated);
        ctx.Received(1).SetActive("w");
        await rpc.DidNotReceive().MigrateWalletAsync(Arg.Any<string>(), Arg.Any<string?>());
    }
}

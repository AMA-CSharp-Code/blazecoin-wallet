using BlazecoinWallet.Core.Services;
using BlazecoinWallet.Core.Services.Wallets;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// Bug-bounty round eighteen (2026-11-20), the desktop wallet's finding: unloading or removing
/// the ACTIVE wallet moved the window to another loaded wallet without raising the "Active Wallet
/// Changed" notice. The write gate only holds while that notice is unacknowledged, so a new
/// passphrase or a fresh receive address could go to the wallet the window had landed on. Sends
/// were pinned separately and were never affected. The move is now announced like the one a
/// failed read makes.
/// </summary>
public class BountyRound18DesktopTests
{
    private sealed class Context(string? active) : IWalletContext
    {
        private string? _active = active;
        public string? Active => _active;
        public event Action? Changed;
        public void SetActive(string? name) { _active = name; Changed?.Invoke(); }
    }

    /// <summary>A node with these wallets loaded; unloading one takes it off the list.</summary>
    private static IWalletRpc Node(params string[] loaded)
    {
        var wallets = loaded.ToList();
        var rpc = Substitute.For<IWalletRpc>();
        rpc.ListWalletsAsync().Returns(_ => Task.FromResult(wallets.ToList()));
        rpc.UnloadWalletAsync(Arg.Any<string>()).Returns(call => { wallets.Remove(call.Arg<string>()); return Task.CompletedTask; });
        return rpc;
    }

    [Fact]
    public async Task Unloading_the_active_wallet_announces_the_move_and_holds_writes_until_it_is_acknowledged()
    {
        var context = new Context("Savings");
        var notice = new WalletSwitchNotice();
        var manager = new WalletManager(Node("Savings", "Primary"), context, notice);
        var binding = new WalletBinding(context, notice);

        var result = await manager.UnloadAsync("Savings");

        Assert.Equal(WalletOpResult.ActiveReassigned, result);
        Assert.Equal("Primary", context.Active);
        Assert.Equal(("Savings", "Primary"), (notice.Current!.From, notice.Current.To));          // was null: no banner, gate open
        var refused = Assert.Throws<WalletSwitchPendingException>(() => binding.BindActiveForWrite());
        Assert.Contains("\"Savings\" is no longer loaded", refused.Message);
        Assert.Contains("nothing was done", refused.Message);

        notice.Dismiss();                                                                         // the user presses OK on the banner
        Assert.Equal("Primary", binding.BindActiveForWrite());
    }

    [Fact]
    public async Task The_notice_is_up_before_the_window_moves()
    {
        // Whatever reacts to the active wallet changing must already see the notice, or a write
        // started in that instant would slip through.
        var context = new Context("Savings");
        var notice = new WalletSwitchNotice();
        WalletSwitch? seenAtTheMove = null;
        context.Changed += () => seenAtTheMove = notice.Current;
        var manager = new WalletManager(Node("Savings", "Primary"), context, notice);

        await manager.UnloadAsync("Savings");

        Assert.NotNull(seenAtTheMove);
        Assert.Equal("Primary", seenAtTheMove!.To);
    }

    [Fact]
    public async Task Unloading_a_wallet_that_is_not_the_active_one_moves_nothing_and_says_nothing()
    {
        var context = new Context("Primary");
        var notice = new WalletSwitchNotice();
        var manager = new WalletManager(Node("Savings", "Primary"), context, notice);

        Assert.Equal(WalletOpResult.Completed, await manager.UnloadAsync("Savings"));

        Assert.Equal("Primary", context.Active);
        Assert.Null(notice.Current);
        Assert.Equal("Primary", new WalletBinding(context, notice).BindActiveForWrite());
    }

    [Fact]
    public async Task With_no_wallet_left_there_is_nothing_to_announce()
    {
        var context = new Context("Savings");
        var notice = new WalletSwitchNotice();
        var manager = new WalletManager(Node("Savings"), context, notice);

        Assert.Equal(WalletOpResult.ActiveReassigned, await manager.UnloadAsync("Savings"));

        Assert.Null(context.Active);
        Assert.Null(notice.Current);            // the window did not move TO a wallet
    }

    [Fact]
    public async Task A_host_with_no_notice_still_moves_to_a_loaded_wallet()
    {
        var context = new Context("Savings");
        var manager = new WalletManager(Node("Savings", "Primary"), context);

        Assert.Equal(WalletOpResult.ActiveReassigned, await manager.UnloadAsync("Savings"));
        Assert.Equal("Primary", context.Active);
    }
}

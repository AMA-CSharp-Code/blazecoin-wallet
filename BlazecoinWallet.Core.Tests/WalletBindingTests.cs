using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// The one place "the wallet on screen" becomes the wallet a write is bound to (audit 2026-11-09).
/// The Send page (Review, Send Max, a straight send), auto-payout Start and the Console all take
/// their wallet from <see cref="WalletBinding"/>; until now the first two read the active wallet
/// themselves and never looked at the switch notice, so a payment could be bound to a wallet the
/// app had moved to a moment earlier and the user had not acknowledged.
/// </summary>
public class WalletBindingTests
{
    private sealed class Context(string? active) : IWalletContext
    {
        private string? _active = active;
        /// <summary>Runs as the active wallet is read - a test uses it to land a switch at that instant.</summary>
        public Action? OnRead;
        public string? Active { get { OnRead?.Invoke(); return _active; } }
        public event Action? Changed;
        public void SetActive(string? name) { _active = name; Changed?.Invoke(); }
    }

    [Fact]
    public void It_binds_the_wallet_on_screen_when_nothing_is_pending()
    {
        var binding = new WalletBinding(new Context("Savings"), new WalletSwitchNotice());

        Assert.Equal("Savings", binding.BindActiveForWrite());
    }

    [Fact]
    public void With_no_wallet_chosen_it_binds_the_nodes_default_endpoint()
    {
        var binding = new WalletBinding(new Context(null), new WalletSwitchNotice());

        Assert.Equal("", binding.BindActiveForWrite());
    }

    [Fact]
    public void Nothing_is_bound_to_a_wallet_the_app_switched_to_until_the_user_has_acknowledged_it()
    {
        // What a healing read does: raise the notice, then move the context.
        var ctx = new Context("Savings");
        var notice = new WalletSwitchNotice();
        var binding = new WalletBinding(ctx, notice);
        notice.Record("Savings", "Primary");
        ctx.SetActive("Primary");

        var ex = Assert.Throws<WalletSwitchPendingException>(() => binding.BindActiveForWrite());   // was: "Primary"

        Assert.Contains("Savings", ex.Message);
        Assert.Contains("Primary", ex.Message);
        Assert.Contains("nothing was done", ex.Message);

        notice.Dismiss();                                      // the user pressed OK on the notice
        Assert.Equal("Primary", binding.BindActiveForWrite());
    }

    [Fact]
    public void A_wallet_the_user_chose_themselves_binds_at_once()
    {
        // Picking a wallet on the Dashboard raises no notice: there is nothing to acknowledge.
        var ctx = new Context("Savings");
        var binding = new WalletBinding(ctx, new WalletSwitchNotice());

        ctx.SetActive("Primary");

        Assert.Equal("Primary", binding.BindActiveForWrite());
    }

    [Fact]
    public void A_switch_that_lands_as_the_wallet_is_read_is_still_caught()
    {
        // The notice is raised BEFORE the context moves, so it must be looked at AFTER the wallet
        // is read: whoever sees the new wallet then also sees the notice. The other order lets a
        // switch slip between the two looks.
        var ctx = new Context("Savings");
        var notice = new WalletSwitchNotice();
        var binding = new WalletBinding(ctx, notice);
        ctx.OnRead = () =>
        {
            ctx.OnRead = null;
            notice.Record("Savings", "Primary");
            ctx.SetActive("Primary");
        };

        Assert.Throws<WalletSwitchPendingException>(() => binding.BindActiveForWrite());
    }

    [Fact]
    public void A_bound_wallet_is_recognised_for_as_long_as_it_stays_the_active_one()
    {
        var ctx = new Context("Savings");
        var binding = new WalletBinding(ctx, new WalletSwitchNotice());
        var bound = binding.BindActiveForWrite();

        Assert.True(binding.IsStillActive(bound, out var now));
        Assert.Equal("Savings", now);

        ctx.SetActive("Primary");                              // a read healed, or the user picked another
        Assert.False(binding.IsStillActive(bound, out now));
        Assert.Equal("Primary", now);                          // for the caller's message

        ctx.SetActive("Savings");
        Assert.True(binding.IsStillActive(bound, out _));
    }

    [Fact]
    public void No_wallet_chosen_and_the_default_endpoint_are_the_same_binding()
    {
        var ctx = new Context(null);
        var binding = new WalletBinding(ctx, new WalletSwitchNotice());

        Assert.True(binding.IsStillActive("", out var now));
        Assert.Equal("", now);

        ctx.SetActive("Primary");                              // a wallet appears: no longer the default endpoint
        Assert.False(binding.IsStillActive("", out _));
    }
}

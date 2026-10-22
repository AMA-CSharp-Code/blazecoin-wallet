using BlazecoinWallet.Core.Services;
using NSubstitute;

namespace BlazecoinWallet.Core.Tests;

/// <summary>
/// <see cref="MessageSigner"/> — the Sign Message sequence out of the Razor panel (SOLID audit A6, 2026-10-22). Pins:
/// the owning wallet is resolved on EVERY call (the panel's cache sent a second address's sign to the first address's
/// wallet); an encrypted wallet is unlocked, signed through, verified and relocked — relocked even when the sign throws;
/// an unencrypted wallet is never unlocked or locked; no passphrase on a locked wallet asks for one and touches nothing;
/// RPC −14 reads as a wrong passphrase; no owner is reported as such.
/// </summary>
public class MessageSignerTests
{
    private const string A = "BQfirstAddressAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string B = "BsecondAddressBBBBBBBBBBBBBBBBBBBB";
    private const string Msg = "blazecoin-verify:621ec0bf94d66ec8cbaf1b852150002c";

    private static IMessageSigningRpc Rpc(string? ownerOfA = "w-a", string? ownerOfB = "w-b", bool? lockedA = true, bool? lockedB = true)
    {
        var rpc = Substitute.For<IMessageSigningRpc>();
        rpc.FindWalletOwningAsync(A, Arg.Any<CancellationToken>()).Returns(ownerOfA);
        rpc.FindWalletOwningAsync(B, Arg.Any<CancellationToken>()).Returns(ownerOfB);
        rpc.IsLockedInAsync("w-a", Arg.Any<CancellationToken>()).Returns(lockedA);
        rpc.IsLockedInAsync("w-b", Arg.Any<CancellationToken>()).Returns(lockedB);
        rpc.SignMessageInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(ci => $"sig-by-{ci.ArgAt<string>(0)}");
        rpc.VerifyMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return rpc;
    }

    [Fact]
    public async Task The_owning_wallet_is_resolved_on_every_call_so_a_changed_address_signs_through_its_own_wallet()
    {
        var rpc = Rpc();
        var signer = new MessageSigner(rpc);

        // First attempt: address A, no passphrase → stops asking for one (wallet w-a).
        var first = await signer.SignAsync(A, Msg, passphrase: null);
        Assert.Equal(SignOutcome.PassphraseNeeded, first.Outcome);
        Assert.Equal("w-a", first.Wallet);

        // The user edits the address to B and supplies B's passphrase: everything must go to w-b.
        var second = await signer.SignAsync(B, Msg, "pass-b");

        Assert.Equal(SignOutcome.Signed, second.Outcome);
        Assert.Equal("w-b", second.Wallet);
        Assert.Equal("sig-by-w-b", second.Signature);
        await rpc.Received(1).UnlockInAsync("w-b", "pass-b", MessageSigner.UnlockSeconds, Arg.Any<CancellationToken>());
        await rpc.DidNotReceive().UnlockInAsync("w-a", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await rpc.Received(1).SignMessageInAsync("w-b", B, Msg, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_encrypted_wallet_is_unlocked_signed_verified_and_relocked()
    {
        var rpc = Rpc();
        var r = await new MessageSigner(rpc).SignAsync(A, Msg, "pass-a");

        Assert.Equal(SignOutcome.Signed, r.Outcome);
        Assert.True(r.Verified);
        Received.InOrder(() =>
        {
            rpc.UnlockInAsync("w-a", "pass-a", MessageSigner.UnlockSeconds, Arg.Any<CancellationToken>());
            rpc.SignMessageInAsync("w-a", A, Msg, Arg.Any<CancellationToken>());
            rpc.VerifyMessageAsync(A, "sig-by-w-a", Msg, Arg.Any<CancellationToken>());
            rpc.LockInAsync("w-a", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task The_wallet_is_relocked_even_when_the_sign_throws()
    {
        var rpc = Rpc();
        rpc.SignMessageInAsync("w-a", A, Msg, Arg.Any<CancellationToken>()).Returns<string>(_ => throw new RpcException(-4, "signing failed"));

        await Assert.ThrowsAsync<RpcException>(() => new MessageSigner(rpc).SignAsync(A, Msg, "pass-a"));

        await rpc.Received(1).LockInAsync("w-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unencrypted_wallet_is_never_unlocked_or_locked()
    {
        var rpc = Rpc(lockedA: false);
        var r = await new MessageSigner(rpc).SignAsync(A, Msg, passphrase: null);

        Assert.Equal(SignOutcome.Signed, r.Outcome);
        await rpc.DidNotReceiveWithAnyArgs().UnlockInAsync(default!, default!, default, default);
        await rpc.DidNotReceiveWithAnyArgs().LockInAsync(default!, default);
    }

    [Fact]
    public async Task A_locked_wallet_without_a_passphrase_asks_for_one_and_touches_nothing()
    {
        var rpc = Rpc();
        var r = await new MessageSigner(rpc).SignAsync(A, Msg, "");

        Assert.Equal(SignOutcome.PassphraseNeeded, r.Outcome);
        Assert.Equal("w-a", r.Wallet);
        await rpc.DidNotReceiveWithAnyArgs().UnlockInAsync(default!, default!, default, default);
        await rpc.DidNotReceiveWithAnyArgs().SignMessageInAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_refused_passphrase_is_reported_and_nothing_is_signed()
    {
        var rpc = Rpc();
        rpc.UnlockInAsync("w-a", "wrong", MessageSigner.UnlockSeconds, Arg.Any<CancellationToken>())
           .Returns(_ => throw new RpcException(-14, "Error: The wallet passphrase entered was incorrect."));

        var r = await new MessageSigner(rpc).SignAsync(A, Msg, "wrong");

        Assert.Equal(SignOutcome.WrongPassphrase, r.Outcome);
        await rpc.DidNotReceiveWithAnyArgs().SignMessageInAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task No_loaded_wallet_owning_the_address_is_reported_as_such()
    {
        var rpc = Rpc(ownerOfA: null);
        var r = await new MessageSigner(rpc).SignAsync(A, Msg, "pass");

        Assert.Equal(SignOutcome.NoWalletOwnsAddress, r.Outcome);
        Assert.Null(r.Wallet);
        await rpc.DidNotReceiveWithAnyArgs().IsLockedInAsync(default!, default);
    }
}

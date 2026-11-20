using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Bug-bounty round eighteen (2026-11-20), the lite wallet's finding: restore discovery stored
/// whatever its scan reached. A gateway that called every address "used" during one restore
/// pushed the stored receive count to 1,000, five times <see cref="AddressRotation.MaxRevealed"/>;
/// the count was loaded again on every unlock, and every balance read then asked about 1,000
/// addresses and ran into the gateway's own rate limit. The change and post-quantum chains had
/// the same gap. The reveal path already held the cap; the restore path now does too.
/// </summary>
public class BountyRound18LiteTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>A gateway that answers "used" for whatever the test says, and never has a coin to show for it.</summary>
    private sealed class ClaimingGateway : IChainReader
    {
        public Func<string, bool> Used = _ => false;
        public int AddressReads;
        public int UtxoReads;

        public bool SupportsHistory => false;
        public bool SupportsChainVerification => true;

        public Task<LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default)
        {
            AddressReads++;
            return Task.FromResult<LiteAddressSummary?>(Used(address) ? new LiteAddressSummary(0, 1, 1, 1) : null);
        }

        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
        {
            UtxoReads++;
            return Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        }

        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);

        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static HashSet<string> Addresses(Func<int, string> address, int count) =>
        Enumerable.Range(0, count).Select(address).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public async Task A_gateway_that_calls_every_address_used_cannot_push_a_restore_past_the_cap()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var gateway = new ClaimingGateway { Used = _ => true };
        var state = new InMemoryWalletStateStore();
        var rotation = new AddressRotation(gateway, state, new LiteTxVerifier(gateway));

        await rotation.DiscoverFromChainAsync(wallet, discoverPq: true);

        Assert.Equal(AddressRotation.MaxRevealed, rotation.RevealedCount);          // was 1,000
        Assert.Equal(AddressRotation.MaxRevealed, rotation.ChangeCount);            // was 1,000
        Assert.Equal(AddressRotation.MaxRevealed, rotation.PqRevealedCount);        // was 1,000
        Assert.Equal(AddressRotation.MaxRevealed, await state.GetRevealedAddressCountAsync());   // and what is STORED is inside it too
        Assert.Equal(AddressRotation.MaxRevealed, await state.GetChangeAddressCountAsync());
        Assert.Equal(AddressRotation.MaxRevealed, await state.GetPqAddressCountAsync());
        Assert.Equal(AddressRotation.MaxRevealed, rotation.UsedChangeAddresses(wallet).Count());

        // The lie costs this one restore some reads and nothing after it: the receive and
        // post-quantum scans stop at the cap, the change scan runs on to its ceiling asking for proof.
        Assert.Equal(200 + 1000 + 200, gateway.AddressReads);
        Assert.Equal(800, gateway.UtxoReads);
    }

    [Fact]
    public async Task A_count_stored_past_the_cap_by_an_older_build_is_brought_back_on_unlock()
    {
        var gateway = new ClaimingGateway();
        var state = new InMemoryWalletStateStore();
        await state.SetRevealedAddressCountAsync(1000);
        await state.SetChangeAddressCountAsync(5000);
        await state.SetPqAddressCountAsync(1000);
        var rotation = new AddressRotation(gateway, state, new LiteTxVerifier(gateway));

        await rotation.LoadAsync();

        Assert.Equal(AddressRotation.MaxRevealed, rotation.RevealedCount);
        Assert.Equal(1000, rotation.ChangeCount);                                   // the change chain's own ceiling
        Assert.Equal(AddressRotation.MaxRevealed, rotation.PqRevealedCount);
        Assert.Equal(AddressRotation.MaxRevealed, await state.GetRevealedAddressCountAsync());   // written back: the next unlock starts clean
        Assert.Equal(1000, await state.GetChangeAddressCountAsync());
        Assert.Equal(AddressRotation.MaxRevealed, await state.GetPqAddressCountAsync());
        Assert.Equal(0, gateway.AddressReads);                                      // an unlock still reads nothing from the network
    }

    [Fact]
    public async Task Counts_inside_the_limits_are_loaded_as_they_were_stored()
    {
        var state = new InMemoryWalletStateStore();
        await state.SetRevealedAddressCountAsync(37);
        await state.SetChangeAddressCountAsync(412);                                // an old, busy wallet: change legitimately past 200
        await state.SetPqAddressCountAsync(3);
        var rotation = new AddressRotation(new ClaimingGateway(), state);

        await rotation.LoadAsync();

        Assert.Equal((37, 412, 3), (rotation.RevealedCount, rotation.ChangeCount, rotation.PqRevealedCount));
        Assert.Equal(412, await state.GetChangeAddressCountAsync());

        await state.SetRevealedAddressCountAsync(0);                                // nothing stored yet: slot 0, as before
        await rotation.LoadAsync();
        Assert.Equal(1, rotation.RevealedCount);
        Assert.Equal(1, await state.GetRevealedAddressCountAsync());
    }

    [Fact]
    public async Task An_ordinary_restore_through_a_gateway_finds_what_it_found_before()
    {
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var used = new HashSet<string>(StringComparer.Ordinal)
        {
            wallet.GetReceiveAddress(0), wallet.GetReceiveAddress(1), wallet.GetReceiveAddress(3),   // a gap at 2: the scan looks past it
            wallet.GetChangeAddress(0), wallet.GetChangeAddress(1),
            wallet.GetPqAddress(0),
        };
        var gateway = new ClaimingGateway { Used = used.Contains };
        var rotation = new AddressRotation(gateway, new InMemoryWalletStateStore(), new LiteTxVerifier(gateway));

        await rotation.DiscoverFromChainAsync(wallet, discoverPq: true);

        Assert.Equal((4, 2, 1), (rotation.RevealedCount, rotation.ChangeCount, rotation.PqRevealedCount));
        Assert.Equal(0, gateway.UtxoReads);                                         // below the cap a gateway's word is still enough
    }

    [Fact]
    public async Task On_your_own_node_a_busy_wallets_change_past_the_cap_is_still_found()
    {
        // No verifier = a personal node: its own word is the proof, so a change chain that really
        // ran to 251 addresses is restored whole. The receive chain stops at the cap either way:
        // this wallet never reveals past it.
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var used = Addresses(i => wallet.GetChangeAddress(i), 251);
        used.UnionWith(Addresses(i => wallet.GetReceiveAddress(i), 251));
        var node = new ClaimingGateway { Used = used.Contains };
        var rotation = new AddressRotation(node, new InMemoryWalletStateStore());

        await rotation.DiscoverFromChainAsync(wallet);

        Assert.Equal(251, rotation.ChangeCount);
        Assert.Equal(AddressRotation.MaxRevealed, rotation.RevealedCount);
        Assert.Equal(0, rotation.PqRevealedCount);
    }

    [Fact]
    public async Task Through_a_gateway_change_past_the_cap_counts_only_with_a_coin_the_wallet_can_prove()
    {
        // 300 change addresses "used" on the gateway's word, none with a provable coin: the scan
        // walks all 300 (and the 20 unused after them) but the count stops where its word stops counting.
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        var gateway = new ClaimingGateway { Used = Addresses(i => wallet.GetChangeAddress(i), 300).Contains };
        var rotation = new AddressRotation(gateway, new InMemoryWalletStateStore(), new LiteTxVerifier(gateway));

        await rotation.DiscoverFromChainAsync(wallet);

        Assert.Equal(AddressRotation.MaxRevealed, rotation.ChangeCount);
        Assert.Equal(100, gateway.UtxoReads);                                       // proof was asked for each claim from 200 to 299
        Assert.Equal(1, rotation.RevealedCount);
    }
}

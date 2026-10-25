using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>
/// The tx page's Chain Verification card (bug-bounty report 2026-10-25): a merkle proof whose
/// block is NOT on the wallet's verified header chain must read as unconfirmed — a standalone
/// proof is cheap to forge, so it never earns a "valid"/"verified" claim on its own.
/// </summary>
public class TxDetailProofUiTests : LiteUiTestBase
{
    // A real mainnet coinbase (block 4,149,841) + its real merkle proof — the same harvest as
    // the Core suite's HeaderChainFixture. Well-formed, but no header chain is wired here.
    private const string ProofTxId = "dc39c09927d36a441b5e5f1e0bf65733cb1f69042974064683e7754e6399d158";
    private const string ProofRawTx = "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff1b0351523f626c617a65636f696e2e636f2e756b0000000101000000ffffffff01a07fb533010000001976a914784c27627312be1c6ed56b98bac798cc97c54f2888ac00000000";
    private const string ProofHex = "0000002016b8c71d5b555ddf678136da52fcc4a6b307a26b85b103c62c7ea4af33f8a4a358d199634e75e7834606742904691fcb3357f60b1e5f5e1b446ad32799c039dcec81616acdad331cdc935a03010000000158d199634e75e7834606742904691fcb3357f60b1e5f5e1b446ad32799c039dc0101";

    /// <summary>Gateway-mode reader serving the fixture tx and its proof, nothing else.</summary>
    private sealed class ProofFakeReader : IChainReader
    {
        public bool SupportsHistory => true;
        public bool SupportsChainVerification => true;
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) =>
            Task.FromResult<string?>(txId == ProofTxId ? ProofRawTx : null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) =>
            Task.FromResult<string?>(txId == ProofTxId ? ProofHex : null);
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => Task.FromResult<LiteAddressSummary?>(null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteChainUtxo>>([]);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
    }

    [Fact]
    public void A_proof_off_the_verified_chain_reads_as_not_yet_confirmed()
    {
        var reader = new ProofFakeReader();
        Wire(new LoadedVault(), reader, new FakeRelay());
        // Last registration wins: the same service, now with a verifier (gateway mode) and no
        // header chain — so the proof is well-formed but cannot be placed on a verified chain.
        Services.AddSingleton(new LiteWalletService(new LoadedVault(), reader, new FakeRelay(),
            verifier: new LiteTxVerifier(reader)));

        var page = RenderComponent<TxDetail>(p => p.Add(x => x.TxId, ProofTxId));

        page.WaitForAssertion(() => Assert.Contains("Not yet independently confirmed", page.Markup));
        Assert.DoesNotContain("Verified on your own chain", page.Markup);
        Assert.DoesNotContain("proof is valid", page.Markup);
    }

    /// <summary>A header feed whose next header does NOT build on the proof's block — what a
    /// gateway serving a block that isn't the chain's at the claimed height looks like.</summary>
    private sealed class ContradictingHeaders : IHeaderReader
    {
        // The proof block's own header: its prev-hash is the block BELOW it, never the block itself.
        public Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([ProofHex[..160]]);
    }

    [Fact]
    public void A_proof_contradicting_the_verified_chain_shows_a_warning()
    {
        var reader = new ProofFakeReader();
        Wire(new LoadedVault(), reader, new FakeRelay());
        var chain = new HeaderChainSync(new ContradictingHeaders());
        Services.AddSingleton(new LiteWalletService(new LoadedVault(), reader, new FakeRelay(),
            verifier: new LiteTxVerifier(reader, headerChain: chain)));

        // Claimed one below the shipped anchor at 4,150,000: the single header up to it won't link.
        // The height arrives the way Home sends it — as ?h= on the route.
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"tx/{ProofTxId}?h=4149999");
        var page = RenderComponent<TxDetail>(p => p.Add(x => x.TxId, ProofTxId));

        page.WaitForAssertion(() => Assert.Contains("Not on the real chain", page.Markup));
        Assert.DoesNotContain("Verified on your own chain", page.Markup);
        Assert.DoesNotContain("Not yet independently confirmed", page.Markup);
    }
}

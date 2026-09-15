using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.UI.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

public class ProvenancePageTests : LiteUiTestBase
{
    /// <summary>FakeReader that ALSO serves ancestry — the gateway-mode shape.</summary>
    private sealed class AncestryFakeReader : IChainReader, IAncestryReader
    {
        public long SpendableSats = 10_000_000_000;
        public LiteAncestryResult Next = LiteAncestryResult.Fail("not scripted");
        public int Calls;

        public bool SupportsHistory => true;
        public bool SupportsChainVerification => false;

        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default)
            => Task.FromResult<LiteAddressSummary?>(new LiteAddressSummary(SpendableSats, SpendableSats, 0, 1));
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteChainUtxo>>(
                SpendableSats > 0 ? [new LiteChainUtxo(new string('a', 64), 0, SpendableSats, 10, 100, false)] : []);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<LiteAncestryResult> GetAncestryAsync(string address, CancellationToken ct = default)
        { Calls++; return Task.FromResult(Next); }
    }

    private static LiteAncestryReport Report(string address, long sats, bool truncated = false)
    {
        var minted = new DateTime(2014, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        var year = new LiteVintageRow("2014", "2014", "Year", sats, 1, 413, minted, minted);
        var month = new LiteVintageRow("2014-05", "May 2014", "Year", sats, 1, 413, minted, minted);
        var model = new LiteVintageModel(sats, [year], [month], []);
        return new LiteAncestryReport(address, sats, 1, 3, model, model, truncated);
    }

    [Fact]
    public void Personal_node_mode_shows_the_unavailable_note_instead_of_a_trace_button()
    {
        // The base FakeReader does NOT implement IAncestryReader — personal-node shape.
        Wire(new LoadedVault(), new FakeReader(), new FakeRelay());

        var page = RenderComponent<Provenance>();

        page.WaitForAssertion(() =>
            Assert.Contains("personal-node mode", page.Markup));
        Assert.DoesNotContain("Trace provenance", page.Markup);
    }

    [Fact]
    public void Trace_renders_the_merged_catalogue()
    {
        var reader = new AncestryFakeReader();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new LiteWalletService(new LoadedVault(), reader, new FakeRelay());
        Services.AddSingleton(svc);
        Services.AddSingleton<IChainReader>(reader);
        Services.AddSingleton<IUnitSettings>(new InMemoryUnitSettings());

        var page = RenderComponent<Provenance>();
        page.WaitForAssertion(() => Assert.Contains("Trace provenance", page.Markup));

        reader.Next = LiteAncestryResult.Ok(Report("addr", 10_000_000_000));
        page.Find("button.lite-btn").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Mint Ancestry", page.Markup);
            Assert.Contains("2014", page.Markup);
            Assert.Contains("May 2014", page.Markup);
            Assert.Contains("100 BLZ held", page.Markup);
        });
        Assert.True(reader.Calls >= 1);
    }

    [Fact]
    public void A_truncated_trace_shows_the_depth_limit_notice()
    {
        var reader = new AncestryFakeReader();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new LiteWalletService(new LoadedVault(), reader, new FakeRelay());
        Services.AddSingleton(svc);
        Services.AddSingleton<IChainReader>(reader);
        Services.AddSingleton<IUnitSettings>(new InMemoryUnitSettings());

        var page = RenderComponent<Provenance>();
        page.WaitForAssertion(() => Assert.Contains("Trace provenance", page.Markup));

        reader.Next = LiteAncestryResult.Ok(Report("addr", 10_000_000_000, truncated: true));
        page.Find("button.lite-btn").Click();

        page.WaitForAssertion(() =>
            Assert.Contains("depth limit", page.Markup));
    }

    [Fact]
    public void A_failed_trace_shows_the_servers_reason()
    {
        var reader = new AncestryFakeReader();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new LiteWalletService(new LoadedVault(), reader, new FakeRelay());
        Services.AddSingleton(svc);
        Services.AddSingleton<IChainReader>(reader);
        Services.AddSingleton<IUnitSettings>(new InMemoryUnitSettings());

        var page = RenderComponent<Provenance>();
        page.WaitForAssertion(() => Assert.Contains("Trace provenance", page.Markup));

        reader.Next = LiteAncestryResult.Fail("The server is busy with another trace — try again shortly.");
        page.Find("button.lite-btn").Click();

        page.WaitForAssertion(() =>
            Assert.Contains("busy with another trace", page.Markup));
        Assert.DoesNotContain("Mint Ancestry", page.Markup);
    }
}

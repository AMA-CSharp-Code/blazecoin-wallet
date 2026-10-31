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

    private static LiteAncestryReport Report(string address, long sats, bool truncated = false, string? truncatedBy = null)
    {
        var minted = new DateTime(2014, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        var year = new LiteVintageRow("2014", "2014", "Year", sats, 1, 413, minted, minted);
        var month = new LiteVintageRow("2014-05", "May 2014", "Year", sats, 1, 413, minted, minted);
        var model = new LiteVintageModel(sats, [year], [month], []);
        return new LiteAncestryReport(address, sats, 1, 3, model, model, truncated, truncatedBy);
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


    [Fact]
    public void A_time_limited_trace_tells_the_user_a_re_trace_goes_deeper()
    {
        // 2026-10-27: the gateway traces in timed slices and caches what it read, so the
        // honest notice is "a floor — re-trace to go deeper", not the depth-limit wording.
        var reader = new AncestryFakeReader();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new LiteWalletService(new LoadedVault(), reader, new FakeRelay());
        Services.AddSingleton(svc);
        Services.AddSingleton<IChainReader>(reader);
        Services.AddSingleton<IUnitSettings>(new InMemoryUnitSettings());

        var page = RenderComponent<Provenance>();
        page.WaitForAssertion(() => Assert.Contains("Trace provenance", page.Markup));

        reader.Next = LiteAncestryResult.Ok(Report("addr", 10_000_000_000, truncated: true, truncatedBy: "time"));
        page.Find("button.lite-btn").Click();

        page.WaitForAssertion(() => Assert.Contains("time limit", page.Markup));
        Assert.Contains("Re-trace to go deeper", page.Markup);
        Assert.DoesNotContain("depth limit", page.Markup);
        // The catalogue is still shown — a floor is a result, not an error.
        Assert.Contains("Mint Ancestry", page.Markup);
        Assert.Contains("Re-trace", page.Find("button.lite-btn").TextContent);
    }

    [Fact]
    public void A_read_capped_trace_keeps_the_depth_limit_notice_and_promises_nothing()
    {
        var reader = new AncestryFakeReader();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new LiteWalletService(new LoadedVault(), reader, new FakeRelay());
        Services.AddSingleton(svc);
        Services.AddSingleton<IChainReader>(reader);
        Services.AddSingleton<IUnitSettings>(new InMemoryUnitSettings());

        var page = RenderComponent<Provenance>();
        page.WaitForAssertion(() => Assert.Contains("Trace provenance", page.Markup));

        reader.Next = LiteAncestryResult.Ok(Report("addr", 10_000_000_000, truncated: true, truncatedBy: "reads"));
        page.Find("button.lite-btn").Click();

        page.WaitForAssertion(() => Assert.Contains("depth limit", page.Markup));
        Assert.DoesNotContain("Re-trace to go deeper", page.Markup);
    }

    [Fact]
    public void A_timed_out_trace_shows_the_timeout_reason_from_the_data_service()
    {
        var reader = new AncestryFakeReader();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new LiteWalletService(new LoadedVault(), reader, new FakeRelay());
        Services.AddSingleton(svc);
        Services.AddSingleton<IChainReader>(reader);
        Services.AddSingleton<IUnitSettings>(new InMemoryUnitSettings());

        var page = RenderComponent<Provenance>();
        page.WaitForAssertion(() => Assert.Contains("Trace provenance", page.Markup));

        reader.Next = LiteAncestryResult.Fail(
            "The gateway did not finish the trace in time — try again; it carries on from where it stopped.");
        page.Find("button.lite-btn").Click();

        page.WaitForAssertion(() => Assert.Contains("did not finish the trace in time", page.Markup));
        Assert.DoesNotContain("Mint Ancestry", page.Markup);
        Assert.False(page.Find("button.lite-btn").HasAttribute("disabled"));   // the retry is one tap away
    }
}

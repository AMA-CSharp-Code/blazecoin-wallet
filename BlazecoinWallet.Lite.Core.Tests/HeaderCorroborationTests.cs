using System.Net;
using System.Text.Json;
using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Core.Tests;

/// <summary>
/// Bug-bounty report 2026-11-07 (Juan Alfonsus): the two-hour clock rule of 1.3.4 is measured against the
/// device's clock while the Phoenix-413 anchor is frozen at the fork, so the latest time a header may
/// claim drifts away from the anchor at one second per second — one header stamped now+2h on top of an
/// ageing checkpoint eases every header after it exponentially, down to powLimit within two days of the
/// checkpoint's harvest. The first test pins that arithmetic against the shipped checkpoint; the rest
/// prove the fix: the verified tip moves only when a SECOND gateway reproduces the run's top header.
/// </summary>
public class HeaderCorroborationTests
{
    private static HeaderCheckpoint Checkpoint =>
        new(HeaderChainFixture.CheckpointHeight, HeaderChainFixture.CheckpointHash, HeaderChainFixture.CheckpointBits);

    private static BlockHeader Parse(string rawHex)
    {
        var factory = BlazecoinNetwork.Instance.Consensus.ConsensusFactory;
        var header = factory.CreateBlockHeader();
        header.ReadWrite(new BitcoinStream(Encoders.Hex.DecodeData(rawHex)) { ConsensusFactory = factory });
        return header;
    }

    private static string? HeaderAt(long height)
    {
        var i = (int)(height - (HeaderChainFixture.CheckpointHeight + 1));
        return i >= 0 && i < HeaderChainFixture.Headers.Length ? HeaderChainFixture.Headers[i] : null;
    }

    private static IReadOnlyList<string> Range(long fromHeight, int count)
    {
        var start = (int)(fromHeight - (HeaderChainFixture.CheckpointHeight + 1));
        if (start < 0 || start >= HeaderChainFixture.Headers.Length) return [];
        return HeaderChainFixture.Headers.Skip(start).Take(count).ToList();
    }

    // ── The finding ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shipped_checkpoint_eases_to_powLimit_within_days_when_only_the_clock_bounds_the_time()
    {
        // Phoenix-413's target for the block AFTER a header is an exact function of that header's time
        // and the fork anchor baked into the shipped checkpoint. A header dated now+2h is what rule 5
        // allows; this is the target it buys for the next one, as the checkpoint ages.
        var cp = HeaderChainSync.MainnetCheckpoint;
        var checkpointTarget = BlazecoinDifficulty.CompactToTarget(cp.Bits);
        var powLimitBits = BlazecoinDifficulty.TargetToCompact(BlazecoinDifficulty.PowLimit);

        uint BitsAfterHeaderDated(long time) =>
            BlazecoinDifficulty.PhoenixNextBits(cp.AnchorBits, cp.AnchorParentTime, BlazecoinDifficulty.PhoenixActivationHeight, cp.Height + 2, time);

        // The honest next header, 30 s after the checkpoint: the target barely moves.
        var honest = BlazecoinDifficulty.CompactToTarget(BitsAfterHeaderDated(cp.Time + 30));
        Assert.True(honest < checkpointTarget * 2 && honest > checkpointTarget / 2);

        // A day after the harvest, stamped two hours ahead of the clock: dozens of times easier (the
        // report measured 51× at about seventeen hours; the rule doubles every 3.44 hours of age).
        // Relative to the checkpoint's own time, so the pin holds for every release's checkpoint (it was a calendar date).
        var aDayOn = cp.Time + 24 * 3600 + HeaderChainValidator.MaxFutureBlockTimeSeconds;
        var eased = BlazecoinDifficulty.CompactToTarget(BitsAfterHeaderDated(aDayOn));
        Assert.True(eased > checkpointTarget * 50, $"eased only {eased / checkpointTarget}×");

        // Three days on, the rule hands out powLimit: ~2^20 scrypt hashes per forged header.
        var threeDaysOn = cp.Time + 3 * 24 * 3600 + HeaderChainValidator.MaxFutureBlockTimeSeconds;
        Assert.Equal(powLimitBits, BitsAfterHeaderDated(threeDaysOn));
    }

    // ── The fix: a second gateway must reproduce the run's top header ─────────────────────────

    /// <summary>A gateway feed (the real fixture headers) that is also the second gateway, scripted.</summary>
    private sealed class TwoGatewayFeed : IHeaderReader, IHeaderCrossCheck
    {
        public Func<long, HeaderSecondOpinion> Second = h => new(SecondGateway.Answered, HeaderAt(h));
        public Func<long, int, IReadOnlyList<string>>? Serve;
        public readonly List<long> Asked = [];
        public readonly List<long> Served = [];
        /// <summary>Which gateway served the batch starting at a height (null = the feed cannot say).</summary>
        public Func<long, Uri?> ServedBy = _ => null;
        /// <summary>Who each second-opinion question named as having served the headers.</summary>
        public readonly List<Uri[]> AskedAbout = [];

        public Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default)
        {
            Served.Add(fromHeight);
            return Task.FromResult(Serve?.Invoke(fromHeight, count) ?? Range(fromHeight, count));
        }

        public async Task<HeaderBatch> GetHeaderBatchAsync(long fromHeight, int count,
            IReadOnlyCollection<Uri>? notFrom = null, CancellationToken ct = default) =>
            new(await GetHeadersAsync(fromHeight, count, ct), ServedBy(fromHeight));

        public Task<HeaderSecondOpinion> HeaderFromAnotherGatewayAsync(long height, IReadOnlyCollection<Uri> servedBy, CancellationToken ct = default)
        {
            Asked.Add(height);
            AskedAbout.Add([.. servedBy]);
            return Task.FromResult(Second(height));
        }
    }

    private static (HeaderChainSync Sync, InMemoryWalletStateStore Store) SyncOver(TwoGatewayFeed feed)
    {
        var store = new InMemoryWalletStateStore();
        var sync = new HeaderChainSync(feed, store, BlazecoinNetwork.Instance, Checkpoint, crossCheck: feed)   // the second gateway is handed in
        {
            Clock = () => Parse(HeaderChainFixture.Headers[^1]).BlockTime, // the fixture is in the past; the clock rule must not interfere
            StateKeys = HeaderStateKeys.Keys,                              // a wallet is attached: the tip is stored, under its seal
        };
        return (sync, store);
    }

    [Fact]
    public async Task A_second_gateway_that_reproduces_the_top_header_lets_the_tip_move()
    {
        var feed = new TwoGatewayFeed();
        var (sync, store) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.Confirmed, sync.LastCorroboration);
        Assert.False(sync.TipHeld);
        Assert.Equal(new[] { HeaderChainFixture.TipHeight }, feed.Asked);         // one question, at the run's top
        Assert.StartsWith($"{HeaderChainFixture.TipHeight}:", HeaderStateKeys.OpenTip(await store.GetVerifiedCheckpointAsync()));
    }

    [Fact]
    public async Task A_second_gateway_with_a_different_block_at_that_height_holds_the_tip()
    {
        var feed = new TwoGatewayFeed { Second = h => new(SecondGateway.Answered, HeaderAt(h - 1)) }; // a real header — the wrong one
        var (sync, store) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);  // 327 headers with valid work, none adopted
        Assert.Equal(HeaderCorroboration.Contradicted, sync.LastCorroboration);
        Assert.True(sync.TipHeld);
        Assert.Contains("different block", sync.TipHeldMessage);
        // Nothing of the held run leaked into the index or the store.
        Assert.Null(sync.VerifiedHashAt(HeaderChainFixture.TipHeight));
        Assert.Null(sync.VerifiedHeightOf(Parse(HeaderChainFixture.Headers[0]).GetHash().ToString()));
        Assert.Null(await store.GetVerifiedCheckpointAsync());

        // When the second gateway comes round to the same chain, the next sync moves the tip.
        feed.Second = h => new(SecondGateway.Answered, HeaderAt(h));
        await sync.SyncAsync();
        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        Assert.False(sync.TipHeld);
    }

    [Fact]
    public async Task A_second_gateway_that_does_not_answer_holds_the_tip()
    {
        var feed = new TwoGatewayFeed { Second = _ => new(SecondGateway.Unavailable, null) };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.Unavailable, sync.LastCorroboration);
        Assert.Contains("could not be confirmed", sync.TipHeldMessage);
    }

    [Fact]
    public async Task A_second_gateway_that_has_not_seen_the_height_yet_holds_the_tip()
    {
        // It answered, but has nothing at that height: it is behind the gateway in use, and "behind"
        // is not agreement. The next sync asks again.
        var feed = new TwoGatewayFeed { Second = _ => new(SecondGateway.Answered, null) };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.Unavailable, sync.LastCorroboration);
    }

    [Fact]
    public async Task A_second_gateway_whose_header_does_not_parse_is_no_confirmation()
    {
        var feed = new TwoGatewayFeed { Second = _ => new(SecondGateway.Answered, "not-a-header") };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.Unavailable, sync.LastCorroboration);
    }

    [Fact]
    public async Task A_second_gateway_that_throws_holds_the_tip()
    {
        var feed = new TwoGatewayFeed { Second = _ => throw new InvalidOperationException("boom") };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.Unavailable, sync.LastCorroboration);
    }

    [Fact]
    public async Task With_no_second_gateway_configured_the_run_stands_on_its_own_work()
    {
        // The documented limit: one gateway configured means one gateway's chain — as before the report.
        var feed = new TwoGatewayFeed { Second = _ => new(SecondGateway.None, null) };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.NoSecondGateway, sync.LastCorroboration);
        Assert.False(sync.TipHeld);
        Assert.Null(sync.TipHeldMessage);
    }

    [Fact]
    public async Task A_feed_without_the_capability_is_treated_as_a_single_gateway()
    {
        var sync = new HeaderChainSync(new PlainFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint)
        {
            Clock = () => Parse(HeaderChainFixture.Headers[^1]).BlockTime,
        };

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.NoSecondGateway, sync.LastCorroboration);
    }

    private sealed class PlainFeed : IHeaderReader
    {
        public Task<IReadOnlyList<string>> GetHeadersAsync(long fromHeight, int count, CancellationToken ct = default) =>
            Task.FromResult(Range(fromHeight, count));
    }

    [Fact]
    public async Task A_feed_that_contradicts_the_run_it_just_served_moves_nothing_and_is_not_believed()
    {
        // Batch one is genuine; the first header of batch two does not build on it. Before the fix the
        // first batch was already committed; now the whole run is dropped — and no second gateway is
        // even asked, because there is nothing worth asking about.
        var feed = new TwoGatewayFeed
        {
            Serve = (from, count) => from == HeaderChainFixture.CheckpointHeight + 251
                ? [HeaderChainFixture.Headers[0]]      // a real header, but it links to the checkpoint, not to header #250
                : Range(from, count),
        };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);
        Assert.Empty(feed.Asked);
        Assert.Null(sync.LastCorroboration);
        // ...and the feed's self-contradiction was not mistaken for a reorg of OUR tip: no rollback probe
        // (a request for the header at the tip's own height) was made.
        Assert.DoesNotContain(HeaderChainFixture.CheckpointHeight, feed.Served);
        Assert.Equal(new[] { HeaderChainFixture.CheckpointHeight + 1, HeaderChainFixture.CheckpointHeight + 251 }, feed.Served);
    }

    [Fact]
    public async Task A_run_cut_short_by_a_bad_header_is_still_put_to_the_second_gateway_before_it_counts()
    {
        // Header #100 has its nonce broken: the validator stops at #99, and that partial run is what
        // the second gateway is asked about — at ITS top, not the feed's claimed tip.
        var tampered = (string[])HeaderChainFixture.Headers.Clone();
        tampered[100] = tampered[100][..152] + "00000000";
        var feed = new TwoGatewayFeed
        {
            Serve = (from, count) =>
            {
                var start = (int)(from - (HeaderChainFixture.CheckpointHeight + 1));
                return start < 0 || start >= tampered.Length ? [] : tampered.Skip(start).Take(count).ToList();
            },
        };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        var expectedTop = HeaderChainFixture.CheckpointHeight + 100;
        Assert.Equal(expectedTop, sync.VerifiedTipHeight);
        Assert.Equal(new[] { expectedTop }, feed.Asked);
        Assert.Equal(HeaderCorroboration.Confirmed, sync.LastCorroboration);
    }

    // ── The gateway data service: the second opinion really comes from a different gateway ───

    private static readonly Uri GatewayA = new("https://a.test/");
    private static readonly Uri GatewayB = new("https://b.test/");

    /// <summary>One fake gateway: serves the fixture headers (up to a cap) at the real endpoint shape.</summary>
    private sealed class Node
    {
        public bool Reachable = true;
        public long HighestKnown = HeaderChainFixture.TipHeight;
        public readonly List<string> Paths = [];
        /// <summary>Requests this gateway lets drop although it is up (it chooses what to answer).</summary>
        public Func<HttpRequestMessage, bool>? Refuse;

        public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (!Reachable || Refuse?.Invoke(request) == true) throw new HttpRequestException("down");
            var path = request.RequestUri.AbsolutePath; // /indexer/api/headers/{from}
            var from = long.Parse(path[(path.LastIndexOf('/') + 1)..]);
            var count = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["count"] ?? "1");
            var headers = Range(from, count).Where((_, i) => from + i <= HighestKnown).ToList();
            var json = JsonSerializer.Serialize(new { headers });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Fleet : HttpMessageHandler
    {
        public readonly Node A = new(), B = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            (request.RequestUri!.Host == "a.test" ? A : B).HandleAsync(request);
    }

    private static IndexerDataService Indexer(Fleet fleet, params Uri[] gateways)
    {
        var failover = new GatewayFailoverHandler(gateways.Select(g => g.ToString()), fleet);
        return new IndexerDataService(new HttpClient(failover) { BaseAddress = failover.Primary }, NullLogger<IndexerDataService>.Instance);
    }

    [Fact]
    public async Task The_second_opinion_comes_from_the_gateway_that_did_not_serve_the_batch()
    {
        var fleet = new Fleet();
        var indexer = Indexer(fleet, GatewayA, GatewayB);

        var batch = await indexer.GetHeaderBatchAsync(HeaderChainFixture.CheckpointHeight + 1, 10);
        Assert.Equal(10, batch.Headers.Count);
        Assert.Equal(GatewayA, batch.Source);                                     // the batch says who served it
        Assert.Single(fleet.A.Paths);
        Assert.Empty(fleet.B.Paths);

        var opinion = await indexer.HeaderFromAnotherGatewayAsync(HeaderChainFixture.CheckpointHeight + 10, [batch.Source!]);

        Assert.Equal(SecondGateway.Answered, opinion.Status);
        Assert.Equal(HeaderAt(HeaderChainFixture.CheckpointHeight + 10), opinion.RawHeader);
        Assert.Single(fleet.A.Paths);                                             // A was not asked again
        Assert.Equal(new[] { "/indexer/api/headers/4149850?count=1" }, fleet.B.Paths);   // B was
    }

    [Fact]
    public async Task A_second_gateway_behind_the_first_answers_with_nothing_at_that_height()
    {
        var fleet = new Fleet();
        fleet.B.HighestKnown = HeaderChainFixture.CheckpointHeight + 5;
        var indexer = Indexer(fleet, GatewayA, GatewayB);

        var opinion = await indexer.HeaderFromAnotherGatewayAsync(HeaderChainFixture.CheckpointHeight + 10, await ServedBatchAsync(indexer));

        Assert.Equal(SecondGateway.Answered, opinion.Status);
        Assert.Null(opinion.RawHeader);
    }

    [Fact]
    public async Task A_second_gateway_that_is_down_is_unavailable_not_absent()
    {
        var fleet = new Fleet();
        fleet.B.Reachable = false;
        var indexer = Indexer(fleet, GatewayA, GatewayB);

        Assert.Equal(SecondGateway.Unavailable,
            (await indexer.HeaderFromAnotherGatewayAsync(HeaderChainFixture.CheckpointHeight + 10, await ServedBatchAsync(indexer))).Status);
    }

    [Fact]
    public async Task With_one_gateway_configured_there_is_nobody_else_to_ask()
    {
        var fleet = new Fleet();
        var indexer = Indexer(fleet, GatewayA);

        Assert.Equal(SecondGateway.None,
            (await indexer.HeaderFromAnotherGatewayAsync(HeaderChainFixture.CheckpointHeight + 10, await ServedBatchAsync(indexer))).Status);
        Assert.Single(fleet.A.Paths);                                             // and A was not asked to vouch for itself
    }

    /// <summary>Fetches a batch and names who served it — what the sync hands to the cross-check
    /// (empty when the client cannot say: no failover list).</summary>
    private static async Task<Uri[]> ServedBatchAsync(IndexerDataService indexer)
    {
        var batch = await indexer.GetHeaderBatchAsync(HeaderChainFixture.CheckpointHeight + 1, 10);
        Assert.Equal(10, batch.Headers.Count);
        return batch.Source == null ? [] : [batch.Source];
    }

    /// <summary>A handler that names who answered but ignores the exclusion — every question goes to A.</summary>
    private sealed class DeafHandler(Fleet fleet) : DelegatingHandler(fleet)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var res = await base.SendAsync(request, ct);
            request.Options.Set(GatewayRouting.AnsweredBy, GatewayA);
            return res;
        }
    }

    [Fact]
    public async Task The_same_gateway_answering_again_is_not_a_second_opinion()
    {
        var fleet = new Fleet();
        var indexer = new IndexerDataService(new HttpClient(new DeafHandler(fleet)) { BaseAddress = GatewayA }, NullLogger<IndexerDataService>.Instance);
        var servedBy = await ServedBatchAsync(indexer);
        Assert.Equal([GatewayA], servedBy);

        var opinion = await indexer.HeaderFromAnotherGatewayAsync(HeaderChainFixture.CheckpointHeight + 10, servedBy);

        Assert.Equal(SecondGateway.None, opinion.Status);   // A vouching for A is nobody's second opinion
        Assert.Null(opinion.RawHeader);
    }

    [Fact]
    public async Task A_client_without_the_failover_handler_cannot_corroborate()
    {
        // No AnsweredBy stamp ⇒ the service cannot know who served the batch ⇒ nobody else to ask.
        var fleet = new Fleet();
        var indexer = new IndexerDataService(new HttpClient(fleet) { BaseAddress = GatewayA }, NullLogger<IndexerDataService>.Instance);
        var servedBy = await ServedBatchAsync(indexer);
        Assert.Empty(servedBy);

        Assert.Equal(SecondGateway.None, (await indexer.HeaderFromAnotherGatewayAsync(HeaderChainFixture.CheckpointHeight + 10, servedBy)).Status);
    }

    [Fact]
    public async Task End_to_end_the_tip_moves_only_when_both_gateways_agree()
    {
        var fleet = new Fleet();
        var indexer = Indexer(fleet, GatewayA, GatewayB);
        var sync = new HeaderChainSync(indexer, new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint, crossCheck: indexer)
        {
            Clock = () => Parse(HeaderChainFixture.Headers[^1]).BlockTime,
        };

        // B is a few blocks behind A: the run A served is held.
        fleet.B.HighestKnown = HeaderChainFixture.TipHeight - 3;
        await sync.SyncAsync();
        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);
        Assert.True(sync.TipHeld);

        // B catches up: the next sync re-fetches the run and commits it.
        fleet.B.HighestKnown = HeaderChainFixture.TipHeight;
        await sync.SyncAsync();
        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        Assert.Equal(HeaderCorroboration.Confirmed, sync.LastCorroboration);
    }

    // ── Audit 2026-11-09: who served a run is a fact of the run, not of the last header request ──

    [Fact]
    public async Task A_header_fetch_another_gateway_answers_mid_run_does_not_change_who_the_run_is_checked_against()
    {
        // Gateway A serves the run. While the wallet is verifying it, a proof check fetches a few
        // headers of its own, and A lets THAT request drop so that B answers it. The run's second
        // opinion must still come from B — A served the run — and B, some blocks behind here, has
        // nothing at the run's top: the tip is held.
        var fleet = new Fleet();
        var top = HeaderChainFixture.CheckpointHeight + 100;
        fleet.A.HighestKnown = top;
        fleet.B.HighestKnown = HeaderChainFixture.CheckpointHeight + 50;
        fleet.A.Refuse = request => request.RequestUri!.Query.Contains("count=5");
        var indexer = Indexer(fleet, GatewayA, GatewayB);
        var sync = new HeaderChainSync(indexer, new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint, crossCheck: indexer);
        var fetchedMidRun = false;
        sync.Clock = () =>
        {
            // Read once per slice verified: the run's batch is in hand and no second gateway has been asked yet.
            if (!fetchedMidRun)
            {
                fetchedMidRun = true;
                indexer.GetHeadersAsync(HeaderChainFixture.CheckpointHeight + 1, 5).GetAwaiter().GetResult();
            }
            return Parse(HeaderChainFixture.Headers[^1]).BlockTime;
        };

        await sync.SyncAsync();

        Assert.True(fetchedMidRun);
        Assert.Contains($"/indexer/api/headers/{top}?count=1", fleet.B.Paths);
        Assert.DoesNotContain($"/indexer/api/headers/{top}?count=1", fleet.A.Paths);   // was: A was asked to confirm its own run
        Assert.Equal(HeaderChainFixture.CheckpointHeight, sync.VerifiedTipHeight);     // was +100: and A's word moved the tip
        Assert.Equal(HeaderCorroboration.Unavailable, sync.LastCorroboration);
    }

    [Fact]
    public async Task A_run_is_checked_against_whoever_served_its_top_header()
    {
        // The feed failed over between batches: A served the first 250 headers, B the rest. The top
        // is B's, and the headers beneath it are bound to it by hash — so B is who must not vouch.
        var feed = new TwoGatewayFeed { ServedBy = from => from == HeaderChainFixture.CheckpointHeight + 1 ? GatewayA : GatewayB };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.TipHeight, sync.VerifiedTipHeight);
        Assert.Equal([GatewayB], Assert.Single(feed.AskedAbout));
    }

    [Fact]
    public async Task A_batch_that_adds_nothing_to_the_run_does_not_change_who_served_its_top()
    {
        // B's batch links to the run but its first header has no valid work: nothing of it joins the
        // run, whose top is still the last header A served.
        var tampered = (string[])HeaderChainFixture.Headers.Clone();
        tampered[250] = tampered[250][..152] + "00000000";
        var feed = new TwoGatewayFeed
        {
            ServedBy = from => from == HeaderChainFixture.CheckpointHeight + 1 ? GatewayA : GatewayB,
            Serve = (from, count) =>
            {
                var start = (int)(from - (HeaderChainFixture.CheckpointHeight + 1));
                return start < 0 || start >= tampered.Length ? [] : tampered.Skip(start).Take(count).ToList();
            },
        };
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Equal(HeaderChainFixture.CheckpointHeight + 250, sync.VerifiedTipHeight);
        Assert.Equal([GatewayA], Assert.Single(feed.AskedAbout));
    }

    [Fact]
    public async Task A_feed_that_cannot_say_who_served_a_run_names_nobody()
    {
        var feed = new TwoGatewayFeed();
        var (sync, _) = SyncOver(feed);

        await sync.SyncAsync();

        Assert.Empty(Assert.Single(feed.AskedAbout));
    }

    [Fact]
    public async Task Headers_that_place_a_block_are_not_taken_from_a_gateway_the_caller_ruled_out()
    {
        // The feed names gateway A as the server of every batch, whatever it was asked to avoid.
        var (sync, _) = SyncOver(new TwoGatewayFeed { ServedBy = _ => GatewayA });

        var ruledOut = await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight, notFrom: [GatewayA]);
        Assert.Equal(ChainMembership.CannotCheck, ruledOut.Membership);
        var allowed = await sync.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight, notFrom: [GatewayB]);
        Assert.Equal(ChainMembership.OnChain, allowed.Membership);

        // A feed with one source has nobody else to ask; without a restriction it serves as before.
        var plain = new HeaderChainSync(new PlainFeed(), new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, Checkpoint);
        Assert.Equal(ChainMembership.CannotCheck,
            (await plain.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight, notFrom: [GatewayA])).Membership);
        Assert.Equal(ChainMembership.OnChain,
            (await plain.LocateAsync(HeaderChainFixture.ProofBlockHash, HeaderChainFixture.ProofBlockHeight)).Membership);
    }
}

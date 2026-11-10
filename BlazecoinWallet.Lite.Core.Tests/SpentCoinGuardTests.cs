using BlazecoinWallet.Lite.Data;
using NSubstitute;
using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;
using S = BlazecoinWallet.Lite.Tests.SpendBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// <see cref="SpentCoinGuard"/> on its own (bug-bounty round six, 2026-11-06): when a coin is
/// asked about, how often, and what each kind of answer does to it. The second gateway is a
/// scripted <see cref="ISpendCrossCheck"/>; the verifier is the real one, fed real evidence from
/// <see cref="SpendBlockFixture"/>.
/// </summary>
public class SpentCoinGuardTests
{
    private static readonly Uri Lister = new("https://a.test/");

    private sealed class SecondGateway : ISpendCrossCheck
    {
        public int MaxOutpointsPerCall { get; set; } = 50;
        public readonly List<int> Asked = [];
        /// <summary>The outputs of each question, in the order they were asked about.</summary>
        public readonly List<int[]> AskedVouts = [];
        public Func<IReadOnlyList<LiteOutpoint>, IReadOnlyList<LiteSpendClaim>?> Answer = _ => [];
        /// <summary>When set, every answer waits for it — a slow gateway.</summary>
        public TaskCompletionSource? Hold;

        public async Task<IReadOnlyList<LiteSpendClaim>?> FindSpendsFromAnotherGatewayAsync(
            IReadOnlyList<LiteOutpoint> outpoints, CancellationToken ct = default)
        {
            lock (Asked) { Asked.Add(outpoints.Count); AskedVouts.Add(outpoints.Select(o => o.Vout).ToArray()); }
            if (Hold != null) await Hold.Task.WaitAsync(ct);
            return Answer(outpoints);
        }

        public int Calls { get { lock (Asked) return Asked.Count; } }
    }

    private sealed class Rig
    {
        public readonly SecondGateway Second = new();
        public readonly SpentCoinGuard Guard;
        public DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        public Rig()
        {
            Guard = new SpentCoinGuard(Second, new LiteTxVerifier(Substitute.For<IChainReader>())) { Clock = () => Now };
        }
        public Task EnsureAsync(params LiteOutpoint[] coins) => Guard.EnsureAsync(coins, TimeSpan.FromSeconds(30));
    }

    private static LiteOutpoint Coin(int i = 0, int vout = 0) => new(F.TxId(i), vout, Lister);
    private static LiteSpendClaim Proof() => new(F.TxId(0), 0, S.TxId(0), S.BlockHeight, S.RawHex(S.TxId(0)), S.ProofHex(S.TxId(0)));

    [Fact]
    public async Task A_coin_is_unknown_until_asked_about_and_clear_when_no_spend_is_named()
    {
        var rig = new Rig();
        Assert.Equal(SpendStanding.Unknown, rig.Guard.StandingOf(F.TxId(0), 0));

        await rig.EnsureAsync(Coin());

        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(F.TxId(0), 0));
        Assert.Equal(1, rig.Second.Calls);
    }

    [Fact]
    public async Task A_proven_spend_marks_the_coin_spent_and_leaves_its_neighbours_alone()
    {
        var rig = new Rig();
        rig.Second.Answer = _ => [Proof()];

        await rig.EnsureAsync(Coin(0), Coin(1));

        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(F.TxId(1), 0));
    }

    [Fact]
    public async Task Coins_are_asked_about_in_batches_of_what_one_request_carries()
    {
        var rig = new Rig();
        rig.Second.MaxOutpointsPerCall = 5;
        var coins = Enumerable.Range(0, 12).Select(i => new LiteOutpoint(F.TxId(0), i, Lister)).ToArray();

        await rig.EnsureAsync(coins);

        Assert.Equal([5, 5, 2], rig.Second.Asked);
        Assert.All(coins, c => Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(c.TxId, c.Vout)));
    }

    [Fact]
    public async Task Two_reads_at_once_cost_one_question()
    {
        var rig = new Rig();
        rig.Second.Hold = new TaskCompletionSource();

        var first = rig.EnsureAsync(Coin());
        var second = rig.EnsureAsync(Coin());
        rig.Second.Hold.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, rig.Second.Calls);
    }

    [Fact]
    public async Task A_read_does_not_wait_past_its_budget_and_the_late_answer_is_announced()
    {
        var rig = new Rig();
        rig.Second.Hold = new TaskCompletionSource();
        var announced = new TaskCompletionSource();
        rig.Guard.Changed += () => announced.TrySetResult();

        await rig.Guard.EnsureAsync([Coin()], TimeSpan.FromMilliseconds(50));
        Assert.Equal(SpendStanding.Unknown, rig.Guard.StandingOf(F.TxId(0), 0));   // not counted while nobody has answered

        rig.Second.Hold.SetResult();
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(F.TxId(0), 0));
    }

    [Fact]
    public async Task A_coin_with_no_known_lister_is_clear_without_a_question()
    {
        var rig = new Rig();

        await rig.EnsureAsync(new LiteOutpoint(F.TxId(0), 0));

        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(F.TxId(0), 0));
        Assert.Equal(0, rig.Second.Calls);
    }

    [Fact]
    public async Task A_spent_coin_stays_spent_when_nobody_answers_and_clears_only_when_told_otherwise()
    {
        var rig = new Rig();
        rig.Second.Answer = _ => [Proof()];
        await rig.EnsureAsync(Coin());
        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));

        rig.Second.Answer = _ => null;                                    // the second gateway goes quiet
        rig.Now += TimeSpan.FromMinutes(11);
        await rig.EnsureAsync(Coin());
        await rig.Guard.WhenIdleAsync();
        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));   // silence clears nothing

        rig.Second.Answer = _ => [Proof() with { SpenderHex = null }];    // a claim it can no longer show
        rig.Now += TimeSpan.FromMinutes(2);
        await rig.EnsureAsync(Coin());
        await rig.Guard.WhenIdleAsync();
        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));

        rig.Second.Answer = _ => [];                                      // asked, and no spend named (a re-organised chain)
        rig.Now += TimeSpan.FromMinutes(2);
        await rig.EnsureAsync(Coin());
        await rig.Guard.WhenIdleAsync();
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(F.TxId(0), 0));
    }

    [Fact]
    public async Task A_coin_this_wallet_spent_is_looked_at_afresh_if_it_is_listed_again_and_then_watched_closely()
    {
        var rig = new Rig();
        await rig.EnsureAsync(Coin(0), Coin(1));
        Assert.Equal(1, rig.Second.Calls);

        rig.Guard.NoteOwnSpend(F.TxId(0), 0);
        Assert.Equal(SpendStanding.Unknown, rig.Guard.StandingOf(F.TxId(0), 0));   // what was known is void
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(F.TxId(1), 0));

        await rig.EnsureAsync(Coin(0), Coin(1));                          // listed again: asked before it can count
        Assert.Equal([2, 1], rig.Second.Asked);

        // Its spend was not mined yet a moment ago. A minute on it is asked about again — the
        // ordinary coin beside it is not, for another nine.
        rig.Now += TimeSpan.FromSeconds(61);
        rig.Second.Answer = _ => [Proof()];
        await rig.EnsureAsync(Coin(0), Coin(1));
        await rig.Guard.WhenIdleAsync();
        Assert.Equal([2, 1, 1], rig.Second.Asked);
        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(F.TxId(1), 0));
    }

    // ── audit 2026-11-09: a claim whose evidence was not fetched this round is not a verdict ──

    /// <summary>A spend named for <paramref name="coin"/> whose evidence the cap left for a later round.</summary>
    private static LiteSpendClaim Waiting(LiteOutpoint coin) =>
        new(coin.TxId, coin.Vout, S.TxId(0), S.BlockHeight, null, null) { EvidencePending = true };

    /// <summary>A spend named for <paramref name="coin"/> with evidence that was fetched and does
    /// not hold: a real mined transaction, but one that spends a different output.</summary>
    private static LiteSpendClaim Failing(LiteOutpoint coin) => Proof() with { TxId = coin.TxId, Vout = coin.Vout };

    [Fact]
    public async Task A_claim_whose_evidence_was_not_fetched_is_neither_clear_nor_a_strike()
    {
        var rig = new Rig();
        rig.Second.Answer = asked => asked.Select(Waiting).ToList();

        for (var round = 0; round < rig.Guard.MaxUnprovenClaims + 3; round++)
        {
            await rig.EnsureAsync(Coin());
            await rig.Guard.WhenIdleAsync();
            Assert.Equal(SpendStanding.Unknown, rig.Guard.StandingOf(F.TxId(0), 0));   // was Clear from the fourth round on
            rig.Now += TimeSpan.FromSeconds(61);
        }

        rig.Second.Answer = _ => [Proof()];                               // its turn comes and the evidence is fetched
        await rig.EnsureAsync(Coin());
        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));
    }

    [Fact]
    public async Task Waiting_for_evidence_uses_up_none_of_the_tries_a_failing_claim_gets()
    {
        var rig = new Rig();
        var coin = Coin(0, 5);
        rig.Second.Answer = asked => asked.Select(Waiting).ToList();
        for (var round = 0; round < 3; round++)
        {
            await rig.EnsureAsync(coin);
            rig.Now += TimeSpan.FromSeconds(61);
        }

        // Evidence that was fetched and failed still counts, and still sets the claim aside in
        // the end — a second gateway cannot hold a coin back for good — but only from here.
        rig.Second.Answer = asked => asked.Select(Failing).ToList();
        for (var strike = 1; strike < rig.Guard.MaxUnprovenClaims; strike++)
        {
            await rig.EnsureAsync(coin);
            Assert.Equal(SpendStanding.Unknown, rig.Guard.StandingOf(coin.TxId, coin.Vout));
            rig.Now += TimeSpan.FromSeconds(61);
        }
        await rig.EnsureAsync(coin);
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(coin.TxId, coin.Vout));
    }

    [Fact]
    public async Task A_judged_coin_keeps_its_standing_while_a_new_claim_waits_for_evidence_and_is_asked_about_again_soon()
    {
        var rig = new Rig();
        var clear = Coin(0, 5);
        rig.Second.Answer = asked => asked.Where(c => c.Vout == 0).Select(_ => Proof()).ToList();
        await rig.EnsureAsync(Coin(), clear);
        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(clear.TxId, clear.Vout));

        rig.Second.Answer = asked => asked.Select(Waiting).ToList();      // both named, neither's evidence fetched
        rig.Now += TimeSpan.FromMinutes(11);
        await rig.EnsureAsync(Coin(), clear);
        await rig.Guard.WhenIdleAsync();
        Assert.Equal(SpendStanding.Spent, rig.Guard.StandingOf(F.TxId(0), 0));
        Assert.Equal(SpendStanding.Clear, rig.Guard.StandingOf(clear.TxId, clear.Vout));

        var calls = rig.Second.Calls;
        rig.Now += TimeSpan.FromSeconds(61);                              // a minute on, not ten
        await rig.EnsureAsync(Coin(), clear);
        await rig.Guard.WhenIdleAsync();
        Assert.Equal(calls + 1, rig.Second.Calls);
    }

    [Fact]
    public async Task Coins_whose_evidence_has_not_been_looked_at_are_asked_about_before_those_whose_has()
    {
        var rig = new Rig();
        var coins = Enumerable.Range(1, 3).Select(v => Coin(0, v)).ToArray();
        // One question fetches evidence for the first output asked about; the rest wait.
        rig.Second.Answer = asked => asked.Select((c, i) => i == 0 ? Failing(c) : Waiting(c)).ToList();

        await rig.EnsureAsync(coins);
        rig.Now += TimeSpan.FromSeconds(61);
        await rig.EnsureAsync(coins);
        rig.Now += TimeSpan.FromSeconds(61);
        await rig.EnsureAsync(coins);

        // Was [1,2,3] every time: the same coin took the evidence each round and the others never got a turn.
        Assert.Equal([[1, 2, 3], [2, 3, 1], [3, 1, 2]], rig.Second.AskedVouts);
    }

    [Fact]
    public async Task Clearing_forgets_everything()
    {
        var rig = new Rig();
        rig.Second.Answer = _ => [Proof()];
        await rig.EnsureAsync(Coin());

        rig.Guard.Clear();

        Assert.Equal(SpendStanding.Unknown, rig.Guard.StandingOf(F.TxId(0), 0));
    }
}

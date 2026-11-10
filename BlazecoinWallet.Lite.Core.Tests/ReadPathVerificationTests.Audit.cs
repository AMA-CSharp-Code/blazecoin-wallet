using BlazecoinWallet.Lite.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using F = BlazecoinWallet.Lite.Tests.MinedBlockFixture;
using S = BlazecoinWallet.Lite.Tests.SpendBlockFixture;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// Audit 2026-11-09: gaps in the second-opinion checks, on the same two-gateway wire as round
/// six (the production client, failover handler, guard and verifier; real proofs both ways).
/// </summary>
public partial class ReadPathVerificationTests
{
    /// <summary>The four fixture coins whose spend <see cref="SpendBlockFixture"/> can prove.</summary>
    private static readonly (string TxId, int Vout, int Spender)[] ProvablySpent =
        [(F.TxId(0), 0, 0), (F.TxId(12), 1, 1), (F.TxId(11), 0, 2), (F.TxId(10), 0, 3)];

    /// <summary>
    /// Twelve coins gateway A lists, every one named as spent by gateway B. Eight of the claims
    /// are false (the transaction B points at does not spend that output); the four true ones
    /// come LAST, so two of them fall beyond the ten whose evidence one question fetches.
    /// </summary>
    private static LiteOutpoint[] TwelveClaimedCoins(Node b)
    {
        var coins = new List<LiteOutpoint>();
        for (var i = 0; i < 8; i++)
        {
            coins.Add(new LiteOutpoint(F.TxId(0), 100 + i, GatewayA));
            b.Spent(F.TxId(0), 100 + i, S.TxId(0));
        }
        foreach (var (txId, vout, spender) in ProvablySpent)
        {
            coins.Add(new LiteOutpoint(txId, vout, GatewayA));
            b.Spent(txId, vout, S.TxId(spender));
        }
        return [.. coins];
    }

    [Fact]
    public async Task The_client_returns_every_spend_named_and_fetches_evidence_in_the_order_asked()
    {
        var w = await OpenPairAsync();
        var coins = TwelveClaimedCoins(w.B);
        w.B.AnswersSpendsBackwards = true;                                // the gateway does not choose who goes first

        var claims = await w.Indexer.FindSpendsFromAnotherGatewayAsync(coins);

        Assert.NotNull(claims);
        Assert.Equal(coins.Select(c => (c.TxId, c.Vout)), claims.Select(c => (c.TxId, c.Vout)));   // was: ten of the twelve
        Assert.All(claims.Take(IndexerDataService.MaxSpendEvidencePerCall), c =>
        {
            Assert.False(c.EvidencePending);
            Assert.NotNull(c.SpenderHex);
            Assert.NotNull(c.SpenderProofHex);
        });
        Assert.All(claims.Skip(IndexerDataService.MaxSpendEvidencePerCall), c =>
        {
            Assert.True(c.EvidencePending);
            Assert.Null(c.SpenderHex);
        });
    }

    [Fact]
    public async Task A_coin_named_as_spent_is_never_cleared_because_its_evidence_was_not_fetched_this_round()
    {
        var w = await OpenPairAsync();
        var guard = w.Service.Spends!;
        var coins = TwelveClaimedCoins(w.B);
        var rawBefore = w.B.RawCalls;

        await guard.EnsureAsync(coins, TimeSpan.FromSeconds(30));

        Assert.Equal(IndexerDataService.MaxSpendEvidencePerCall, w.B.RawCalls - rawBefore);   // the cap on evidence stands
        // Was: the two coins past the cap came back Clear — counted as unspent for ten minutes,
        // and again at every re-check, although the second gateway had named their spends.
        Assert.DoesNotContain(coins, c => guard.StandingOf(c.TxId, c.Vout) == SpendStanding.Clear);
        Assert.Equal(SpendStanding.Spent, guard.StandingOf(ProvablySpent[0].TxId, ProvablySpent[0].Vout));
        Assert.Equal(SpendStanding.Spent, guard.StandingOf(ProvablySpent[1].TxId, ProvablySpent[1].Vout));
        Assert.Equal(SpendStanding.Unknown, guard.StandingOf(ProvablySpent[2].TxId, ProvablySpent[2].Vout));
        Assert.Equal(SpendStanding.Unknown, guard.StandingOf(ProvablySpent[3].TxId, ProvablySpent[3].Vout));
    }

    [Fact]
    public async Task Every_one_of_twelve_claimed_coins_is_judged_within_a_few_rounds()
    {
        var w = await OpenPairAsync();
        var guard = w.Service.Spends!;
        var coins = TwelveClaimedCoins(w.B);

        await guard.EnsureAsync(coins, TimeSpan.FromSeconds(30));
        w.Now += TimeSpan.FromSeconds(61);
        await guard.EnsureAsync(coins, TimeSpan.FromSeconds(30));         // the coins that waited go first
        await guard.WhenIdleAsync();

        Assert.All(ProvablySpent, p => Assert.Equal(SpendStanding.Spent, guard.StandingOf(p.TxId, p.Vout)));

        // The eight false claims had their evidence fetched and it failed: those are strikes, and
        // a claim that keeps failing is set aside — a second gateway cannot hold coins back for good.
        for (var round = 3; round <= guard.MaxUnprovenClaims + 1; round++)
        {
            w.Now += TimeSpan.FromSeconds(61);
            await guard.EnsureAsync(coins, TimeSpan.FromSeconds(30));
            await guard.WhenIdleAsync();
        }
        Assert.All(coins.Take(8), c => Assert.Equal(SpendStanding.Clear, guard.StandingOf(c.TxId, c.Vout)));
        Assert.All(ProvablySpent, p => Assert.Equal(SpendStanding.Spent, guard.StandingOf(p.TxId, p.Vout)));
    }

    // ════════════════════════ the headers that place a spend ════════════════════════
    // With a header chain the spending transaction's block must be placed on it: its hash is
    // linked, header by header, to the next hash the wallet trusts. Those headers are fetched.

    private const int HeadersAboveSpend = 10;

    /// <summary>Headers hash-linked on top of the block that spent the coin, and the hash of the
    /// last one — the tip this rig's wallet trusts. Linking is all that is checked on the way to
    /// a trusted hash, so they carry no work.</summary>
    private static readonly Lazy<(string[] Headers, string TipHash)> LinkAboveSpend = new(() =>
    {
        var factory = BlazecoinNetwork.Instance.Consensus.ConsensusFactory;
        var prev = S.Block.Header.GetHash();
        var headers = new string[HeadersAboveSpend];
        for (var i = 0; i < headers.Length; i++)
        {
            var header = factory.CreateBlockHeader();
            header.HashPrevBlock = prev;
            header.Bits = new Target(F.Bits);
            header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_790_001_600 + i * 30);
            header.Nonce = (uint)i;
            var bytes = new MemoryStream();
            header.ReadWrite(new BitcoinStream(bytes, true) { ConsensusFactory = factory });
            headers[i] = Convert.ToHexString(bytes.ToArray()).ToLowerInvariant();
            prev = header.GetHash();
        }
        return (headers, prev.ToString());
    });

    private static IReadOnlyList<string> ServeLinkAboveSpend(long fromHeight, int count)
    {
        var start = (int)(fromHeight - (S.BlockHeight + 1));
        return start < 0 || start >= HeadersAboveSpend ? [] : LinkAboveSpend.Value.Headers.Skip(start).Take(count).ToList();
    }

    private static IReadOnlyList<string> NoHeaders(long fromHeight, int count) => [];

    private sealed class SpendProofRig(SpentCoinGuard guard, LiteTxVerifier verifier, Fleet fleet)
    {
        public SpentCoinGuard Guard => guard;
        public LiteTxVerifier Verifier => verifier;
        public Node A => fleet.A;
        public Node B => fleet.B;
        public DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        public LiteOutpoint Coin { get; } = new(F.TxId(0), 0, GatewayA);
        public SpendStanding Standing => guard.StandingOf(Coin.TxId, Coin.Vout);
        public Task LookAsync() => guard.EnsureAsync([Coin], TimeSpan.FromSeconds(30));
    }

    /// <summary>The two-gateway wire with a header chain: gateway A listed the coin, gateway B names
    /// its spend, and the wallet's verified tip sits ten blocks above the block that spent it.</summary>
    private static SpendProofRig OpenSpendProofRig(long claimedSpendHeight = S.BlockHeight)
    {
        var fleet = new Fleet();
        var failover = new GatewayFailoverHandler([GatewayA.ToString(), GatewayB.ToString()], fleet);
        var indexer = new IndexerDataService(new HttpClient(failover) { BaseAddress = failover.Primary }, NullLogger<IndexerDataService>.Instance);
        var tip = new HeaderCheckpoint(S.BlockHeight + HeadersAboveSpend, LinkAboveSpend.Value.TipHash, F.Bits);
        var chain = new HeaderChainSync(indexer, new InMemoryWalletStateStore(), BlazecoinNetwork.Instance, tip, HeaderAnchors.FromBytes([]));
        var verifier = new LiteTxVerifier(indexer, headerChain: chain);
        var rig = new SpendProofRig(new SpentCoinGuard(indexer, verifier), verifier, fleet);
        rig.Guard.Clock = () => rig.Now;
        fleet.B.Spent(F.TxId(0), 0, S.TxId(0), claimedSpendHeight);
        return rig;
    }

    [Fact]
    public async Task The_headers_that_place_a_spend_do_not_come_from_the_gateway_that_listed_the_coin()
    {
        var rig = OpenSpendProofRig();
        rig.A.Headers = NoHeaders;                 // the lister answers — with nothing: it is the party being doubted
        rig.B.Headers = ServeLinkAboveSpend;

        await rig.LookAsync();

        // Was Unknown ("could not be checked"), a strike a round, and Clear — counted — from the fourth on.
        Assert.Equal(SpendStanding.Spent, rig.Standing);
        Assert.Equal(0, rig.A.HeaderCalls);
        Assert.True(rig.B.HeaderCalls >= 1);
    }

    [Fact]
    public async Task A_spend_whose_proof_could_not_be_checked_stays_an_open_question()
    {
        var rig = OpenSpendProofRig();
        rig.A.Headers = NoHeaders;
        rig.B.Headers = NoHeaders;                 // the headers are not to be had this round, from anyone

        for (var round = 0; round < rig.Guard.MaxUnprovenClaims + 2; round++)
        {
            await rig.LookAsync();
            Assert.Equal(SpendStanding.Unknown, rig.Standing);   // was Clear from the fourth round: "could not check" counted as "failed"
            rig.Now += TimeSpan.FromSeconds(61);
        }

        rig.B.Headers = ServeLinkAboveSpend;
        await rig.LookAsync();
        Assert.Equal(SpendStanding.Spent, rig.Standing);
    }

    [Theory]
    [InlineData(S.BlockHeight + 3)]   // a height the spending block is not at: the headers arrive and the link breaks
    [InlineData(0)]                   // no height at all: nothing the claim could ever be checked against
    public async Task A_spend_proof_that_was_checked_and_failed_still_counts_and_is_set_aside_in_the_end(long claimedHeight)
    {
        var rig = OpenSpendProofRig(claimedHeight);
        rig.B.Headers = ServeLinkAboveSpend;

        for (var strike = 1; strike < rig.Guard.MaxUnprovenClaims; strike++)
        {
            await rig.LookAsync();
            Assert.Equal(SpendStanding.Unknown, rig.Standing);
            rig.Now += TimeSpan.FromSeconds(61);
        }
        await rig.LookAsync();
        Assert.Equal(SpendStanding.Clear, rig.Standing);         // a second gateway cannot hold a coin back for good
    }

    [Fact]
    public async Task The_verifier_tells_a_spend_proof_that_failed_from_one_it_could_not_check()
    {
        var rig = OpenSpendProofRig();
        var real = new LiteSpendClaim(F.TxId(0), 0, S.TxId(0), S.BlockHeight, S.RawHex(S.TxId(0)), S.ProofHex(S.TxId(0)));
        IReadOnlyCollection<Uri> lister = [GatewayA];

        rig.B.Headers = NoHeaders;
        Assert.Equal(SpendProof.CannotCheck, await rig.Verifier.JudgeSpendAsync(real, lister));
        Assert.Equal(SpendProof.Failed, await rig.Verifier.JudgeSpendAsync(real with { SpenderHeight = 0 }, lister));   // no height: never checkable
        Assert.Equal(SpendProof.Failed, await rig.Verifier.JudgeSpendAsync(real with { SpenderHex = null }, lister));
        Assert.Equal(SpendProof.Failed, await rig.Verifier.JudgeSpendAsync(real with { Vout = 1 }, lister));

        rig.B.Headers = ServeLinkAboveSpend;
        Assert.Equal(SpendProof.Failed, await rig.Verifier.JudgeSpendAsync(real with { SpenderHeight = S.BlockHeight + 3 }, lister));   // the link breaks
        Assert.Equal(SpendProof.Proven, await rig.Verifier.JudgeSpendAsync(real, lister));
        Assert.Equal(0, rig.A.HeaderCalls);                      // the lister was never asked for a header
    }

    // ════════════════════════ one rule for "this coin can be spent" ════════════════════════
    // A send proved its coins and asked a second gateway about them AFRESH; the old-key sweep
    // proved them its own way and read whatever the guard had last been told.

    [Fact]
    public async Task A_coin_spent_between_the_check_and_the_sweep_is_not_swept()
    {
        var w = await OpenPairAsync((a, _) => a.Coin(F.Foreign, F.TxId(11), 0, OldKeyRealSats));
        var check = await w.Service.CheckWifAsync(OldWif);
        Assert.Equal(OldKeyRealSats, check.SpendableSatoshis);

        w.B.Spent(F.TxId(11), 0, S.TxId(2));               // spent elsewhere a moment later; gateway A goes on listing it
        var sweep = await w.Service.SweepWifAsync(OldWif);

        Assert.False(sweep.Success);                       // was: signed and broadcast, the spent coin its only input
        Assert.Contains("already been spent", sweep.Error);
        Assert.Null(w.A.BroadcastHex);
        Assert.Null(w.B.BroadcastHex);
    }

    [Fact]
    public async Task One_rule_sorts_the_coins_of_a_spend_and_stops_when_enough_are_proven()
    {
        var fleet = new Fleet();
        fleet.B.Spent(F.TxId(12), 1, S.TxId(1));
        var failover = new GatewayFailoverHandler([GatewayA.ToString(), GatewayB.ToString()], fleet);
        var indexer = new IndexerDataService(new HttpClient(failover) { BaseAddress = failover.Primary }, NullLogger<IndexerDataService>.Instance);
        var verifier = new LiteTxVerifier(indexer);
        ReadPathVerification Rule() => new(verifier, new ChainCheckQueue { Pace = TimeSpan.Zero }, new SpentCoinGuard(indexer, verifier),
            new VerifiedCoinLedger(new InMemoryWalletStateStore()), headerSync: null);
        static LiteOwnedUtxo Listed(string txId, int vout, long sats, int confirmations) =>
            new(F.Receive0, new LiteChainUtxo(txId, vout, sats, confirmations, confirmations > 0 ? F.BlockHeight : 0, false) { Source = GatewayA }, Verified: false);
        LiteOwnedUtxo[] coins =
        [
            Listed(F.TxId(0), 0, TenBlz + 5, 25),               // real — and worth 10 BLZ, whatever the row says
            Listed(F.TxId(12), 1, 20_000_000, 25),             // real, and gateway B can show it was spent
            Listed(new string('e', 64), 0, 7_000_000, 25),     // made up
            Listed(F.TxId(12), 0, 30_000_000, 0),              // not mined yet
        ];

        var all = await Rule().ProveSpendableAsync(coins, needSatoshis: null, int.MaxValue, default);

        var spendable = Assert.Single(all.Spendable);
        Assert.Equal((F.TxId(0), TenBlz, true), (spendable.Utxo.TxId, spendable.Utxo.Amount, spendable.Verified));
        Assert.Equal(20_000_000 + 7_000_000, all.UnverifiedSatoshis);
        Assert.Equal(30_000_000, all.UnconfirmedSatoshis);
        Assert.Equal(0, all.ImmatureSatoshis);

        // Enough is enough: the largest coin covers the need, and the others are not even looked at.
        var spendQuestions = fleet.B.SpendCalls;
        var enough = await Rule().ProveSpendableAsync(coins, needSatoshis: 1_000, int.MaxValue, default);
        Assert.Equal(TenBlz, enough.Total);
        Assert.Equal(0, enough.UnverifiedSatoshis);
        Assert.Equal(spendQuestions + 1, fleet.B.SpendCalls);
    }

    [Fact]
    public void Mature_means_one_thing()
    {
        var rule = new ReadPathVerification(null, null, null, new VerifiedCoinLedger(new InMemoryWalletStateStore()), headerSync: null);
        static LiteChainUtxo Coin(int confirmations, bool coinbase) => new(F.TxId(0), 0, 1, confirmations, F.BlockHeight, coinbase);

        Assert.False(rule.IsMature(Coin(0, coinbase: false)));
        Assert.True(rule.IsMature(Coin(1, coinbase: false)));
        Assert.False(rule.IsMature(Coin(ReadPathVerification.CoinbaseMaturity - 1, coinbase: true)));
        Assert.True(rule.IsMature(Coin(ReadPathVerification.CoinbaseMaturity, coinbase: true)));
        Assert.Equal(30, LiteWalletService.CoinbaseMaturity);

        // …and it is written once: the wallet service neither restates it nor proves a coin itself.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "BlazecoinWallet.Lite.Core"))) dir = dir.Parent;
        var service = File.ReadAllText(Path.Combine(dir!.FullName, "BlazecoinWallet.Lite.Core", "LiteWalletService.cs"));
        Assert.DoesNotContain("? CoinbaseMaturity", service);
        Assert.DoesNotContain("< CoinbaseMaturity", service);
        Assert.DoesNotContain(".VerifyAsync(", service);
        Assert.DoesNotContain("CheckNowAsync", service);
        Assert.DoesNotContain(".Split(':')", service);        // no stored format is parsed there any more
        Assert.DoesNotContain(".Split(';'", service);
    }

    // ════════════════════════ a bad address is refused before coins are counted ════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("blz1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4")]   // bech32: SegWit never activates here
    public async Task A_send_to_something_that_is_not_an_address_says_so_whatever_the_balance(string destination)
    {
        var w = await OpenPairAsync();                           // an empty wallet
        var rawBefore = w.A.RawCalls;

        var send = await w.Service.SendAsync(destination, 1_000);
        var sendMax = await w.Service.SendMaxAsync(destination);

        foreach (var result in new[] { send, sendMax })
        {
            Assert.False(result.Success);
            Assert.Contains("not a valid Blazecoin address", result.Error);   // was "Insufficient spendable balance" / "no spendable coins"
            Assert.DoesNotContain("balance", result.Error);
        }
        Assert.Equal(rawBefore, w.A.RawCalls);                   // and no coin was proven for it
    }

    // ════════════════════════ protections are handed in, not discovered ════════════════════════
    // The spent-coin guard, restore's second gateway and the header cross-check used to switch on
    // only when the reader object happened to implement an extra interface (`reader is …`), so
    // anything wrapped around the reader turned them off without a word.

    /// <summary>A reader that is ONLY a reader: what a cache or a logging wrapper would look like.</summary>
    private sealed class OnlyAReader(IChainReader inner) : IChainReader
    {
        public bool SupportsHistory => inner.SupportsHistory;
        public bool SupportsChainVerification => inner.SupportsChainVerification;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => inner.GetAddressAsync(a, ct);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => inner.GetUtxosAsync(a, ct);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => inner.GetHistoryAsync(a, p, ps, ct);
        public Task<string?> GetRawTransactionHexAsync(string t, CancellationToken ct = default) => inner.GetRawTransactionHexAsync(t, ct);
        public Task<string?> GetTxOutProofAsync(string t, CancellationToken ct = default) => inner.GetTxOutProofAsync(t, ct);
    }

    /// <summary>A head's composition (AddLiteWallet) over the two-gateway wire, with the reader wrapped.</summary>
    private static ServiceProvider ComposeWithAWrappedReader(Fleet fleet, bool headerSync)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISeedVault>(new MemoryVault());
        services.AddLiteWallet(new LiteWalletOptions
        {
            GatewayUrls = [GatewayA.ToString(), GatewayB.ToString()],
            InnerHandlerFactory = () => fleet,
            DisableHeaderChainSync = !headerSync,
        });
        services.AddSingleton<IChainReader>(sp => new OnlyAReader(sp.GetRequiredService<ILiteWalletData>()));   // the last registration wins
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_wrapped_reader_keeps_the_spent_coin_guard_and_restores_second_gateway()
    {
        var fleet = new Fleet();
        fleet.A.Used(F.Receive0);
        fleet.B.Used(F.Receive0);
        fleet.B.Used(LiteHdWallet.Restore(F.Mnemonic).GetReceiveAddress(3));   // gateway A says "unused"; B knows better
        ScriptResurrectedCoin(fleet.A, fleet.B);
        await using var sp = ComposeWithAWrappedReader(fleet, headerSync: false);
        Assert.IsType<OnlyAReader>(sp.GetRequiredService<IChainReader>());
        var wallet = sp.GetRequiredService<LiteWalletService>();

        await wallet.RestoreWalletAsync(F.Mnemonic);
        var coin = Assert.Single(await wallet.GetAllUtxosAsync());

        Assert.Equal(4, wallet.RevealedCount);                 // was 1: restore asked the wrapper alone
        Assert.True(coin.ProvenSpent);                         // was Verified: no guard was built
        Assert.Equal(0, await wallet.GetSpendableAsync());
    }

    [Fact]
    public void A_wrapped_reader_keeps_the_header_feed_and_its_second_gateway()
    {
        using var sp = ComposeWithAWrappedReader(new Fleet(), headerSync: true);

        var chain = sp.GetRequiredService<HeaderChainSync>();

        Assert.True(chain.Available);                          // was false: the wrapper "is not" a header reader
        Assert.True(chain.HasCrossCheck);
    }

    [Fact]
    public void Each_configuration_registers_the_capabilities_it_really_has()
    {
        // Gateway mode: all of them. A head that opted out of header sync: no feed, the rest stay.
        using (var gateway = ComposeWithAWrappedReader(new Fleet(), headerSync: true))
        {
            Assert.NotNull(gateway.GetService<ISpendCrossCheck>());
            Assert.NotNull(gateway.GetService<IAddressUsageCrossCheck>());
            Assert.NotNull(gateway.GetService<IHeaderCrossCheck>());
            Assert.NotNull(gateway.GetService<IHeaderReader>());
        }
        using (var optedOut = ComposeWithAWrappedReader(new Fleet(), headerSync: false))
        {
            Assert.Null(optedOut.GetService<IHeaderReader>());
            Assert.False(optedOut.GetRequiredService<HeaderChainSync>().Available);
            Assert.NotNull(optedOut.GetService<ISpendCrossCheck>());
            Assert.NotNull(optedOut.GetService<IAddressUsageCrossCheck>());
        }

        // The user's own node: authoritative, nobody else to ask — none of them, and no guards.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISeedVault>(new MemoryVault());
        services.AddLiteWallet(new LiteWalletOptions
        {
            UsePersonalNode = true,
            PersonalNode = new PersonalNodeOptions("http://127.0.0.1:55413/", RpcUser: "u", RpcPassword: "p"),
        });
        using var node = services.BuildServiceProvider();
        Assert.Null(node.GetService<ISpendCrossCheck>());
        Assert.Null(node.GetService<IAddressUsageCrossCheck>());
        Assert.Null(node.GetService<IHeaderCrossCheck>());
        Assert.Null(node.GetService<IHeaderReader>());
        var wallet = node.GetRequiredService<LiteWalletService>();
        Assert.Null(wallet.Checks);
        Assert.Null(wallet.Spends);
        Assert.False(node.GetRequiredService<HeaderChainSync>().HasCrossCheck);
    }

    [Fact]
    public void The_guards_are_built_from_what_the_service_is_handed_never_from_what_the_reader_is()
    {
        var indexer = new IndexerDataService(new HttpClient(new Fleet()) { BaseAddress = GatewayA }, NullLogger<IndexerDataService>.Instance);
        var verifier = new LiteTxVerifier(indexer);

        var wired = new LiteWalletService(new MemoryVault(), indexer, verifier: verifier, spendCrossCheck: indexer);
        Assert.NotNull(wired.Checks);
        Assert.NotNull(wired.Spends);

        // The reader IS a spend cross-check, and that is not what decides it.
        var notHanded = new LiteWalletService(new MemoryVault(), indexer, verifier: verifier);
        Assert.NotNull(notHanded.Checks);
        Assert.Null(notHanded.Spends);

        // No verifier (the user's own node): nothing is checked, so nothing is asked either.
        var authoritative = new LiteWalletService(new MemoryVault(), indexer, spendCrossCheck: indexer);
        Assert.Null(authoritative.Checks);
        Assert.Null(authoritative.Spends);
    }
}

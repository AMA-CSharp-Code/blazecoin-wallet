using BlazecoinWallet.Lite.Data;
using BlazecoinWallet.Lite.Pq;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BlazecoinWallet.Lite.Core.Tests;

/// <summary>
/// Post-quantum BY DEFAULT in the lite wallets (web + Android), 2026-09-28 — the lite twin of desktop
/// 2.0.9. Before it, change was legacy-only (every spend of a BQ… coin put its change back on ECDSA),
/// a legacy-key sweep landed on the legacy receive address, and there was no one-step move to BQ….
/// Pins: before the fork nothing changes (legacy change on the BIP44 change chain, which advances);
/// after it change goes to the wallet's BQ… address WITHOUT advancing (or gapping) the legacy change
/// chain; the WIF sweep and the move land on BQ…; the move spends only legacy coins, in one send with
/// one output, capped per run; and a restore still finds everything (the PQ chain is gap-scanned).
/// </summary>
public class PqDefaultsTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static readonly Network Net = BlazecoinNetwork.Instance;

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored = TestMnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    /// <summary>Per-address coins; every listed address counts as used. Coins sit at height 100 with 10
    /// confirmations, so the implied tip is 109 — tests choose the activation height around it.</summary>
    private sealed class AddressReader : IChainReader
    {
        public readonly Dictionary<string, List<LiteChainUtxo>> Utxos = new(StringComparer.Ordinal);
        public readonly HashSet<string> Used = new(StringComparer.Ordinal);
        public bool SupportsHistory => true;
        public bool SupportsChainVerification => false;
        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default)
            => Task.FromResult(Used.Contains(a) || Utxos.ContainsKey(a) ? new LiteAddressSummary(0, 0, 0, 1) : null);
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteChainUtxo>>(Utxos.TryGetValue(a, out var l) ? l : []);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>([]);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class CapturingRelay : ITxRelay
    {
        public readonly List<string> Sent = [];
        public LiteBroadcastResult? Next;
        public Task<LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default)
        {
            Sent.Add(hex);
            return Task.FromResult(Next ?? LiteBroadcastResult.Ok(Transaction.Parse(hex, Net).GetHash().ToString()));
        }
    }

    private static LiteChainUtxo Coin(char tag, int vout, long sats, string? scriptHex = null)
        => new(new string(tag, 64), vout, sats, 10, 100, false, scriptHex);

    private static string Hex(byte[] b) => Encoders.Hex.EncodeData(b);

    private static readonly LiteHdWallet W = LiteHdWallet.Restore(TestMnemonic);

    /// <param name="activation">100 = the fork is active at the implied tip (109); null = no height yet.</param>
    private static (LiteWalletService svc, AddressReader reader, CapturingRelay relay, InMemoryWalletStateStore state) Build(long? activation)
    {
        var reader = new AddressReader();
        var relay = new CapturingRelay();
        var state = new InMemoryWalletStateStore();
        var svc = new LiteWalletService(new MemoryVault(), reader, relay, stateStore: state) { PqActivationHeight = activation };
        return (svc, reader, relay, state);
    }

    // ════════════════════════════════════════════════════════════ change

    [Fact]
    public async Task Before_the_fork_change_stays_on_the_legacy_change_chain_and_advances_it()
    {
        var (svc, reader, relay, state) = Build(activation: null);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 100_000_000)];
        await svc.UnlockAsync();

        var r = await svc.SendAsync(W.GetReceiveAddress(5), 30_000_000);

        Assert.True(r.Success, r.Error);
        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.Contains(tx.Outputs, o => o.ScriptPubKey == new BitcoinPubKeyAddress(W.GetChangeAddress(0), Net).ScriptPubKey && o.Value.Satoshi == 70_000_000);
        Assert.Equal(1, await state.GetChangeAddressCountAsync());
        Assert.Equal(0, svc.PqRevealedCount);
    }

    [Fact]
    public async Task After_the_fork_change_goes_to_BQ_and_the_legacy_change_chain_is_untouched()
    {
        var (svc, reader, relay, state) = Build(activation: 100);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 100_000_000)];
        await svc.UnlockAsync();

        var r = await svc.SendAsync(W.GetReceiveAddress(5), 30_000_000);

        Assert.True(r.Success, r.Error);
        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.Equal(new BitcoinPubKeyAddress(W.GetReceiveAddress(5), Net).ScriptPubKey, tx.Outputs[0].ScriptPubKey);
        Assert.Equal(W.GetPqKey(0).ScriptPubKey, tx.Outputs[1].ScriptPubKey.ToBytes());
        Assert.Equal(70_000_000, tx.Outputs[1].Value.Satoshi);
        Assert.Equal(0, await state.GetChangeAddressCountAsync());   // no gap in the legacy chain
        Assert.Equal(1, svc.PqRevealedCount);                        // first BQ address revealed for it
        Assert.Equal(1, await state.GetPqAddressCountAsync());
    }

    [Fact]
    public async Task Post_quantum_change_reuses_the_newest_revealed_BQ_address()
    {
        var (svc, reader, relay, state) = Build(activation: 100);
        await state.SetPqAddressCountAsync(3);                       // three BQ addresses revealed earlier
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 100_000_000)];
        await svc.UnlockAsync();

        Assert.True((await svc.SendAsync(W.GetReceiveAddress(5), 10_000_000)).Success);

        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.Equal(W.GetPqKey(2).ScriptPubKey, tx.Outputs[1].ScriptPubKey.ToBytes());
        Assert.Equal(3, svc.PqRevealedCount);                        // nothing new revealed
    }

    [Fact]
    public async Task Spending_a_BQ_coin_returns_its_change_to_BQ()
    {
        var (svc, reader, relay, state) = Build(activation: 100);
        var pq0 = W.GetPqKey(0);
        await state.SetPqAddressCountAsync(1);
        reader.Utxos[pq0.Address()] = [Coin('b', 0, 80_000_000, Hex(pq0.ScriptPubKey))];
        await svc.UnlockAsync();

        var r = await svc.SendAsync(W.GetReceiveAddress(5), 20_000_000);

        Assert.True(r.Success, r.Error);
        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.Equal(PqInputVerdict.OK, PqInputVerifier.Verify(tx, 0, 80_000_000, pq0.ScriptPubKey, activated: true));
        Assert.Equal(pq0.ScriptPubKey, tx.Outputs[1].ScriptPubKey.ToBytes());   // the 09-28 mainnet case, fixed
        Assert.Equal(60_000_000, tx.Outputs[1].Value.Satoshi);
    }

    [Fact]
    public async Task Send_max_has_no_change_either_side_of_the_fork()
    {
        foreach (var activation in new long?[] { null, 100 })
        {
            var (svc, reader, relay, state) = Build(activation);
            reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 100_000_000)];
            await svc.UnlockAsync();
            Assert.True((await svc.SendMaxAsync(W.GetReceiveAddress(5))).Success);
            Assert.Single(Transaction.Parse(Assert.Single(relay.Sent), Net).Outputs);
            Assert.Equal(0, await state.GetChangeAddressCountAsync());
        }
    }

    [Fact]
    public async Task A_restore_finds_coins_left_on_the_BQ_change_address()
    {
        var (svc, reader, _, state) = Build(activation: 100);
        reader.Used.Add(W.GetPqAddress(0));                          // change landed there before the restore
        reader.Utxos[W.GetPqAddress(0)] = [Coin('c', 1, 70_000_000, Hex(W.GetPqKey(0).ScriptPubKey))];

        await svc.RestoreWalletAsync(TestMnemonic);

        Assert.Equal(1, svc.PqRevealedCount);
        Assert.Equal(70_000_000, await svc.GetSpendableAsync());
    }

    [Fact]
    public async Task A_wallet_with_only_BQ_coins_learns_the_tip_from_them_without_header_sync()
    {
        // 2026-09-28: the tip used to come from LEGACY coins only, so a wallet that had moved
        // everything to BQ… (and had no header sync) never opened the gate and showed zero.
        var (svc, reader, _, state) = Build(activation: 100);
        await state.SetPqAddressCountAsync(1);
        reader.Utxos[W.GetPqAddress(0)] = [Coin('b', 0, 60_000_000, Hex(W.GetPqKey(0).ScriptPubKey))];
        await svc.UnlockAsync();

        Assert.Equal(60_000_000, await svc.GetSpendableAsync());
        Assert.True(svc.PqActivated);
        Assert.Equal(109, svc.KnownTipHeight);
    }

    [Fact]
    public async Task BQ_coins_read_for_the_tip_are_still_not_owned_below_the_fork_height()
    {
        var (svc, reader, _, state) = Build(activation: 5_000_000);     // tip 109 is far below it
        await state.SetPqAddressCountAsync(1);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 10_000_000)];
        reader.Utxos[W.GetPqAddress(0)] = [Coin('b', 0, 60_000_000, Hex(W.GetPqKey(0).ScriptPubKey))];
        await svc.UnlockAsync();

        Assert.Equal(10_000_000, await svc.GetSpendableAsync());       // the BQ coin can't be spent yet
        Assert.False(svc.PqActivated);
        Assert.Equal(0, (await svc.MoveLegacyToPostQuantumAsync()).MovedCoins);
    }

    // ════════════════════════════════════════════════════════════ internal destination + WIF sweep

    [Fact]
    public async Task The_internal_destination_is_legacy_before_the_fork_and_BQ_after_it()
    {
        var (before, r1, _, _) = Build(activation: null);
        r1.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 1_000_000)];
        await before.UnlockAsync();
        Assert.Equal(before.Address, await before.GetInternalDestinationAsync());
        Assert.Equal(0, before.PqRevealedCount);

        var (after, r2, _, _) = Build(activation: 100);
        r2.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 1_000_000)];
        await after.UnlockAsync();
        Assert.Equal(W.GetPqAddress(0), await after.GetInternalDestinationAsync());
        Assert.Equal(W.GetPqAddress(0), await after.GetInternalDestinationAsync());   // stable, no second reveal
        Assert.Equal(1, after.PqRevealedCount);
    }

    [Fact]
    public async Task A_legacy_key_sweep_lands_on_BQ_after_the_fork()
    {
        var (svc, reader, relay, _) = Build(activation: 100);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 1_000_000)];   // gives the wallet its tip
        using var old = new Key();
        var oldAddress = old.GetAddress(ScriptPubKeyType.Legacy, Net).ToString();
        reader.Utxos[oldAddress] = [Coin('d', 0, 25_000_000), Coin('d', 1, 5_000_000)];
        await svc.UnlockAsync();

        var r = await svc.SweepWifAsync(old.GetWif(Net).ToString());

        Assert.True(r.Success, r.Error);
        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        var output = Assert.Single(tx.Outputs);
        Assert.Equal(W.GetPqKey(0).ScriptPubKey, output.ScriptPubKey.ToBytes());
        Assert.Equal(30_000_000, output.Value.Satoshi);
    }

    [Fact]
    public async Task A_legacy_key_sweep_before_the_fork_lands_on_the_legacy_receive_address()
    {
        var (svc, reader, relay, _) = Build(activation: null);
        using var old = new Key();
        reader.Utxos[old.GetAddress(ScriptPubKeyType.Legacy, Net).ToString()] = [Coin('d', 0, 25_000_000)];
        await svc.UnlockAsync();

        Assert.True((await svc.SweepWifAsync(old.GetWif(Net).ToString())).Success);

        var output = Assert.Single(Transaction.Parse(Assert.Single(relay.Sent), Net).Outputs);
        Assert.Equal(new BitcoinPubKeyAddress(svc.Address, Net).ScriptPubKey, output.ScriptPubKey);
    }

    // ════════════════════════════════════════════════════════════ move everything to post-quantum

    [Fact]
    public async Task The_breakdown_says_how_much_is_still_legacy()
    {
        var (svc, reader, _, state) = Build(activation: 100);
        await state.SetPqAddressCountAsync(1);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 40_000_000)];
        reader.Utxos[W.GetPqAddress(0)] = [Coin('b', 0, 60_000_000, Hex(W.GetPqKey(0).ScriptPubKey))];
        await svc.UnlockAsync();

        var b = await svc.GetBalanceBreakdownAsync();

        Assert.Equal(100_000_000, b.Spendable);
        Assert.Equal(40_000_000, b.SpendableLegacy);
    }

    [Fact]
    public async Task Moving_is_refused_before_the_fork_and_sends_nothing()
    {
        var (svc, reader, relay, _) = Build(activation: null);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 40_000_000)];
        await svc.UnlockAsync();

        var r = await svc.MoveLegacyToPostQuantumAsync();

        Assert.False(r.Success);
        Assert.Contains("hasn't been set", r.Error);
        Assert.Equal(1, r.RemainingLegacyCoins);
        Assert.Empty(relay.Sent);
    }

    [Fact]
    public async Task Moving_sends_every_legacy_coin_in_one_output_to_BQ_and_leaves_BQ_coins_alone()
    {
        var (svc, reader, relay, state) = Build(activation: 100);
        await state.SetPqAddressCountAsync(1);
        await state.SetChangeAddressCountAsync(1);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 40_000_000), Coin('a', 1, 10_000_000)];
        reader.Utxos[W.GetChangeAddress(0)] = [Coin('e', 0, 5_000_000)];                               // legacy change counts
        reader.Utxos[W.GetPqAddress(0)] = [Coin('b', 0, 60_000_000, Hex(W.GetPqKey(0).ScriptPubKey))]; // already safe
        await svc.UnlockAsync();

        var r = await svc.MoveLegacyToPostQuantumAsync();

        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.MovedCoins);
        Assert.Equal(55_000_000, r.MovedSatoshis);
        Assert.Equal(0, r.RemainingLegacyCoins);
        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.Equal(3, tx.Inputs.Count);
        Assert.DoesNotContain(tx.Inputs, i => i.PrevOut.Hash == uint256.Parse(new string('b', 64)));
        var output = Assert.Single(tx.Outputs);                      // one output, no change
        Assert.Equal(W.GetPqKey(0).ScriptPubKey, output.ScriptPubKey.ToBytes());
        Assert.Equal(55_000_000, output.Value.Satoshi);              // zero fee
        Assert.Equal(r.TxId, tx.GetHash().ToString());

        // The moved coins are held out of reads until the gateway catches up, so a second press finds nothing.
        Assert.Equal(0, (await svc.GetBalanceBreakdownAsync()).SpendableLegacy);
        var again = await svc.MoveLegacyToPostQuantumAsync();
        Assert.False(again.Success);
        Assert.Contains("no legacy coins", again.Error);
        Assert.Single(relay.Sent);
    }

    [Fact]
    public async Task A_wallet_with_nothing_legacy_is_told_so()
    {
        var (svc, reader, relay, state) = Build(activation: 100);
        await state.SetPqAddressCountAsync(1);
        reader.Utxos[W.GetPqAddress(0)] = [Coin('b', 0, 60_000_000, Hex(W.GetPqKey(0).ScriptPubKey))];
        await svc.UnlockAsync();

        var r = await svc.MoveLegacyToPostQuantumAsync();

        Assert.False(r.Success);
        Assert.Contains("already on a post-quantum address", r.Error);
        Assert.Empty(relay.Sent);
    }

    [Fact]
    public async Task A_move_takes_at_most_MaxInputsPerMove_coins_largest_first_and_reports_the_rest()
    {
        var (svc, reader, relay, _) = Build(activation: 100);
        var coins = Enumerable.Range(0, LiteWalletService.MaxInputsPerMove + 1).Select(i => Coin('f', i, 1_000_000 + i)).ToList();
        reader.Utxos[W.GetReceiveAddress(0)] = coins;
        await svc.UnlockAsync();

        var r = await svc.MoveLegacyToPostQuantumAsync();

        Assert.True(r.Success, r.Error);
        Assert.Equal(LiteWalletService.MaxInputsPerMove, r.MovedCoins);
        Assert.Equal(1, r.RemainingLegacyCoins);
        var tx = Transaction.Parse(Assert.Single(relay.Sent), Net);
        Assert.DoesNotContain(tx.Inputs, i => i.PrevOut.N == 0);    // the smallest coin waits for the next run
        Assert.True(tx.GetSerializedSize() <= PqScript.MaxStandardTxBytes);
    }

    [Fact]
    public async Task A_rejected_move_reports_the_reason_and_nothing_is_held_back()
    {
        var (svc, reader, relay, _) = Build(activation: 100);
        reader.Utxos[W.GetReceiveAddress(0)] = [Coin('a', 0, 40_000_000)];
        relay.Next = LiteBroadcastResult.Fail("txn-mempool-conflict", BroadcastFailureKind.Rejected);
        await svc.UnlockAsync();

        var r = await svc.MoveLegacyToPostQuantumAsync();

        Assert.False(r.Success);
        Assert.Contains("txn-mempool-conflict", r.Error);
        Assert.Equal(1, r.RemainingLegacyCoins);
        Assert.Equal(40_000_000, (await svc.GetBalanceBreakdownAsync()).SpendableLegacy);   // not marked spent
    }
}

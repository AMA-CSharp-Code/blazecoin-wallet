using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using NBitcoin;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// The 2026-07-25 feature set: address rotation (BIP44 external chain + gap-limit
/// discipline), legacy WIF sweep, multi-address history merging, and the incoming-payment
/// watcher. A per-address scripted <see cref="ILiteWalletData"/> fake stands in for the
/// gateway so every test controls exactly which address holds what.
/// </summary>
public class LiteRotationSweepWatcherTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private sealed class MemoryVault : ISeedVault
    {
        public string? Stored;
        public Task<bool> HasWalletAsync() => Task.FromResult(Stored != null);
        public Task SaveMnemonicAsync(string m) { Stored = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Stored);
        public Task ClearAsync() { Stored = null; return Task.CompletedTask; }
    }

    /// <summary>Per-address scripted data source — each address's coins/summary/history are
    /// independent, exactly like the real Indexer.</summary>
    private sealed class FakeWalletData : ILiteWalletData
    {
        public readonly Dictionary<string, List<LiteChainUtxo>> Utxos = [];
        public readonly Dictionary<string, LiteAddressSummary> Summaries = [];
        public readonly Dictionary<string, List<LiteHistoryEntry>> History = [];
        public LiteBroadcastResult BroadcastResult = LiteBroadcastResult.Ok(new string('c', 64));
        public string? CapturedBroadcastHex;

        public bool SupportsHistory => true;
        public bool SupportsChainVerification => false; // no verifier in these unit tests

        public Task<LiteAddressSummary?> GetAddressAsync(string address, CancellationToken ct = default)
            => Task.FromResult(Summaries.TryGetValue(address, out var s) ? s : null);

        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string address, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteChainUtxo>>(Utxos.TryGetValue(address, out var u) ? u : []);

        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string address, int page = 1, int pageSize = 25, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteHistoryEntry>>(History.TryGetValue(address, out var h) ? h : []);

        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<LiteBroadcastResult> BroadcastAsync(string rawTxHex, CancellationToken ct = default)
        {
            CapturedBroadcastHex = rawTxHex;
            return Task.FromResult(BroadcastResult);
        }

        public void MarkUsed(string address) =>
            Summaries[address] = new LiteAddressSummary(0, 1, 1, 1);

        public void AddUtxo(string address, string txId, int vout, long sats, int confs = 5, bool coinbase = false)
        {
            if (!Utxos.TryGetValue(address, out var list)) Utxos[address] = list = [];
            list.Add(new LiteChainUtxo(txId, vout, sats, confs, 100, coinbase));
        }
    }

    private static async Task<(LiteWalletService service, FakeWalletData data)> BuildUnlockedAsync()
    {
        var data = new FakeWalletData();
        var service = new LiteWalletService(new MemoryVault(), data);
        await service.RestoreWalletAsync(TestMnemonic);
        return (service, data);
    }

    private static string Slot(int i) => LiteHdWallet.Restore(TestMnemonic).GetReceiveAddress(i);
    private static string ChangeSlot(int i) => LiteHdWallet.Restore(TestMnemonic).GetChangeAddress(i);
    private static string TxId(char c) => new(c, 64);

    // ── Backup-verified flag ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Created_wallet_is_unverified_until_marked()
    {
        var service = new LiteWalletService(new MemoryVault(), new FakeWalletData());
        await service.CreateNewWalletAsync();
        Assert.False(await service.IsBackupVerifiedAsync());   // set BEFORE the ceremony shows words

        await service.MarkBackupVerifiedAsync();               // the quiz passed
        Assert.True(await service.IsBackupVerifiedAsync());
    }

    [Fact]
    public async Task Restored_wallet_is_verified_by_possession()
    {
        var (service, _) = await BuildUnlockedAsync();          // restores from the typed phrase
        Assert.True(await service.IsBackupVerifiedAsync());
    }

    [Fact]
    public async Task Reveal_mnemonic_returns_the_stored_phrase_only_when_unlocked()
    {
        var service = new LiteWalletService(new MemoryVault(), new FakeWalletData());
        await Assert.ThrowsAsync<InvalidOperationException>(service.RevealMnemonicAsync);

        await service.RestoreWalletAsync(TestMnemonic);
        Assert.Equal(TestMnemonic, await service.RevealMnemonicAsync());
    }

    // ── Address rotation ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reveal_next_address_rotates_and_keeps_old_addresses_live()
    {
        var (service, data) = await BuildUnlockedAsync();
        var first = service.Address;
        Assert.Equal(Slot(0), first);

        data.MarkUsed(first); // a payment arrived on slot 0
        var (ok, error) = await service.RevealNextAddressAsync();

        Assert.True(ok, error);
        Assert.Equal(Slot(1), service.Address);            // fresh slot is now current
        Assert.Equal([Slot(0), Slot(1)], service.Addresses); // the old one is still watched
    }

    [Fact]
    public async Task Reveal_refuses_beyond_the_gap_limit_until_an_address_is_used()
    {
        var (service, _) = await BuildUnlockedAsync();

        // Nothing ever used: reveals succeed until GapLimit consecutive unused exist.
        for (var i = 0; i < LiteWalletService.GapLimit - 1; i++)
        {
            var (ok, _) = await service.RevealNextAddressAsync();
            Assert.True(ok, $"reveal #{i + 1} should be allowed");
        }
        Assert.Equal(LiteWalletService.GapLimit, service.RevealedCount);

        var (blocked, error) = await service.RevealNextAddressAsync();
        Assert.False(blocked);
        Assert.Contains("unused", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reveal_unblocks_once_a_recent_address_receives_a_payment()
    {
        var (service, data) = await BuildUnlockedAsync();
        for (var i = 0; i < LiteWalletService.GapLimit - 1; i++)
            Assert.True((await service.RevealNextAddressAsync()).Ok);
        Assert.False((await service.RevealNextAddressAsync()).Ok);

        data.MarkUsed(service.Address); // payment lands on the newest slot
        Assert.True((await service.RevealNextAddressAsync()).Ok);
        Assert.Equal(LiteWalletService.GapLimit + 1, service.RevealedCount);
    }

    [Fact]
    public async Task Restore_gap_scans_the_chain_and_rediscovers_used_addresses()
    {
        var data = new FakeWalletData();
        data.MarkUsed(Slot(0));
        data.MarkUsed(Slot(1));
        data.MarkUsed(Slot(3)); // gap at 2 — scan must look past it

        var service = new LiteWalletService(new MemoryVault(), data);
        await service.RestoreWalletAsync(TestMnemonic);

        Assert.Equal(4, service.RevealedCount);   // slots 0..3 revealed
        Assert.Equal(Slot(3), service.Address);   // current = last used
    }

    [Fact]
    public async Task Rotation_position_persists_through_the_state_store()
    {
        var data = new FakeWalletData();
        var vault = new MemoryVault();
        var store = new InMemoryWalletStateStore();

        var service = new LiteWalletService(vault, data, stateStore: store);
        await service.RestoreWalletAsync(TestMnemonic);
        data.MarkUsed(service.Address);
        await service.RevealNextAddressAsync();
        Assert.Equal(2, service.RevealedCount);

        // Same vault + store, fresh service (an app restart): position survives.
        var service2 = new LiteWalletService(vault, data, stateStore: store);
        Assert.True(await service2.UnlockAsync());
        Assert.Equal(2, service2.RevealedCount);
        Assert.Equal(Slot(1), service2.Address);
    }

    [Fact]
    public async Task Reveal_stops_at_the_absolute_cap_even_if_the_gateway_claims_all_used()
    {
        // A lying gateway that reports every slot "used" could otherwise push reveals without
        // bound (audit S8/M2). The hard cap stops it; a real user never hits it.
        var (service, data) = await BuildUnlockedAsync();
        for (var i = 0; i < AddressRotation.MaxRevealed; i++) data.MarkUsed(Slot(i));

        var lastOk = true;
        while (lastOk && service.RevealedCount < AddressRotation.MaxRevealed)
            lastOk = (await service.RevealNextAddressAsync()).Ok;

        Assert.Equal(AddressRotation.MaxRevealed, service.RevealedCount);
        var (blocked, error) = await service.RevealNextAddressAsync();
        Assert.False(blocked);
        Assert.Contains("maximum", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verifier_gated_reveal_ignores_a_gateways_fake_used_claim()
    {
        // H1: a lying gateway claims every slot is "used" (TxCount>0 + a UTXO) but can serve
        // no valid raw tx / proof, so nothing VERIFIES. Under a verifier (gateway mode), the
        // reveal gate must not let those fake claims push reveals past the real gap.
        var data = new FakeWalletData();
        var wallet = LiteHdWallet.Restore(TestMnemonic);
        for (var i = 0; i < AddressRotation.MaxRevealed; i++)
        {
            data.MarkUsed(Slot(i));
            data.AddUtxo(Slot(i), new string((char)('a' + (i % 26)), 64), 0, 1_000);
        }

        var gated = new AddressRotation(data, new InMemoryWalletStateStore(), new LiteTxVerifier(data));
        await gated.InitializeNewAsync();
        var ok = true;
        while (ok && gated.RevealedCount < AddressRotation.MaxRevealed) (ok, _) = await gated.RevealNextAsync(wallet);
        Assert.Equal(AddressRotation.GapLimit, gated.RevealedCount); // stopped at the gap despite fake "used"

        // Contrast: personal-node mode (no verifier) trusts the TxCount, so slot 0's "used"
        // claim unlocks the next reveal immediately.
        var trusting = new AddressRotation(data, new InMemoryWalletStateStore());
        await trusting.InitializeNewAsync();
        Assert.True((await trusting.RevealNextAsync(wallet)).Ok);
    }

    [Fact]
    public async Task Zero_confirmation_coins_are_not_counted_spendable()
    {
        // A gateway-fabricated 0-conf output must not inflate the spendable balance (audit
        // S7); it would fail verification at spend time anyway.
        var (service, data) = await BuildUnlockedAsync();
        data.AddUtxo(service.Address, TxId('a'), 0, 5_000_000, confs: 0);          // unconfirmed
        data.AddUtxo(service.Address, TxId('b'), 0, 3_000_000, confs: 1);          // confirmed
        Assert.Equal(3_000_000, await service.GetSpendableAsync());
    }

    // ── Multi-address spending / aggregation ────────────────────────────────────────────

    [Fact]
    public async Task Send_spends_coins_from_several_addresses_with_change_to_the_change_chain()
    {
        var (service, data) = await BuildUnlockedAsync();
        data.MarkUsed(service.Address);
        await service.RevealNextAddressAsync(); // current = slot 1, slot 0 still live

        data.AddUtxo(Slot(0), TxId('a'), 0, 6_000_000);
        data.AddUtxo(Slot(1), TxId('b'), 0, 6_000_000);

        var dest = Slot(9); // any external address not ours-revealed
        var result = await service.SendAsync(dest, 10_000_000);

        Assert.True(result.Success, result.Error);
        var tx = Transaction.Parse(data.CapturedBroadcastHex, BlazecoinNetwork.Instance);
        Assert.Equal(2, tx.Inputs.Count); // needed coins from BOTH addresses
        var destScript = new BitcoinPubKeyAddress(dest, BlazecoinNetwork.Instance).ScriptPubKey;
        var changeScript = new BitcoinPubKeyAddress(ChangeSlot(0), BlazecoinNetwork.Instance).ScriptPubKey;
        Assert.Equal(10_000_000, tx.Outputs.Single(o => o.ScriptPubKey == destScript).Value.Satoshi);
        Assert.Equal(2_000_000, tx.Outputs.Single(o => o.ScriptPubKey == changeScript).Value.Satoshi); // internal chain
    }

    [Fact]
    public async Task Change_advances_the_chain_and_is_spendable_and_restore_discovers_it()
    {
        var (service, data) = await BuildUnlockedAsync();
        data.AddUtxo(Slot(0), TxId('a'), 0, 10_000_000);

        // First send creates change → change chain advances to index 1.
        var r1 = await service.SendAsync(Slot(9), 4_000_000);
        Assert.True(r1.Success, r1.Error);
        var tx1 = Transaction.Parse(data.CapturedBroadcastHex, BlazecoinNetwork.Instance);
        Assert.Contains(tx1.Outputs, o => o.ScriptPubKey ==
            new BitcoinPubKeyAddress(ChangeSlot(0), BlazecoinNetwork.Instance).ScriptPubKey);

        // The change coin is spendable (its key resolves): fund the change address and send it.
        data.Utxos.Clear();
        data.AddUtxo(ChangeSlot(0), TxId('c'), 0, 6_000_000, confs: 3);
        Assert.Equal(6_000_000, await service.GetSpendableAsync());
        var r2 = await service.SendAsync(Slot(8), 5_000_000);
        Assert.True(r2.Success, r2.Error);
        // Second change goes to a FRESH change address (index 1) — no reuse.
        var tx2 = Transaction.Parse(data.CapturedBroadcastHex, BlazecoinNetwork.Instance);
        Assert.Contains(tx2.Outputs, o => o.ScriptPubKey ==
            new BitcoinPubKeyAddress(ChangeSlot(1), BlazecoinNetwork.Instance).ScriptPubKey);

        // A mnemonic-only restore rediscovers the used change addresses from the chain.
        data.MarkUsed(ChangeSlot(0));
        data.MarkUsed(ChangeSlot(1));
        var fresh = new LiteWalletService(new MemoryVault(), data);
        await fresh.RestoreWalletAsync(TestMnemonic);
        data.Utxos.Clear();
        data.AddUtxo(ChangeSlot(1), TxId('d'), 0, 2_000_000, confs: 3);
        Assert.Equal(2_000_000, await fresh.GetSpendableAsync()); // found + spendable after restore
    }

    [Fact]
    public async Task Balance_and_summary_aggregate_across_revealed_addresses()
    {
        var (service, data) = await BuildUnlockedAsync();
        data.MarkUsed(service.Address);
        await service.RevealNextAddressAsync();

        data.AddUtxo(Slot(0), TxId('a'), 0, 1_000_000);
        data.AddUtxo(Slot(1), TxId('b'), 0, 2_500_000);
        data.Summaries[Slot(0)] = new LiteAddressSummary(1_000_000, 2_000_000, 1_000_000, 3);
        data.Summaries[Slot(1)] = new LiteAddressSummary(2_500_000, 2_500_000, 0, 1);

        Assert.Equal(3_500_000, await service.GetSpendableAsync());
        var summary = await service.GetSummaryAsync();
        Assert.Equal(3_500_000, summary!.Balance);
        Assert.Equal(4, summary.TxCount);
    }

    // ── History ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_merges_addresses_and_nets_transactions_touching_both()
    {
        var (service, data) = await BuildUnlockedAsync();
        data.MarkUsed(service.Address);
        await service.RevealNextAddressAsync();

        var when = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        // A self-rotation tx: slot 0 sent 5, of which 2 came back to slot 1 as change —
        // net wallet effect −3, ONE row.
        data.History[Slot(0)] =
        [
            new LiteHistoryEntry(TxId('d'), 200, when, -5_000_000, "sent"),
            new LiteHistoryEntry(TxId('e'), 150, when.AddMinutes(-30), 4_000_000, "received"),
        ];
        data.History[Slot(1)] =
        [
            new LiteHistoryEntry(TxId('d'), 200, when, 2_000_000, "received"),
            new LiteHistoryEntry(TxId('f'), 300, when.AddMinutes(30), 1_000_000, "received"),
        ];

        var history = await service.GetHistoryAsync();

        Assert.Equal(3, history.Count); // txid 'd' collapsed to one row
        var netted = history.Single(h => h.TxId == TxId('d'));
        Assert.Equal(-3_000_000, netted.Amount);
        Assert.Equal("sent", netted.Type);
        Assert.Equal(TxId('f'), history[0].TxId); // newest (highest block) first
    }

    [Fact]
    public async Task History_nets_per_utxo_rows_of_one_send_even_single_address()
    {
        // The gateway ledger is per-UTXO: one send consuming three coins (3+2+1 BLZ)
        // emits three "spent" rows sharing the spending txid. The wallet view must show
        // ONE −6 row (2026-07-27 user report — the single-address path skipped netting).
        var (service, data) = await BuildUnlockedAsync();

        var when = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        data.History[service.Address] =
        [
            new LiteHistoryEntry(TxId('d'), 200, when, -3_000_000_00, "sent"),
            new LiteHistoryEntry(TxId('d'), 200, when, -2_000_000_00, "sent"),
            new LiteHistoryEntry(TxId('d'), 200, when, -1_000_000_00, "sent"),
            new LiteHistoryEntry(TxId('e'), 150, when.AddMinutes(-30), 6_000_000_00, "received"),
        ];

        var history = await service.GetHistoryAsync();

        Assert.Equal(2, history.Count);
        var send = history.Single(h => h.TxId == TxId('d'));
        Assert.Equal(-6_000_000_00, send.Amount);
        Assert.Equal("sent", send.Type);
    }

    // ── Legacy WIF sweep ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_wif_reports_what_the_key_holds_including_immature_coins()
    {
        var (service, data) = await BuildUnlockedAsync();
        var oldKey = new Key();
        var wif = oldKey.GetWif(BlazecoinNetwork.Instance).ToString();
        var oldAddress = oldKey.GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString();

        data.AddUtxo(oldAddress, TxId('1'), 0, 7_000_000, confs: 100);
        data.AddUtxo(oldAddress, TxId('2'), 0, 1_000_000, confs: 5, coinbase: true); // immature

        var check = await service.CheckWifAsync(wif);

        Assert.True(check.Valid, check.Error);
        Assert.Equal(oldAddress, check.Address);
        Assert.Equal(7_000_000, check.SpendableSatoshis);
        Assert.Equal(1_000_000, check.ImmatureSatoshis);
        Assert.Equal(2, check.UtxoCount);
    }

    [Fact]
    public async Task Check_wif_rejects_garbage_without_touching_the_chain()
    {
        var (service, _) = await BuildUnlockedAsync();
        var check = await service.CheckWifAsync("definitely-not-a-key");
        Assert.False(check.Valid);
        Assert.Contains("private key", check.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sweep_moves_everything_the_old_key_holds_into_the_wallet()
    {
        var (service, data) = await BuildUnlockedAsync();
        var oldKey = new Key();
        var wif = oldKey.GetWif(BlazecoinNetwork.Instance).ToString();
        var oldAddress = oldKey.GetAddress(ScriptPubKeyType.Legacy, BlazecoinNetwork.Instance).ToString();

        data.AddUtxo(oldAddress, TxId('1'), 0, 7_000_000, confs: 100);
        data.AddUtxo(oldAddress, TxId('2'), 1, 3_000_000, confs: 50);

        var result = await service.SweepWifAsync(wif);

        Assert.True(result.Success, result.Error);
        var tx = Transaction.Parse(data.CapturedBroadcastHex, BlazecoinNetwork.Instance);
        Assert.Equal(2, tx.Inputs.Count);
        var o = Assert.Single(tx.Outputs); // everything, one output, no change
        Assert.Equal(10_000_000, o.Value.Satoshi);
        var homeScript = new BitcoinPubKeyAddress(service.Address, BlazecoinNetwork.Instance).ScriptPubKey;
        Assert.Equal(homeScript, o.ScriptPubKey);
        // The sweep landing is OUR OWN doing — the payment watcher must not toast it.
        Assert.Contains(result.TxId, service.SessionSentTxIds);
    }

    [Fact]
    public async Task Sweep_with_an_empty_key_fails_gracefully()
    {
        var (service, _) = await BuildUnlockedAsync();
        var wif = new Key().GetWif(BlazecoinNetwork.Instance).ToString();
        var result = await service.SweepWifAsync(wif);
        Assert.False(result.Success);
        Assert.Contains("no spendable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Incoming-payment watcher ────────────────────────────────────────────────────────

    [Fact]
    public async Task Watcher_baselines_silently_then_notifies_a_new_payment_once()
    {
        var (service, data) = await BuildUnlockedAsync();
        using var watcher = new IncomingPaymentWatcher(service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;

        data.AddUtxo(service.Address, TxId('a'), 0, 1_000_000);
        await watcher.PollOnceAsync();          // baseline — pre-existing coins are silent
        Assert.Empty(events);

        data.AddUtxo(service.Address, TxId('b'), 0, 2_000_000, confs: 1);
        await watcher.PollOnceAsync();
        var p = Assert.Single(events);
        Assert.Equal(TxId('b'), p.TxId);
        Assert.Equal(2_000_000, p.AmountSatoshis);

        await watcher.PollOnceAsync();          // no re-notification for the same coin
        Assert.Single(events);
    }

    [Fact]
    public async Task Watcher_ignores_change_from_our_own_sends()
    {
        var (service, data) = await BuildUnlockedAsync();
        using var watcher = new IncomingPaymentWatcher(service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;

        data.AddUtxo(service.Address, TxId('a'), 0, 10_000_000);
        await watcher.PollOnceAsync(); // baseline

        // A send: the relay accepts, and its change lands back as a NEW outpoint.
        var sentTxId = TxId('c');
        data.BroadcastResult = LiteBroadcastResult.Ok(sentTxId);
        var sent = await service.SendAsync(Slot(9), 4_000_000);
        Assert.True(sent.Success, sent.Error);
        data.Utxos[service.Address].Clear();
        data.AddUtxo(service.Address, sentTxId, 1, 6_000_000, confs: 0); // the change output

        await watcher.PollOnceAsync();
        Assert.Empty(events); // own change is not an incoming payment

        // But a genuinely foreign payment still notifies.
        data.AddUtxo(service.Address, TxId('d'), 0, 500_000, confs: 0);
        await watcher.PollOnceAsync();
        Assert.Single(events);
    }

    [Fact]
    public async Task Watcher_sees_payments_on_older_rotation_addresses_too()
    {
        var (service, data) = await BuildUnlockedAsync();
        data.MarkUsed(service.Address);
        await service.RevealNextAddressAsync(); // current = slot 1

        using var watcher = new IncomingPaymentWatcher(service);
        var events = new List<IncomingPayment>();
        watcher.PaymentReceived += events.Add;
        await watcher.PollOnceAsync(); // baseline (empty)

        data.AddUtxo(Slot(0), TxId('e'), 0, 750_000, confs: 1); // pays the OLD address
        await watcher.PollOnceAsync();

        var p = Assert.Single(events);
        Assert.Equal(Slot(0), p.Address);
        Assert.Equal(750_000, p.AmountSatoshis);
    }
}

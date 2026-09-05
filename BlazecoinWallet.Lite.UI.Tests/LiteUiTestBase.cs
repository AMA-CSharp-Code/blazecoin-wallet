using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazecoinWallet.Lite.UI.Tests;

/// <summary>Shared bUnit fakes + wiring for the lite-wallet page tests.</summary>
public abstract class LiteUiTestBase : TestContext
{
    protected const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>Vault pre-loaded with a wallet so UnlockAsync() succeeds (pages redirect to /setup otherwise).</summary>
    protected sealed class LoadedVault : ISeedVault
    {
        public string? Words = TestMnemonic;
        public Task<bool> HasWalletAsync() => Task.FromResult(Words != null);
        public Task SaveMnemonicAsync(string m) { Words = m; return Task.CompletedTask; }
        public Task<string?> LoadMnemonicAsync() => Task.FromResult(Words);
        public Task ClearAsync() { Words = null; return Task.CompletedTask; }
    }

    protected sealed class FakeReader : IChainReader
    {
        public bool HistorySupported = true;
        public long SpendableSats = 10_000_000; // 0.1 BLZ
        /// <summary>Confirmed summary balance when it should DIFFER from the utxos served
        /// (an in-flight send: the gateway hides mempool-spent outpoints from the utxo
        /// list while the summary still counts them). Null → equals SpendableSats.</summary>
        public long? SummaryBalance;
        public IReadOnlyList<LiteHistoryEntry> History = [];
        public bool SupportsHistory => HistorySupported;
        public bool SupportsChainVerification => false;

        public Task<LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default)
            => Task.FromResult<LiteAddressSummary?>(new LiteAddressSummary(
                SummaryBalance ?? SpendableSats, SummaryBalance ?? SpendableSats, 0, 1));
        public Task<IReadOnlyList<LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LiteChainUtxo>>(
                SpendableSats > 0 ? [new LiteChainUtxo(new string('a', 64), 0, SpendableSats, 10, 100, false)] : []);
        public Task<IReadOnlyList<LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default)
            => Task.FromResult(History);
        public Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    protected sealed class FakeRelay : ITxRelay
    {
        public int Calls;
        public LiteBroadcastResult Result = LiteBroadcastResult.Ok("cafebabe");
        public Task<LiteBroadcastResult> BroadcastAsync(string hex, CancellationToken ct = default)
        { Calls++; return Task.FromResult(Result); }
    }

    protected sealed class FakeScanner : IQrScanner
    {
        public bool IsAvailable { get; init; } = true;
        public string? Next;
        public Task<string?> ScanAsync() => Task.FromResult(Next);
    }

    /// <summary>Explorer settings for the page tests (default: no explorer configured).</summary>
    protected InMemoryExplorerSettings Explorer { get; } = new();

    /// <summary>Registers a LiteWalletService over the given fakes + the scanner (null → none).</summary>
    protected LiteWalletService Wire(ISeedVault vault, IChainReader reader, ITxRelay relay,
        IQrScanner? scanner = null, IUnitSettings? units = null)
    {
        // LiteLayout imports the coin-skin JS module on render; loose JSInterop lets the
        // module + its start/stop calls no-op in tests.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new LiteWalletService(vault, reader, relay);
        Services.AddSingleton(svc);
        Services.AddSingleton<IChainReader>(reader); // pages (Watch, TxDetail) inject this directly
        Services.AddSingleton(scanner ?? new FakeScanner { IsAvailable = false });
        Services.AddSingleton<IUnitSettings>(units ?? new InMemoryUnitSettings());
        Services.AddSingleton<IExplorerSettings>(Explorer);
        Services.AddSingleton<ISkinSettings>(new InMemorySkinSettings());
        Services.AddSingleton<ILabelStore, InMemoryLabelStore>();
        Services.AddSingleton<IAddressBook, InMemoryAddressBook>();
        Services.AddSingleton<IWatchList, InMemoryWatchList>();
        Services.AddSingleton<IPinLock, InMemoryPinLock>();
        Services.AddSingleton<IBiometricAuth, NullBiometricAuth>();
        Services.AddSingleton<AppLockSession>();
        Services.AddSingleton<IWalletLocker, PinGateWalletLocker>();
        Services.AddSingleton<IAutoLockSettings, InMemoryAutoLockSettings>();
        Services.AddSingleton<CometPulse>(); // Home comet event recolour (LiteLayout/Send pulse it)
        // A no-feed header sync (Available == false) — Settings hides its verification card and
        // the wallet falls back to gateway confirmations, exactly like personal-node mode.
        Services.AddSingleton(new HeaderChainSync());
        return svc;
    }
}

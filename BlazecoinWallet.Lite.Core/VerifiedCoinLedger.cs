using System.Collections.Concurrent;

namespace BlazecoinWallet.Lite;

/// <summary>What a proven coin IS, read from the proof and not from the gateway's row: whether
/// its funding transaction is a coinbase, and the height the header chain placed its block at
/// (0 = no header feed) (bug-bounty report 2026-11-06, fourth round).</summary>
internal readonly record struct CoinFacts(bool IsCoinbase, long Height);

/// <summary>
/// One proven coin as it is remembered across restarts. The stored line is
/// <c>txid:vout:address:sats:coinbase(0|1):height</c>; a line in any other shape — the
/// four-field form 1.3.3 wrote says nothing about the coin's kind — is not a remembered coin,
/// and the coin is simply proven again.
/// </summary>
internal sealed record RememberedCoin(string TxId, int Vout, string Address, long Satoshis, bool IsCoinbase, long Height)
{
    public string Serialize() => $"{TxId}:{Vout}:{Address}:{Satoshis}:{(IsCoinbase ? 1 : 0)}:{Height}";

    public static RememberedCoin? TryParse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var p = line.Split(':');
        return p.Length == 6 && p[0].Length == 64 && int.TryParse(p[1], out var vout) && vout >= 0 &&
               long.TryParse(p[3], out var sats) && sats > 0 &&
               (p[4] == "0" || p[4] == "1") && long.TryParse(p[5], out var height) && height >= 0
            ? new RememberedCoin(p[0], vout, p[2], sats, p[4] == "1", height)
            : null;
    }
}

/// <summary>
/// The wallet's memory of the coins it has PROVEN (audit 2026-11-09: taken out of the wallet
/// service, which had come to own a stored format). Two things live here: what each proven coin
/// is (<see cref="CoinFacts"/>, for this session), and the set that survives a restart so the
/// balance need not wait for every coin to be proven again — coins joined by ';', sealed with a
/// key from the wallet's seed (<see cref="SealedValue"/>, purpose <see cref="RememberedCoinsSeal.Purpose"/>).
/// The store is ordinary app storage: a set this wallet did not seal is ignored, never an error
/// (bug-bounty report 2026-11-09), and losing it only costs re-verification.
/// </summary>
internal sealed class VerifiedCoinLedger
{
    /// <summary>Most proven coins remembered across restarts (largest first) — bounds the store.</summary>
    public const int MaxPersisted = 2000;

    private readonly IWalletStateStore _store;
    private readonly ConcurrentDictionary<string, CoinFacts> _facts = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private string? _saved;

    public VerifiedCoinLedger(IWalletStateStore store) => _store = store;

    // The owning address is part of a coin's identity here: the proof is "this output pays THIS address".
    private static string Key(string txId, int vout, string address) => $"{txId}:{vout}:{address}";

    /// <summary>Records what a coin just proven is.</summary>
    public void Note(string txId, int vout, string address, CoinFacts facts) => _facts[Key(txId, vout, address)] = facts;

    public bool TryGetFacts(string txId, int vout, string address, out CoinFacts facts) =>
        _facts.TryGetValue(Key(txId, vout, address), out facts);

    /// <summary>
    /// The coins proven in an earlier session, once per unlock of the store — but only a set this
    /// wallet sealed itself. Empty when it was already read, the wallet is locked (nothing to
    /// open a seal with), or the seal does not open: those coins are proven again and written
    /// back sealed. The caller adopts what comes back; their facts are already noted.
    /// </summary>
    public async Task<IReadOnlyList<RememberedCoin>> LoadAsync(LiteHdWallet? wallet)
    {
        if (_loaded || wallet == null) return [];
        _loaded = true;
        try
        {
            var stored = await _store.GetVerifiedCoinsAsync();
            if (!SealedValue.TryOpen(wallet.DeriveStateKey(RememberedCoinsSeal.Purpose), stored, out var text))
                return [];
            _saved = text;
            var coins = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(RememberedCoin.TryParse).OfType<RememberedCoin>().ToList();
            foreach (var coin in coins)
                Note(coin.TxId, coin.Vout, coin.Address, new CoinFacts(coin.IsCoinbase, coin.Height));
            return coins;
        }
        catch { return []; /* losing the remembered set only costs a re-verification */ }
    }

    /// <summary>Remembers the proven coins the gateway still lists (those whose facts are known).
    /// An empty view is not saved — a failed read returns no coins, and must not forget the
    /// proofs — and nothing is written while the wallet is locked or when nothing changed.</summary>
    public async Task SaveAsync(LiteHdWallet? wallet, IEnumerable<(string TxId, int Vout, string Address, long Satoshis)> proven)
    {
        var coins = new List<RememberedCoin>();
        foreach (var (txId, vout, address, satoshis) in proven)
            if (TryGetFacts(txId, vout, address, out var facts))
                coins.Add(new RememberedCoin(txId, vout, address, satoshis, facts.IsCoinbase, facts.Height));
        if (coins.Count == 0) return;

        var text = string.Join(';', coins
            .OrderByDescending(c => c.Satoshis)
            .Take(MaxPersisted)
            .OrderBy(c => c.TxId, StringComparer.Ordinal).ThenBy(c => c.Vout)
            .Select(c => c.Serialize()));
        if (text == _saved || wallet == null) return;
        _saved = text;
        try { await _store.SetVerifiedCoinsAsync(SealedValue.Seal(wallet.DeriveStateKey(RememberedCoinsSeal.Purpose), text)); }
        catch { /* best effort */ }
    }

    /// <summary>Forgets everything (wallet reset): the next unlock reads the store afresh.</summary>
    public void Clear()
    {
        _facts.Clear();
        _loaded = false;
        _saved = null;
    }
}

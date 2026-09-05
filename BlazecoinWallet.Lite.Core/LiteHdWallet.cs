using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>
/// The lite wallet's HD key manager: a BIP39 mnemonic is the ONLY backup artifact (a phone
/// can't ship a wallet.dat), from which every key derives deterministically along
/// m/44'/413'/account'/change/index (see <see cref="BlazecoinNetwork.Bip44CoinType"/>).
/// Addresses are legacy P2PKH ('B…') only — the chain has no segwit.
/// Network-parametrizable (mainnet default; the regtest harness passes
/// <see cref="BlazecoinNetwork.Regtest"/>) — key DERIVATION is network-independent, only
/// the address/WIF ENCODING changes.
/// This class holds key material in memory; PERSISTING the mnemonic/seed is the app head's
/// job (Android Keystore via SecureStorage), never this library's.
/// </summary>
public sealed class LiteHdWallet
{
    private readonly ExtKey _root;
    private readonly Network _network;

    /// <summary>The mnemonic this wallet was created/restored from — show once for backup.</summary>
    public string MnemonicWords { get; }

    private LiteHdWallet(Mnemonic mnemonic, string passphrase, Network? network)
    {
        MnemonicWords = mnemonic.ToString();
        _root = mnemonic.DeriveExtKey(passphrase);
        _network = network ?? BlazecoinNetwork.Instance;
    }

    /// <summary>Creates a brand-new wallet with a fresh 12- or 24-word English mnemonic.</summary>
    public static LiteHdWallet CreateNew(int words = 12, string passphrase = "", Network? network = null)
    {
        var wordCount = words switch
        {
            12 => WordCount.Twelve,
            24 => WordCount.TwentyFour,
            _ => throw new ArgumentOutOfRangeException(nameof(words), "Mnemonic must be 12 or 24 words."),
        };
        return new LiteHdWallet(new Mnemonic(Wordlist.English, wordCount), passphrase, network);
    }

    /// <summary>Restores a wallet from an existing mnemonic (validates the BIP39 checksum).</summary>
    public static LiteHdWallet Restore(string mnemonicWords, string passphrase = "", Network? network = null)
    {
        // NBitcoin's ctor validates the wordlist but NOT the checksum — a typo'd mnemonic
        // would silently restore to a different (empty) wallet. Fail loudly instead.
        var mnemonic = new Mnemonic(mnemonicWords, Wordlist.English);
        if (!mnemonic.IsValidChecksum)
            throw new FormatException("Invalid mnemonic: BIP39 checksum does not match (typo in the words?).");
        return new LiteHdWallet(mnemonic, passphrase, network);
    }

    private ExtKey DeriveKey(int index, int account, bool change)
        => _root.Derive(new KeyPath($"m/44'/{BlazecoinNetwork.Bip44CoinType}'/{account}'/{(change ? 1 : 0)}/{index}"));

    /// <summary>The P2PKH receive address at the given index (external chain).</summary>
    public string GetReceiveAddress(int index, int account = 0)
        => DeriveKey(index, account, change: false).GetPublicKey()
            .GetAddress(ScriptPubKeyType.Legacy, _network).ToString();

    /// <summary>The P2PKH change address at the given index (internal chain).</summary>
    public string GetChangeAddress(int index, int account = 0)
        => DeriveKey(index, account, change: true).GetPublicKey()
            .GetAddress(ScriptPubKeyType.Legacy, _network).ToString();

    /// <summary>The private key for a receive/change slot — used by the transaction signer.</summary>
    public Key GetPrivateKey(int index, bool change = false, int account = 0)
        => DeriveKey(index, account, change).PrivateKey;

    /// <summary>WIF export of a single key (prefix 154 on mainnet), for power users.</summary>
    public string GetWif(int index, bool change = false, int account = 0)
        => GetPrivateKey(index, change, account).GetWif(_network).ToString();

    /// <summary>The account-level extended PUBLIC key (m/44'/413'/account'), for watch-only:
    /// hand it to another device to monitor this wallet's receive balance with no keys.</summary>
    public string GetAccountXpub(int account = 0)
        => _root.Derive(new KeyPath($"m/44'/{BlazecoinNetwork.Bip44CoinType}'/{account}'"))
            .Neuter().GetWif(_network).ToString();

    /// <summary>Signs a message with a receive-slot key (Blazecoin signed message — same
    /// magic as the daemon's signmessage). Verifies against the slot's P2PKH address.</summary>
    public string SignMessage(int index, string message, int account = 0)
        => BlazecoinMessage.Sign(GetPrivateKey(index, account: account), message);
}

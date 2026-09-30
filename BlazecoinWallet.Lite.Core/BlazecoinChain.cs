namespace BlazecoinWallet.Lite;

/// <summary>
/// Blazecoin mainnet chain constants for client-side key/address/transaction work,
/// pinned to the daemon's consensus source (Blazecoin_Wallet_V2_Core
/// src/kernel/chainparams.cpp — CMainParams). The lite wallet signs locally, so these
/// MUST match the chain exactly; the test suite guards them against drift.
/// </summary>
public static class BlazecoinChain
{
    /// <summary>Base58 P2PKH address version byte — addresses start with 'B' (chainparams.cpp:167).</summary>
    public const byte PubKeyAddressPrefix = 26;

    /// <summary>Base58 P2SH script-address version byte (chainparams.cpp:168).</summary>
    public const byte ScriptAddressPrefix = 5;

    /// <summary>WIF private-key version byte = 128 + 26 (chainparams.cpp:169).</summary>
    public const byte SecretKeyPrefix = 154;

    /// <summary>BIP32 extended public key version (xpub — Bitcoin-standard, chainparams.cpp:170).</summary>
    public static readonly byte[] ExtPublicKeyPrefix = { 0x04, 0x88, 0xB2, 0x1E };

    /// <summary>BIP32 extended private key version (xprv — Bitcoin-standard, chainparams.cpp:171).</summary>
    public static readonly byte[] ExtSecretKeyPrefix = { 0x04, 0x88, 0xAD, 0xE4 };

    /// <summary>P2P network magic (pchMessageStart) — fb c0 b6 db.</summary>
    public static readonly byte[] MessageMagic = { 0xFB, 0xC0, 0xB6, 0xDB };

    /// <summary>Mainnet P2P port.</summary>
    public const int P2PPort = 55414;

    /// <summary>Target block spacing in seconds.</summary>
    public const int BlockSpacingSeconds = 30;

    /// <summary>Satoshis per BLZ.</summary>
    public const long Coin = 100_000_000;

    /// <summary>MAX_MONEY = 2,065,000,000 BLZ in satoshis.</summary>
    public const long MaxMoney = 2_065_000_000L * Coin;

    /// <summary>
    /// The chain is legacy-only: no segwit/bech32 outputs exist and the desktop wallet
    /// already refuses blz1 destinations. The lite wallet builds P2PKH only.
    /// </summary>
    public const bool SegwitEnabled = false;

    /// <summary>
    /// H_Q — the height at which the post-quantum output type P2PQH (PQ_SIGNATURES.md §7)
    /// activates: from this height the daemon accepts OP_CHECKPQSIG spends and relays
    /// payments to BQ addresses. NULL = not chosen yet (the 13 decisions were ratified
    /// 2026-09-14; the height is a separate, later decision). While null — or while the
    /// tip is below it — the lite wallet refuses to PAY a BQ address (coin parked until
    /// H_Q, §3.6), refuses to SPEND a P2PQH coin (rejected by every node), and does not
    /// offer BQ receive addresses (§13 decision 8). Pinned by the test suite.
    /// </summary>
    public static readonly long? PqSigActivationHeight = 4_250_000; // H_Q chosen 2026-09-15 (PQ_SIGNATURES.md s7/s15)
}

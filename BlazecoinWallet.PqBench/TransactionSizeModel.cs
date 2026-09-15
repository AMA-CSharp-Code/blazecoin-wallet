namespace BlazecoinWallet.PqBench;

/// <summary>
/// PQ_SIGNATURES.md §4, computed rather than typed: how big one input is for a scheme, and how many fit
/// a Blazecoin block (1,000,000 serialized bytes — weight 4 M with no witness) and a standard
/// transaction (MAX_STANDARD_TX_WEIGHT 400,000 ⇒ 100,000 bytes). Legacy P2PKH is the baseline:
/// scriptSig = push(DER sig + hashtype) + push(33-byte pubkey); P2PQH (spec §3.3) = push(sig) +
/// push(algo_id || pubkey). Push overhead follows the script pushdata rules (1 byte ≤ 75, 2 bytes
/// ≤ 255, 3 bytes ≤ 65,535).
/// </summary>
public sealed record InputSizeEstimate(
    string Scheme, int ScriptPubKeyBytes, int ScriptSigBytes, int InputBytes,
    int InputsPerBlock, int InputsPerStandardTx, long InputsPerDay)
{
    public const int BlockBytes = 1_000_000;
    public const int StandardTxBytes = 100_000;
    public const int BlocksPerDay = 2_880;              // 30 s target
    private const int BlockOverhead = 80 + 1 + 10 + 34;  // header, tx count, one tx's fixed fields, one output
    private const int TxOverhead = 10 + 34;              // version, in/out counts, locktime, one output

    public static int PushOverhead(int len) => len <= 75 ? 1 : len <= 255 ? 2 : 3;
    public static int VarIntBytes(long n) => n < 0xfd ? 1 : n <= 0xffff ? 3 : 5;

    public static InputSizeEstimate ForScheme(ISignatureScheme scheme)
    {
        var isLegacy = scheme.Family == "ECDSA";
        var keyBlob = isLegacy ? scheme.PublicKeyBytes : 1 + scheme.PublicKeyBytes; // algo_id byte, spec §3.1
        var scriptSig = PushOverhead(scheme.SignatureBytes) + scheme.SignatureBytes + PushOverhead(keyBlob) + keyBlob;
        var input = 36 + VarIntBytes(scriptSig) + scriptSig + 4;
        var perBlock = (BlockBytes - BlockOverhead) / input;
        var perTx = (StandardTxBytes - TxOverhead) / input;
        return new InputSizeEstimate(scheme.Name, isLegacy ? 25 : 34, scriptSig, input, perBlock, perTx, (long)perBlock * BlocksPerDay);
    }
}

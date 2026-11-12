namespace BlazecoinWallet.Core.Services;

/// <summary>
/// The serialized sizes the desktop planners price a transaction with - the one place they live
/// (audit 2026-11-12: the Quantum sweep planner and the consolidation planner each carried their
/// own copy of the legacy figures, the way the dust floors were duplicated before
/// <see cref="DustPolicy"/>).
/// </summary>
public static class TransactionSizes
{
    /// <summary>Version 4 + lock time 4 + one byte each for the input and output counts.</summary>
    public const int Overhead = 10;
    /// <summary>A legacy P2PKH input with a compressed key.</summary>
    public const int LegacyInput = 148;
    /// <summary>A legacy P2PKH output.</summary>
    public const int LegacyOutput = 34;
    /// <summary>A P2PQH input: outpoint 36 + length 3 + scriptSig 3,739 (ML-DSA-44 signature and key) + sequence 4 (PQ_SIGNATURES.md section 3.3).</summary>
    public const int PostQuantumInput = 3_782;
    /// <summary>A P2PQH output: 8 value + 1 length + 34-byte scriptPubKey (PQ_SIGNATURES.md section 5).</summary>
    public const int PostQuantumOutput = 43;
    /// <summary>MAX_STANDARD_TX_WEIGHT / 4: a larger transaction is not relayed. It admits 26 post-quantum inputs.</summary>
    public const int MaxStandardTx = 100_000;
}

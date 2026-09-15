using BlazecoinWallet.Core.Services.Provenance;

namespace BlazecoinWallet.Core.Services.Quantum;

/// <summary>
/// The three wallet reads behind the quantum-exposure scan. Kept as its own slice so the
/// scanner can be tested against a hand-rolled fake, the same way the ancestry tracer is.
/// </summary>
public interface IExposureRpc
{
    /// <summary>Every spendable output the wallet holds, with the address it sits on (satoshis).</summary>
    Task<IReadOnlyList<UnspentOutput>> ListUnspentAsync(CancellationToken ct = default);

    /// <summary>
    /// Distinct txids of every transaction this wallet SENT (category "send"). Those are the
    /// only transactions whose inputs this wallet signed, so they are the only places one of
    /// its public keys can have been revealed on-chain.
    /// </summary>
    Task<IReadOnlyList<string>> ListSpendingTxIdsAsync(int maxTransactions = 100_000, CancellationToken ct = default);

    /// <summary>The raw scriptSig hex of each non-coinbase input of one wallet transaction.</summary>
    Task<IReadOnlyList<string>> GetInputScriptSigHexAsync(string txid, CancellationToken ct = default);
}

/// <summary>Why an address is, or is not, exposed to a future quantum attacker.</summary>
public enum ExposureStatus
{
    /// <summary>Holds coins and has never signed: its public key is still only a hash on-chain.</summary>
    Unexposed,
    /// <summary>Holds coins AND has signed at least one input: the public key is public forever.</summary>
    Exposed,
    /// <summary>A post-quantum (BQ…, P2PQH) address: its ML-DSA-44 key gives a quantum computer
    /// nothing to work on, spent or not — never "exposed" (PQ_SIGNATURES.md §5).</summary>
    PostQuantum,
}

/// <summary>One funded address and what the chain already knows about its key.</summary>
public sealed record AddressExposure(
    string Address,
    long Satoshis,
    IReadOnlyList<UnspentOutput> Outputs,
    ExposureStatus Status,
    /// <summary>The first wallet transaction whose scriptSig revealed this key, when exposed.</summary>
    string? ExposingTxId)
{
    public int OutputCount => Outputs.Count;
    /// <summary>More than one output on one address is reuse: the address received more than once.</summary>
    public bool IsReused => Outputs.Count > 1;
}

/// <summary>The scan result: every funded address classified, plus the totals the page shows.</summary>
public sealed record ExposureReport(
    IReadOnlyList<AddressExposure> Addresses,
    long TotalSatoshis,
    long ExposedSatoshis,
    /// <summary>Distinct addresses whose key has been revealed, funded or not.</summary>
    int ExposedKeyCount,
    int TransactionsScanned,
    DateTime ScannedAtUtc)
{
    /// <summary>Legacy coins whose key is still only a hash (post-quantum coins counted separately).</summary>
    public long UnexposedSatoshis => TotalSatoshis - ExposedSatoshis - PostQuantumSatoshis;
    /// <summary>Coins on post-quantum (BQ…) addresses — safe whether spent from or not.</summary>
    public long PostQuantumSatoshis => Addresses.Where(a => a.Status == ExposureStatus.PostQuantum).Sum(a => a.Satoshis);
    public int ExposedAddressCount => Addresses.Count(a => a.Status == ExposureStatus.Exposed);
    public int UnexposedAddressCount => Addresses.Count(a => a.Status == ExposureStatus.Unexposed);
    public int PostQuantumAddressCount => Addresses.Count(a => a.Status == ExposureStatus.PostQuantum);
}

/// <summary>Progress for the scan: which transaction of how many, and keys found so far.</summary>
public readonly record struct ExposureProgress(int TransactionsDone, int TransactionsTotal, int ExposedKeysFound);

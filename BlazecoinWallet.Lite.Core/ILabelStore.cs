namespace BlazecoinWallet.Lite;

/// <summary>
/// Local, per-platform annotations on transactions and addresses (a txid or an address is
/// the key). Purely cosmetic — never key material, never leaves the device — so a plain
/// preference blob is the right home. Makes the transaction history readable ("rent", "from
/// Alice", "cold storage").
/// </summary>
public interface ILabelStore
{
    /// <summary>The label for a txid/address, or null if none.</summary>
    string? Get(string key);
    /// <summary>Set (or clear, with null/empty) a label.</summary>
    void Set(string key, string? label);
    /// <summary>All stored labels (for export / a manage view).</summary>
    IReadOnlyDictionary<string, string> All { get; }
}

/// <summary>Non-persistent default (dev/test); real heads register a persistent one.</summary>
public sealed class InMemoryLabelStore : ILabelStore
{
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    public string? Get(string key) => _labels.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) _labels.Remove(key);
        else _labels[key] = label.Trim();
    }
    public IReadOnlyDictionary<string, string> All => _labels;
}

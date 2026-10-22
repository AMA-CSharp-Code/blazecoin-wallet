namespace BlazecoinWallet.Lite;

/// <summary>
/// User-adjustable P2P fallback node list — the peers the wallet broadcasts to when every
/// gateway is unreachable. ADVANCED and rarely touched; defaults to the chain's seed nodes.
/// A change takes effect on the next app start (the wallet service is built once).
/// </summary>
public interface IP2PNodeSettings
{
    /// <summary>The built-in seed nodes — the "reset" target.</summary>
    IReadOnlyList<string> Default { get; }

    /// <summary>The node list in effect right now.</summary>
    IReadOnlyList<string> Current { get; }

    /// <summary>True when the user has overridden the default.</summary>
    bool IsCustom { get; }

    /// <summary>Persist a new node list (already validated host:port by the caller).</summary>
    void Save(IReadOnlyList<string> endpoints);

    /// <summary>Restore the default seed nodes.</summary>
    void ResetToDefault();
}

/// <summary>Non-persistent default for dev/test hosts (real heads register a persistent one).</summary>
public sealed class InMemoryP2PNodeSettings : IP2PNodeSettings
{
    private List<string> _current;

    public InMemoryP2PNodeSettings(IReadOnlyList<string> defaults)
    {
        Default = defaults.ToArray();
        _current = defaults.ToList();
    }

    public IReadOnlyList<string> Default { get; }
    public IReadOnlyList<string> Current => _current;
    public bool IsCustom => !_current.SequenceEqual(Default);
    public void Save(IReadOnlyList<string> endpoints) => _current = endpoints.ToList();
    public void ResetToDefault() => _current = Default.ToList();
}

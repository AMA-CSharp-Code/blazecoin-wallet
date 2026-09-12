namespace BlazecoinWallet.Lite;

/// <summary>
/// User-adjustable gateway configuration, persisted per platform (Preferences on Android,
/// Keychain-adjacent stores later). Editing this is an ADVANCED action — most users never
/// touch it; the wallet ships pointed at the official gateway. A change takes effect on the
/// next app start (the HTTP client + failover list are built once at composition).
/// </summary>
public interface IGatewaySettings
{
    /// <summary>The built-in official gateway list — the "reset" target.</summary>
    IReadOnlyList<string> Default { get; }

    /// <summary>The gateway list in effect right now.</summary>
    IReadOnlyList<string> Current { get; }

    /// <summary>True when the user has overridden the default.</summary>
    bool IsCustom { get; }

    /// <summary>Persist a new gateway list (already validated https/loopback by the caller).</summary>
    void Save(IReadOnlyList<string> urls);

    /// <summary>Restore the official default.</summary>
    void ResetToDefault();
}

/// <summary>
/// Non-persistent default used by dev/test hosts (and as the fallback registration). Real
/// heads register a persistent implementation (Android Preferences) before AddLiteWallet.
/// </summary>
public sealed class InMemoryGatewaySettings : IGatewaySettings
{
    private List<string> _current;

    public InMemoryGatewaySettings(IReadOnlyList<string> defaults)
    {
        Default = defaults.ToArray();
        _current = defaults.ToList();
    }

    public IReadOnlyList<string> Default { get; }
    public IReadOnlyList<string> Current => _current;
    public bool IsCustom => !_current.SequenceEqual(Default);
    public void Save(IReadOnlyList<string> urls) => _current = urls.ToList();
    public void ResetToDefault() => _current = Default.ToList();
}

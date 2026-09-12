using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.Lite;

/// <summary>
/// User-adjustable "connect via my own node" configuration (the trustless/sovereignty mode:
/// balances from your daemon's own chainstate, no indexer trusted). ADVANCED — off by
/// default; the wallet ships in gateway mode. A change takes effect on the next app start.
/// Persisted per platform; the RPC PASSWORD is a secret and stored in secure storage
/// (Keystore) by the platform implementation, never in plain preferences.
/// </summary>
public interface IPersonalNodeSettings
{
    /// <summary>True → the wallet reads/broadcasts via the personal node instead of the gateway.</summary>
    bool Enabled { get; }

    /// <summary>The node connection when enabled; null otherwise.</summary>
    PersonalNodeOptions? Config { get; }

    /// <summary>Turn on personal-node mode with the given connection (already validated).</summary>
    void Enable(PersonalNodeOptions config);

    /// <summary>Turn off — back to gateway mode.</summary>
    void Disable();
}

/// <summary>Non-persistent default for dev/test hosts (real heads register a persistent one).</summary>
public sealed class InMemoryPersonalNodeSettings : IPersonalNodeSettings
{
    public bool Enabled { get; private set; }
    public PersonalNodeOptions? Config { get; private set; }
    public void Enable(PersonalNodeOptions config) { Config = config; Enabled = true; }
    public void Disable() { Enabled = false; Config = null; }
}

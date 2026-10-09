namespace BlazecoinWallet.Lite;

/// <summary>The block-explorer base URL for tx/address deep links (empty = no explorer),
/// persisted per platform. Display-only convenience — never touches wallet security.</summary>
public interface IExplorerSettings
{
    /// <summary>Explorer permalink root, or "" when none is set.</summary>
    string BaseUrl { get; }

    /// <summary>Persist a new base URL (already validated https/empty by the caller).</summary>
    void Save(string baseUrl);
}

/// <summary>Non-persistent default (dev/test); real heads register a persistent one.</summary>
public sealed class InMemoryExplorerSettings : IExplorerSettings
{
    public string BaseUrl { get; private set; } = BlazecoinWallet.Lite.Data.LiteExplorer.DefaultBaseUrl; // the apex permalink root (2026-09-05)
    public void Save(string baseUrl) => BaseUrl = baseUrl;
}

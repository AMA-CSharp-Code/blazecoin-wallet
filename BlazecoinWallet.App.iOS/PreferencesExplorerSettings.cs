using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.App.iOS;

/// <summary>Android-persistent <see cref="IExplorerSettings"/> over MAUI Preferences
/// (display-only, no secret). Defaults to the production explorer; an explicitly saved "" disables links.</summary>
public sealed class PreferencesExplorerSettings : IExplorerSettings
{
    private const string Key = "explorer_url";
    public string BaseUrl => Preferences.Default.Get(Key, LiteExplorer.DefaultBaseUrl);   // fresh profile → the apex explorer (2026-09-05)
    public void Save(string baseUrl) => Preferences.Default.Set(Key, baseUrl);
}

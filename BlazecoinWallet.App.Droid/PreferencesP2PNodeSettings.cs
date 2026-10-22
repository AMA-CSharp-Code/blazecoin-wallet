using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.Droid;

/// <summary>
/// Android-persistent <see cref="IP2PNodeSettings"/> over MAUI Preferences (the same
/// "p2p_nodes" key MauiProgram reads at startup), so an advanced-user change survives
/// restarts and applies on the next launch. These are unauthenticated peer endpoints, so
/// there's no secret to protect — plain Preferences is fine.
/// </summary>
public sealed class PreferencesP2PNodeSettings : IP2PNodeSettings
{
    private const string Key = "p2p_nodes";
    private readonly string _defaultCsv;

    public PreferencesP2PNodeSettings(string defaultCsv) => _defaultCsv = defaultCsv;

    public IReadOnlyList<string> Default => Split(_defaultCsv);
    public IReadOnlyList<string> Current => Split(Preferences.Default.Get(Key, _defaultCsv));
    public bool IsCustom => !Current.SequenceEqual(Default);

    public void Save(IReadOnlyList<string> endpoints) =>
        Preferences.Default.Set(Key, string.Join(';', endpoints));

    public void ResetToDefault() => Preferences.Default.Remove(Key);

    private static string[] Split(string csv) =>
        csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

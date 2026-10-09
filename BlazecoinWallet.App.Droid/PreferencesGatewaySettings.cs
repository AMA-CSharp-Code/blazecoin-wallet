using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.Droid;

/// <summary>
/// Android-persistent <see cref="IGatewaySettings"/> over MAUI Preferences (the same
/// "gateway_urls" key MauiProgram reads at startup), so an advanced-user change survives
/// restarts and takes effect on the next launch.
/// </summary>
public sealed class PreferencesGatewaySettings : IGatewaySettings
{
    private const string Key = "gateway_urls";
    private readonly string _defaultCsv;

    public PreferencesGatewaySettings(string defaultCsv) => _defaultCsv = defaultCsv;

    public IReadOnlyList<string> Default => Split(_defaultCsv);
    public IReadOnlyList<string> Current => Split(Preferences.Default.Get(Key, _defaultCsv));
    public bool IsCustom => !Current.SequenceEqual(Default);

    public void Save(IReadOnlyList<string> urls) =>
        Preferences.Default.Set(Key, string.Join(';', urls));

    public void ResetToDefault() => Preferences.Default.Remove(Key);

    private static string[] Split(string csv) =>
        csv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

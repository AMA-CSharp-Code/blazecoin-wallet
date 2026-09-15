using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.iOS;

/// <summary>Android-persistent <see cref="IUnitSettings"/> over MAUI Preferences (display-only,
/// no secret). The choice applies immediately in the UI and survives restarts.</summary>
public sealed class PreferencesUnitSettings : IUnitSettings
{
    private const string Key = "display_unit";

    public LiteUnit Unit => Enum.TryParse<LiteUnit>(Preferences.Default.Get(Key, nameof(LiteUnit.Blz)), out var u)
        ? u : LiteUnit.Blz;

    public void Set(LiteUnit unit) => Preferences.Default.Set(Key, unit.ToString());
}

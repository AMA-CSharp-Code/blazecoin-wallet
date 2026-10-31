using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.iOS;

/// <summary>Android-persistent <see cref="ISkinSettings"/> over MAUI Preferences (cosmetic,
/// no secret). Default = LiteSkinResolver.Default; the choice applies immediately and survives
/// restarts. Migrates the pre-choice "coin_background" bool the first time.</summary>
public sealed class PreferencesSkinSettings : ISkinSettings
{
    private const string Key = "skin_choice";
    private const string LegacyKey = "coin_background"; // bool, from the single-skin era

    public LiteSkin Skin
    {
        get
        {
            var raw = Preferences.Default.Get(Key, string.Empty);
            if (Enum.TryParse<LiteSkin>(raw, out var skin)) return skin;
            // Migration: honour an explicit single-skin-era choice; fresh installs get the shared default.
            if (Preferences.Default.ContainsKey(LegacyKey))
                return Preferences.Default.Get(LegacyKey, true) ? LiteSkin.PixelatedCoins : LiteSkin.None;
            return LiteSkinResolver.Default;
        }
    }

    public void SetSkin(LiteSkin skin) => Preferences.Default.Set(Key, skin.ToString());
}

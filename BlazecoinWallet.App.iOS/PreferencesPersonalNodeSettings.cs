using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.Data;

namespace BlazecoinWallet.App.iOS;

/// <summary>
/// Android-persistent <see cref="IPersonalNodeSettings"/>. Non-secret fields (enabled flag,
/// RPC URL, username, cookie path) live in Preferences; the RPC PASSWORD is a secret and is
/// stored in SecureStorage (Android Keystore-backed), never in plain preferences.
/// </summary>
public sealed class PreferencesPersonalNodeSettings : IPersonalNodeSettings
{
    private const string EnabledKey = "node_enabled";
    private const string UrlKey = "node_rpc_url";
    private const string UserKey = "node_rpc_user";
    private const string CookieKey = "node_cookie_path";
    private const string PasswordKey = "node_rpc_password"; // SecureStorage

    public bool Enabled => Preferences.Default.Get(EnabledKey, false);

    public PersonalNodeOptions? Config
    {
        get
        {
            if (!Enabled) return null;
            var url = Preferences.Default.Get(UrlKey, "");
            if (string.IsNullOrEmpty(url)) return null;
            var user = Preferences.Default.Get(UserKey, "");
            var cookie = Preferences.Default.Get(CookieKey, "");
            // SecureStorage is async; block briefly — this runs once at startup.
            var pw = SecureStorage.Default.GetAsync(PasswordKey).GetAwaiter().GetResult();
            return new PersonalNodeOptions(
                RpcUrl: url,
                CookieFilePath: string.IsNullOrEmpty(cookie) ? null : cookie,
                RpcUser: string.IsNullOrEmpty(user) ? null : user,
                RpcPassword: string.IsNullOrEmpty(pw) ? null : pw);
        }
    }

    public void Enable(PersonalNodeOptions config)
    {
        Preferences.Default.Set(UrlKey, config.RpcUrl);
        Preferences.Default.Set(UserKey, config.RpcUser ?? "");
        Preferences.Default.Set(CookieKey, config.CookieFilePath ?? "");
        if (!string.IsNullOrEmpty(config.RpcPassword))
            SecureStorage.Default.SetAsync(PasswordKey, config.RpcPassword).GetAwaiter().GetResult();
        Preferences.Default.Set(EnabledKey, true);
    }

    public void Disable()
    {
        Preferences.Default.Set(EnabledKey, false);
        SecureStorage.Default.Remove(PasswordKey);
    }
}

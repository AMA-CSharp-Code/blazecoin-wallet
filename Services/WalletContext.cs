using Microsoft.Maui.Storage;
using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Maui.Services;

/// <summary>MAUI implementation of <see cref="IWalletContext"/> (interface lives in
/// BlazecoinWallet.Core). Persists the active wallet via MAUI Preferences.</summary>
public class WalletContext : IWalletContext
{
    private const string PrefKey = "active_wallet";
    private string? _active;

    public event Action? Changed;

    public WalletContext()
    {
        var saved = Preferences.Default.Get(PrefKey, string.Empty);
        _active = string.IsNullOrEmpty(saved) ? null : saved;
    }

    public string? Active => _active;

    public void SetActive(string? name)
    {
        var normalized = string.IsNullOrEmpty(name) ? null : name;
        if (_active == normalized) return;

        _active = normalized;
        if (normalized is null) Preferences.Default.Remove(PrefKey);
        else Preferences.Default.Set(PrefKey, normalized);

        Changed?.Invoke();
    }
}

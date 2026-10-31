namespace BlazecoinWallet.Lite;

/// <summary>
/// Device biometric authentication (fingerprint/face) — a DISTINCT axis from the PIN lock
/// (audit round-3 F5), so it lives in its own file. A seam the app head fills with a
/// platform provider; the null default reports unavailable so the UI simply falls back to
/// the PIN (Send's biometric gate becomes a no-op, the lock screen hides its biometric
/// button).
/// </summary>
public interface IBiometricAuth
{
    /// <summary>Whether the device can do biometric auth right now (hardware present + a
    /// fingerprint/face enrolled). Async because querying the platform is async.</summary>
    Task<bool> IsAvailableAsync();
    /// <summary>Prompt for a biometric check; true when the user authenticated.</summary>
    Task<bool> AuthenticateAsync(string reason);
}

/// <summary>No biometric available (default; heads that support it register their own).</summary>
public sealed class NullBiometricAuth : IBiometricAuth
{
    public Task<bool> IsAvailableAsync() => Task.FromResult(false);
    public Task<bool> AuthenticateAsync(string reason) => Task.FromResult(false);
}

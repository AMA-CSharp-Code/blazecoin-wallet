using BlazecoinWallet.Lite;
using LocalAuthentication;

namespace BlazecoinWallet.App.iOS;

/// <summary>
/// iOS biometric gate over <see cref="LAContext"/> (Face ID / Touch ID), bridged to the
/// platform-free <see cref="IBiometricAuth"/> — the twin of the Droid head's
/// DroidBiometricAuth. Availability = a biometry-capable, enrolled device
/// (<see cref="LAPolicy.DeviceOwnerAuthenticationWithBiometrics"/> — deliberately NOT the
/// passcode-fallback policy: the wallet's own PIN is the fallback, matching Android).
/// Face ID additionally requires the NSFaceIDUsageDescription entry in Info.plist.
/// COMPILE-VERIFIED ONLY until the Mac/device smoke gate exists — the null default covers
/// any device without biometrics, same contract as Android.
/// </summary>
public sealed class IosBiometricAuth : IBiometricAuth
{
    public Task<bool> IsAvailableAsync()
    {
        using var ctx = new LAContext();
        return Task.FromResult(
            ctx.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out var _));
    }

    public async Task<bool> AuthenticateAsync(string reason)
    {
        using var ctx = new LAContext();
        if (!ctx.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out var _))
            return false;

        // EvaluatePolicyAsync completes from the system sheet's callback; a cancel,
        // lockout or failed match resolves false — never throws to the caller.
        var result = await ctx.EvaluatePolicyAsync(
            LAPolicy.DeviceOwnerAuthenticationWithBiometrics, reason);
        return result.Item1;
    }
}

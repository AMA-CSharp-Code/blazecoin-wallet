using AndroidX.Biometric;
using AndroidX.Fragment.App;
using BlazecoinWallet.Lite;
using Java.Util.Concurrent;

namespace BlazecoinWallet.App.Droid;

/// <summary>
/// Android biometric gate over AndroidX <see cref="BiometricPrompt"/> (fingerprint/face),
/// bridged to the platform-free <see cref="IBiometricAuth"/>. Availability = strong biometric
/// enrolled; authentication runs the system prompt and completes the TaskCompletionSource
/// from the prompt callback. Wraps the current <see cref="FragmentActivity"/> (the MAUI
/// MainActivity is one). Runtime behaviour is validated by the tracked emulator smoke test —
/// this is compile-verified here; the null default covers any device without biometrics.
/// </summary>
public sealed class DroidBiometricAuth : IBiometricAuth
{
    private static FragmentActivity? Activity =>
        Platform.CurrentActivity as FragmentActivity;

    public Task<bool> IsAvailableAsync()
    {
        // Availability needs only a Context, and Platform.CurrentActivity is still null
        // while the Blazor layout initialises on a cold start — querying via the activity
        // hid the biometrics button even with a strong sensor enrolled (found in the
        // 2026-07-27 emulator smoke). AppContext always exists; only the prompt itself
        // needs the FragmentActivity.
        var can = BiometricManager.From(Platform.AppContext)
            .CanAuthenticate(BiometricManager.Authenticators.BiometricStrong);
        return Task.FromResult(can == BiometricManager.BiometricSuccess);
    }

    public Task<bool> AuthenticateAsync(string reason)
    {
        var activity = Activity;
        if (activity == null) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>();
        var executor = Executors.NewSingleThreadExecutor()!;
        var prompt = new BiometricPrompt(activity, executor, new Callback(tcs));

        var info = new BiometricPrompt.PromptInfo.Builder()
            .SetTitle("Blazecoin Wallet")
            .SetSubtitle(reason)
            .SetNegativeButtonText("Cancel")
            .SetAllowedAuthenticators(BiometricManager.Authenticators.BiometricStrong)
            .Build();

        // Marshal the prompt onto the UI thread; the callback resolves the task.
        activity.RunOnUiThread(() => prompt.Authenticate(info));
        return tcs.Task;
    }

    private sealed class Callback(TaskCompletionSource<bool> tcs) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => tcs.TrySetResult(true);
        public override void OnAuthenticationFailed() { /* a single bad read — the prompt stays up */ }
        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
            => tcs.TrySetResult(false); // cancelled / lockout / no hardware
    }
}

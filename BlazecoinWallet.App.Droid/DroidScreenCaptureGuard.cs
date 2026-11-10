using Android.Views;
using BlazecoinWallet.Lite;

namespace BlazecoinWallet.App.Droid;

/// <summary>
/// Android's answer to <see cref="IScreenCaptureGuard"/>: <c>FLAG_SECURE</c> on the activity's
/// window. While it is set the platform blanks the window from screenshots, MediaProjection
/// screen recording, casting and the recent-apps thumbnail. Set only while a page that shows a
/// recovery phrase or a private key is on screen (bug-bounty report 2026-11-09 — until then the
/// flag was never set and a plain screen capture returned the whole phrase).
/// </summary>
public sealed class DroidScreenCaptureGuard : ScreenCaptureGuard
{
    protected override void Apply(bool protect)
    {
        // Window flags belong to the UI thread; a Blazor page asks from the WebView's dispatcher.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var window = Platform.CurrentActivity?.Window;
            if (window is null) return;
            if (protect) window.AddFlags(WindowManagerFlags.Secure);
            else window.ClearFlags(WindowManagerFlags.Secure);
        });
    }
}

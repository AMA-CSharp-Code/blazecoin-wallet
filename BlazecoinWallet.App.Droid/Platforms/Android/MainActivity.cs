using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace BlazecoinWallet.App.Droid;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 15+ enforces edge-to-edge for apps targeting API 35+: the WebView drew
        // under the status bar, parking the wifi/battery icons on top of the app header and
        // making the Settings gear untappable (found in the 2026-07-27 emulator smoke).
        // Pad the content view by the system-bar insets — the durable fix; the styles.xml
        // windowOptOutEdgeToEdgeEnforcement opt-out is deprecated and ignored at target 36.
        var content = FindViewById(Android.Resource.Id.Content);
        if (content is not null)
        {
            // The revealed strips (behind status/nav bars) show this view's background —
            // black reads as part of the wallet's dark chrome.
            content.SetBackgroundColor(Android.Graphics.Color.Black);
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarsInsetsListener());
        }
    }

    private sealed class SystemBarsInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsetsCompat.Consumed;
        }
    }
}

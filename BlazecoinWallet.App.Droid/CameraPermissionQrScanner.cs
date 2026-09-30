using BlazecoinWallet.Lite;
using BlazecoinWallet.Lite.UI;

namespace BlazecoinWallet.App.Droid;

/// <summary>
/// Droid decoration over the shared <see cref="JsQrScanner"/>: ensures the OS CAMERA
/// permission has been requested before the scan overlay opens — the WebView-level
/// grant in <see cref="CameraWebChromeClient"/> only applies once the app itself holds
/// the permission. The runtime permission dialog is another app-initiated,
/// activity-stopping flow, so it runs inside an app-lock external-operation window
/// (the same protection the file chooser needed). A denial is never a dead end:
/// the overlay's camera-unavailable message + image-upload fallback take over.
/// </summary>
internal sealed class CameraPermissionQrScanner(JsQrScanner inner, AppLockSession lockSession) : IQrScanner
{
    public bool IsAvailable => inner.IsAvailable;

    public async Task<string?> ScanAsync()
    {
        using (lockSession.BeginExternalOperation())
        {
            try
            {
                if (await Permissions.CheckStatusAsync<Permissions.Camera>() != PermissionStatus.Granted)
                    await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Camera>());
            }
            catch
            {
                // Unsupported / denied — the overlay handles a camera-less scan gracefully.
            }
        }
        return await inner.ScanAsync();
    }
}

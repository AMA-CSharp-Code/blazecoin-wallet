using BlazecoinWallet.Lite;
using Microsoft.JSInterop;

namespace BlazecoinWallet.Lite.UI;

/// <summary>
/// <see cref="IQrScanner"/> for the Blazor WebView / browser: scanning is done in-page by
/// js/lite-qr.js (getUserMedia + jsQR over canvas pixels), so this only bridges the call and
/// returns the decoded string. Works on any head whose WebView can reach the camera — the Droid
/// head (which grants the WebView camera permission) and the dev host (desktop browser). The JS
/// overlay also offers an "upload image" fallback, so a camera denial is never a dead end.
/// </summary>
public sealed class JsQrScanner(IJSRuntime js, AppLockSession lockSession) : IQrScanner
{
    private readonly IJSRuntime _js = js;
    private readonly AppLockSession _lock = lockSession;

    /// <summary>The head only registers this where a camera is expected; the JS overlay handles
    /// runtime denial (and the image fallback), so this stays a simple true.</summary>
    public bool IsAvailable => true;

    public async Task<string?> ScanAsync()
    {
        // The upload fallback launches the system file chooser, which stops the activity;
        // without this window the background re-lock fires on return and the lock-screen
        // swap destroys the Send page mid-scan (2026-07-27 emulator smoke finding).
        using var externalFlow = _lock.BeginExternalOperation();
        try
        {
            await using var mod = await _js.InvokeAsync<IJSObjectReference>(
                "import", "./_content/BlazecoinWallet.Lite.UI/js/lite-qr.js");
            return await mod.InvokeAsync<string?>("scan");
        }
        catch
        {
            // JS not available (unlikely in a WebView) or the module errored — behave like a
            // cancelled scan rather than throwing into the Send page.
            return null;
        }
    }
}

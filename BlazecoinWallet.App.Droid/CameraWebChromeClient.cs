using Android.Webkit;
using AWebView = Android.Webkit.WebView;

namespace BlazecoinWallet.App.Droid;

/// <summary>
/// Wraps MAUI's internal BlazorWebView <see cref="WebChromeClient"/> (retrieved via the
/// API-26 <c>WebView.WebChromeClient</c> getter) and adds the ONE behaviour it lacks:
/// granting the in-page getUserMedia camera request so the QR overlay can scan live
/// video. Everything else — critically <see cref="OnShowFileChooser"/>, which the scan
/// overlay's image-upload fallback rides — forwards to the wrapped client untouched
/// (replacing the client outright was the documented blocker for this feature).
/// The grant is triple-fenced: video capture only, the app's own origin only, and only
/// when the OS-level CAMERA permission is already held (the scanner requests it before
/// opening the overlay — see CameraPermissionQrScanner).
/// </summary>
internal sealed class CameraWebChromeClient(WebChromeClient inner) : WebChromeClient
{
    /// <summary>MAUI BlazorWebView serves the app under a fixed loopback-ish origin —
    /// observed as https://0.0.0.1/ on MAUI 10 Android (older releases used 0.0.0.0);
    /// accept both so a toolkit bump can't silently kill the camera again.</summary>
    private static readonly string[] AppOrigins = ["https://0.0.0.1", "https://0.0.0.0"];

    private readonly WebChromeClient _inner = inner;

    public override void OnPermissionRequest(PermissionRequest? request)
    {
        var origin = request?.Origin?.ToString() ?? string.Empty;
        Android.Util.Log.Info("BlzCamera",
            $"OnPermissionRequest origin='{origin}' resources=[{string.Join(",", request?.GetResources() ?? [])}]");
        if (request?.GetResources() is { } resources
            && resources.Contains(PermissionRequest.ResourceVideoCapture)
            && AppOrigins.Any(o => origin.StartsWith(o, StringComparison.OrdinalIgnoreCase))
            && Platform.AppContext.CheckSelfPermission(Android.Manifest.Permission.Camera)
                == Android.Content.PM.Permission.Granted)
        {
            // Grant ONLY the camera resource — anything else bundled into the same
            // request (microphone, DRM) stays ungranted by omission.
            request.Grant([PermissionRequest.ResourceVideoCapture]);
            return;
        }
        _inner.OnPermissionRequest(request);
    }

    public override void OnPermissionRequestCanceled(PermissionRequest? request)
        => _inner.OnPermissionRequestCanceled(request);

    // ── Pure forwarding below — MAUI's client owns these behaviours. ──

    public override bool OnShowFileChooser(AWebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
        => _inner.OnShowFileChooser(webView, filePathCallback, fileChooserParams);

    public override void OnProgressChanged(AWebView? view, int newProgress) => _inner.OnProgressChanged(view, newProgress);
    public override void OnReceivedTitle(AWebView? view, string? title) => _inner.OnReceivedTitle(view, title);
    public override void OnReceivedIcon(AWebView? view, Android.Graphics.Bitmap? icon) => _inner.OnReceivedIcon(view, icon);
    public override bool OnConsoleMessage(ConsoleMessage? consoleMessage) => _inner.OnConsoleMessage(consoleMessage);

    public override bool OnCreateWindow(AWebView? view, bool isDialog, bool isUserGesture, Android.OS.Message? resultMsg)
        => _inner.OnCreateWindow(view, isDialog, isUserGesture, resultMsg);
    public override void OnCloseWindow(AWebView? window) => _inner.OnCloseWindow(window);

    public override bool OnJsAlert(AWebView? view, string? url, string? message, JsResult? result)
        => _inner.OnJsAlert(view, url, message, result);
    public override bool OnJsConfirm(AWebView? view, string? url, string? message, JsResult? result)
        => _inner.OnJsConfirm(view, url, message, result);
    public override bool OnJsPrompt(AWebView? view, string? url, string? message, string? defaultValue, JsPromptResult? result)
        => _inner.OnJsPrompt(view, url, message, defaultValue, result);

    public override void OnGeolocationPermissionsShowPrompt(string? origin, GeolocationPermissions.ICallback? callback)
        => _inner.OnGeolocationPermissionsShowPrompt(origin, callback);
    public override void OnGeolocationPermissionsHidePrompt() => _inner.OnGeolocationPermissionsHidePrompt();

    public override void OnShowCustomView(Android.Views.View? view, ICustomViewCallback? callback)
        => _inner.OnShowCustomView(view, callback);
    public override void OnHideCustomView() => _inner.OnHideCustomView();
}

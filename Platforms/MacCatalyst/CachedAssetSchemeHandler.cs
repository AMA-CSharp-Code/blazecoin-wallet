using System.Globalization;
using Foundation;
using WebKit;

namespace BlazecoinWallet.Maui.Platforms.MacCatalyst;

/// <summary>
/// Serves files under the app bundle's wwwroot at <c>blz://assets/&lt;path&gt;</c> with a long
/// <c>Cache-Control: max-age</c>, so WebKit's memory cache can keep them between page visits.
/// MAUI's own <c>app://</c> scheme handler stamps every response with
/// <c>Cache-Control: no-cache, max-age=0, must-revalidate, no-store</c>, which makes WebKit
/// re-fetch and re-decode every background tile on every navigation (a few hundred WebP
/// decodes per page); on an Intel Mac that showed as images loading slowly and page changes
/// stalling (sampled 2026-09-24). The tile painters in mining-background.js prefix their
/// image URLs with <c>window.__assetBase</c>, which MainPage sets to this scheme on Catalyst.
/// </summary>
internal sealed class CachedAssetSchemeHandler : NSObject, IWKUrlSchemeHandler
{
    public const string Scheme = "blz";
    public const string Base = "blz://assets/";

    private readonly string _root;
    private static int _served;

    public CachedAssetSchemeHandler(string wwwroot)
    {
        _root = Path.GetFullPath(wwwroot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    [Export("webView:startURLSchemeTask:")]
    public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        var url = urlSchemeTask.Request.Url;
        var rel = Uri.UnescapeDataString((url?.Path ?? string.Empty).TrimStart('/'));

        string full = string.Empty;
        try { if (rel.Length > 0) full = Path.GetFullPath(Path.Combine(_root, rel)); }
        catch { /* malformed path -> 404 below */ }

        if (url is null || full.Length == 0 || !full.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(full))
        {
            Respond(urlSchemeTask, url, 404, Array.Empty<byte>(), "text/plain");
            return;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch
        {
            Respond(urlSchemeTask, url, 404, Array.Empty<byte>(), "text/plain");
            return;
        }

        if (Interlocked.Increment(ref _served) == 1)
            Console.WriteLine($"blz asset scheme: serving {_root} (first request {rel})");

        Respond(urlSchemeTask, url, 200, bytes, ContentType(full));
    }

    [Export("webView:stopURLSchemeTask:")]
    public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        // Responses are produced synchronously in StartUrlSchemeTask; nothing to cancel.
    }

    private static void Respond(IWKUrlSchemeTask task, NSUrl? url, int status, byte[] body, string contentType)
    {
        var headers = new NSMutableDictionary<NSString, NSString>
        {
            { (NSString)"Content-Type", (NSString)contentType },
            { (NSString)"Content-Length", (NSString)body.Length.ToString(CultureInfo.InvariantCulture) },
            { (NSString)"Cache-Control", (NSString)(status == 200 ? "public, max-age=31536000, immutable" : "no-store") },
        };
        using var response = new NSHttpUrlResponse(url ?? new NSUrl(Base), status, "HTTP/1.1", headers);
        task.DidReceiveResponse(response);
        if (body.Length > 0) task.DidReceiveData(NSData.FromArray(body));
        task.DidFinish();
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}

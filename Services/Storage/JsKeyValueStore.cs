using Microsoft.JSInterop;
using BlazecoinWallet.Core.Services.Storage;

namespace BlazecoinWallet.Maui.Services.Storage;

/// <summary><see cref="IKeyValueStore"/> backed by the WebView's localStorage
/// via JS interop. Every call swallows interop failures (returning null / no-op)
/// so callers get the same forgiving behaviour the Razor pages relied on when
/// they called localStorage directly — e.g. before the WebView is ready.</summary>
public sealed class JsKeyValueStore : IKeyValueStore
{
    private readonly IJSRuntime _js;

    public JsKeyValueStore(IJSRuntime js) => _js = js;

    public async Task<string?> GetAsync(string key)
    {
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch { return null; }
    }

    public async Task SetAsync(string key, string value)
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", key, value); }
        catch { /* best-effort */ }
    }

    public async Task RemoveAsync(string key)
    {
        try { await _js.InvokeVoidAsync("localStorage.removeItem", key); }
        catch { /* best-effort */ }
    }
}

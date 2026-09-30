namespace BlazecoinWallet.Core.Services.Storage;

/// <summary>Minimal async key/value persistence so services can save/restore
/// small bits of state without depending on the browser's localStorage (or any
/// concrete store) directly. The desktop app backs this with the WebView's
/// localStorage via JS interop (<see cref="JsKeyValueStore"/>); unit tests can
/// substitute an in-memory fake.
///
/// All methods are best-effort: implementations swallow transport failures
/// (e.g. the WebView not being ready yet), returning null / no-op. This matches
/// how the pages have always treated localStorage.</summary>
public interface IKeyValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

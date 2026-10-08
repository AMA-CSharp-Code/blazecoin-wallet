using BlazecoinWallet.Core.Services.Storage;

namespace BlazecoinWallet.Core.Tests;

/// <summary>In-memory <see cref="IKeyValueStore"/> for unit tests — stands in for
/// the WebView localStorage without any MAUI/JS dependency.</summary>
internal sealed class FakeKeyValueStore : IKeyValueStore
{
    public readonly Dictionary<string, string> Data = new();

    public Task<string?> GetAsync(string key)
        => Task.FromResult(Data.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value)
    {
        Data[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        Data.Remove(key);
        return Task.CompletedTask;
    }
}

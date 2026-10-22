namespace BlazecoinWallet.Lite;

/// <summary>
/// Camera QR scanning, supplied by the app head (the Droid head brings the device camera).
/// Always registered (null-object when no camera exists) so pages inject it normally
/// instead of service-locating (F4); the UI gates its scan button on <see cref="IsAvailable"/>.
/// </summary>
public interface IQrScanner
{
    /// <summary>False when this head has no scanner — the UI hides its scan affordance.</summary>
    bool IsAvailable { get; }

    /// <summary>Opens the scanner; resolves to the decoded text, or null when cancelled.</summary>
    Task<string?> ScanAsync();
}

/// <summary>The no-camera head's scanner: never available, never scans.</summary>
public sealed class NullQrScanner : IQrScanner
{
    public bool IsAvailable => false;
    public Task<string?> ScanAsync() => Task.FromResult<string?>(null);
}

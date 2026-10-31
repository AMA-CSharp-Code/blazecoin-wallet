namespace BlazecoinWallet.Core.Services.Mining;

/// <summary>Snapshot of the miner's state at a point in time. Returned by
/// GetStatus() and produced by the StatusChanged event.</summary>
public class MiningStatus
{
    public bool IsRunning { get; set; }
    public int ActiveThreads { get; set; }
    public int ConfiguredThreads { get; set; }
    public int ThrottlePercent { get; set; }
    public double HashesPerSecond { get; set; }
    public long TotalHashes { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int BlocksFound { get; set; }
    public int? CurrentBlockHeight { get; set; }
    public long? CurrentTemplateRefreshUnix { get; set; }
    public string? PayoutAddress { get; set; }
    /// <summary>Set when the payout address is not post-quantum, saying why (null for a BQ… payout).</summary>
    public string? PayoutAddressNote { get; set; }
    public string? LastError { get; set; }
    public int MaxAllowedThreads { get; set; }
}

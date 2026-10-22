namespace BlazecoinWallet.Core.Services.Mining;

public class MiningOptions
{
    /// <summary>Number of hashing worker threads. Hard-capped at
    /// Environment.ProcessorCount - 4 by the service to always leave cores
    /// for the OS, UI, and the WebView2 host.</summary>
    public int ThreadCount { get; set; }

    /// <summary>0..90. Percentage of each worker's loop spent sleeping. 0 =
    /// flat-out, 50 ≈ half throttle, 90 = mostly idle.</summary>
    public int ThrottlePercent { get; set; }

    /// <summary>Payout address (legacy P2PKH, starts with 'B'). If null, the
    /// service will call getnewaddress to mint one.</summary>
    public string? PayoutAddress { get; set; }
}

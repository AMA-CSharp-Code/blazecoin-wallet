namespace BlazecoinWallet.Core.Services.Mining;

/// <summary>
/// Subset of the getblocktemplate response that the solo miner consumes.
/// Field names line up case-insensitively with the lower-case JSON Bitcoin
/// Core returns (Version, PreviousBlockHash, etc. → "version", "previousblockhash", …).
/// </summary>
public class BlockTemplate
{
    public int Version { get; set; }
    public string? PreviousBlockHash { get; set; }
    public List<BlockTemplateTx>? Transactions { get; set; }
    public long CoinbaseValue { get; set; }
    public string? Target { get; set; }
    public long MinTime { get; set; }
    public long CurTime { get; set; }
    public string? Bits { get; set; }
    public int Height { get; set; }
    public string? NonceRange { get; set; }
}

public class BlockTemplateTx
{
    public string? Data { get; set; }
    public string? TxId { get; set; }
    public string? Hash { get; set; }
}

using System.Text.RegularExpressions;
using BlazecoinWallet.Lite.Gateway.Rpc;

namespace BlazecoinWallet.Lite.Gateway;

public sealed record BroadcastOutcome(string Status, string? TxId, string? Reason)
{
    public static BroadcastOutcome Accepted(string txId) => new("accepted", txId, null);
    public static BroadcastOutcome Invalid(string reason) => new("invalid", null, reason);
    public static BroadcastOutcome Rejected(string reason) => new("rejected", null, reason);
    public static BroadcastOutcome Unreachable() => new("unreachable", null, "The network node is temporarily unreachable — try again shortly.");
}

/// <summary>
/// The broadcast relay, behavior-mirrored from the full Indexer's TxBroadcastService (same
/// shape checks, same testmempoolaccept gate, same outcome classification) so a wallet
/// can't tell the two gateways apart. Never signs, never holds keys, surfaces only the
/// daemon's own reject reasons.
/// </summary>
public sealed partial class LiteBroadcastService
{
    // Smallest meaningful raw tx is ~60 bytes; a standard tx is capped at 100,000 bytes.
    private const int MinHexLength = 120;
    public const int DefaultMaxHexLength = 200_000;

    private readonly DaemonRpcClient _rpc;
    private readonly IConfiguration _config;
    private readonly ILogger<LiteBroadcastService> _logger;

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex HexPattern();

    public LiteBroadcastService(DaemonRpcClient rpc, IConfiguration config, ILogger<LiteBroadcastService> logger)
    {
        _rpc = rpc;
        _config = config;
        _logger = logger;
    }

    public async Task<BroadcastOutcome> BroadcastAsync(string? rawTxHex, CancellationToken ct = default)
    {
        var maxLength = _config.GetValue("Broadcast:MaxHexLength", DefaultMaxHexLength);

        if (string.IsNullOrWhiteSpace(rawTxHex))
            return BroadcastOutcome.Invalid("Raw transaction hex is required.");
        var hex = rawTxHex.Trim();
        if (hex.Length < MinHexLength)
            return BroadcastOutcome.Invalid("Raw transaction hex is too short to be a transaction.");
        if (hex.Length > maxLength)
            return BroadcastOutcome.Invalid($"Raw transaction exceeds the {maxLength / 2:N0}-byte limit.");
        if (hex.Length % 2 != 0 || !HexPattern().IsMatch(hex))
            return BroadcastOutcome.Invalid("Raw transaction must be an even-length hex string.");

        try
        {
            var (allowed, rejectReason) = await _rpc.TestMempoolAcceptAsync(hex, ct);
            if (!allowed)
            {
                _logger.LogInformation("Broadcast rejected by mempool policy: {Reason}", rejectReason);
                return BroadcastOutcome.Rejected(rejectReason ?? "Rejected by mempool policy.");
            }

            var txId = await _rpc.SendRawTransactionAsync(hex, ct);
            _logger.LogInformation("Broadcast accepted: {TxId}", txId);
            return BroadcastOutcome.Accepted(txId);
        }
        catch (DaemonUnreachableException ex)
        {
            _logger.LogWarning(ex, "Broadcast relay could not reach the daemon RPC");
            return BroadcastOutcome.Unreachable();
        }
        catch (DaemonRpcException ex)
        {
            // The daemon's message is a policy/consensus verdict — safe and useful to surface.
            _logger.LogInformation(ex, "Broadcast rejected by the daemon");
            return BroadcastOutcome.Rejected(ex.Message);
        }
    }
}

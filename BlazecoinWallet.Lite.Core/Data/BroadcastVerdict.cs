using System.Net;

namespace BlazecoinWallet.Lite.Data;

/// <summary>What ONE gateway said about a broadcast.</summary>
internal enum RelayVerdict
{
    /// <summary>No gateway could be reached (or there was no other gateway to ask).</summary>
    NoAnswer,
    /// <summary>It answered without judging the transaction: 503, 429, any other 5xx.</summary>
    NotJudged,
    Accepted,
    Duplicate,
    Rejected,
}

/// <summary>One gateway's answer to a broadcast.</summary>
internal sealed record RelayAttempt(RelayVerdict Verdict, string? Reason, string? RelayTxId, Uri? AnsweredBy)
{
    public static readonly RelayAttempt None = new(RelayVerdict.NoAnswer, null, null, null);

    /// <summary>
    /// What a gateway's reply amounts to. A success status is an acceptance. Only 400 (not a
    /// transaction) and 422 (the daemon refused it) JUDGE the transaction — a rejection, or a
    /// duplicate when the reason says the network already has it; a 429 or any 5xx judged nothing
    /// (and only a 503's reason is worth repeating to the user).
    /// </summary>
    public static RelayAttempt FromReply(HttpStatusCode status, string? relayTxId, string? reason, Uri? answeredBy)
    {
        if ((int)status is >= 200 and <= 299)
            return new RelayAttempt(RelayVerdict.Accepted, null, relayTxId, answeredBy);

        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity))
            return new RelayAttempt(RelayVerdict.NotJudged, status == HttpStatusCode.ServiceUnavailable ? reason : null, null, answeredBy);

        reason ??= $"The network rejected the transaction ({(int)status}).";
        return new RelayAttempt(
            BroadcastReasons.Classify(reason) == BroadcastFailureKind.Duplicate ? RelayVerdict.Duplicate : RelayVerdict.Rejected,
            reason, null, answeredBy);
    }
}

/// <summary>
/// What the NETWORK made of a broadcast — as far as that can be known through gateways the
/// wallet does not trust (bug-bounty reports 2026-11-06). The rule and its wording live here,
/// apart from the HTTP client that collects the two answers (audit 2026-11-09).
/// <list type="bullet">
/// <item>The txid returned is the wallet's own (<see cref="LiteTxId"/>), never the relay's string.</item>
/// <item>An acceptance stands on one honest gateway; it is BACKED when the other gateway also
/// took the transaction or already had it.</item>
/// <item>A rejection is the network's judgement only when two gateways agree; one gateway's
/// rejection is reported as exactly that.</item>
/// <item>Answers that judged nothing are an outage, so the peer-to-peer fallback still runs.</item>
/// </list>
/// With nobody else to ask the result says so through <see cref="LiteBroadcastResult.Uncorroborated"/>.
/// </summary>
internal static class BroadcastVerdict
{
    public const string ServiceUnreachable = "Could not reach the Blazecoin service — check your connection.";
    public const string AcceptedWithoutTxId = "The relay accepted the transaction but returned no txid.";
    public const string AlreadyKnown = "The network already has this transaction.";
    public const string OneGatewayOnly =
        " — reported by one gateway only; no second gateway could confirm it. " +
        "If this looks wrong, switch gateway in Settings and try again.";
    public const string NodeUnreachable = "The network node is temporarily unreachable — try again shortly.";

    /// <param name="first">The gateway in use.</param>
    /// <param name="second">A gateway on another server, or <see cref="RelayAttempt.None"/> when
    /// there was nobody else or nobody else answered.</param>
    /// <param name="ownTxId">The id the wallet computed from the bytes it sent (null when they were not a transaction).</param>
    public static LiteBroadcastResult Combine(RelayAttempt first, RelayAttempt second, string? ownTxId)
    {
        if (first.Verdict == RelayVerdict.NoAnswer)
            return LiteBroadcastResult.Fail(ServiceUnreachable, BroadcastFailureKind.Unreachable);

        var acceptances = Count(first, second, RelayVerdict.Accepted);
        var duplicates = Count(first, second, RelayVerdict.Duplicate);
        var rejections = Count(first, second, RelayVerdict.Rejected);

        if (acceptances > 0)
        {
            // Backed when the other gateway also took it or already had it.
            var backed = acceptances + duplicates == 2;
            var txId = ownTxId ?? first.RelayTxId ?? second.RelayTxId;
            return string.IsNullOrEmpty(txId)
                ? LiteBroadcastResult.Fail(AcceptedWithoutTxId, BroadcastFailureKind.Rejected, uncorroborated: !backed)
                : LiteBroadcastResult.Ok(txId, uncorroborated: !backed);
        }

        if (duplicates > 0)
        {
            var reason = (first.Verdict == RelayVerdict.Duplicate ? first : second).Reason ?? AlreadyKnown;
            return LiteBroadcastResult.Fail(reason, BroadcastFailureKind.Duplicate, uncorroborated: duplicates < 2);
        }

        if (rejections == 2)
            return LiteBroadcastResult.Fail(first.Reason!, BroadcastFailureKind.Rejected);

        if (rejections == 1)
        {
            var reason = (first.Verdict == RelayVerdict.Rejected ? first : second).Reason!;
            return LiteBroadcastResult.Fail(reason + OneGatewayOnly, BroadcastFailureKind.Rejected, uncorroborated: true);
        }

        // Nobody judged anything (outage answers only) — fallback-eligible.
        return LiteBroadcastResult.Fail(first.Reason ?? second.Reason ?? NodeUnreachable, BroadcastFailureKind.Unreachable);
    }

    /// <summary>True when a gateway answered with a transaction id that is not the id of the
    /// transaction it was sent — worth a log line, never used.</summary>
    public static bool NamesAnotherTransaction(RelayAttempt attempt, string? ownTxId) =>
        attempt.RelayTxId != null && ownTxId != null &&
        !string.Equals(attempt.RelayTxId, ownTxId, StringComparison.OrdinalIgnoreCase);

    private static int Count(RelayAttempt a, RelayAttempt b, RelayVerdict verdict)
        => (a.Verdict == verdict ? 1 : 0) + (b.Verdict == verdict ? 1 : 0);
}

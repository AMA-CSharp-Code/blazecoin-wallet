using Microsoft.Extensions.Logging;

namespace BlazecoinWallet.Lite.Data;

/// <summary>What became of a request that had to be answered by someone ELSE
/// (<see cref="SecondOpinionClient"/>). The response is set only for
/// <see cref="SecondGateway.Answered"/>; the caller reads it and disposes this.</summary>
internal readonly record struct SecondOpinion(SecondGateway Status, HttpResponseMessage? Response, Uri? AnsweredBy) : IDisposable
{
    /// <summary>True when another server answered with a success status — an answer worth reading.</summary>
    public bool IsSuccess => Status == SecondGateway.Answered && Response is { IsSuccessStatusCode: true };

    public void Dispose() => Response?.Dispose();
}

/// <summary>
/// THE way the wallet asks a second gateway (audit 2026-11-09). Every second opinion — a
/// broadcast, a header, a spend, its evidence, an address's use, the headers that place a
/// block — used to set <see cref="GatewayRouting.Exclude"/> and read the outcome for itself,
/// each with its own idea of "nobody else" and "nobody answered". They all come through here,
/// so the three outcomes mean one thing:
/// <list type="bullet">
/// <item><see cref="SecondGateway.Answered"/> — a SERVER other than the excluded ones
/// (<see cref="GatewaySite"/>) answered. Any HTTP status is an answer; the caller reads it.</item>
/// <item><see cref="SecondGateway.None"/> — there is nobody else: nobody was named to exclude (a
/// client with no failover list), every configured gateway is one of the excluded servers, or
/// the transport ignored the exclusion and an excluded server answered again.</item>
/// <item><see cref="SecondGateway.Unavailable"/> — someone else is configured and none of them
/// delivered an answer (unreachable, timed out).</item>
/// </list>
/// The caller's own cancellation is never an outcome: it is thrown.
/// </summary>
internal sealed class SecondOpinionClient
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public SecondOpinionClient(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <param name="exclude">The gateways whose word is being checked; no name of their servers is asked.</param>
    /// <param name="question">What was asked, for the log line when nobody else answers ("the header question").</param>
    public async Task<SecondOpinion> SendAsync(HttpRequestMessage request, IReadOnlyCollection<Uri>? exclude,
        string question, CancellationToken ct = default)
    {
        if (exclude is not { Count: > 0 }) return new(SecondGateway.None, null, null);
        HttpResponseMessage? response = null;
        try
        {
            request.Options.Set(GatewayRouting.Exclude, exclude);
            response = await _http.SendAsync(request, ct);
            request.Options.TryGetValue(GatewayRouting.AnsweredBy, out var answeredBy);
            // Nobody named, or an excluded server under any of its names: the exclusion was not
            // honoured, and its own word again is nobody's second opinion.
            if (answeredBy == null || GatewaySite.SameAsAny(answeredBy, exclude))
            {
                response.Dispose();
                return new(SecondGateway.None, null, null);
            }
            return new(SecondGateway.Answered, response, answeredBy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { response?.Dispose(); throw; }
        catch (NoOtherGatewayException) { return new(SecondGateway.None, null, null); }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger.LogWarning(ex, "No second gateway answered {Question}", question);
            return new(SecondGateway.Unavailable, null, null);
        }
    }
}

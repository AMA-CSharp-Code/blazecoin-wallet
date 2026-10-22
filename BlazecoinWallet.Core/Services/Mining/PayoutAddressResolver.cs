using BlazecoinWallet.Core.Services.Quantum;

namespace BlazecoinWallet.Core.Services.Mining;

/// <summary>The address a solo-mined coinbase pays to, and why when it is not post-quantum.</summary>
public sealed record PayoutAddressChoice(string Address, BlazecoinAddressKind Kind, string? Note);

/// <summary>
/// Picks the solo miner's payout address. A configured address is used as given (legacy or BQ…);
/// otherwise a fresh BQ… address from the wallet — coinbase outputs to P2PQH are valid, and the pool
/// has paid every coinbase to BQ… since block 4,250,880 — falling back to a fresh legacy address only
/// when the wallet cannot mint BQ…, with a note saying so. A miner must not stop over this, which is
/// why this fallback exists here and nowhere that moves existing coins.
/// </summary>
public sealed class PayoutAddressResolver
{
    public const string Label = "mining payout";

    private readonly IMiningRpc _rpc;
    private readonly IAddressMintRpc? _mint;

    public PayoutAddressResolver(IMiningRpc rpc, IAddressMintRpc? mint = null)
    {
        _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _mint = mint;
    }

    public async Task<PayoutAddressChoice> ResolveAsync(string? configured, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var kind = BitcoinProtocol.KindOf(configured)
                ?? throw new InvalidOperationException($"Invalid payout address \"{configured}\": not a legacy B… or post-quantum BQ… address.");
            return new PayoutAddressChoice(configured.Trim(), kind,
                kind == BlazecoinAddressKind.Legacy ? "The configured payout address is legacy (not post-quantum)." : null);
        }

        string? note = null;
        if (_mint is not null)
        {
            PqAddressResult r;
            try { r = await _mint.GetNewPostQuantumAddressAsync(Label, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { r = PqAddressResult.Fail(PqAddressOutcome.Failed, ex.Message); }
            if (r.IsOk && BitcoinProtocol.KindOf(r.Address) == BlazecoinAddressKind.PostQuantum)
                return new PayoutAddressChoice(r.Address!, BlazecoinAddressKind.PostQuantum, null);
            note = "Paying to a legacy address: " + (r.IsOk ? "the node returned a non-BQ address." : r.Explain());
        }

        var legacy = await _rpc.GetNewAddressAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(legacy))
            throw new InvalidOperationException("getnewaddress returned no address");
        if (BitcoinProtocol.KindOf(legacy) != BlazecoinAddressKind.Legacy)
            throw new InvalidOperationException($"Invalid payout address \"{legacy}\": the node returned something that is not a legacy address.");
        return new PayoutAddressChoice(legacy.Trim(), BlazecoinAddressKind.Legacy, note);
    }
}

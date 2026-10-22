# BlazecoinWallet.Lite.Core

The Blazecoin lite-wallet core as a package, for services that need the chain without a wallet UI:
address and xpub derivation (legacy `B…` P2PKH and post-quantum `BQ…` P2PQH), client-side transaction
building and signing (ECDSA and ML-DSA-44), trustless UTXO and merkle-proof verification against a
self-verified header chain, and the data client for the public lite gateways (`gw` / `gw2.blazecoin.co.uk`).

Platform-free by rule: nothing in here touches a device keystore or a UI framework.

## Version = wallet version

The package version equals the desktop/lite wallet release it was packed from (2.0.8 → 2.0.8). Chain rules
live in this library, so **a consumer pinned across a consensus change verifies with old rules** — every
fork rollout bumps the package in every consumer ahead of the activation height.

## Entry points

- `BlazecoinAddress`, `WatchXpub`, `LiteHdWallet` — addresses, watch-only derivation, HD keys
- `IChainReader` / `ITxRelay` / `IHeaderReader` — the data surface; `IndexerDataService` implements them
  over the gateway, `GatewayFailoverHandler` gives client-side failover across gateways
- `HeaderChainSync`, `LiteTxVerifier` — the C1/M3 trustless verification pattern
- `LiteWalletServiceCollectionExtensions.AddLiteWallet` — the wallet-shaped composition root; services
  that only read the chain wire the pieces above directly

Feed: the AMA-CSharp-Code private GitHub Packages source. Source: `BlazecoinWallet.Maui`, project
`BlazecoinWallet.Lite.Core`. Depends on `BlazecoinWallet.Core` at the same version.

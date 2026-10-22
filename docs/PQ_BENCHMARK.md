# PQ signature benchmark — measured numbers for `PQ_SIGNATURES.md`

> Post-quantum plan item 4 (MAUI `ROADMAP.md` §G), built 2026-09-14. **Library only** —
> `BlazecoinWallet.PqBench` + `BlazecoinWallet.PqBench.Tests` (22 pins) + `BlazecoinWallet.PqBench.Runner`.
> No address type, no wallet integration: by decision nothing spendable exists before the
> `PQ_SIGNATURES.md` fork (V2 repo). The numbers below are what that spec's §2 and §4 cite.

## What it measures

Key and signature sizes (as **measured** from the library, cross-checked against the FIPS 204/205
constants by the tests) and keygen / sign / verify medians for:

- the wallet's **current path** — secp256k1 ECDSA through NBitcoin, exactly as the lite wallet
  signs (DER low-S + sighash byte, compressed pubkey);
- **ML-DSA-44 / 65 / 87** (FIPS 204) through BouncyCastle 2.6.2 — already a `BlazecoinWallet.Core`
  dependency — using the seed-deterministic `FromSeed` key generation and the deterministic signing
  variant, with the spec's `ctx = "blazecoin-tx-v1"` (the tests prove a wrong `ctx` fails);
- **SLH-DSA-SHA2-128s / 128f** (FIPS 205), the hash-based fallback family;
- **ML-DSA-44 through the .NET 10 BCL** (`System.Security.Cryptography.MLDsa`, experimental
  SYSLIB5006) — reported as *not supported* where the OS has no PQC provider.

Plus the spec's **transaction-size model** (`InputSizeEstimate`): one input's bytes under the P2PQH
scriptSig layout (`push(sig) push(algo_id ‖ pubkey)`), inputs per 1 MB block, per 100 KB standard
transaction, per day — computed from the pushdata rules, not typed.

## How to run

```
dotnet test BlazecoinWallet.PqBench.Tests
dotnet run --project BlazecoinWallet.PqBench.Runner -c Release          # full profile, prints Markdown
dotnet run --project BlazecoinWallet.PqBench.Runner -c Release -- quick # smoke
```

Release matters (Debug timings are meaningless). The runner does a full throw-away pass first: with a
short warm-up the first BouncyCastle scheme measured pays the tiered-JIT cost of the shared lattice
code and reads slower than the larger parameter sets — that artefact is what the first run of the
day showed (ML-DSA-44 "slower" than ML-DSA-65) and what the 30-round warm-up + discarded pass remove.

## Results — 2026-09-14, dev box

Measured 2026-09-14 10:24 UTC on Microsoft Windows 10.0.22631 / X64 (AMD EPYC 9654, 2 × 96 cores),
.NET 10.0.11; **single thread**, Stopwatch, 30 warm-up rounds excluded. Message = a 32-byte digest;
FIPS schemes signed with ctx `blazecoin-tx-v1`. BouncyCastle is pure managed code (no AVX2 path).

| Scheme | Provider | Cat. | Public key | Signature | KeyGen median | Sign median | Verify median | Verify p90 |
|---|---|---|---|---|---|---|---|---|
| ECDSA secp256k1 | NBitcoin 10.0.7 | — | 33 B | 71 B | 254 µs | 783 µs | 254 µs | 381 µs |
| ML-DSA-44 | BouncyCastle 2.6.2 | 2 | 1,312 B | 2,420 B | 194 µs | 537 µs | 132 µs | 135 µs |
| ML-DSA-65 | BouncyCastle 2.6.2 | 3 | 1,952 B | 3,309 B | 236 µs | 498 µs | 192 µs | 193 µs |
| ML-DSA-87 | BouncyCastle 2.6.2 | 5 | 2,592 B | 4,627 B | 328 µs | 869 µs | 303 µs | 307 µs |
| SLH-DSA-SHA2-128s | BouncyCastle 2.6.2 | 1 | 32 B | 7,856 B | 96.0 ms | 835.3 ms | 728 µs | 741 µs |
| SLH-DSA-SHA2-128f | BouncyCastle 2.6.2 | 1 | 32 B | 17,088 B | 1.52 ms | 37.1 ms | 2.20 ms | 2.23 ms |
| ML-DSA-44 | .NET 10 BCL (OS-backed) | 2 | 1,312 B | 2,420 B | — | — | — | *not supported on this platform* |

| Scheme | scriptPubKey | scriptSig | Input | Inputs / 1 MB block | Inputs / standard tx (100 KB) | Inputs / day (2,880 blocks) | Verify time for a full block |
|---|---|---|---|---|---|---|---|
| ECDSA secp256k1 | 25 B | 107 B | **148 B** | 6,755 | 675 | 19,454,400 | 1.7 s |
| ML-DSA-44 | 34 B | 3,739 B | **3,782 B** | 264 | 26 | 760,320 | 35 ms |
| ML-DSA-65 | 34 B | 5,268 B | **5,311 B** | 188 | 18 | 541,440 | 36 ms |
| ML-DSA-87 | 34 B | 7,226 B | **7,269 B** | 137 | 13 | 394,560 | 42 ms |
| SLH-DSA-SHA2-128s | 34 B | 7,893 B | **7,936 B** | 125 | 12 | 360,000 | 91 ms |
| SLH-DSA-SHA2-128f | 34 B | 17,125 B | **17,168 B** | 58 | 5 | 167,040 | 127 ms |

## What the numbers say (for the spec)

1. **ML-DSA-44 is the right first algorithm.** Verify is *faster* than the wallet's current managed
   ECDSA verify (132 µs vs 254 µs) and ~2× faster than ML-DSA-87; signing is 537 µs — a 26-input
   standard transaction signs in ~14 ms. Size, not CPU, is the constraint, exactly as the spec argues.
2. **A fully post-quantum block is cheap to validate:** 264 ML-DSA-44 inputs verify in ~35 ms of
   single-thread managed time; the daemon's C reference implementation will be faster still. The
   spec's provisional sigop weight of 50 per `OP_CHECKPQSIG` is therefore very conservative — 1,600
   verifications per block would cost ~0.2 s; the byte limit binds long before verification time does.
3. **SLH-DSA is a fallback, not a candidate:** 835 ms per signature for the compact 128s set (a 12-input
   transaction takes 10 s to sign) and 7.9 KB per input; the fast 128f set signs in 37 ms but costs
   17 KB per input (58 inputs per block). Reserved ID only, as the spec says.
4. **The ML-DSA-87 size figure in the spec was 7,272; the exact value is 7,269** (36 + 3-byte varint +
   (3 + 4,627 + 3 + 2,593) + 4). 137 inputs per block stands.
5. **Platform finding for the lite-wallet matrix:** the .NET 10 BCL `MLDsa` type is *not supported* on
   this Windows 11 23H2 box (CNG PQC needs 24H2 / Server 2025; Linux needs OpenSSL 3.5). BouncyCastle
   works everywhere the wallet already runs (Windows, Android, WASM), so it is the implementation of
   record for the C# twin; the BCL is an optional accelerator where present.
6. **The managed ECDSA baseline is slow** (783 µs sign / 254 µs verify — NBitcoin's pure-managed
   secp256k1, no native `libsecp256k1`). Not a PQ finding, but worth knowing: the lite wallet's
   signing path would be ~10× faster on native secp256k1, and a PQ input costs it *less* CPU than a
   legacy input does today.

## Caveats

Single-threaded medians on one machine with one library version; two significant figures. Managed
BouncyCastle, no SIMD: the daemon-side C implementation (spec §10.2) will change the absolute
figures, not the ranking. Sizes are exact.

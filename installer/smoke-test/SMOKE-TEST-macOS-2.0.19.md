# macOS 2.0.19 — smoke test (2026-11-07)

Built on the M1 Pro from wallet `main` 7aab5c6 (Windows 2.0.19: bug-bounty rounds six, seven and nine — vintage sends refuse blz1 addresses, PQ dust floor 11,475 sat, Auto Payout dry run no longer counts) and Core `blazecoin-v2` 027998f49d (**2.1.2**: no witness while SegWit inactive, PQ dust by real input, key-cache validation, timelock warning). Full build incl. the universal node (first daemon rebuild since 2.1.1 on 2026-10-09; depends cache reused; new cache `~/blz-mac-build-2.0.19/daemon`). sha256 59e57c7abd1faf6b09a655d6a68ee3f7dbeca5366ee8a1b0b9127244193c1de8 (133325071 bytes).

- Core tests: 617/617 on macOS, no Mac changes needed; no platform assumptions in the new desktop code; no new RPC names.
- Bundle: 2.0.19 / build 20; wallet, blazecoind, blazecoin-cli all x86_64 + arm64; blazecoind -version v2.1.2 (minos 11.0); codesign --verify --deep OK; entitlements applied; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme.
- M1: wallet open with no node before; 2.0.18 replaced, wallet started the 2.1.2 node (version 20102, /Blazecoin:2.1.2/) on the existing data, no crash reports, Primary loaded, txindex synced, asset_probe 735/735 errors 0; at tip within a minute (4,402,282 → 4,402,328).
- Intel (macOS 26.7.1, logged in): nothing running before; installed over SSH, launched cleanly, 2.1.2 node at tip 4,402,336, no crash reports, Radeon active, Primary loaded, txindex synced, asset_probe 735/735 errors 0.
- NOT exercised by me: the fixed paths (a vintage send to a blz1 address being refused, a BQ change below 11,475 sat, an Auto Payout dry run). Publish authorised by Andrew ("same build out again").
- 2026-11-07 00:53 UTC: v2.0.19-macos PUBLISHED, marked Latest; public download re-hashed and matches.

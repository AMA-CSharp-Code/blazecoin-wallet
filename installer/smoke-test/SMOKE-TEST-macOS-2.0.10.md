# macOS 2.0.10 — smoke test (2026-09-30)

Built on the M1 Pro (macOS 27, Xcode 26.4) from private `main` 5d99a86 (Windows 2.0.10 incl. both re-issues: mid-output vintage send fix, two-model Provenance tables). No Mac-specific code changes were needed. Cached universal Core 2.1.0 node reused (no src/depends commits in the Core repo since the 2026-09-22 daemon build). sha256 d076e865c4a1da4c9c2f170e1fa8fb79ae2cf9f31a551d007b4e138264d46051 (132744460 bytes).

- Core tests: 444/444 on macOS, first run, no changes.
- Bundle: CFBundleShortVersionString 2.0.10 / CFBundleVersion 11; wallet and blazecoind both x86_64 + arm64; entitlements applied; codesign --verify --deep OK; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme; NSSupportsAutomaticGraphicsSwitching false.
- M1: wallet and node were not running; installed over 2.0.9, wallet started the node itself, no crash reports, Primary loaded, txindex synced, asset_probe blz://assets/ 735/735 errors 0. Node catching up (4,286,282 of 4,290,280 headers at check time).
- Intel (2019 16" MBP, macOS 26.5.1): wallet and node were not running; installed over SSH, wallet started the node, no crash reports, display on the Radeon Pro 5500M, at tip 4,290,746, Primary loaded, asset_probe 735/735 errors 0.
- Node side, read-only: a JSON-RPC batch request (the form the new tracer uses) returns all replies with no errors on the M1's node.
- NOT exercised by me (needs Andrew's clicks): a Provenance trace (progress counter, Cancel, two-model tables) and a vintage send. No coins moved, no addresses minted.
- 2026-09-30 09:37 UTC: v2.0.10-macos PUBLISHED on AMA-CSharp-Code/blazecoin-wallet on Andrew's go, marked Latest; public download re-hashed and matches.

# macOS 2.0.18 — smoke test (2026-11-06, second Mac release that day)

Built on the M1 Pro from wallet `main` cf8ca87 (Windows 2.0.18: bug-bounty rounds three to five — PQ change on Provenance vintage sends and Auto Payout, solo miner payout address follows the active wallet via MinerPayoutMemory) with the cached universal **Core 2.1.1** node (`~/blz-mac-build-2.0.11/daemon`; the one new Core commit is a document). sha256 b0da0d14c7866daf2956e36fed3f1031472a960bc738554e3538378ed8241b24 (133314336 bytes).

- Core tests: 613/613 on macOS, no Mac changes needed. No platform assumptions in the new desktop code or BountyRound4DesktopTests.
- Bundle: 2.0.18 / build 19; wallet + node x86_64 + arm64; blazecoind v2.1.1; codesign --verify --deep OK; entitlements applied; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme.
- M1: wallet/node not running before (machine had been off); installed over 2.0.17, wallet started the node, no crash reports, Primary loaded, txindex synced, asset_probe 735/735 errors 0; catching up 4,401,225 → 4,401,881.
- Intel (macOS 26.7.1, logged in): wallet/node not running before; installed over SSH, launched cleanly first time, no crash reports, Radeon active, node 2.1.1 at tip 4,401,900, Primary loaded, txindex synced, asset_probe 735/735 errors 0.
- NOT exercised by me: the fixed paths (a vintage send's change address, an Auto Payout, the miner's payout address across a wallet switch) — they send or mine. Publish authorised in advance by Andrew ("push the release").
- 2026-11-06 20:59 UTC: v2.0.18-macos PUBLISHED, marked Latest; public download re-hashed and matches.

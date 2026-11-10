# macOS 2.0.20 — smoke test (2026-11-10)

Built on the M1 Pro from wallet `main` e9c3e8d (Windows 2.0.20: bug-bounty round thirteen + the 2026-11-09 audit — no write on a switched-to wallet until acknowledged, typed amounts, one payable-address rule, fee estimate on the pinned wallet) and Core `blazecoin-v2` 985fae2e4f (**2.1.3**: p2sh-segwit refused, one output rule for every send, legacy change while SegWit inactive, fee_estimates.dat read back). Full build incl. the universal node (new cache `~/blz-mac-build-2.0.20/daemon`). sha256 ad05e170183f9f7539c1c7c67e31c1f8aed671610db208bb5e1281cabe158009 (133369709 bytes).

- Core tests: 755/755 on macOS, no Mac changes needed; DaemonProcessManager change is a method rename only; no platform assumptions in the new desktop code.
- Bundle: 2.0.20 / build 21; wallet, blazecoind, blazecoin-cli all x86_64 + arm64; blazecoind -version v2.1.3 (minos 11.0); codesign --verify --deep OK; entitlements applied; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme.
- M1: wallet open with no node before; 2.0.19 replaced, wallet started the 2.1.3 node (version 20103, /Blazecoin:2.1.3/) on the existing data, no crash reports, Primary loaded, txindex synced, asset_probe 735/735 errors 0; catching up 4,402,406 → 4,406,396.
- Intel (macOS 26.7.1, logged in): nothing running before; installed over SSH, launched cleanly, 2.1.3 node at tip 4,411,963, no crash reports, Radeon active, Primary loaded, txindex synced, asset_probe 735/735 errors 0.
- NOT exercised by me: the fixed paths (writes after a wallet switch, comma amounts, address refusal at Send/sweep). Publish authorised by Andrew ("push the release").
- 2026-11-10 07:36 UTC: v2.0.20-macos PUBLISHED, marked Latest; public download re-hashed and matches.

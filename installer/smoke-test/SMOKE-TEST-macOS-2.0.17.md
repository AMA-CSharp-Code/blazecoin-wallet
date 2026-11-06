# macOS 2.0.17 — smoke test (2026-11-06)

Built on the M1 Pro from wallet `main` 2f9675d (Windows 2.0.17: bug-bounty round — sends pinned to the reviewed wallet, RPC -18 retried for reads only, auto-payout stops on wallet change, WalletSwitchBanner, payment-link comma amount, Dashboard concurrent-read recovery) with the cached universal **Core 2.1.1** node (`~/blz-mac-build-2.0.11/daemon`; Core repo has NO new commits). sha256 7f7cbe1b3189244650aad325f588dc62c7f4cc6cacda1af539995ae49de478b9 (133311351 bytes).

- Core tests: 604/604 on macOS, no Mac changes needed. AmountText parses with CultureInfo.InvariantCulture (no locale dependence on a Mac set to a comma-decimal region).
- Bundle: 2.0.17 / build 18; wallet + node x86_64 + arm64; blazecoind v2.1.1; codesign --verify --deep OK; entitlements applied; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme.
- M1: wallet/node not running before; installed over 2.0.16, wallet started the node, no crash reports, Primary loaded, txindex synced, asset_probe 735/735 errors 0; catching up 4,385,276 → 4,389,267.
- Intel (macOS 26.7.1, logged in): installed over SSH, launched cleanly first time, no crash reports, Radeon active, node 2.1.1, Primary loaded (V1.5_Import not auto-loaded, as always after a node restart), txindex synced, asset_probe 735/735 errors 0; catching up 4,391,250 → 4,400,680.
- NOT exercised by me: the fixed paths themselves (unloading the active wallet mid-send, auto-payout with a wallet switch, the switch notice, a comma-amount payment link) — they involve sends. Publish authorised in advance by Andrew ("It will need releasing on github").
- 2026-11-06 10:54 UTC: v2.0.17-macos PUBLISHED, marked Latest; public download re-hashed and matches.

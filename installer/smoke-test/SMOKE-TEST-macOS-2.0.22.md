# macOS 2.0.22 — smoke test (2026-11-12)

Built on the M1 Pro from wallet `main` 956af99 (2.0.22 / build 23: Consolidate Coins card on the Send page, Console argument parsing via RpcCommandLine, CSV formula-injection fix from bug-bounty round fourteen, two pinned fork-announcement keys) with the cached universal **Core 2.1.3** node (`~/blz-mac-build-2.0.20/daemon`; Core's only `src/` change since 985fae2e4f is `src/bench/pq_verify.cpp`, not part of blazecoind/blazecoin-cli; version still 2.1.3). Release notes were pre-written on the Windows side (commit 956af99); size and sha256 filled in here. sha256 1c565d15925e7bf11432bc4a46e3dad13d283cab8296b6da4133fe2e6ba6137d (133463474 bytes).

- Core tests: 819/819 on macOS, no Mac changes needed (TransactionExportServiceTests uses Environment.NewLine and passes with \n).
- Bundle: 2.0.22 / build 23; wallet + node x86_64 + arm64; blazecoind v2.1.3; codesign --verify --deep OK; entitlements applied; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme.
- M1: nothing running before; installed over 2.0.21, wallet started the node, no crash reports, Primary loaded, txindex synced, asset_probe 735/735 errors 0; catching up 4,411,998 → 4,415,988.
- Intel (macOS 26.7.1, logged in): nothing running before; installed over SSH, launched cleanly, node 2.1.3, no crash reports, Radeon active, Primary loaded, txindex synced, asset_probe 735/735 errors 0; catching up 4,420,157 → 4,420,939.
- At publish time no `v2.0.22-windows` release existed yet (latest Windows: 2.0.21); the drafted notes say Windows 2.0.22 carries the same changes.
- NOT exercised by me: the Consolidate Coins card (it broadcasts), Console quoting, a CSV export. Publish authorised by Andrew ("push to github").
- 2026-11-12 19:29 UTC: v2.0.22-macos PUBLISHED, marked Latest; public download re-hashed and matches.

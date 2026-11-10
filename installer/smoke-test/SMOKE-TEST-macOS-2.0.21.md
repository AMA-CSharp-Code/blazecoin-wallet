# macOS 2.0.21 — smoke test (2026-11-10)

Mac-led release: Andrew saw a flashing "Primary ›" prompt on the Console page of 2.0.20 (the wallet name had been put inside the yellow blinking prompt span). Fix in shared page code (`RpcConsole.razor`: bare `›`), version bump to 2.0.21 / build 22 / installer 2.0.21, commit 3cb605f on `main`. Built with the cached Core 2.1.3 node (`~/blz-mac-build-2.0.20/daemon`). sha256 fecb178a40e7ddf3baabddaf4dcd69f8a723e4b86f60d709bef4755167560ba9 (133368822 bytes).

- Core tests: 755/755 on macOS.
- Bundle: 2.0.21 / build 22; wallet + node x86_64 + arm64; blazecoind v2.1.3; codesign --verify --deep OK; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme.
- M1 and Intel: both wallets had been shut down gracefully by Andrew; installed, launched cleanly, nodes 2.1.3 at tip, no crash reports, Primary loaded, txindex synced, asset_probe 735/735 errors 0 on both; Intel on the Radeon.
- Andrew confirmed on the Mac: "fix worked" (bare arrow). Windows 2.0.20 remains current there; the fix reaches Windows with its next build from main.
- 2026-11-10 07:55 UTC: v2.0.21-macos PUBLISHED on Andrew's go, marked Latest; public download re-hashed and matches.

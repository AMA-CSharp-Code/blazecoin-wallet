# macOS 2.0.9 — smoke test (2026-09-28)

Built on the M1 Pro (macOS 27, Xcode 26.4) from private `main` (4bff567 Windows 2.0.9 + 067f7e6 Mac fixes), cached universal Core 2.1.0 node (daemon source unchanged since 2026-09-22). sha256 907b7ba442a6b5729343c3b547376e7a7d526960fd927539168f64ecec7c4d09 (132523776 bytes).

- Core tests: 435/435 on macOS after 067f7e6. Before it, 23 PostQuantumWalletCreatorTests failed on macOS only: hard-coded C:\ paths are relative to Path.IsPathFullyQualified on Unix. Product fix in the same commit: UserFolders.Documents() (explicit ~/Documents) for the Create post-quantum wallet backup default and the Backup page, platform-form example path in the validation message.
- Bundle: CFBundleShortVersionString 2.0.9 / CFBundleVersion 10, x86_64 + arm64, entitlements applied, .xamarin/{maccatalyst-arm64,maccatalyst-x64}/runtimeconfig.bin, blz:// asset scheme, NSSupportsAutomaticGraphicsSwitching false.
- M1: installed over 2.0.8, wallet started the stopped node itself, no crash reports, Primary loaded with 2 active pqkh descriptors (BQ-capable), asset_probe base blz://assets/ 735/735 errors 0. Node catching up from 4,274,156.
- Intel (2019 16" MBP, macOS 26.5.1): installed over SSH, node adopted, no crash reports, display on the Radeon Pro 5500M, Primary loaded, asset_probe 735/735 errors 0, blocks 4,286,259.
- Node side verified read-only: `help getnewaddress` accepts "pq" on both.
- NOT exercised by me (needs Andrew's clicks): Receive page BQ address, Quantum page Move to BQ / Create post-quantum wallet, Send change review. No coins moved, no addresses minted, no wallets created in testing.
- 2026-09-28: Andrew exercised the new post-quantum features on BOTH Macs (M1 + Intel) and reports they work.
- 2026-09-28 20:54 UTC: v2.0.9-macos PUBLISHED on AMA-CSharp-Code/blazecoin-wallet, marked Latest; public download re-hashed and matches.

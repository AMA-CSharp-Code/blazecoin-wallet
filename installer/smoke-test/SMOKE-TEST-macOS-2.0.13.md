# macOS 2.0.13 — smoke test (2026-10-22)

Built on the M1 Pro from wallet `main` 4a6a333 (Windows 2.0.13: Sign Message owning-wallet fix in Core's MessageSigner; 2.0.12 was the Windows re-bundle with Core 2.1.1 the Mac already shipped) with the cached universal **Core 2.1.1** node (`~/blz-mac-build-2.0.11/daemon`; Core `src/` unchanged since 07321df144, new Core commits are docs/scripts). sha256 8b25d98f5e079ca1065c48481887f97f56fae76efa4341bbd4ca36cdcbdb2559 (133044191 bytes).

- Core tests: 464/464 on macOS, no Mac changes needed. MessageSigner uses only signmessage/verifymessage/walletpassphrase/walletlock, all present on 2.1.1.
- Bundle: 2.0.13 / build 14; wallet + node x86_64 + arm64; blazecoind -version v2.1.1; codesign --verify --deep OK; entitlements applied; .xamarin/{maccatalyst-arm64,maccatalyst-x64}; blz:// asset scheme.
- M1: wallet/node were not running; installed over 2.0.11, wallet started the node (subversion /Blazecoin:2.1.1/), no crash reports, Primary loaded, txindex synced, asset_probe 735/735 errors 0; catching up 4,319,797 → 4,323,785.
- Intel (macOS 26.7.1, logged in): installed over SSH, launched first time (no stall this time), no crash reports, Radeon active, node 2.1.1, Primary loaded, txindex synced, asset_probe 735/735 errors 0; catching up 4,321,515 → 4,351,509.
- NOT exercised by me: Sign Message with a passphrase and an address change (the fixed path). Publish authorised in advance by Andrew ("Lets get it up onto git hub once completed").
- 2026-10-22 21:35 UTC: v2.0.13-macos PUBLISHED, marked Latest; public download re-hashed and matches.

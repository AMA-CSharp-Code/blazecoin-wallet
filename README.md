# Blazecoin Wallet V2

Wallets for **Blazecoin (BLZ)**, the 2014 scrypt coin revived on a Bitcoin Core 28 node in 2026.
One repository, two wallet families:

- **Desktop wallet** (`BlazecoinWallet.Maui`) — a .NET 10 MAUI Blazor Hybrid app for Windows that runs
  its own full node: it bundles `blazecoind`, starts it beside the app, and talks to it over local RPC
  with cookie authentication. Mining, sending, receiving, backups, the Phoenix-413 retarget verifier,
  a Provenance (vintage) view and an Explorer link-out. Ships as an Inno Setup installer.
- **Lite wallet** (`BlazecoinWallet.Lite.*`, `BlazecoinWallet.App.Droid`, `BlazecoinWallet.App.iOS`,
  `BlazecoinWallet.Web.Wasm`) — a thin client with no local node: keys stay on the device, the
  gateway only relays. Android APK, iOS scaffold and a browser (WebAssembly) build sharing one Blazor
  codebase. Headers are verified on-device (scrypt proof-of-work and difficulty), so confirmations are
  not taken on a gateway's word.

Website: https://blazecoin.co.uk · Web wallet: https://gw.blazecoin.co.uk · Node: see the
Blazecoin Core V2 repository.

## Downloads

The desktop installer and the Android APK are on the **Releases** page. Each release lists SHA-256
checksums — verify before you install. The installer is unsigned; Windows SmartScreen will ask you to
confirm.

## Building the desktop wallet

Requirements: .NET 10 SDK with the MAUI workload, Windows 10 19041+ target, and a built
`blazecoind.exe` / `blazecoin-cli.exe` from Blazecoin Core V2.

```powershell
dotnet publish -f net10.0-windows10.0.19041.0 -c Release -p:PublishProfile=win-x64
.\installer\Build-Installer.ps1 -DaemonDir <folder with blazecoind.exe>
```

The publish is self-contained (~350 MB); the target machine needs only the WebView2 runtime, which the
installer bootstraps if absent.

## Building the lite wallet

```powershell
dotnet build BlazecoinWallet.Mobile.sln
dotnet test BlazecoinWallet.Lite.Core.Tests
```

The Android head builds with the standard MAUI Android workload; the web head publishes as a static
WebAssembly bundle behind any HTTPS host.

## Tests

`BlazecoinWallet.Core.Tests`, `BlazecoinWallet.Lite.Core.Tests`, `BlazecoinWallet.Lite.UI.Tests` and
`BlazecoinWallet.Lite.Gateway.Tests` — including the Phoenix-413 vector set shared with the node.

## Licence and disclaimer

MIT. Blazecoin is a mined, traded digital currency with no issuer and no promise of value; nothing here
is financial advice. Keep your seed and wallet backups — no one can recover coins for you.

# Blazecoin Wallet 2.0.8 (macOS)

**What this is:** the first macOS build of the V2 desktop wallet — the same wallet as the Windows release,
as a **universal** app (Apple Silicon and Intel), **bundling Blazecoin Core v2.1.0**, the post-quantum
fork node. It runs a full node on your Mac; the wallet talks to it locally, nothing leaves your machine
except normal peer-to-peer traffic.

> **Re-issued 2026-09-24.** The zip on this tag was replaced during the day as the Intel build was brought
> up to the Apple Silicon one (details under *What changed*). If you downloaded earlier, download again
> and check the hash below; the Apple Silicon build only gained the smaller fixes.

### Download & verify

- **`BlazecoinWalletV2-2.0.8-macOS-universal.zip`** — the wallet application (126 MB)

Verify the download is intact:

```
shasum -a 256 BlazecoinWalletV2-2.0.8-macOS-universal.zip
```

Expected:

```
51c57a6c852311cb6d06245c133278a1bed939465041c45d0691a4d6c4c51f09
```

### Install & first launch

This build is ad-hoc signed (not notarized — there is no Apple Developer account behind it), so macOS
Gatekeeper blocks the first launch. Unzip the download and move **Blazecoin Wallet V2.app** into **/Applications**,
then get past Gatekeeper one of two ways:

**macOS 15 (Sequoia) and later** — double-click the app, click **Done** on the "Apple could not verify…"
dialog, open **System Settings → Privacy & Security**, scroll down to **Security**, click **Open Anyway**
next to the app and confirm. (On macOS 12–14: right-click the app → **Open** → **Open**.)

**Any version, one Terminal command** — removes the download's quarantine flag:

```
xattr -dr com.apple.quarantine "/Applications/Blazecoin Wallet V2.app"
```

### What changed in the re-issues (2026-09-24)

Verified on a 2021 M1 Pro (macOS 27) and a 2019 16-inch Intel MacBook Pro (macOS 26.5).

- **Intel Macs launch and run properly.** The first upload quit on launch on Intel; the Intel half of the
  app now runs the standard .NET runtime instead of an interpreter, so it is as responsive as the Apple
  Silicon half.
- **Dual-GPU Intel MacBook Pros use their discrete GPU** while the wallet is open. macOS was driving the
  Retina display with the integrated GPU, which made every page feel slow. Expect a little more fan and
  battery use on those machines while the wallet runs; it hands the GPU back when you quit.
- **Faster page changes and image loading** on every Mac: background images are cached between pages
  instead of being reloaded and decoded every time, and the Import, Backup and Quantum Exposure
  animations no longer redraw the whole screen every frame.
- **Provenance works out of the box.** The bundled node now keeps a transaction index, which the
  Provenance page needs. The index builds in the background after the first launch and takes about
  15 minutes on an M1; Provenance shows results once it is complete.
- **Sync progress is visible from the first minute.** While the node pre-syncs headers the status bar
  says so, with a count, instead of sitting at "0 / 0 blocks".
- **One-click "Create wallet"** on the first-run screen (the Terminal command is still shown as a fallback).
- **PDF export of transactions** works on Apple Silicon. It needs macOS 15 or newer.
- **V1.5 wallet import** converts the old Berkeley DB file directly, including files written by the
  macOS V1.5 build, and rescans from where V1.5 left off.
- The app icon is the Blazecoin icon, the dashboard coins are sharp on Retina displays, and the window
  is dark regardless of the system appearance.

### Notes for users

- Wallet and chain data live at `~/Library/Application Support/BlazecoinV2.0/`. The wallet starts the
  bundled node on launch and stops it cleanly when you quit; a node you started yourself is left alone
  (and then keeps running after you quit — stop it with the command below if you need to).
- First launch downloads and verifies the full chain — allow a few hours. The status bar shows the
  header pre-sync, then headers, then blocks.
- A brand-new install has no wallet yet, so the first screen says **No wallet found** — click
  **Create wallet "Primary"** and the app makes one. (The card also shows the equivalent Terminal
  command; the node's tools live inside the app bundle, which is why it carries the full path:

  ```
  "/Applications/Blazecoin Wallet V2.app/Contents/MacOS/blazecoin-cli" -named createwallet wallet_name="Primary"
  ```

  Then click **Check again**. (`… blazecoin-cli stop` stops the node the same way.)
- **Bringing a V1.5 wallet across:** Import page → **Choose wallet.dat…** → your V1.5 `wallet.dat` (on a Mac
  it is at `~/Library/Application Support/BlazecoinV1.5/wallet.dat`; the file dialog hides the Library
  folder, so press ⌘⇧G and paste the path, or copy the file to your Desktop first). It is converted to the
  V2 descriptor format and rescanned from where V1.5 left off; your V1.5 file is not modified and a
  `.legacy.bak` copy is kept beside the new wallet.
- Back up your wallet (Backup page) after you receive coins.

### Known limitations

- Requires macOS 12 (Monterey) or newer; PDF export requires macOS 15 or newer.
- Not notarized: the Gatekeeper step above is needed on every fresh download.
- The Backup page has no folder picker on macOS — type or paste the destination path.
- Transaction exports save straight to `~/Downloads`.
- On the Intel MacBook Pro the Quantum Exposure page's drifting background can still drop the odd frame.

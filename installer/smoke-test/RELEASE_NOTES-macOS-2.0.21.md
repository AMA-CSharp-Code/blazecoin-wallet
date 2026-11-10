# Blazecoin Wallet 2.0.21 (macOS)

**What changed:** one fix on the **Console** page. In 2.0.20 the active wallet's name sat inside the yellow blinking
prompt, so the Console showed a flashing "Primary ›". The prompt is the bare arrow again; the strip above the input
and every history line still name the wallet a command runs against. Nothing else changed since 2.0.20, and the
bundled node is still **Blazecoin Core 2.1.3**.

This Mac release is a step ahead of Windows: 2.0.20 is the current Windows installer, and the same fix reaches
Windows with its next build.

Everything from 2.0.20 is included — the bug-bounty fixes of 2026-11-09 and the audit that followed them: nothing
acts on a wallet the app switched to until you acknowledge the banner, typed amounts never read `1,5` as 15, only a
`B…` or `BQ…` address can be paid, and node 2.1.3 refuses wrapped SegWit addresses and puts every send through one
output check.

### Download & verify

- **`BlazecoinWalletV2-2.0.21-macOS-universal.zip`** — the wallet application (127 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.21-macOS-universal.zip
```

Expected:

```
fecb178a40e7ddf3baabddaf4dcd69f8a723e4b86f60d709bef4755167560ba9
```

### Install or update

Quit the wallet first (that stops its node), unzip, and move **Blazecoin Wallet V2.app** into **/Applications**,
replacing the old one. Wallets, settings and chain data in `~/Library/Application Support/BlazecoinV2.0/` are kept.

The build is ad-hoc signed, not notarized, so Gatekeeper blocks the first launch of each new download:

**macOS 15 (Sequoia) and later** — double-click the app, click **Done** on the "Apple could not verify…" dialog, open
**System Settings → Privacy & Security**, click **Open Anyway** next to the app and confirm. (On macOS 12–14:
right-click the app → **Open** → **Open**.)

**Any version, one Terminal command:**

```
xattr -dr com.apple.quarantine "/Applications/Blazecoin Wallet V2.app"
```

If the wallet appears to do nothing on its very first launch after a macOS update, quit it and open it again.

### Known limitations

- Requires macOS 12 (Monterey) or newer; PDF export requires macOS 15 or newer.
- Not notarized: the Gatekeeper step above is needed on every fresh download.
- Provenance needs the node's transaction index, which builds in the background after the first launch (about
  15 minutes on an M1).
- The Backup page has no folder picker on macOS — type or paste the destination path.
- Transaction exports save straight to `~/Downloads`.

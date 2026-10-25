# Blazecoin Wallet 2.0.13 (macOS)

**What changed:** one fix in **Preferences → Sign Message**. If you were asked for a passphrase and then changed
the address before signing, the wallet used to unlock the *first* address's wallet and the node refused the
signature ("wrong key"). The signing sequence — find the wallet that holds the address, unlock it for 60 seconds,
sign, check the signature with the node, lock again — now lives in the wallet's core library and looks the wallet
up afresh on every attempt. Changing the address also clears the previous attempt, and the passphrase is never kept
past one try. No signature was ever produced with the wrong key; the fault was a refusal and a needless unlock.

Same wallet as the Windows 2.0.13, as a **universal** app for Apple Silicon and Intel Macs. The bundled node is
still **Blazecoin Core 2.1.1**, as in the macOS 2.0.11 (there was no macOS 2.0.12: that Windows release only
re-bundled the node the Mac already had).

**Under the hood:** the sequence is covered by unit tests now (it previously lived only in the page). Nothing
else in the wallet changed.

### Download & verify

- **`BlazecoinWalletV2-2.0.13-macOS-universal.zip`** — the wallet application (126 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.13-macOS-universal.zip
```

Expected:

```
8b25d98f5e079ca1065c48481887f97f56fae76efa4341bbd4ca36cdcbdb2559
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

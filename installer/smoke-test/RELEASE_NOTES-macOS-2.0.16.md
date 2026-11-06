# Blazecoin Wallet 2.0.16 (macOS)

**What changed:** the **Provenance** page can now ask the block explorer about a wallet's lineage, and it no longer
crashes on wallets with a long consolidation history. Same wallet as the Windows 2.0.16, as a **universal** app for
Apple Silicon and Intel Macs, still bundling **Blazecoin Core 2.1.1**. (There were no macOS 2.0.14 or 2.0.15: those
version numbers were library packages for the lite wallets, with no desktop build.)

**Explorer Lineage.** The wallet's own provenance trace reads the chain through your node and stops at a fixed read
budget. A wallet whose coins have been consolidated many times over the years can exhaust that budget long before
the trace reaches the old blocks, so it showed only recent years. The page now has an **Explorer Lineage** card: one
click asks the block explorer, which has every output's lineage stored, and lays its figures beside your own trace —
each year and special vintage marked **verified** (both sources hold it), **explorer only** (your trace never got
there) or **local only** (coins newer than the explorer's watermark). Each year shows the block range it spans. The
request sends the wallet's receiving addresses to the explorer, so it is never made without asking; a Preferences
toggle (off by default) can make it automatic.

**Send by explorer lineage.** A third way to pick coins in **Send A Vintage**: whole outputs the explorer says are
mostly the chosen vintage by its proportional model, with a minimum-purity choice from 100 % down to "any — purest
first". These plans are marked as *not verified against this wallet's own trace*, because the explorer's figures are
proportional shares, not the exact pedigree your local trace gives.

**Fixed: a crash on deep histories.** Tracing a wallet with thousands of outputs behind a long chain of
consolidations could overflow the stack and close the wallet. Both walks now yield whenever the runtime reports the
stack is low. A regression test covers it.

**Well-behaved asks.** The explorer allows 120 requests a minute from one computer. The wallet now spaces its
requests under that, waits the time the explorer asks for if it is told to slow down and then carries on from where
it stopped, and limits one ask to 1,000 addresses (largest holdings first).

**Also:** the **Sign Message** fix from 2.0.13 is included.

### Download & verify

- **`BlazecoinWalletV2-2.0.16-macOS-universal.zip`** — the wallet application (127 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.16-macOS-universal.zip
```

Expected:

```
bc9f27e474f0470cefdac9f73eeb1572ab07535822768969cab91e5ed3173ad8
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

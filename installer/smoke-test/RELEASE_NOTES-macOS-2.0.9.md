# Blazecoin Wallet 2.0.9 (macOS)

**What changed:** the desktop wallet now puts your coins on **post-quantum addresses** — BQ… addresses, secured by
ML-DSA-44 since the post-quantum fork at block 4,250,000 — and keeps them there. Same release as the Windows
2.0.9, as a **universal** app for Apple Silicon and Intel Macs. It **still bundles Blazecoin Core 2.1.0**, the fork
node; nothing about the node changes.

New in this release:
- **Move to post-quantum.** On the Quantum Exposure page every legacy address has a **Move to BQ** button, and
  **Move everything to post-quantum** moves the whole wallet in one go. The destination is a fresh BQ… address from
  the wallet itself, and the page refuses anything that is not one. A plan is sent once; if a send times out the page
  says its result is unknown rather than inviting a retry.
- **Create a post-quantum wallet, right there.** A wallet created before node 2.1.0 has no post-quantum keys and
  cannot make a BQ… address. The Quantum page now says so plainly and offers to create a new wallet on the spot:
  encrypted with your passphrase by default (an unencrypted option exists for wallets that must sign on their own),
  backed up to a file you choose (by default in `~/Documents/Blazecoin Wallet Backups/`), and its first BQ… address
  filled in as the destination. Nothing is sent until you press Broadcast.
- **Receive on post-quantum addresses.** The Receive page offers **Post-quantum (BQ…)** or **Legacy (B…)**,
  post-quantum by default whenever the wallet can make one, and the list shows each address's type. A BQ… address can
  be paid by the desktop wallet 2.0.5 or later, the web wallet and the Android lite wallet 1.2.0 or later;
  **V1.5 and older wallets cannot pay a BQ… address**, so give those senders a legacy address.
- **Change stays post-quantum.** When you send from a wallet with post-quantum keys, the change comes back to a new
  BQ… address, and the review step shows where the change goes. A send that times out now says its result is unknown
  and asks you to check Transactions before sending again.
- **Solo mining pays to BQ….** The built-in miner's automatic payout address is post-quantum when the wallet can
  make one.
- **Clearer Quantum table.** Legacy addresses are listed above post-quantum ones, wear their own amber
  "Legacy · unexposed" badge, and the collapsed row says how many of each it hides.
- **Stricter address check.** A Bitcoin "1…" address is no longer mistaken for a legacy Blazecoin address.

Everything from the 2.0.8 macOS re-issues is included: Intel Macs run at full speed and dual-GPU MacBook Pros use
their discrete GPU while the wallet is open, images are cached between pages, Provenance works out of the box, and
V1.5 wallets import directly.

### Download & verify

- **`BlazecoinWalletV2-2.0.9-macOS-universal.zip`** — the wallet application (126 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.9-macOS-universal.zip
```

Expected:

```
907b7ba442a6b5729343c3b547376e7a7d526960fd927539168f64ecec7c4d09
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

### Known limitations

- Requires macOS 12 (Monterey) or newer; PDF export requires macOS 15 or newer.
- Not notarized: the Gatekeeper step above is needed on every fresh download.
- The Backup page has no folder picker on macOS — type or paste the destination path.
- Transaction exports save straight to `~/Downloads`.

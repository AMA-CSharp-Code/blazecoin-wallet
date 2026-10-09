# Blazecoin Wallet 2.0.11 (macOS)

**What changed:** proving that an address is yours is now one paste and one click, and this build carries
**Blazecoin Core 2.1.1**, the maintenance release of the post-quantum fork node. Same wallet as the Windows 2.0.11,
as a **universal** app for Apple Silicon and Intel Macs.

**Sign Message, in Preferences.** The website's Wallet Address card asks you to sign a short challenge with the key
behind your address before it will lock that address to your account (the gate for the collector rewards). Until
now that meant typing an RPC command into the Console. Now:

- Open **Preferences** and scroll to **Sign Message** (the sidebar button takes you straight there).
- Paste the whole `signmessage "…" "blazecoin-verify:…"` line the card shows, or just the challenge text. The address
  and message fill in by themselves.
- Click **Sign**, then **Copy signature**, and paste it into the card.

The wallet finds which of your loaded wallets holds the address, so it works from any window. If that wallet is
encrypted it asks for the passphrase in place, unlocks for 60 seconds, signs, and locks again. It checks the
signature against the node before showing it. Post-quantum `BQ…` addresses sign too: their signature is about
5,000 characters, shown whole, and the website accepts it.

**Also fixed**

- **Console.** Wallet commands typed into the Console failed with "Wallet file not specified" whenever more than one
  wallet was loaded. They now go to the window's active wallet.

**Bundled node: Blazecoin Core 2.1.1** (the Windows 2.0.11 installer still ships 2.1.0; the Mac build is the first
wallet with 2.1.1). Consensus is unchanged — a 2.1.0 node and a 2.1.1 node follow the same rules and stay on the
same chain. 2.1.1 adds a checkpoint at the post-quantum activation block 4,250,000 and refreshes the initial-sync
anchors to block 4,319,107, so a Mac syncing from scratch gets there faster and rejects a wrong chain at the fork
before validating it. No rescan or re-index is needed; wallets and chain data carry over unchanged.

Everything from the earlier macOS releases is included: the Provenance trace for post-quantum-consolidated wallets
(2.0.10), post-quantum receive, change and *Move to post-quantum* (2.0.9), full-speed Intel Macs with the discrete
GPU on dual-GPU MacBook Pros, cached images, and direct V1.5 wallet import (2.0.8).

### Download & verify

- **`BlazecoinWalletV2-2.0.11-macOS-universal.zip`** — the wallet application (126 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.11-macOS-universal.zip
```

Expected:

```
5e08831cc8c73cb5758aa188f8090a73045ca821a449cecf8de54df51523e4c6
```

### Install or update

Quit the wallet first (that stops its node), unzip, and move **Blazecoin Wallet V2.app** into **/Applications**,
replacing the old one. Wallets, settings and chain data in `~/Library/Application Support/BlazecoinV2.0/` are kept,
and the new node picks them up as they are.

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
- Provenance needs the node's transaction index, which builds in the background after the first launch (about
  15 minutes on an M1).
- The Backup page has no folder picker on macOS — type or paste the destination path.
- Transaction exports save straight to `~/Downloads`.

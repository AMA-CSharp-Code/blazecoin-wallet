# Blazecoin Wallet 2.0.10 (macOS)

**What changed:** the **Provenance page** now traces wallets whose coins were moved to post-quantum addresses. Same
release as the Windows 2.0.10, as a **universal** app for Apple Silicon and Intel Macs. It **still bundles Blazecoin
Core 2.1.0**, the fork node; nothing about the node changes.

Moving a wallet to post-quantum (2.0.9's *Move everything to post-quantum*) folds many small outputs into a few large
ones, and a large output can have a very long history behind it: for a mining or payout wallet, every block it ever
mined. In 2.0.9 a trace of such a wallet could sit with nothing but a running clock for a long time and, on the
biggest wallets, never finish. In 2.0.10:

- **The trace reports as it reads.** The counter of transactions read moves from the first seconds, whether the
  wallet has ten thousand small coins or one large one.
- **Cancel.** A running trace has a Cancel button, and leaving the page stops it.
- **The wallet stays responsive** while a trace runs: the walk happens in the background, and the page only paints.
- **It finishes.** The proportional ("haircut") lineage is now kept per vintage — a calendar month, or a special
  block — instead of per block, so the memory a trace needs no longer grows with the chain's history. A payout-sized
  wallet with 250,000 blocks behind its coins traces in a few minutes. The pedigree (FIFO) view is unchanged and still
  exact per block.
- **Fewer round trips.** A transaction's parents are read from the node in batches rather than one at a time.
- **Honest limits.** If a lineage is larger than the trace's read budget, the page says so and shows the catalogue as
  a floor: what could not be read is left unproven, never guessed.

Legacy wallets and legacy `B…` coins trace exactly as before.

Also in this build: **both accounting models on every table.** The Year Vintages and Special Vintages tables show the
pedigree (FIFO) figure and the proportional figure side by side — what is sendable next to what the website credits —
with a dash where a model holds nothing, and the block counts per model. The Pedigree/Proportional switch is gone; the
summary line reports both totals.

Also fixed: **sending a vintage that sits in the middle of a blended coin.** Such a send returns other lineage to you
both before and after the vintage, and the wallet gave both of those change outputs the same address — which the node
refuses ("duplicated address"). The leading change now goes to a second fresh address, and a plan that would pay any
address twice is refused at planning time with the reason.

Everything from the earlier macOS releases is included: post-quantum receive, change and *Move to post-quantum*
(2.0.9), full-speed Intel Macs with the discrete GPU on dual-GPU MacBook Pros, cached images, and direct V1.5 wallet
import (2.0.8).

### Download & verify

- **`BlazecoinWalletV2-2.0.10-macOS-universal.zip`** — the wallet application (126 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.10-macOS-universal.zip
```

Expected:

```
d076e865c4a1da4c9c2f170e1fa8fb79ae2cf9f31a551d007b4e138264d46051
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
- Provenance needs the node's transaction index, which builds in the background after the first launch (about
  15 minutes on an M1).
- The Backup page has no folder picker on macOS — type or paste the destination path.
- Transaction exports save straight to `~/Downloads`.

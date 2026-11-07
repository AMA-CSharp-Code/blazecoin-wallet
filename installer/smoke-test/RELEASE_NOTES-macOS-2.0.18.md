# Blazecoin Wallet 2.0.18 (macOS)

**What changed:** two fixes reported through the bug bounty programme. Neither involves an attacker or a loss of
coins; both are about coins ending up somewhere you did not choose. Update if you use **Provenance vintage sends**,
**Auto Payout** or the **solo miner**. Same wallet as the Windows 2.0.18, as a **universal** app for Apple Silicon
and Intel Macs, still bundling **Blazecoin Core 2.1.1**.

**Change now returns to a post-quantum address on every send path.** The Send page already did. Two other paths had
not caught up: a vintage send from the Provenance page made its change address a legacy one, and an Auto Payout left
the choice to the node's default. Spending part of a post-quantum coin either way put the remainder back behind an
older ECDSA key. Now:

- A vintage send returns its change to a post-quantum address whenever the wallet can make one. If the wallet should
  be able to and cannot, the page says so and sends nothing; it never falls back to a legacy address quietly.
- Auto Payout decides the change address the same way the Send page does, once per payout, and still pays only from
  the wallet it was armed on.
- A wallet with no post-quantum keys sends exactly as before.

**The solo miner's payout address follows the wallet.** The miner keeps its payout address across a stop so a
restart does not make a new one each time. That address was not tied to a wallet, so after switching wallets a
restarted miner paid the wallet you had just left. Now a stopped miner drops the address when the active wallet
changes and makes a new one in the wallet you are on. A miner that is running when you switch keeps paying where it
started (the Mining page shows the address) and drops the address when it stops.

**Credit:** reported through the bug bounty programme. Thank you.

Everything from 2.0.17 is included (a payment always comes out of the wallet you reviewed it on).

### Download & verify

- **`BlazecoinWalletV2-2.0.18-macOS-universal.zip`** — the wallet application (127 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.18-macOS-universal.zip
```

Expected:

```
b0da0d14c7866daf2956e36fed3f1031472a960bc738554e3538378ed8241b24
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

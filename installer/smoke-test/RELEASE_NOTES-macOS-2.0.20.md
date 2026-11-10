# Blazecoin Wallet 2.0.20 (macOS)

**What changed:** the bug-bounty fixes of 2026-11-09 and the audit that followed them, and the bundled node moves to
**Blazecoin Core 2.1.3**. Update if you run more than one wallet in the app, use **Auto Payout**, **Provenance
vintage sends** or the **Quantum sweep**, or type amounts with a decimal comma. Same wallet as the Windows 2.0.20, as
a **universal** app for Apple Silicon and Intel Macs.

**Nothing acts on a wallet the app switched to until you say so.** When the selected wallet is no longer loaded on
the node, a read re-points the app at another wallet and a banner tells you. Until now the Console, a new address, a
label, signing — and, found by the audit, the Send page, Auto Payout Start, a vintage send and the Quantum sweep —
could run against that wallet before the banner was acknowledged; a Console `sendtoaddress` typed for one wallet
paid from another. Every write now waits for the acknowledgement, the Console and the Send page name the wallet they
run against, and a vintage send or sweep is bound to the wallet it was planned on.

**Typed amounts never read `1,5` as 15.** The Send page and the four Auto Payout amounts are text fields with one
strict grammar; an unreadable amount is shown and refuses to go on.

**Only a `B…` or `BQ…` address can be paid** from the Send page, Auto Payout, the vintage planners and the sweep. A
`blz1…` SegWit address, a foreign-chain address or a script-hash address is refused with one message (the Console is
unchanged).

**Fee estimate, lock and Console.** The fee shown at review is priced on the wallet the send will leave; locking a
wallet is always allowed; the Console strip says which wallet commands run against.

**Node 2.1.3 (bundled):** wrapped SegWit (`p2sh-segwit`) addresses are refused like `bech32`; `sendall` and a
hand-given change address go through the same output check as every other send; change is legacy while SegWit is
inactive; the node reads its own fee-estimates file again (every restart used to begin with an empty estimator).
Consensus is unchanged: 2.1.2 and 2.1.3 nodes stay on the same chain. No rescan or re-index is needed; the new node
starts on the same data.

**Credit:** reported through the bug bounty programme by three researchers. Thank you.

### Download & verify

- **`BlazecoinWalletV2-2.0.20-macOS-universal.zip`** — the wallet application (127 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.20-macOS-universal.zip
```

Expected:

```
ad05e170183f9f7539c1c7c67e31c1f8aed671610db208bb5e1281cabe158009
```

### Install or update

Quit the wallet first (that stops its node), unzip, and move **Blazecoin Wallet V2.app** into **/Applications**,
replacing the old one. Wallets, settings and chain data in `~/Library/Application Support/BlazecoinV2.0/` are kept,
and the 2.1.3 node picks them up as they are.

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

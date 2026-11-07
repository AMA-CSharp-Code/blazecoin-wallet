# Blazecoin Wallet 2.0.19 (macOS)

**What changed:** three fixes reported through the bug bounty programme, and the bundled node moves to **Blazecoin
Core 2.1.2**. Update if you use **Provenance vintage sends** or **Auto Payout**, or if you mine or run a node — 2.1.2
closes a way for one transaction to stop block production on any node. Same wallet as the Windows 2.0.19, as a
**universal** app for Apple Silicon and Intel Macs.

**A vintage send refuses a `blz1…` address.** SegWit never activates on this chain, so a `blz1…` (bech32) output can
be mined but never spent: coins sent to one are gone. The Send page and Auto Payout already refused such an address.
The four Provenance (vintage) planners checked only that the recipient was not blank, so a vintage send to a `blz1…`
address would have built a transaction the node accepted and the coins would have been lost. One rule now covers the
recipient, the change address and the leading-change address on every vintage path, and Auto Payout shares it.

**The post-quantum dust floor.** Node 2.1.2 prices a post-quantum output's dust threshold by the input that spends
it, so the floor for a `BQ…` output is 11,475 satoshis rather than 573. The transaction builder, the vintage
planners, the explorer-lineage send and the quantum sweep planner all use the new floor, so a change or sweep output
is never one the network refuses.

**An Auto Payout dry run no longer counts.** A dry run recorded itself as a payout, so it used up the daily cap and
started the cooldown as if coins had moved. It now records nothing.

**Node 2.1.2 (bundled):** the mempool refuses any transaction that carries witness data or creates a witness output
while SegWit is not active, and the block assembler skips witness transactions — before this, one cheap standard
transaction could sit in the mempool and make every block template fail, halting block production on that node until
it was cleared. The node wallet refuses to make, pay or spend from a bech32 address; a corrupt post-quantum key-cache
record makes the wallet file load as corrupt instead of handing out an unspendable address; `importdescriptors`
warns that timelocks are not enforced in blocks; and the dust rule above. Consensus is unchanged: 2.1.1 and 2.1.2
nodes stay on the same chain. No rescan or re-index is needed; the new node starts on the same data.

**Credit:** reported through the bug bounty programme by two researchers. Thank you.

### Download & verify

- **`BlazecoinWalletV2-2.0.19-macOS-universal.zip`** — the wallet application (127 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.19-macOS-universal.zip
```

Expected:

```
59e57c7abd1faf6b09a655d6a68ee3f7dbeca5366ee8a1b0b9127244193c1de8
```

### Install or update

Quit the wallet first (that stops its node), unzip, and move **Blazecoin Wallet V2.app** into **/Applications**,
replacing the old one. Wallets, settings and chain data in `~/Library/Application Support/BlazecoinV2.0/` are kept,
and the 2.1.2 node picks them up as they are.

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

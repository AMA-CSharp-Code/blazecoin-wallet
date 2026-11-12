# Blazecoin Wallet 2.0.22 (macOS)

**What changed since 2.0.21:**

- **Consolidate Coins** (Send page). A wallet that has received many small payments, such as mining payouts, can
  hold thousands of coins, and a large payment from it may then be refused as too big for one transaction. The new
  card counts the coins, plans the merge (how many transactions, the fee, how many coins are left afterwards) and
  shows the plan before anything is sent. The coins go back to a new address in the same wallet. Coins still waiting
  for their first confirmation are shown and left out, with a note to count again once they confirm. Before you can
  broadcast you tick a box confirming that the transactions cannot be undone. When the coins go to a post-quantum
  address there is a second box, confirming that you have a backup made after the wallet's post-quantum keys were
  created. Run it again for another round if more than one coin is left.
- **Console.** Quoted text, empty `""` arguments and JSON arguments are read correctly, so commands that failed
  because of their quotes now work.
- **CSV exports** (bug-bounty round fourteen, 2026-11-12): a cell can no longer start with a formula when the file is
  opened in a spreadsheet.
- The two fork-announcement signing keys are built in.

The bundled node is still **Blazecoin Core 2.1.3**. Windows 2.0.22 carries the same changes.

### Download & verify

- **`BlazecoinWalletV2-2.0.22-macOS-universal.zip`** — the wallet application (127 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.22-macOS-universal.zip
```

Expected:

```
1c565d15925e7bf11432bc4a46e3dad13d283cab8296b6da4133fe2e6ba6137d
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

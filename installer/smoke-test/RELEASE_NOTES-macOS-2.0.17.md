# Blazecoin Wallet 2.0.17 (macOS)

**What changed:** a security fix for people who keep **more than one wallet** loaded, reported through the bug
bounty programme. Update if you do; it is harmless if you don't. Same wallet as the Windows 2.0.17, as a
**universal** app for Apple Silicon and Intel Macs, still bundling **Blazecoin Core 2.1.1**.

**A payment now always comes out of the wallet you reviewed it on.** If the wallet you had selected was no longer
loaded on the node — it was unloaded, or the node restarted without it — the app used to switch to another loaded
wallet and repeat whatever it was doing. That was meant for balance and history pages, but it applied to everything,
so a payment confirmed while looking at one wallet could be sent, unchanged, from a different one and reported as a
normal success. Now:

- Only read-only requests (balances, history, addresses) are repeated on another wallet. Sending, signing, creating
  an address, labelling and changing or entering a passphrase stop with a plain message: the wallet is not loaded,
  nothing was done.
- A payment carries the name of the wallet it was reviewed on and runs against exactly that wallet. The review step
  shows it ("from wallet …"), and if the active wallet has changed by the time you confirm, the wallet says so and
  sends nothing.
- **Auto Payout** remembers the wallet it was armed on. If the active wallet is no longer that one it stops, sends
  nothing more, and says why; press Start to arm it on the wallet you mean.
- When the app does switch wallets for you, a notice at the top of the window says which wallet went away and which
  one you are looking at now. It stays until you dismiss it.

On a Mac this matters in one common situation: the node loads only `Primary` at start-up, so a wallet you imported
(a V1.5 import, for example) is not loaded after a restart until you load it under Active Wallet. With 2.0.17 the
app tells you when it has moved you to another wallet instead of doing so silently.

**Also fixed: a payment link with a comma in its amount.** An amount such as `1,5` in a pasted `blazecoin:` link
was read as 15. Link amounts now take digits and a decimal point only; anything else is left out for you to enter.

**Also fixed: the Dashboard stopping on "the selected wallet is no longer loaded".** When the app switched wallets
for you, the Dashboard could show that error panel until you pressed Retry. Requests made together now all recover.

### Download & verify

- **`BlazecoinWalletV2-2.0.17-macOS-universal.zip`** — the wallet application (127 MB)

```
shasum -a 256 BlazecoinWalletV2-2.0.17-macOS-universal.zip
```

Expected:

```
7f7cbe1b3189244650aad325f588dc62c7f4cc6cacda1af539995ae49de478b9
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

# Google Play listing — Blazecoin Wallet (Android)

Prepared 2026-08-23 for the first upload (internal testing track). Copy is US-clean per the
2026-07-13/16 homepage compliance sweep: participation/mining framing, no price/upside/"invest"
language, no referral-income promises. Everything here is paste-ready for the Play Console.

## App identity

| Field | Value |
|---|---|
| Package name | `com.blazecoin.wallet` |
| App name (max 30) | `Blazecoin Wallet` |
| Default language | English (United Kingdom) — `en-GB` |
| Category | Finance |
| Tags (pick up to 5) | Cryptocurrency wallet, Bitcoin-style wallet, Finance tools |
| Contact email | `admin@blazecoin.co.uk` |
| Website | `https://blazecoin.co.uk` |
| Privacy policy URL | `https://gw.blazecoin.co.uk/privacy.html` (same page also at `https://gw2.blazecoin.co.uk/privacy.html`) |
| Version for this upload | `1.1.0` (versionCode `4`) — artifact `artifacts\com.blazecoin.wallet-Signed.aab`, rebuilt 2026-08-26 for Phoenix-413 (sha256 `40e1b3ee…`; supersedes the 08-24 `1.0.0 (2)` build) |
| Upload key | `C:\Users\<you>\BlazecoinSigning\blazecoin-upload.keystore` (SHA-256 `15:6E:F9:75…76:FD`); enrol in **Play App Signing** at first upload so the upload key is resettable |

## Short description (max 80 chars)

```
Non-custodial Blazecoin (BLZ) wallet. Your keys stay on your phone.
```
(68 chars)

## Full description (max 4000 chars)

```
Blazecoin Wallet is the official non-custodial wallet for Blazecoin (BLZ), a scrypt
proof-of-work coin that has been mined continuously since May 2014.

YOUR KEYS, YOUR PHONE
• A 12-word recovery phrase is generated on your device and stored in Android's
  hardware-backed secure storage. It never leaves the phone and we never see it.
• Every transaction is signed on the device. The app has no accounts, no sign-up,
  no email, no analytics and no ads.
• Optional PIN and fingerprint/face unlock, automatic re-lock when you switch apps,
  and a backup check that makes sure you really wrote your phrase down.

VERIFIES, DOESN'T TRUST
• The wallet reads the blockchain through Blazecoin gateway servers but checks
  everything it is told: each coin you spend is proven against the real transaction
  and its block, and block headers are verified with proof-of-work on the device.
  A misbehaving server can go offline, but it cannot steal from you.
• Two independent gateways in different countries with automatic failover, a
  peer-to-peer broadcast fallback, and a "personal node" mode if you run your own
  Blazecoin node.

EVERYDAY USE
• Send and receive BLZ; scan addresses and payment links with the camera.
• Fresh receive address for every payment, with full history across all of them.
• Send Max, address book, labels, watch-only addresses, message sign/verify.
• Recover coins from a 2014-era wallet by sweeping its private key (WIF) in one tap.
• Provenance view: see which mining year each of your coins descends from.
• Four dashboard skins.

ABOUT BLAZECOIN
Blazecoin launched on 23 May 2014 with a 413 BLZ block reward in honour of a
volunteer fire station. Blocks every 30 seconds, open source, mined by the
community. Restore this wallet anywhere with your 12 words — the same phrase also
works in the Blazecoin web wallet.

This wallet does not buy, sell or exchange currency and is not an investment
product. Cryptocurrency values can fall as well as rise. Keep your recovery phrase
safe: nobody — including us — can recover it for you.
```

## Graphics (in `play\graphics\`)

| Asset | File | Spec |
|---|---|---|
| Hi-res icon | `icon-512.png` | 512×512 PNG, no alpha ✅ |
| Feature graphic | `feature-graphic-1024x500.png` | 1024×500 PNG ✅ |
| Phone screenshots | `play\screenshots\play-ready\*-1080x2160.png` (4: home/receive/send/settings) | ≥2, ≤2:1 aspect (Play caps at 2:1 — the raw 1080×2400 emulator captures were cropped to 1080×2160), 320–3840 px |

## Content rating questionnaire (IARC) — answers

Finance / Utility app. **No** to: violence, sexuality, language, controlled substances,
gambling (no simulated gambling, no real-money gambling), user interaction / user-generated
content, sharing location, purchases of digital goods. The app lets users **send
cryptocurrency** — answer "Yes" where the questionnaire asks whether the app "facilitates
the exchange/transfer of real currency or cryptocurrency" (it is a wallet, not an exchange).
Expected rating: PEGI 3 / ESRB Everyone (some stores auto-raise finance apps to "Parental
guidance"; accept whatever IARC returns).

## Target audience & content

- Target age: **18 and over** (financial app). Not designed for children.
- News app: No. COVID-19 app: No. Government app: No.
- **Financial features declaration** (Play Console › App content › Financial features):
  select **"Cryptocurrency exchanges and wallets" → non-custodial wallet**; the app does not
  provide loans, payments, or exchange services. Some regions require extra documentation
  for *exchanges*; a non-custodial wallet that does not convert or custody funds is the
  low-friction category — state that plainly if asked.
- Ads: **No ads**. In-app purchases: **None**.

## Data safety form — see `DATA_SAFETY.md`

## Internal testing track — first upload steps

1. Play Console › Create app → name `Blazecoin Wallet`, default language en-GB, App, Free,
   accept the declarations.
2. **Set up → App integrity → Play App Signing**: choose "Use Google-generated key" and
   upload the AAB; Google records `blazecoin-upload.keystore`'s certificate as the upload
   key. (Do NOT pick "export and upload a key from Java keystore" — the keystore stays
   local; the AAB's signature is enough.)
3. **Test and release → Testing → Internal testing → Create new release** → upload
   `com.blazecoin.wallet-Signed.aab` → release name `1.1.0 (4)` → release notes:
   `First internal build: non-custodial BLZ wallet — keys on device, PIN/biometric lock,
   QR scan, address rotation, WIF sweep, provenance view.` → Save → Review release → Start
   rollout to Internal testing.
4. Testers tab → create an email list (your Google account(s)) → copy the **opt-in URL** →
   open it on the real phone, accept, install from Play. **This is the first Play-delivered real-device pass** (a sideloaded 1.0.0 already ran on a real phone 2026-08-24; biometric send on real hardware remains untested) —
   run the emulator smoke script by hand: create wallet → backup quiz → receive 0.01 BLZ
   from the desktop payout wallet → confirm toast/1-conf → biometric Send Max back → check
   both ends reconcile → verify-backup → Forget wallet.
5. Fill **Store listing** (copy above + graphics + screenshots), **App content** (privacy
   URL, ads = no, content rating, target audience, data safety, financial features),
   **Store settings** (category Finance, contact email), **Pricing** (Free).
6. Promote the same release to **Closed testing** / **Production** only after the
   real-device pass is clean and the legal docs (ToS/Disclaimer) exist on the website
   (Play does not require them, but the listing links to blazecoin.co.uk).

## Release checklist (every upload)

- bump `ApplicationDisplayVersion` / `ApplicationVersion` in `BlazecoinWallet.App.Droid.csproj`
- refresh `HeaderChainSync.MainnetCheckpoint` (Lite.Core) to a recent retarget boundary
- `.\Build-AndroidRelease.ps1 -OutDir <explicit path>` (PS 5.1 `-File` leaves `$PSScriptRoot`
  empty in `param()` defaults)
- `aapt dump badging artifacts\com.blazecoin.wallet-Signed.apk | findstr versionCode`
- install the APK on the emulator (`adb install -r`) and smoke before uploading the AAB

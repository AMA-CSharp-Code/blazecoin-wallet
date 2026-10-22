# Play Console › App content › Data safety — answers for Blazecoin Wallet

Prepared 2026-08-23 from the shipped code (Lite.Core / App.Droid) and the privacy policy at
`https://gw.blazecoin.co.uk/privacy.html`. Re-check this file whenever a feature starts
sending anything new off-device.

## Overview questions

| Question | Answer | Why |
|---|---|---|
| Does your app collect or share any of the required user data types? | **Yes** | Wallet addresses + signed transactions + the device IP reach the project's gateway servers over HTTPS (Google counts data that leaves the device to the developer's server as "collected", even if not stored). |
| Is all of the user data collected by your app encrypted in transit? | **Yes** | HTTPS only; `GatewayFailoverHandler` rejects non-https gateways (loopback excepted for dev); `usesCleartextTraffic=false`. |
| Do you provide a way for users to request that their data is deleted? | **Yes** (no account; deleting the wallet in-app / uninstalling removes everything local; server side holds no account data — state this in the free-text field) | Policy §6. |
| Does your app follow the Families policy / target children? | **No** | Target age 18+. |
| Independent security review? | **No** | Optional badge; not requested. |

## Data types — what to tick

Tick **only** these; everything else = "No".

### Financial info → *Other financial info*
- **Collected: Yes** · **Shared: No**
- Processed ephemerally? **No** (requests are logged briefly in service journals — treat as "not ephemeral" to be safe)
- Required or optional: **Required** (the wallet cannot show a balance without querying its addresses)
- Purpose: **App functionality**
- Description to paste: *"Wallet addresses and signed transactions are sent to Blazecoin gateway servers to read balances and broadcast payments. The app is non-custodial: keys never leave the device. No names, emails or accounts exist."*

### App activity → *Other actions* — **No**
(No analytics, no event tracking, no crash reporting SDK — there is no telemetry of any kind.)

### Device or other IDs — **No**
(No advertising ID, no device ID, no install ID is read or sent. The per-IP rate-limit on the
servers is an infrastructure control on the connection, not a collected identifier; Google's
form treats IP addresses as "not a data type" unless stored as an identifier — they are not.)

### Photos and videos / Files and docs — **No**
(The QR upload fallback decodes a user-chosen image on-device and never uploads it. Google's
guidance: data processed on-device only and not transmitted is not "collected".)

### Location · Personal info · Health · Messages · Audio · Calendar · Contacts · Web browsing · App info & performance — **No**

## Permissions (for the "Permissions" declaration / reviewer notes)

| Permission | Why |
|---|---|
| `INTERNET`, `ACCESS_NETWORK_STATE` | Talk to the gateway servers; detect offline. |
| `CAMERA` | QR scanner for addresses / BIP21 links (frames decoded on-device, never stored). Optional — upload-image fallback exists. |
| `USE_BIOMETRIC`, `USE_FINGERPRINT` | Optional unlock / send authorisation via AndroidX `BiometricPrompt`; the app receives only success/failure. (These two arrive via the androidx.biometric manifest merge, not our own manifest — verified against the signed 1.0.0 APK with `aapt dump permissions`, 2026-08-24; the 1.1.0 (4) rebuild of 2026-08-26 changed only the version numbers, no manifest changes.) |
| *(no storage / location / contacts / phone permissions)* | |

`allowBackup=false` + `dataExtractionRules` opt the app out of cloud and device-to-device
backup (policy §2) — mention in reviewer notes if asked why wallet data does not transfer.

## Security practices section

- Data encrypted in transit: **Yes** (HTTPS/TLS).
- Users can request deletion: **Yes** — link the privacy page §6 (`https://gw.blazecoin.co.uk/privacy.html#6`).
- Data handling: keys in Android Keystore-backed `SecureStorage`; PIN stored as salted PBKDF2 hash;
  no third-party SDKs.

## Financial features declaration (separate section under App content)

- "Does your app provide any financial features?" → **Yes** → **Cryptocurrency wallets / exchanges** → select **wallet (non-custodial)**.
- It does **not** offer: loans, BNPL, payments/money transfer between users in fiat, exchange/trading, or investment advice.
- Some countries ask for a licence number for *exchanges/custodial wallets*; answer N/A — the app never holds, converts or transmits user funds on the user's behalf (the user signs and broadcasts their own transaction to a public blockchain).

## Reviewer notes (App content › App access)

"All features are available without login — there are no accounts. To test send/receive the
reviewer needs BLZ; the app works fully with an empty wallet (create → backup quiz → receive
screen → settings). A funded test is not required; if you want one, email
admin@blazecoin.co.uk and we will send a small amount to the address the reviewer generates."

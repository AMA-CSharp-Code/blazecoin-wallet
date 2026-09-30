# Clean-machine smoke test

Proves the V2.0 wallet installer's **first-run chain** on a genuinely clean
Windows instance — the one thing static analysis and this dev box can't verify,
because the dev box already has WebView2 and a legacy `%APPDATA%\BlazecoinV2.0`
datadir (the live Indexer).

## What it checks

`Run-SmokeTest.ps1` runs **inside** a clean instance and verifies, in order:

1. Silent install exits 0, lands `{localappdata}\Programs\Blazecoin Wallet V2.0`
2. `blazecoind.exe` + `blazecoin-cli.exe` ship **beside** the wallet exe
3. `blazecoin.conf` lands in the daemon datadir — on a clean machine there's no
   legacy Roaming dir, so it must resolve to **`%LOCALAPPDATA%\BlazecoinV2.0`**
   (the branch the dev box can't exercise; installer / daemon / wallet must agree)
4. Launching the wallet **auto-starts the bundled daemon** (beside-exe discovery)
5. The daemon writes `.cookie` and **cookie auth** answers RPC (no creds shipped)
6. **IBD begins** — headers/blocks advance, peers connect
7. **WebView2**: `msedgewebview2` children spawn ⇒ the UI rendered. On an image
   without the runtime, the installer's bootstrapper installs it first.

Result: console PASS/FAIL per check + a machine-readable `smoke-result.json`.

## Current status

Hyper-V (vehicle B, automated) was the chosen vehicle: **run 7 on Win11 25H2 passed
13/13 on 2026-07-12** via `hyperv/Provision-HyperVSmokeTest.ps1`. Harness closed —
full history, root causes and fixes in `FINDINGS-2026-07-07.md`.

The 2.0.1 (2026-09-05), 2.0.2 (2026-09-11), 2.0.3 and 2.0.4 (both 2026-09-12) installers have **not** been recorded as re-run
through this harness; they keep the 2.0.0 layout and daemon-beside-exe contract. The next
release is the one to re-smoke (`Run-SmokeTest.ps1` still expects the `BlazecoinWalletV2-Setup-<ver>.exe` name).

## Vehicles

### A. Windows Sandbox (easiest, disposable, no persistent VM)

One-time enable (admin **+ reboot** — do it in a daemon maintenance window, the
reboot drops the 3 live mainnet daemons):

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

Then (no admin needed):

```powershell
.\Launch-Sandbox.ps1
```

It maps the installer in read-only, a writable `results\` out, and auto-runs the
test on logon. Caveat: Sandbox images usually **have** WebView2 (Edge is present),
so this covers everything except the WebView2-**absent** bootstrapper branch.

### B. Hyper-V eval VM (covers the WebView2-absent branch too)

No host reboot (Hyper-V already installed). Needs elevation + a Windows 11
evaluation ISO. Fully automated by `hyperv/Provision-HyperVSmokeTest.ps1`
(unattended install → in-guest run → result pull-back; see `hyperv/README.md`) —
this is the vehicle that produced the 13/13 pass. A base eval image exercises the
WebView2-absent bootstrapper path a Sandbox image skips.

### C. Physical clean machine

Copy `BlazecoinWalletV2-Setup-*.exe` + `Run-SmokeTest.ps1` onto any fresh
Windows box and run the script. Same checks, real hardware.

## Manual run (any vehicle)

```powershell
.\Run-SmokeTest.ps1 -SetupExe <path-to-setup.exe> -ResultDir <writable-folder>
# -NoLaunch  : install + verify layout/conf only (CI-friendly, no UI)
# -TimeoutSec: how long to wait for daemon start + first IBD progress (default 180)
```

# Hyper-V eval-VM smoke test

Fully-automated clean-machine smoke test in a **throwaway Hyper-V VM**. No host
reboot (unlike Windows Sandbox), and a stock Windows install image exercises the
**WebView2-absent bootstrapper branch** that Sandbox can't (Sandbox ships Edge).

## What it does

`Provision-HyperVSmokeTest.ps1` (run **elevated** — one UAC for the whole run):

1. Inspects the Windows ISO, picks an edition (Pro preferred) + its generic
   install key, and substitutes them into `autounattend.xml`.
2. Builds a tiny secondary ISO carrying the answer file (IMAPI2FS COM — no ADK).
3. Creates a Gen2 VM (blank dynamic VHDX) on the **Default Switch** (NAT internet,
   so the guest daemon can reach the `addnode` seed peers and run IBD).
4. Boots it; nudges past the "press any key to boot from CD" prompt during the
   first boot window only.
5. Waits for the unattended install to finish and auto-log-on local admin `smoke`
   (reachable via **PowerShell Direct** — no guest networking needed for control).
6. Copies the setup exe + `Run-SmokeTest.ps1` into the guest, runs the test in the
   **interactive** session (scheduled task as `smoke`, so the WebView2 UI renders).
7. Pulls `smoke-result.json` back to `-WorkDir`; tears the VM down (`-KeepVM` to keep).

## Run

```powershell
# elevated PowerShell (one UAC)
.\Provision-HyperVSmokeTest.ps1 `
    -IsoPath  F:\BlazecoinSmokeVM\Windows11.iso `
    -SetupExe ..\..\Output\BlazecoinWalletV2-Setup-2.0.0.exe
```

Progress is transcripted to `<WorkDir>\provision.log`; the machine-readable
verdict lands in `<WorkDir>\smoke-result.json`.

## Notes / requirements

- **Windows ISO**: use **Windows 11** (policy decided 2026-07-09; the harness ISO is
  `F:\BlazecoinSmokeVM\Windows11.iso`, Enterprise Eval 25H2). Win10 images that haven't
  taken updates since ~2022 CET-fail-fast .NET 10 (`0x80131506`) and are **known to fail
  the 4 runtime checks** — see `..\FINDINGS-2026-07-07.md`; the old `Windows10.iso` was
  deleted. `autounattend.xml` carries the LabConfig TPM/CPU/RAM bypass Win11 setup needs
  on a vTPM-less VM (Secure Boot stays ON). The script still warns if the image build is
  below 19041 (the wallet's `net10.0-windows10.0.19041.0` floor).
- **`smoke` / `Blz-Smoke!2026`** is a throwaway guest credential (matches
  `autounattend.xml`). Not a secret: the VM is disposable, NAT-isolated, and
  deleted at the end.
- **Coverage vs. the dev box**: this is the one vehicle that exercises the fresh
  **Local** datadir path (no legacy Roaming), the WebView2-absent bootstrapper,
  and the live first-run integration (daemon auto-start → cookie auth → IBD), none
  of which the dev box can (it has WebView2 + the live Indexer's Roaming datadir).
- The `Windows11.iso`, the generated answer-file ISO, VHDX, and results are kept under
  `-WorkDir` (default `F:\BlazecoinSmokeVM`), outside the repo.

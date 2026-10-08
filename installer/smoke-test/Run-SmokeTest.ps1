<#
.SYNOPSIS
    Clean-machine smoke test for the Blazecoin Wallet V2.0 installer.

    Runs INSIDE a fresh Windows instance (Windows Sandbox, a throwaway Hyper-V
    VM, or a physical clean machine). Vehicle-agnostic: it only needs the setup
    exe present and a folder it may write results into.

    What it proves — the first-run chain that static analysis cannot:
      1. Silent install lands the expected layout under {localappdata}\Programs
      2. blazecoind.exe + blazecoin-cli.exe ship BESIDE the wallet exe
      3. blazecoin.conf lands in the daemon datadir. On a clean machine there is
         NO legacy %APPDATA%\BlazecoinV2.0, so it must resolve to the LOCAL path
         %LOCALAPPDATA%\BlazecoinV2.0 (the branch this box can't exercise).
      4. Launching the wallet auto-starts the bundled daemon (beside-exe discovery)
      5. The daemon writes a .cookie and cookie auth answers RPC (no creds shipped)
      6. IBD begins — headers/blocks advance, peers connect
      7. WebView2: on an image without the runtime, the installer's bootstrapper
         installs it; msedgewebview2 children spawn => the UI rendered.

    Non-destructive: installs per-user, never touches host daemons (there are none
    on a clean instance). Leaves everything for inspection; the vehicle is disposable.

.PARAMETER SetupExe
    Path to BlazecoinWalletV2-Setup-*.exe (defaults to the one mapped beside this script).
.PARAMETER ResultDir
    Folder to write the JSON + transcript result into (defaults to script dir).
.PARAMETER TimeoutSec
    How long to wait for daemon start + first IBD progress (default 180).
.PARAMETER NoLaunch
    Install + verify layout/conf only; skip launching the wallet (CI-friendly).
#>
[CmdletBinding()]
param(
    [string]$SetupExe  = "",
    [string]$ResultDir = "",
    [int]   $TimeoutSec = 180,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Continue"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($SetupExe -eq "") {
    # In Sandbox the exe is mapped beside this script; for a manual/dev-box run it
    # lives in the sibling installer\Output. Search both, newest first.
    $SetupExe = (Get-ChildItem @($Here, (Join-Path (Split-Path $Here) "Output")) -Filter "BlazecoinWalletV2-Setup-*.exe" -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if ($ResultDir -eq "") { $ResultDir = $Here }
New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

$Checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    $Checks.Add([pscustomobject]@{ name = $name; ok = $ok; detail = $detail })
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1}{2}" -f $tag, $name, $(if ($detail) { " -- $detail" } else { "" }))
}

Write-Host "=== Blazecoin Wallet V2.0 - clean-machine smoke test ==="
Write-Host ("host      : {0}" -f $env:COMPUTERNAME)
Write-Host ("setup exe : {0}" -f $SetupExe)
Write-Host ("results   : {0}" -f $ResultDir)
Write-Host ""

# Pre-flight: this must be a CLEAN machine. Warn (don't fail) if a legacy datadir
# exists -- it means we're NOT testing the fresh-install Local-path branch.
$roaming = Join-Path $env:APPDATA "BlazecoinV2.0"
$local   = Join-Path $env:LOCALAPPDATA "BlazecoinV2.0"
if (Test-Path $roaming) {
    Write-Host "WARNING: legacy %APPDATA%\BlazecoinV2.0 exists -- this is NOT a clean machine." -ForegroundColor Yellow
    Write-Host "         The fresh-install Local-datadir branch will NOT be exercised." -ForegroundColor Yellow
}

if (-not $SetupExe -or -not (Test-Path $SetupExe)) {
    Check "setup-exe-present" $false "not found: $SetupExe"
    $Checks | ConvertTo-Json -Depth 4 | Out-File (Join-Path $ResultDir "smoke-result.json") -Encoding utf8
    exit 1
}
Check "setup-exe-present" $true ("{0} MB" -f [math]::Round((Get-Item $SetupExe).Length/1MB))

# 1. Silent install
$log = Join-Path $ResultDir "install.log"
Write-Host "`n-- installing (silent) --"
$p = Start-Process -FilePath $SetupExe `
    -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/LOG=`"$log`"" `
    -Wait -PassThru
Check "installer-exit-0" ($p.ExitCode -eq 0) ("exit {0}" -f $p.ExitCode)

# 2. Layout under {localappdata}\Programs\Blazecoin Wallet V2.0
$app = Join-Path $env:LOCALAPPDATA "Programs\Blazecoin Wallet V2.0"
Check "app-dir"        (Test-Path $app) $app
$walletExe = Join-Path $app "BlazecoinWallet.Maui.exe"
$daemonExe = Join-Path $app "blazecoind.exe"
$cliExe    = Join-Path $app "blazecoin-cli.exe"
Check "wallet-exe"     (Test-Path $walletExe) $walletExe
Check "daemon-beside"  (Test-Path $daemonExe) "blazecoind.exe beside wallet"
Check "cli-beside"     (Test-Path $cliExe)    "blazecoin-cli.exe beside wallet"

# 3. Conf into the daemon datadir. Clean machine => must be the LOCAL path.
$dataDir = if (Test-Path $roaming) { $roaming } else { $local }
$conf    = Join-Path $dataDir "blazecoin.conf"
Check "conf-placed"    (Test-Path $conf) $conf
Check "conf-in-local"  (-not (Test-Path $roaming)) ("datadir resolved to {0}" -f $dataDir)

if ($NoLaunch) {
    Write-Host "`n-- -NoLaunch: skipping runtime checks --"
} else {
    # 4. Launch the wallet -> it should auto-start the bundled daemon
    Write-Host "`n-- launching wallet --"
    if (Test-Path $walletExe) { Start-Process -FilePath $walletExe | Out-Null }

    # 5-6. Poll for daemon start, cookie auth, and first IBD progress
    $cookie = Join-Path $dataDir ".cookie"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $daemonUp = $false; $rpcOk = $false; $ibd = $false; $blocks = -1; $headers = -1; $peers = -1
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (-not (Get-Process blazecoind -ErrorAction SilentlyContinue)) { continue }
        $daemonUp = $true
        if (-not (Test-Path $cookie)) { continue }
        # cookie auth: -datadir lets the cli read .cookie itself
        $info = & $cliExe "-datadir=$dataDir" -rpcclienttimeout=5 getblockchaininfo 2>$null | Out-String
        if ($LASTEXITCODE -ne 0 -or -not $info.Trim()) { continue }
        $rpcOk = $true
        try {
            $j = $info | ConvertFrom-Json
            $blocks = [int]$j.blocks; $headers = [int]$j.headers; $ibd = [bool]$j.initialblockdownload
        } catch {}
        $ni = & $cliExe "-datadir=$dataDir" -rpcclienttimeout=5 getconnectioncount 2>$null
        if ($LASTEXITCODE -eq 0) { $peers = [int]($ni | Select-Object -First 1) }
        if ($headers -gt 0) { break }   # IBD has begun pulling headers
    }
    Check "daemon-autostarted" $daemonUp "blazecoind process present"
    Check "cookie-auth-rpc"    $rpcOk    "getblockchaininfo answered via .cookie"
    Check "ibd-began"          ($headers -gt 0) ("blocks={0} headers={1} ibd={2} peers={3}" -f $blocks,$headers,$ibd,$peers)

    # 7. WebView2 UI rendered => msedgewebview2 children of the wallet
    Start-Sleep -Seconds 5
    $wv = Get-Process msedgewebview2 -ErrorAction SilentlyContinue
    Check "webview2-rendered" ($null -ne $wv) ("{0} msedgewebview2 process(es)" -f (@($wv).Count))
    $wvPv = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -Name pv -ErrorAction SilentlyContinue).pv
    Check "webview2-runtime-present" ([bool]$wvPv) ("pv={0} (bootstrapper installs it if the image lacked it)" -f $(if($wvPv){$wvPv}else{'<absent>'}))
}

# Summary + machine-readable result
$fail = @($Checks | Where-Object { -not $_.ok })
Write-Host "`n=== RESULT: $(if ($fail.Count -eq 0) {'ALL PASS'} else {"$($fail.Count) FAILED"}) ($($Checks.Count) checks) ==="
$result = [pscustomobject]@{
    host      = $env:COMPUTERNAME
    setupExe  = $SetupExe
    cleanMachine = (-not (Test-Path $roaming))
    passed    = ($fail.Count -eq 0)
    failed    = $fail.Count
    checks    = $Checks
}
$result | ConvertTo-Json -Depth 5 | Out-File (Join-Path $ResultDir "smoke-result.json") -Encoding utf8
Write-Host ("wrote {0}" -f (Join-Path $ResultDir "smoke-result.json"))
if ($fail.Count -ne 0) { exit 1 }

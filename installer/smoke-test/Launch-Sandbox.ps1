<#
.SYNOPSIS
    Run the clean-machine smoke test inside Windows Sandbox (disposable, no reboot
    once the feature is enabled). Zero risk to the host: Sandbox is a throwaway VM.

    Windows Sandbox is the ideal vehicle -- a genuinely clean Windows image with
    NO legacy %APPDATA%\BlazecoinV2.0 and no dev tooling, so it exercises the
    fresh-install Local-datadir branch this dev box cannot.

    Prereq (one-time, needs admin + a reboot -- schedule a daemon maintenance window):
        Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
    Then run this (no admin needed):
        .\Launch-Sandbox.ps1

    It maps the installer folder in read-only + a writable results folder out, and
    auto-runs Run-SmokeTest.ps1 on logon. Read smoke-result.json in .\results when done.

.NOTE
    Modern Sandbox images ship Edge, so the WebView2 runtime is usually PRESENT ->
    this proves install + Local-datadir + daemon auto-start + cookie auth + IBD +
    UI render, but typically NOT the WebView2-ABSENT bootstrapper branch. For that,
    use a base Windows image without Edge/WebView2 (a stock Hyper-V eval VM).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$Here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$Results  = Join-Path $Here "results"
New-Item -ItemType Directory -Force -Path $Results | Out-Null

if (-not (Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue) -and -not (Test-Path "$env:WINDIR\System32\WindowsSandbox.exe")) {
    Write-Host "Windows Sandbox is not enabled on this machine." -ForegroundColor Yellow
    Write-Host "Enable it (admin + reboot, in a daemon maintenance window):" -ForegroundColor Yellow
    Write-Host "  Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All"
    exit 1
}

$setup = (Get-ChildItem (Join-Path (Split-Path $Here) "Output") -Filter "BlazecoinWalletV2-Setup-*.exe" -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1)
if (-not $setup) { throw "installer not found -- run installer\Build-Installer.ps1 first" }

# Sandbox sees the mapped host folders at C:\Users\WDAGUtilityAccount\Desktop\<name>
$logon = 'powershell -ExecutionPolicy Bypass -NoProfile -File ' +
         'C:\Users\WDAGUtilityAccount\Desktop\smoke\Run-SmokeTest.ps1 ' +
         '-SetupExe C:\Users\WDAGUtilityAccount\Desktop\installer\' + $setup.Name + ' ' +
         '-ResultDir C:\Users\WDAGUtilityAccount\Desktop\results'

$wsb = @"
<Configuration>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$(Split-Path $Here)\Output</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\installer</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$Here</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\smoke</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$Results</HostFolder><SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>$logon</Command></LogonCommand>
</Configuration>
"@

$wsbPath = Join-Path $Here "Blazecoin-SmokeTest.wsb"
$wsb | Out-File $wsbPath -Encoding utf8
Write-Host "Launching Windows Sandbox..." -ForegroundColor Green
Write-Host "  It will silently install, launch the wallet, start the daemon, and begin IBD."
Write-Host "  Watch the sandbox window; results land in: $Results\smoke-result.json"
Start-Process $wsbPath

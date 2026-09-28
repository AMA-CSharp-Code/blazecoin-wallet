<#
.SYNOPSIS
    Build the Blazecoin Wallet V2.0 Windows installer.

    Pipeline: publish the wallet (self-contained win-x64, via the checked-in
    publish profile) -> verify the daemon binaries + conf template -> fetch the
    WebView2 Evergreen bootstrapper (once) -> compile BlazecoinWalletV2.iss.

.PARAMETER SkipPublish
    Reuse the existing publish output instead of re-publishing.
.PARAMETER DaemonDir
    Directory containing blazecoind.exe + blazecoin-cli.exe to bundle.
    Default: the sibling Blazecoin_Wallet_V2_Core repo's src\ copies.

.NOTES
    Output: installer\Output\BlazecoinWalletV2-Setup-<version>.exe
    Requires Inno Setup 6 (winget install JRSoftware.InnoSetup).
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$DaemonDir = ""
)

$ErrorActionPreference = "Stop"
$InstallerDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $InstallerDir

if ($DaemonDir -eq "") { $DaemonDir = Join-Path (Split-Path -Parent $RepoRoot) "Blazecoin_Wallet_V2_Core\src" }
$ConfTemplate = Join-Path (Split-Path -Parent $RepoRoot) "Blazecoin_Wallet_V2_Core\share\examples\blazecoin.conf"
$PublishDir   = Join-Path $RepoRoot "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$Redist       = Join-Path $InstallerDir "redist"
$Bootstrapper = Join-Path $Redist "MicrosoftEdgeWebView2Setup.exe"

function Log([string]$m) { Write-Host ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $m) }
function Fail([string]$m) { Log "FATAL: $m"; exit 1 }

# 1. Publish the wallet
if ($SkipPublish) {
    Log "Publish: SKIPPED (-SkipPublish)"
} else {
    Log "Publishing wallet (self-contained win-x64)..."
    Push-Location $RepoRoot
    dotnet publish BlazecoinWallet.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release -p:PublishProfile=win-x64 --nologo -v:minimal
    $ec = $LASTEXITCODE
    Pop-Location
    if ($ec -ne 0) { Fail "dotnet publish failed ($ec)" }
}
if (-not (Test-Path (Join-Path $PublishDir "BlazecoinWallet.Maui.exe"))) { Fail "publish output missing: $PublishDir" }

# 2. Daemon binaries + conf template
foreach ($f in @("blazecoind.exe", "blazecoin-cli.exe")) {
    if (-not (Test-Path (Join-Path $DaemonDir $f))) { Fail "$f not found in $DaemonDir (build the daemon first)" }
}
if (-not (Test-Path $ConfTemplate)) { Fail "conf template not found: $ConfTemplate" }
Log "Daemon binaries: $DaemonDir"

# 3. WebView2 Evergreen bootstrapper (tiny stub; cached in redist\, gitignored)
if (-not (Test-Path $Bootstrapper)) {
    Log "Downloading WebView2 Evergreen bootstrapper..."
    New-Item -ItemType Directory -Force -Path $Redist | Out-Null
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $Bootstrapper
}
if ((Get-Item $Bootstrapper).Length -lt 500KB) { Fail "WebView2 bootstrapper looks wrong (too small)" }

# 4. Compile
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Fail "ISCC.exe not found - winget install JRSoftware.InnoSetup" }

Log "Compiling installer..."
& $iscc (Join-Path $InstallerDir "BlazecoinWalletV2.iss") "/DDaemonDir=$DaemonDir" "/DConfTemplate=$ConfTemplate" /Qp
if ($LASTEXITCODE -ne 0) { Fail "ISCC failed ($LASTEXITCODE)" }

$out = Get-ChildItem (Join-Path $InstallerDir "Output") -Filter "BlazecoinWalletV2-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Log "DONE: $($out.FullName) ($([math]::Round($out.Length / 1MB)) MB)"

<#
.SYNOPSIS
    Packs BlazecoinWallet.Core + BlazecoinWallet.Lite.Core as NuGet packages at the wallet's version and
    pushes them to the org's private GitHub Packages feed.

.DESCRIPTION
    Decided 2026-09-26 (ROADMAP §A): the Phase 4 repos (blazecoin-pay, blazecoin-iot tooling,
    blazecoin-governance) consume Lite.Core as a package, id = assembly = namespace, version = the wallet's
    ApplicationDisplayVersion. Lite.Core project-references Core, so both are packed and pushed together at
    the same version - a consumer restoring Lite.Core X.Y.Z resolves Core X.Y.Z from the same feed.

    Run it as part of a wallet release (after the version bump, before/with the installer build). Idempotent:
    --skip-duplicate means re-running for an already-published version is a no-op for that version.
    The feed source 'github' must be registered (dotnet nuget list source) - it is on the dev box.

.PARAMETER Version
    Override the version (default: read from BlazecoinWallet.Maui.csproj ApplicationDisplayVersion).
.PARAMETER NoPush
    Pack only (packages land in installer/Output/nuget/).
.PARAMETER Suffix
    Optional prerelease suffix, e.g. 'alpha.1' -> 2.0.8-alpha.1 (for a consumer that needs an unreleased change).

.EXAMPLE
    .\Publish-LiteCorePackages.ps1                # pack + push 2.0.8 (or whatever the csproj says)
    .\Publish-LiteCorePackages.ps1 -NoPush        # just build the .nupkg files
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$NoPush,
    [string]$Suffix,
    [string]$Source = 'github'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $PSScriptRoot 'Output\nuget'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $Version) {
    $csproj = Get-Content (Join-Path $repo 'BlazecoinWallet.Maui.csproj') -Raw
    if ($csproj -notmatch '<ApplicationDisplayVersion>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</ApplicationDisplayVersion>') {
        throw 'ApplicationDisplayVersion not found in BlazecoinWallet.Maui.csproj'
    }
    $Version = $Matches[1]
}
if ($Suffix) { $Version = "$Version-$Suffix" }
Write-Host "Packing BlazecoinWallet.Core + BlazecoinWallet.Lite.Core at $Version -> $out"

$projects = @(
    (Join-Path $repo 'BlazecoinWallet.Core\BlazecoinWallet.Core.csproj'),
    (Join-Path $repo 'BlazecoinWallet.Lite.Core\BlazecoinWallet.Lite.Core.csproj')
)
foreach ($p in $projects) {
    # Release, deterministic, version pinned; the ProjectReference Core -> becomes a package dependency
    # at the same version because both are packed with the same -p:Version.
    dotnet pack $p -c Release -o $out -p:Version=$Version -p:ContinuousIntegrationBuild=true --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $p" }
}

$pkgs = Get-ChildItem $out -Filter "*.$Version.nupkg" | Where-Object { $_.Name -match '^BlazecoinWallet\.(Lite\.)?Core\.' }
if ($pkgs.Count -ne 2) { throw "Expected 2 packages for $Version, found $($pkgs.Count): $($pkgs.Name -join ', ')" }
$pkgs | ForEach-Object { Write-Host ("  {0}  {1:N0} bytes" -f $_.Name, $_.Length) }

if ($NoPush) { Write-Host 'NoPush: done.'; return }

# Push Core first so Lite.Core's dependency exists when it lands. The gh CLI token (write:packages) is the
# API key; nothing is stored. --skip-duplicate keeps a re-run harmless.
$token = (& gh auth token 2>$null)
if (-not $token) { throw 'gh auth token returned nothing - run gh auth login (needs write:packages).' }
foreach ($name in 'BlazecoinWallet.Core', 'BlazecoinWallet.Lite.Core') {
    $pkg = $pkgs | Where-Object { $_.Name -eq "$name.$Version.nupkg" }
    Write-Host "Pushing $($pkg.Name) -> $Source"
    dotnet nuget push $pkg.FullName --source $Source --api-key $token --skip-duplicate
    if ($LASTEXITCODE -ne 0) { throw "push failed for $($pkg.Name)" }
}
Remove-Variable token
Write-Host "Published BlazecoinWallet.Core + BlazecoinWallet.Lite.Core $Version to '$Source'."

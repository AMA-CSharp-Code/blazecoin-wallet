<#
.SYNOPSIS
    Harvests the lite wallet's shipped header-anchor table from a synced mainnet daemon.

.DESCRIPTION
    Writes BlazecoinWallet.Lite.Core/Data/mainnet-header-anchors.bin: the block hash at every
    height that is a multiple of -Interval, from genesis up to (tip - 200) rounded down — 32 bytes
    per anchor, in the display (RPC) byte order, nothing else. The lite wallet proves a merkle
    proof's block is on the chain by linking it forward to the next anchor (HeaderAnchors.cs), so
    this table is trusted exactly like the shipped checkpoint: harvest it from your own synced
    node, and refresh it at every lite release so the newest coins sit under an anchor.

    The RPC cookie is read from the daemon's datadir and never printed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/Build-HeaderAnchors.ps1
    powershell -ExecutionPolicy Bypass -File Tools/Build-HeaderAnchors.ps1 -RpcPort 55413 -CookiePath "$env:APPDATA\BlazecoinV2.0\.cookie"
#>
param(
    [int]$RpcPort = 55413,
    [string]$CookiePath = "$env:APPDATA\BlazecoinV2.0\.cookie",
    [int]$Interval = 1000,
    [string]$OutFile = (Join-Path $PSScriptRoot '..\BlazecoinWallet.Lite.Core\Data\mainnet-header-anchors.bin')
)

$ErrorActionPreference = 'Stop'
$GenesisHash = '5d871c1b6ea542c2bb8a3b3ac70028a591bbf81369e90c2446c1a2bbfb89459b' # src/kernel/chainparams.cpp, main

if (-not (Test-Path $CookiePath)) { throw "No RPC cookie at $CookiePath - is the daemon running?" }
$cookie = (Get-Content -Raw $CookiePath).Trim()
$auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($cookie))
$uri = "http://127.0.0.1:$RpcPort/"

function Invoke-Rpc([object]$Body) {
    $json = ConvertTo-Json -InputObject $Body -Depth 5 -Compress
    Invoke-RestMethod -Uri $uri -Method Post -Body $json -ContentType 'application/json' -Headers @{ Authorization = $auth }
}

$info = (Invoke-Rpc @{ id = 1; method = 'getblockchaininfo'; params = @() }).result
if ($info.chain -ne 'main') { throw "Daemon is on '$($info.chain)', not mainnet." }
if ($info.initialblockdownload) { throw 'Daemon is still in initial block download.' }

$maxHeight = [long]([math]::Floor(($info.blocks - 200) / $Interval) * $Interval)
$heights = for ($h = 0L; $h -le $maxHeight; $h += $Interval) { $h }
Write-Host "Tip $($info.blocks); harvesting $($heights.Count) anchors (every $Interval to $maxHeight)."

$hashes = New-Object 'System.Collections.Generic.List[string]'
for ($i = 0; $i -lt $heights.Count; $i += 500) {
    $chunk = $heights[$i..([math]::Min($i + 499, $heights.Count - 1))]
    $batch = @($chunk | ForEach-Object { @{ id = $_; method = 'getblockhash'; params = @($_) } })
    # PS 5.1 returns a JSON array as ONE object; assigning first then piping unrolls it.
    $raw = Invoke-Rpc $batch
    $replies = $raw | Sort-Object { [long]$_.id }
    foreach ($r in $replies) {
        if ($null -ne $r.error) { throw "getblockhash $($r.id) failed: $($r.error.message)" }
        $hashes.Add([string]$r.result)
    }
}
if ($hashes.Count -ne $heights.Count) { throw "Expected $($heights.Count) hashes, got $($hashes.Count)." }
if ($hashes[0] -ne $GenesisHash) { throw "Height 0 is $($hashes[0]), not the Blazecoin genesis block." }

$bytes = New-Object byte[] ($hashes.Count * 32)
for ($i = 0; $i -lt $hashes.Count; $i++) {
    for ($b = 0; $b -lt 32; $b++) { $bytes[$i * 32 + $b] = [Convert]::ToByte($hashes[$i].Substring($b * 2, 2), 16) }
}
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($OutFile), $bytes)
Write-Host "Wrote $($bytes.Length) bytes to $OutFile (last anchor $maxHeight = $($hashes[$hashes.Count - 1]))."
Write-Host "Update HeaderAnchors.MainnetInterval if -Interval changed."

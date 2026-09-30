<#
.SYNOPSIS
    Provision a throwaway Hyper-V VM from a Windows install ISO, unattended, and
    run the Blazecoin Wallet V2.0 clean-machine smoke test inside it.

    ELEVATED (Hyper-V admin). One UAC prompt for the whole run. No host reboot.

    Flow:
      1. Build a tiny secondary ISO carrying autounattend.xml (IMAPI2FS COM; no ADK).
      2. Create a Gen2 VM (blank dynamic VHDX) on the Default Switch (NAT internet
         for IBD), attach the Windows ISO + the autounattend ISO.
      3. Start it; nudge past the "press any key to boot from CD" prompt during the
         FIRST boot window only (never on later setup reboots).
      4. Wait for PowerShell Direct (guest booted + auto-logged-on as 'smoke').
      5. Copy the setup exe + Run-SmokeTest.ps1 into the guest.
      6. Run the smoke test in the INTERACTIVE session (scheduled task as 'smoke')
         so the WebView2 UI actually renders; poll for smoke-result.json.
      7. Copy the result back to the host; tear the VM down (unless -KeepVM).

    The guest is disposable and network-isolated from host state except the Default
    Switch NAT. Nothing here touches the 3 live mainnet daemons.

.PARAMETER IsoPath      Windows install ISO (Win10 build 19041+ or Win11).
.PARAMETER SetupExe     BlazecoinWalletV2-Setup-*.exe to test.
.PARAMETER SmokeScript  Run-SmokeTest.ps1 (defaults to ..\Run-SmokeTest.ps1).
.PARAMETER Unattend     autounattend.xml (defaults to .\autounattend.xml).
.PARAMETER WorkDir      Scratch dir for the VHDX + autounattend ISO + result.
.PARAMETER KeepVM       Leave the VM running for inspection instead of deleting it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$IsoPath,
    [Parameter(Mandatory)][string]$SetupExe,
    [string]$SmokeScript = "",
    [string]$Unattend    = "",
    [string]$WorkDir     = "F:\BlazecoinSmokeVM",
    [string]$VMName      = "BlazecoinSmoke",
    [int64] $MemoryBytes = 6GB,
    [int]   $Cpus        = 4,
    [int]   $ReadyTimeoutMin   = 60,
    [int]   $SmokeTimeoutMin   = 30,
    [switch]$KeepVM
)
$ErrorActionPreference = "Stop"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($SmokeScript -eq "") { $SmokeScript = Join-Path (Split-Path $Here) "Run-SmokeTest.ps1" }
if ($Unattend    -eq "") { $Unattend    = Join-Path $Here "autounattend.xml" }

# Known throwaway guest credential (matches autounattend.xml). Not a secret — the
# VM is disposable, network-isolated behind the Default Switch NAT, and deleted at end.
$GuestUser = "smoke"
$GuestPass = "Blz-Smoke!2026"

function Log([string]$m) { Write-Host ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $m) }
function Die([string]$m) { Log "FATAL: $m"; exit 1 }

# --- preconditions ---
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Die "must run elevated (Hyper-V admin)."
}
foreach ($p in @($IsoPath,$SetupExe,$SmokeScript,$Unattend)) { if (-not (Test-Path $p)) { Die "not found: $p" } }
if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) { Die "Hyper-V PowerShell module not available." }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
# Transcript so the (detached, elevated) run can be monitored from the parent shell.
try { Start-Transcript -Path (Join-Path $WorkDir "provision.log") -Force | Out-Null } catch {}

# --- IMAPI2FS: build the autounattend ISO (no ADK/oscdimg needed) ---
# Uses the canonical New-IsoFile pattern: stage the xml into a temp dir, AddTree
# it to the file-system image, then persist the result IStream via a compiled
# helper (IStream.Read block loop).
if (-not ('BlzISOFile' -as [type])) {
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.CompilerOptions = '/unsafe'
    $cp.GenerateInMemory = $true
    Add-Type -CompilerParameters $cp -TypeDefinition @'
public class BlzISOFile {
    public unsafe static void Create(string path, object stream, int blockSize, int totalBlocks) {
        int bytes = 0;
        byte[] buf = new byte[blockSize];
        var ptr = (System.IntPtr)(&bytes);
        var o = System.IO.File.OpenWrite(path);
        var i = stream as System.Runtime.InteropServices.ComTypes.IStream;
        if (o != null) {
            while (totalBlocks-- > 0) { i.Read(buf, blockSize, ptr); o.Write(buf, 0, bytes); }
            o.Flush(); o.Close();
        }
    }
}
'@
}

function New-AutounattendIso([string]$XmlPath, [string]$OutIso) {
    $staging = Join-Path $env:TEMP ("unattend_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Copy-Item $XmlPath (Join-Path $staging "autounattend.xml") -Force
    try {
        $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
        $fsi.FileSystemsToCreate = 3            # ISO9660 + Joliet
        $fsi.VolumeName = "UNATTEND"
        $fsi.Root.AddTree($staging, $false)     # $false => contents at root, not the dir itself
        $res = $fsi.CreateResultImage()
        if (Test-Path $OutIso) { Remove-Item $OutIso -Force }
        [BlzISOFile]::Create($OutIso, $res.ImageStream, $res.BlockSize, $res.TotalBlocks)
    } finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- inspect the Windows ISO: pick an edition + its generic install key ---
# Generic (non-activating) install keys let unattended Setup proceed without a prompt.
$GenericKeys = @{
    'Windows 10 Pro'                       = 'VK7JG-NPHTM-C97JM-9MPGT-3V66T'
    'Windows 10 Home'                      = 'YTMG3-N6DKC-DKB77-7M9GH-8HVX7'
    'Windows 10 Education'                 = 'NW6C2-QMPVW-D7KKK-3GKT6-VCFB2'
    'Windows 10 Enterprise'                = 'NPPR9-FWDCX-D2C8J-H872K-2YT43'
    'Windows 10 Enterprise Evaluation'     = 'NPPR9-FWDCX-D2C8J-H872K-2YT43'
    'Windows 10 Pro Education'             = '8PTT6-RNW4C-6V7J2-C2D3X-MHBPB'
}
# Prefer the Enterprise Evaluation edition first (that is what the auto-downloaded
# eval ISO ships). Eval media is effectively single-edition, but keep the fallbacks.
$PreferOrder = @('Windows 10 Enterprise Evaluation','Windows 10 Pro','Windows 10 Enterprise','Windows 10 Education','Windows 10 Pro Education','Windows 10 Home')

Log "inspecting ISO editions..."
$mount = Mount-DiskImage -ImagePath $IsoPath -PassThru
$drv = ($mount | Get-Volume).DriveLetter
$srcDir = (($drv.ToString()) + ':\sources')
$wim = @((Join-Path $srcDir 'install.wim'), (Join-Path $srcDir 'install.esd')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $wim) { Dismount-DiskImage -ImagePath $IsoPath | Out-Null; Die 'no install.wim or install.esd under the ISO sources folder' }
$editions = Get-WindowsImage -ImagePath $wim | Select-Object ImageName, ImageIndex
Log ('editions: ' + (($editions | ForEach-Object { $_.ImageName }) -join ' | '))
$chosen = $null
foreach ($p in $PreferOrder) { $chosen = $editions | Where-Object { $_.ImageName -eq $p } | Select-Object -First 1; if ($chosen) { break } }
if (-not $chosen) { $chosen = $editions | Select-Object -First 1 }
$imageName = $chosen.ImageName
$prodKey   = if ($GenericKeys.ContainsKey($imageName)) { $GenericKeys[$imageName] } else { $GenericKeys['Windows 10 Pro'] }
# Evaluation images cannot be selected by product key: supplying ANY key makes
# Setup filter the image list to empty ("No images are available") and the
# unattended install stalls at the OS-selection page. Eval media needs no key,
# so omit the whole <ProductKey> element for eval editions.
$keyXml = if ($imageName -match 'Evaluation') { '' } else { "<ProductKey><Key>$prodKey</Key></ProductKey>" }
# Confirm the ISO build satisfies the wallet TFM floor (10.0.19041).
$img = Get-WindowsImage -ImagePath $wim -Index $chosen.ImageIndex
$build = $img.Version
Log ("chosen edition: '{0}' (index {1}), build {2}, key {3}" -f $imageName, $chosen.ImageIndex, $build, $(if ($keyXml -eq '') { '(omitted - eval media)' } else { $prodKey }))
Dismount-DiskImage -ImagePath $IsoPath | Out-Null
try { if (([version]$build).Build -lt 19041) { Log ('WARNING: build ' + $build + ' is below 19041 (wallet TFM floor); the wallet may not run.') } } catch {}

# --- materialise the answer file with the edition + key substituted ---
$Answer = Join-Path $WorkDir "autounattend.resolved.xml"
(Get-Content $Unattend -Raw).Replace('@@IMAGE_NAME@@', $imageName).Replace('@@PRODUCT_KEY_XML@@', $keyXml) |
    Out-File $Answer -Encoding utf8

# --- clean any prior VM of the same name (must happen BEFORE the autounattend
# ISO build: a leftover VM holds autounattend.iso open via its DVD drive) ---
$old = Get-VM -Name $VMName -ErrorAction SilentlyContinue
if ($old) { Log "removing existing VM $VMName"; if ($old.State -ne 'Off') { Stop-VM $VMName -TurnOff -Force }; Remove-VM $VMName -Force }

$UnattendIso = Join-Path $WorkDir "autounattend.iso"
Log "Building autounattend ISO -> $UnattendIso"
try { New-AutounattendIso -XmlPath $Answer -OutIso $UnattendIso }
catch { Die "autounattend ISO build failed: $($_.Exception.Message)" }

# --- pick a switch with internet (Default Switch = NAT) ---
$sw = Get-VMSwitch -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'Default Switch' } | Select-Object -First 1
if (-not $sw) { $sw = Get-VMSwitch | Where-Object { $_.SwitchType -eq 'External' } | Select-Object -First 1 }
if (-not $sw) { Die "no Default Switch or External switch for guest internet (IBD needs it)." }
Log "switch: $($sw.Name)"

# --- create the VM ---
$vhdx = Join-Path $WorkDir "$VMName.vhdx"
if (Test-Path $vhdx) { Remove-Item $vhdx -Force }
Log "creating Gen2 VM ($([math]::Round($MemoryBytes/1GB))GB RAM, $Cpus vCPU, 64GB dynamic VHDX)"
New-VHD -Path $vhdx -SizeBytes 64GB -Dynamic | Out-Null
New-VM -Name $VMName -Generation 2 -MemoryStartupBytes $MemoryBytes -VHDPath $vhdx -SwitchName $sw.Name | Out-Null
Set-VM -Name $VMName -ProcessorCount $Cpus -AutomaticCheckpointsEnabled $false
$dvdWin = Add-VMDvdDrive -VMName $VMName -Path $IsoPath -Passthru
Add-VMDvdDrive -VMName $VMName -Path $UnattendIso | Out-Null
# Gen2: boot the Windows install DVD first
Set-VMFirmware -VMName $VMName -FirstBootDevice $dvdWin
# Win10/11 Gen2 install works with Secure Boot on (Microsoft template).
Set-VMFirmware -VMName $VMName -EnableSecureBoot On -SecureBootTemplate 'MicrosoftWindows'

# --- start + nudge past "press any key to boot from CD" during first boot only ---
Log "starting VM"
Start-VM -Name $VMName
try {
    $vmWmi = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$VMName'"
    $kb = Get-CimAssociatedInstance -InputObject $vmWmi -ResultClassName Msvm_Keyboard
    $svc = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemManagementService
    # The "press any key" prompt appears within ~2-5s of firmware; keep the Enter
    # window SHORT. Win11 media reaches the setup UI in ~10-15s, where a stray
    # Enter lands on the focused Cancel button and opens "Are you sure you want
    # to quit?" — a modal that silently stalls the unattended install at 0%
    # (and a further Enter could press Yes). Run-7 evidence (2026-07-12): the
    # modal can appear AFTER a short fixed chaser window, and **ESC does NOT
    # dismiss it on Win11 26200 media — the 'N' (No) accelerator does** (proven
    # live: 3x ESC left the modal up; one 'N' resumed the install). So: fewer
    # Enters (3 over 6s covers the 2-5s prompt), then chase 'N'+ESC pairs every
    # 5s out to t≈90s. 'N' is inert on the boot prompt and on the unattended
    # progress page (no text fields there); ESC kept for any other dialog type.
    Log "nudging boot prompt (Enter x3 during first 6s, then N+ESC chasers to ~90s to clear any quit dialog)"
    for ($i=0; $i -lt 3; $i++) {
        Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = [uint32]0x0D } -ErrorAction SilentlyContinue | Out-Null
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 4
    for ($i=0; $i -lt 16; $i++) {
        Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = [uint32]0x4E } -ErrorAction SilentlyContinue | Out-Null
        Start-Sleep -Milliseconds 500
        Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = [uint32]0x1B } -ErrorAction SilentlyContinue | Out-Null
        Start-Sleep -Seconds 5
    }
} catch { Log "keyboard nudge unavailable ($($_.Exception.Message)); relying on boot-order fallthrough" }

# --- wait for PowerShell Direct (guest installed + auto-logged-on) ---
$cred = New-Object PSCredential($GuestUser, (ConvertTo-SecureString $GuestPass -AsPlainText -Force))
Log "waiting for guest (PowerShell Direct); unattended Windows install takes ~15-30 min..."
$deadline = (Get-Date).AddMinutes($ReadyTimeoutMin)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 20
    try {
        $n = Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop
        if ($n) { $ready = $true; Log "guest up: $n"; break }
    } catch { }
}
if (-not $ready) { Die "guest never became reachable via PowerShell Direct within $ReadyTimeoutMin min." }

# --- inject the installer + smoke script, and launch it — REBOOT-RESILIENT ---
# Run-7 evidence (2026-07-12): PowerShell Direct becomes reachable during an
# EARLIER unattend logon pass, and the guest then does its final reboot
# underneath us — the broken $sess turned the next Invoke-Command into a
# terminating error ($ErrorActionPreference=Stop) that killed the whole script
# silently at "launching smoke test". Everything in this phase is idempotent
# (mkdir -Force / Copy -Force / Register -Force / Start), so on any remoting
# failure we rebuild the session and redo the phase from the top.
function New-GuestSession {
    foreach ($try in 1..30) {
        try { return New-PSSession -VMName $VMName -Credential $cred -ErrorAction Stop }
        catch { Start-Sleep -Seconds 10 }
    }
    Die "could not (re)establish a PowerShell Direct session to $VMName."
}
$sess = $null
$phaseDone = $false
foreach ($attempt in 1..5) {
    try {
        if ($sess) { Remove-PSSession $sess -ErrorAction SilentlyContinue }
        $sess = New-GuestSession
        Log "copying setup exe + smoke script into guest C:\smoke (attempt $attempt)"
        Invoke-Command -Session $sess -ScriptBlock { New-Item -ItemType Directory -Force -Path C:\smoke, C:\smoke\results | Out-Null }
        Copy-Item -ToSession $sess -Path $SetupExe    -Destination "C:\smoke\BlazecoinWalletV2-Setup.exe" -Force
        Copy-Item -ToSession $sess -Path $SmokeScript -Destination "C:\smoke\Run-SmokeTest.ps1" -Force

        # Run the smoke test in the INTERACTIVE session so WebView2 renders.
        # PowerShell Direct is reachable BEFORE the auto-logon desktop session is
        # up; starting an Interactive-logon-type task with no interactive session
        # fails silently (task stays Ready, LastTaskResult 267011 "has not yet
        # run"). So wait for the smoke user's explorer.exe first, then start and
        # VERIFY it launched.
        Log "waiting for the smoke user's interactive desktop (explorer.exe)..."
        $edeadline = (Get-Date).AddMinutes(5)
        $hasDesktop = $false
        while ((Get-Date) -lt $edeadline) {
            $hasDesktop = Invoke-Command -Session $sess -ScriptBlock {
                [bool](Get-Process explorer -ErrorAction SilentlyContinue)
            }
            if ($hasDesktop) { break }
            Start-Sleep -Seconds 10
        }
        if (-not $hasDesktop) { Die "smoke user's interactive desktop never appeared within 5 min." }
        Log "launching smoke test in the guest interactive session"
        $started = Invoke-Command -Session $sess -ScriptBlock {
            $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-ExecutionPolicy Bypass -NoProfile -File C:\smoke\Run-SmokeTest.ps1 -SetupExe C:\smoke\BlazecoinWalletV2-Setup.exe -ResultDir C:\smoke\results'
            $p = New-ScheduledTaskPrincipal -UserId 'smoke' -LogonType Interactive -RunLevel Highest
            Register-ScheduledTask -TaskName 'BlzSmoke' -Action $a -Principal $p -Force | Out-Null
            # Run-7 evidence (2026-07-12): explorer.exe can be visible ~1-2 min BEFORE
            # the smoke user's interactive logon is actually complete, and a
            # Start-ScheduledTask issued into that gap silently does nothing (task
            # stays Ready / "has not yet run"). 6 tries over ~90s expired just before
            # the real logon; the run needed a manual task start. Retry for 5 minutes.
            foreach ($try in 1..20) {
                Start-ScheduledTask -TaskName 'BlzSmoke'
                Start-Sleep -Seconds 5
                $i = Get-ScheduledTaskInfo -TaskName 'BlzSmoke'
                if ((Get-ScheduledTask -TaskName 'BlzSmoke').State -eq 'Running' -or $i.LastRunTime.Year -gt 2000) { return $true }
                Start-Sleep -Seconds 10
            }
            return $false
        }
        if (-not $started) { Die "BlzSmoke task never entered Running state (LastTaskResult stays 'not yet run')." }
        $phaseDone = $true
        break
    } catch {
        Log "guest session dropped ($(($_.Exception.Message -split "`n")[0])); reconnecting..."
        Start-Sleep -Seconds 15
    }
}
if (-not $phaseDone) { Die "inject/launch phase failed after 5 session rebuilds." }

# --- poll for the result ---
Log "waiting for smoke-result.json (IBD warm-up can take a few min)..."
$sdeadline = (Get-Date).AddMinutes($SmokeTimeoutMin)
$resultJson = $null
while ((Get-Date) -lt $sdeadline) {
    Start-Sleep -Seconds 15
    try {
        $resultJson = Invoke-Command -Session $sess -ScriptBlock {
            if (Test-Path C:\smoke\results\smoke-result.json) { Get-Content C:\smoke\results\smoke-result.json -Raw } else { $null }
        }
    } catch {
        Log "guest session dropped during result poll; reconnecting..."
        Remove-PSSession $sess -ErrorAction SilentlyContinue
        $sess = New-GuestSession
    }
    if ($resultJson) { break }
}
if (-not $resultJson) { Log "WARNING: no result within $SmokeTimeoutMin min (guest may still be in IBD)." }
else {
    $outFile = Join-Path $WorkDir "smoke-result.json"
    $resultJson | Out-File $outFile -Encoding utf8
    # also pull the install log + transcript for diagnosis
    Invoke-Command -Session $sess -ScriptBlock { if (Test-Path C:\smoke\results\install.log) { Get-Content C:\smoke\results\install.log -Raw } } |
        Out-File (Join-Path $WorkDir "install.log") -Encoding utf8
    Log "RESULT written: $outFile"
    Write-Host "`n---- smoke-result.json ----`n$resultJson`n---------------------------"
}

Remove-PSSession $sess
if ($KeepVM) { Log "-KeepVM: leaving $VMName running for inspection." }
else {
    Log "tearing down VM $VMName"
    Stop-VM $VMName -TurnOff -Force -ErrorAction SilentlyContinue
    Remove-VM $VMName -Force -ErrorAction SilentlyContinue
    Remove-Item $vhdx -Force -ErrorAction SilentlyContinue
    Remove-Item $UnattendIso -Force -ErrorAction SilentlyContinue
}
Log "done."

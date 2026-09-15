; Blazecoin Wallet V2.0 — Windows installer (Inno Setup 6)
;
; Per-user install (no admin/UAC): app -> {localappdata}\Programs\Blazecoin Wallet V2.0
;
; What it ships:
;   * the self-contained wallet publish folder (CoreCLR + WindowsAppRuntime bundled)
;   * blazecoind.exe + blazecoin-cli.exe NEXT TO the wallet exe — the wallet's
;     DaemonProcessManager auto-discovers and launches the daemon from there
;   * the default blazecoin.conf laid into the DAEMON's datadir (legacy
;     %APPDATA%\BlazecoinV2.0 if it exists, else %LOCALAPPDATA%\BlazecoinV2.0 —
;     mirroring blazecoind's GetDefaultDataDir), never overwriting an existing conf
;   * the WebView2 Evergreen bootstrapper, run only when the runtime is missing
;
; Build:  installer\Build-Installer.ps1   (publishes the wallet, fetches the
;         WebView2 bootstrapper, resolves the daemon binaries, runs ISCC)
;
; Path defines (overridable: ISCC /DWalletPublishDir=... etc.):
#ifndef WalletPublishDir
  #define WalletPublishDir "..\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif
#ifndef DaemonDir
  #define DaemonDir "..\..\Blazecoin_Wallet_V2_Core\src"
#endif
#ifndef ConfTemplate
  #define ConfTemplate "..\..\Blazecoin_Wallet_V2_Core\share\examples\blazecoin.conf"
#endif
#ifndef WebView2Bootstrapper
  #define WebView2Bootstrapper "redist\MicrosoftEdgeWebView2Setup.exe"
#endif
#ifndef AppVersion
  #define AppVersion "2.0.7"
#endif

[Setup]
AppId={{E7E39BF9-F5CD-4A56-A993-ECDB6889BB00}
AppName=Blazecoin Wallet V2.0
AppVersion={#AppVersion}
AppPublisher=Blazecoin
AppPublisherURL=https://github.com/AMA-CSharp-Code
DefaultDirName={autopf}\Blazecoin Wallet V2.0
DefaultGroupName=Blazecoin
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=BlazecoinWalletV2-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\BlazecoinWallet.Maui.exe
SetupIconFile=blazecoin.ico
; The wallet datadir (chain + wallet.dat) is USER DATA — never touched on uninstall.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Wallet (self-contained publish output)
Source: "{#WalletPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; Daemon + cli beside the wallet exe (auto-discovered by DaemonProcessManager)
Source: "{#DaemonDir}\blazecoind.exe";    DestDir: "{app}"; Flags: ignoreversion
Source: "{#DaemonDir}\blazecoin-cli.exe"; DestDir: "{app}"; Flags: ignoreversion
; Default node config into the daemon's datadir — never overwrite a user's conf,
; and never remove it on uninstall (it may hold user-added settings).
Source: "{#ConfTemplate}"; DestDir: "{code:GetBlazecoinDataDir}"; DestName: "blazecoin.conf"; Flags: onlyifdoesntexist uninsneveruninstall
; WebView2 Evergreen bootstrapper — extracted to temp, run only when needed
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\Blazecoin Wallet V2.0"; Filename: "{app}\BlazecoinWallet.Maui.exe"
Name: "{autodesktop}\Blazecoin Wallet V2.0"; Filename: "{app}\BlazecoinWallet.Maui.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; \
  StatusMsg: "Installing the Microsoft WebView2 runtime (required by the wallet UI)..."; \
  Check: WebView2Missing; Flags: waituntilterminated
Filename: "{app}\BlazecoinWallet.Maui.exe"; Description: "{cm:LaunchProgram,Blazecoin Wallet V2.0}"; \
  Flags: nowait postinstall skipifsilent

[Code]
// Mirror of blazecoind's GetDefaultDataDir() (args.cpp): the legacy Roaming datadir
// wins if it exists; fresh installs live under Local. Must match the daemon AND the
// wallet's RpcCredentials.DefaultDataDir() or the conf lands where nothing reads it.
function GetBlazecoinDataDir(Param: string): string;
var
  Legacy: string;
begin
  Legacy := ExpandConstant('{userappdata}') + '\BlazecoinV2.0';
  if DirExists(Legacy) then
    Result := Legacy
  else
    Result := ExpandConstant('{localappdata}') + '\BlazecoinV2.0';
end;

// WebView2 Evergreen runtime detection (per Microsoft docs): the 'pv' value under
// the EdgeUpdate client key, machine-wide (WOW6432Node on x64) or per-user.
function WebView2Missing: Boolean;
var
  Ver: string;
begin
  Result := True;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0') then
    Result := False
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Ver) and (Ver <> '') and (Ver <> '0.0.0.0') then
    Result := False;
end;

// ---- .NET 10 / CET OS-build preflight (smoke-test FINDINGS, 2026-07-07/12) ----
// On a Windows 10 kernel not serviced since ~2022, .NET 10 fail-fasts at startup
// (0x80131506, "Your Windows doesn't fully support CET") on CET-capable CPUs
// (Intel 11th-gen+ / AMD Zen 3+): user shadow stacks get enabled for the process
// but the kernel lacks the special user-mode APC support CoreCLR requires with
// them. CoreCLR's own probe for that support is the kernel32 QueueUserAPC2
// export, so test the exact capability instead of guessing servicing builds.
// CETCompat=false does NOT avoid the crash, and the per-exe IFEO mitigation
// needs admin (this is a per-user installer) — so a preflight warning is the
// right installer-side move. Win11 (build 22000+) always has the API.
function GetModuleHandleW(lpModuleName: string): LongWord;
  external 'GetModuleHandleW@kernel32.dll stdcall';
function GetProcAddress(hModule: LongWord; lpProcName: AnsiString): LongWord;
  external 'GetProcAddress@kernel32.dll stdcall';

function InitializeSetup: Boolean;
var
  V: TWindowsVersion;
begin
  Result := True;
  GetWindowsVersionEx(V);
  if V.Build >= 22000 then Exit;  // Windows 11+
  if GetProcAddress(GetModuleHandleW('kernel32.dll'), 'QueueUserAPC2') = 0 then
  begin
    Log('OS preflight: QueueUserAPC2 missing (Win10 build ' + IntToStr(V.Build) +
        ') - .NET 10 CET fail-fast expected on CET-capable CPUs.');
    Result := SuppressibleMsgBox(
      'This Windows 10 installation is missing an update that the wallet''s .NET 10 runtime requires.' + #13#10#13#10 +
      'On modern CPUs (Intel 11th-gen or newer, AMD Ryzen 5000 or newer) the wallet will crash the moment it starts ' +
      '("Your Windows doesn''t fully support CET", error 0x80131506).' + #13#10#13#10 +
      'Please install all available Windows updates (or upgrade to Windows 11) and then run this installer again.' + #13#10#13#10 +
      'Install anyway?', mbError, MB_YESNO, IDYES) = IDYES;
  end
  else if V.Build < 19045 then
  begin
    Log('OS preflight: Win10 build ' + IntToStr(V.Build) + ' is below 22H2.');
    Result := SuppressibleMsgBox(
      'This PC is running a Windows 10 version older than 22H2, which is below what the wallet is tested on. ' +
      'Updating Windows to 22H2 (or Windows 11) is recommended.' + #13#10#13#10 +
      'Install anyway?', mbInformation, MB_YESNO, IDYES) = IDYES;
  end;
end;

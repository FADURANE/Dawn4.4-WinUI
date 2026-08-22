#define MyAppName "Dawn4.4 Control"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "FADURANE"
#define MyAppExeName "Dawn44.WinUI.exe"
#define SourceDir "..\Dawn44.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish-unpackaged"
#ifndef WinAppRuntimeDir
#define WinAppRuntimeDir "..\packages\microsoft.windowsappsdk.runtime\2.2.0\tools\MSIX\win10-x64"
#endif

[Setup]
AppId={{5285669B-2E9E-45E6-A60C-576F4267C768}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=no
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=Dawn44ControlSetup-{#MyAppVersion}-x64
SetupIconFile=..\Dawn44.WinUI\Assets\Dawn44Control.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763
UninstallDisplayIcon={app}\Assets\Dawn44Control.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The publish directory also holds Dawn44.Background.exe, which the build copies in beside the GUI:
; the two exes find each other by file name in a shared directory (ModeExecutable.Resolve), so they
; must stay side by side.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "{#WinAppRuntimeDir}\Microsoft.WindowsAppRuntime.Main.2.msix"; DestDir: "{tmp}\WinAppRuntime"; Flags: deleteafterinstall
Source: "{#WinAppRuntimeDir}\Microsoft.WindowsAppRuntime.Singleton.2.msix"; DestDir: "{tmp}\WinAppRuntime"; Flags: deleteafterinstall
Source: "{#WinAppRuntimeDir}\Microsoft.WindowsAppRuntime.DDLM.2.msix"; DestDir: "{tmp}\WinAppRuntime"; Flags: deleteafterinstall
Source: "{#WinAppRuntimeDir}\Microsoft.WindowsAppRuntime.2.msix"; DestDir: "{tmp}\WinAppRuntime"; Flags: deleteafterinstall

[InstallDelete]
Type: files; Name: "{app}\resources.pri"

[Registry]
; Declared only so uninstall removes the auto-start entry the app itself writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#MyAppName}"; Flags: dontcreatekey uninsdeletevalue

[UninstallRun]
; The app registers an elevated logon task when "Run as administrator" is enabled.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#MyAppName} Startup"" /F"; Flags: runhidden; RunOnceId: "DeleteStartupTask"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Dawn44Control.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Dawn44Control.ico"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$ErrorActionPreference = 'Stop'; $packages = @('Microsoft.WindowsAppRuntime.Main.2.msix','Microsoft.WindowsAppRuntime.Singleton.2.msix','Microsoft.WindowsAppRuntime.DDLM.2.msix','Microsoft.WindowsAppRuntime.2.msix'); foreach ($package in $packages) {{ $path = Join-Path '{tmp}\WinAppRuntime' $package; Add-AppxPackage -Path $path -ForceApplicationShutdown }}"""; StatusMsg: "Installing Windows App Runtime 2.2..."; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

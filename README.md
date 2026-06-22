# Dawn4.4 Control

[中文说明](README.zh-CN.md)

This repository was completed with Codex.

A lightweight Windows controller for the MOONDROP Dawn 4.4mm USB DAC.

The app talks to the Dawn 4.4mm through its HID interface, so it can adjust device settings while Windows keeps normal USB Audio playback on the standard audio driver.

## Features

- Volume control with global hotkeys and a Windows-style volume OSD.
- Gain, LED, and filter mode control.
- Tray icon with quick actions.
- Plug/unplug detection with status refresh and tray notification.
- Optional start with Windows, launching silently to the tray.
- English and Chinese UI.
- Custom background image with position and zoom adjustment.
- Inno Setup installer with selectable install directory.

## Requirements

- Windows 10 19041 or newer / Windows 11.
- .NET 8 Desktop Runtime.
- Windows App Runtime 2.x.

The installer bundles the Windows App Runtime packages used by this build. For development, install Visual Studio with WinUI/.NET desktop tooling.

## Build

Publish the unpackaged x64 build:

```powershell
$repo = $PWD.Path
$msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if (!(Test-Path $msbuild)) {
  $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
  $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
  $msbuild = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
}

& $msbuild `
  "$repo\Dawn44.WinUI\Dawn44.WinUI.csproj" `
  /restore `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:PublishProfile=unpackaged-x64 `
  /p:WindowsPackageType=None `
  /t:Publish `
  /m `
  /noLogo
```

Build the installer with Inno Setup 6:

```powershell
$repo = $PWD.Path
$iscc = 'ISCC.exe'
if (!(Get-Command $iscc -ErrorAction SilentlyContinue)) {
  $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
}
$winAppRuntimeDir = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk.runtime\2.2.0\tools\MSIX\win10-x64'

& $iscc /DWinAppRuntimeDir="$winAppRuntimeDir" "$repo\installer\Dawn44Control.iss"
```

See [PACKAGING.md](PACKAGING.md) for packaging notes.

## Configuration

Runtime settings are stored under:

```text
%LOCALAPPDATA%\Dawn4.4 Control\
```

The startup option writes an HKCU Run entry. When enabled, Windows starts the app with `--tray`, so it initializes silently into the tray instead of opening the main window.

## Acknowledgements

Thanks to these projects for protocol and implementation inspiration:

- [shaypower/DawnPro-GUI](https://github.com/shaypower/DawnPro-GUI)
- [Tommy-Geenexus/usb-dongle-control](https://github.com/Tommy-Geenexus/usb-dongle-control)

# Packaging

The preferred installer route is now an unpackaged, framework-dependent x64 build plus an Inno Setup installer.

Why:

- The installer can ask for the install directory.
- The app remains framework-dependent, so it does not bundle the .NET runtime.
- The installed `.exe` can be launched as administrator when games need elevated global hotkeys.
- MSIX packaging can remain available as a fallback, but it is no longer the preferred route.

## Build Publish Output

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
  /m
```

Output:

```text
Dawn44.WinUI\Dawn44.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish-unpackaged
```

Current publish output is about 36 MB on the development machine. It is not self-contained; most of the size is WinUI and Windows SDK projection files copied beside the app.

## Build Installer

Install Inno Setup 6, then run:

```powershell
$repo = $PWD.Path
$iscc = 'ISCC.exe'
if (!(Get-Command $iscc -ErrorAction SilentlyContinue)) {
  $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
}
$winAppRuntimeDir = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk.runtime\2.2.0\tools\MSIX\win10-x64'

& $iscc /DWinAppRuntimeDir="$winAppRuntimeDir" "$repo\installer\Dawn44Control.iss"
```

Output:

```text
installer\Output\Dawn44ControlSetup-1.0.15-x64.exe
```

The installer bundles the x64 Windows App Runtime 2.2 MSIX packages from the local NuGet cache and installs them before launching the app.

## Runtime Requirements

This build intentionally does not include the .NET runtime. The target machine needs:

- .NET 8 Desktop Runtime
- Windows App SDK runtime compatible with this project, installed by the Inno installer

On the development machine, Visual Studio already provides these. For distribution to another PC, install the runtimes first or add bootstrap checks to the installer later.

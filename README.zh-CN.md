# Dawn4.4 Control

[English](README.md)

该仓库由 Codex 完成。

一个轻量级的 Windows 控制工具，用于管理水月雨 MOONDROP Dawn 4.4mm USB DAC。

应用通过 Dawn 4.4mm 的 HID 接口与设备通信，因此可以在 Windows 继续使用标准 USB Audio 驱动播放音频的同时，调整设备设置。

## 功能

- 音量控制，支持全局快捷键和 Windows 风格音量 OSD。
- 增益、LED 和滤波模式控制。
- 托盘图标与快捷操作。
- 插拔检测、状态刷新和托盘通知。
- 可选开机启动，并静默启动到托盘。
- 英文和中文界面。
- 自定义背景图片，支持位置和缩放调整。
- Inno Setup 安装包，支持选择安装目录。

## 运行要求

- Windows 10 19041 或更新版本 / Windows 11。
- .NET 8 Desktop Runtime。
- Windows App Runtime 2.x。

安装包会包含当前构建使用的 Windows App Runtime 包。开发时请安装 Visual Studio，并启用 WinUI/.NET 桌面开发相关工具。

## 构建

发布 unpackaged x64 构建：

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

使用 Inno Setup 6 构建安装包：

```powershell
$repo = $PWD.Path
$iscc = 'ISCC.exe'
if (!(Get-Command $iscc -ErrorAction SilentlyContinue)) {
  $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
}
$winAppRuntimeDir = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk.runtime\2.2.0\tools\MSIX\win10-x64'

& $iscc /DWinAppRuntimeDir="$winAppRuntimeDir" "$repo\installer\Dawn44Control.iss"
```

更多打包说明见 [PACKAGING.md](PACKAGING.md)。

## 配置

运行时设置会保存在：

```text
%LOCALAPPDATA%\Dawn4.4 Control\
```

开机启动选项会写入 HKCU Run 注册表项。启用后，Windows 会使用 `--tray` 参数启动应用，使其静默初始化到托盘，而不是打开主窗口。

## 致谢

感谢以下项目在协议和实现方面提供的参考：

- [shaypower/DawnPro-GUI](https://github.com/shaypower/DawnPro-GUI)
- [Tommy-Geenexus/usb-dongle-control](https://github.com/Tommy-Geenexus/usb-dongle-control)

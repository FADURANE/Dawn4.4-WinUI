using Dawn44.Core;
using System;

namespace Dawn44.Background;

/// <summary>
/// The handful of strings the resident's tray menu needs, in the language the GUI is set to.
/// </summary>
/// <remarks>
/// Deliberately a copy of the four entries rather than a share of the GUI's table: that table lives in
/// <c>MainWindow.xaml.cs</c> next to a hundred window-only strings, and pulling it into Core to save
/// four lines would drag the whole thing — plus its language state — across the boundary.
/// </remarks>
internal static class Strings
{
    private static readonly bool Chinese =
        string.Equals(SettingsStore.GetLanguage(), "zh", StringComparison.OrdinalIgnoreCase);

    public static string Text(string key)
    {
        return key switch
        {
            "Tooltip" => Chinese ? "Dawn4.4 Control（后台模式）" : "Dawn4.4 Control (background mode)",
            "TrayModeTitle" => Chinese ? "运行模式" : "Run mode",
            "ModeGui" => Chinese ? "窗口模式" : "Window mode",
            "ModeBackground" => Chinese ? "后台模式（仅快捷键）" : "Background mode (shortcuts only)",
            "Exit" => Chinese ? "退出" : "Exit",
            _ => key,
        };
    }
}

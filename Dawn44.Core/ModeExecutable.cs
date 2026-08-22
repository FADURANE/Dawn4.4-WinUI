using System;
using System.Diagnostics;
using System.IO;

namespace Dawn44.Core;

/// <summary>
/// Maps an <see cref="AppMode"/> to the executable that serves it, and starts that executable.
/// </summary>
/// <remarks>
/// <para>
/// The installer puts both exes in the same directory, so one is found from the other by file name
/// rather than by a path stored in settings — a path would go stale the moment the user reinstalls
/// somewhere else, and there is no window in background mode to report that with.
/// </para>
/// <para>
/// <see cref="Resolve"/> returning <see langword="null"/> is a normal case, not an error: a
/// development run of the GUI, or an install from a version before the resident shipped, has no
/// <c>Dawn44.Background.exe</c> beside it. Callers fall back to the GUI rather than registering a
/// logon entry that points at nothing.
/// </para>
/// </remarks>
public static class ModeExecutable
{
    /// <summary>
    /// Start hidden in the tray. Meaningless for the resident, which has no window to hide, so it is
    /// deliberately not passed to it — see <see cref="ArgumentsFor"/>.
    /// </summary>
    public const string TraySwitch = "--tray";

    public const string GuiFileName = "Dawn44.WinUI.exe";

    public const string BackgroundFileName = "Dawn44.Background.exe";

    /// <summary>The command line a mode is auto-started or handed over with.</summary>
    public static string ArgumentsFor(AppMode mode)
    {
        return mode == AppMode.Background ? string.Empty : TraySwitch;
    }

    internal static string FileNameFor(AppMode mode)
    {
        return mode == AppMode.Background ? BackgroundFileName : GuiFileName;
    }

    /// <summary>
    /// Full path to the executable for <paramref name="mode"/>, or <see langword="null"/> when it is
    /// not installed next to the running one.
    /// </summary>
    public static string? Resolve(AppMode mode)
    {
        var directory = Path.GetDirectoryName(Elevation.GetExecutablePath());
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var path = Path.Combine(directory, FileNameFor(mode));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Starts the other mode's executable.
    /// </summary>
    /// <remarks>
    /// <c>UseShellExecute = false</c> on purpose: the child then inherits this process's token, so an
    /// elevated GUI hands over to an elevated resident with no consent prompt, and the arbitration
    /// that follows happens between two processes at the same integrity level. When this process is
    /// not elevated the resident elevates itself if the setting asks for it, which is one prompt the
    /// user initiated by choosing to switch.
    /// </remarks>
    public static bool TryStart(string exePath, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = false,
            };

            if (!string.IsNullOrEmpty(arguments))
            {
                startInfo.Arguments = arguments;
            }

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Could not start {exePath}.", ex);
            return false;
        }
    }
}

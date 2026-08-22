using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace Dawn44.Core;

/// <summary>
/// UAC helpers shared by both modes.
/// </summary>
/// <remarks>
/// Callers must release the single-instance claim <em>before</em> calling
/// <see cref="TryRestartAsAdmin"/>: the elevated child starts while this process is still alive and
/// would otherwise see itself as a duplicate and exit immediately.
/// </remarks>
public static class Elevation
{
    public const string ElevatedRelaunchSwitch = "--elevated-relaunch";

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string? GetExecutablePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
    }

    /// <summary>
    /// Relaunches this executable through the <c>runas</c> verb, forwarding
    /// <paramref name="launchArguments"/> plus <see cref="ElevatedRelaunchSwitch"/> so the new
    /// process does not try to elevate again.
    /// </summary>
    /// <param name="dropArguments">
    /// Arguments to strip on the way through — the GUI drops <c>--tray</c> when the window is
    /// already visible so the restarted instance does not hide itself.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the consent prompt was refused or never answered
    /// (ERROR_CANCELLED, 1223). Callers must keep running unelevated rather than exiting: an
    /// unanswered logon-time prompt would otherwise mean the app never starts at all.
    /// </returns>
    public static bool TryRestartAsAdmin(IEnumerable<string> launchArguments, params string[] dropArguments)
    {
        try
        {
            var exePath = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            var arguments = string.Join(
                ' ',
                launchArguments
                    .Where(argument => !dropArguments.Contains(argument, StringComparer.OrdinalIgnoreCase))
                    .Append(ElevatedRelaunchSwitch)
                    .Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
            });

            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}

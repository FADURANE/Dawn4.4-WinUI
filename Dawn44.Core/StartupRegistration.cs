using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Dawn44.Core;

/// <summary>
/// Registers or removes the logon auto-start entry.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms, picked by whether the app wants to start elevated:
/// </para>
/// <para>
/// The HKCU <c>Run</c> key is the normal path, but it cannot auto-start an elevated process —
/// Windows would have to raise a UAC consent prompt at logon, and when nothing answers it the
/// process exits without ever showing a window (v1.0.18 diagnosed exactly this). A logon scheduled
/// task at <c>HighestAvailable</c> with an <c>InteractiveToken</c> starts elevated with no prompt,
/// so that is used whenever Run-as-administrator is on.
/// </para>
/// <para>
/// Creating that task itself needs administrator rights. When the app is not yet elevated the
/// attempt fails and the Run key is used instead; the task gets created on the next elevated launch,
/// because startup registration is re-applied at startup.
/// </para>
/// <para>
/// The executable path and its arguments are parameters rather than being read from the current
/// process, because the GUI and the background executable register the same entry and switching
/// modes has to rewrite it to point at the other one.
/// </para>
/// </remarks>
public static class StartupRegistration
{
    public const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunRegistryName = "Dawn4.4 Control";
    public const string StartupTaskName = "Dawn4.4 Control Startup";

    private const int SchTasksTimeoutMs = 10000;

    /// <param name="enabled">Whether the app should start at logon.</param>
    /// <param name="exePath">Executable to register; resolved from the current process when null.</param>
    /// <param name="arguments">Command line to pass at logon, for example <c>--tray</c>.</param>
    /// <param name="preferScheduledTask">
    /// Set when Run-as-administrator is on, which makes the scheduled task the preferred mechanism.
    /// </param>
    public static bool Apply(bool enabled, string? exePath, string arguments, bool preferScheduledTask)
    {
        exePath ??= Elevation.GetExecutablePath();
        if (enabled && string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        if (enabled && preferScheduledTask && TryCreateStartupTask(exePath!, arguments))
        {
            SetRunRegistryValue(null);
            return true;
        }

        TryDeleteStartupTask();
        return SetRunRegistryValue(enabled ? $"\"{exePath}\" {arguments}" : null);
    }

    private static bool SetRunRegistryValue(string? command)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (command is null)
            {
                key.DeleteValue(RunRegistryName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(RunRegistryName, command);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateStartupTask(string exePath, string arguments)
    {
        var xmlPath = Path.Combine(
            Path.GetTempPath(),
            $"Dawn44ControlStartup-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks only accepts Unicode task definitions.
            File.WriteAllText(xmlPath, BuildStartupTaskXml(exePath, arguments), Encoding.Unicode);
            return RunSchTasks("/Create", "/TN", StartupTaskName, "/XML", xmlPath, "/F");
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch
            {
                // Temp file cleanup is best effort.
            }
        }
    }

    private static void TryDeleteStartupTask()
    {
        RunSchTasks("/Delete", "/TN", StartupTaskName, "/F");
    }

    private static bool RunSchTasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(SchTasksTimeoutMs) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Element order here is what Task Scheduler expects; it rejects the definition if the order is
    /// changed. The declaration must say UTF-16 and the file must actually be written as UTF-16.
    /// </summary>
    internal static string BuildStartupTaskXml(string exePath, string arguments)
    {
        string user;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            user = identity.Name;
        }

        var workingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;

        var head = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Dawn4.4 Control in the background at logon.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
            """;

        var tail = $"""
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exePath)}</Command>
                  <Arguments>{Escape(arguments)}</Arguments>
                  <WorkingDirectory>{Escape(workingDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        return head + Environment.NewLine + tail;

        static string Escape(string value) => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}

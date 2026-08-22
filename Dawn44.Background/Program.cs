using Dawn44.Core;
using System;
using System.Linq;
using System.Threading;

namespace Dawn44.Background;

/// <summary>
/// The headless resident: no console, no OSD and no window that can be activated. It exists to do
/// exactly one thing — move the DAC volume when the configured shortcuts are pressed.
/// </summary>
/// <remarks>
/// <para>
/// The shortcut bindings are read once at startup, which is correct rather than lazy: the two modes
/// are mutually exclusive, so the GUI cannot be open to change them while this process runs, and a
/// switch back picks up whatever was saved.
/// </para>
/// <para>
/// The one piece of UI is the tray icon in <see cref="TrayHost"/>, whose window is
/// <c>HWND_MESSAGE</c> and therefore cannot be activated or take focus.
/// </para>
/// </remarks>

internal static class Program
{
    private const string StopSwitch = "--stop";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // Nothing on screen would show this, so the log is the only place it can surface.
            DiagnosticLog.Write("Background mode stopped by an unhandled exception.", ex);
            ModeArbitration.Release();
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (HasSwitch(args, StopSwitch))
        {
            ModeArbitration.RequestExit();
            return 0;
        }

        // Elevation is settled before arbitration, deliberately: in-game shortcuts depend on it, and
        // resolving it first means the handover that follows happens between two processes at the
        // same integrity level.
        if (!HasSwitch(args, Elevation.ElevatedRelaunchSwitch)
            && SettingsStore.GetRunAsAdmin()
            && !Elevation.IsCurrentProcessElevated()
            && Elevation.TryRestartAsAdmin(args))
        {
            return 0;
        }

        switch (ModeArbitration.TakeOver(AppMode.Background))
        {
            case TakeOverResult.SameModeAlreadyRunning:
                // Another resident is already serving the shortcuts; leave it alone.
                return 0;

            case TakeOverResult.OtherModeDidNotRelease:
                // TakeOver has already logged why, and never force-kills.
                return 1;
        }

        var device = new DawnHidDevice();
        var volume = new VolumeController(device)
        {
            Faulted = ex => DiagnosticLog.Write("Volume worker fault.", ex),
            // Rare by design, and the only trace of a keypress that was deliberately not applied.
            ReadRejected = message => DiagnosticLog.Write(message),
            // The resident's own memory trace. Elevated and NativeAOT, it cannot be profiled from
            // outside, so it reports where its memory is after a burst instead.
            FootprintObserved = message => DiagnosticLog.Write(message),
        };
        volume.Start();

        var watcher = new HotkeyWatcher(
            SettingsStore.GetVolumeUpHotkey,
            SettingsStore.GetVolumeDownHotkey,
            () => volume.Change(1),
            () => volume.Change(-1))
        {
            CallbackFaulted = ex => DiagnosticLog.Write("Shortcut callback fault.", ex),
        };
        watcher.Start();

        DiagnosticLog.Write(
            $"Background mode resident: pid {Environment.ProcessId}, "
            + $"elevated={Elevation.IsCurrentProcessElevated()}.");
        DiagnosticLog.Write(ProcessFootprint.Describe());

        ClaimBackgroundModeAsync();

        // The tray icon, and with it the only window this process owns — a message-only one, which is
        // why it cannot pull a full-screen game to the desktop. Its message loop also carries the
        // handover poll, so parking the main thread in WaitForExitRequest is no longer needed.
        var exit = new TrayHost().Run();

        watcher.Stop();
        volume.Stop();
        ModeArbitration.Release();

        if (exit == TrayExit.SwitchToGui)
        {
            StartGui();
        }

        DiagnosticLog.Write($"Background mode exited: {exit}.");
        return 0;
    }

    /// <summary>
    /// Records background mode as the one running, and points the logon entry at this executable.
    /// </summary>
    /// <remarks>
    /// On a thread of its own because the elevated path creates a logon scheduled task by shelling out
    /// to <c>schtasks</c>, which can take a second. On the message loop that second would delay both
    /// the tray icon and the handover poll — long enough for a window launched in that gap to give up
    /// waiting for this process to release.
    /// </remarks>
    internal static void ClaimBackgroundModeAsync()
    {
        var thread = new Thread(() =>
        {
            try
            {
                StartupRegistration.ClaimCurrentMode(AppMode.Background);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Could not record background mode as the running one.", ex);
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
    }

    /// <summary>
    /// Hands the session to the window after the user picked window mode in the tray menu.
    /// </summary>
    /// <remarks>
    /// Started with no arguments rather than <c>--tray</c>: the user asked for the window, so it has to
    /// be visible. <c>Mode</c> and the logon entry are left to the GUI, which claims them itself on
    /// every launch path. <see cref="ModeArbitration.Release"/> has already run, so the window's own
    /// arbitration finds no owner and starts without waiting.
    /// </remarks>
    private static void StartGui()
    {
        var exePath = ModeExecutable.Resolve(AppMode.Gui);
        if (exePath is null)
        {
            DiagnosticLog.Write(
                $"Window mode was requested, but {ModeExecutable.GuiFileName} is not installed beside "
                + "this executable. Nothing is running now.");
            return;
        }

        ModeExecutable.TryStart(exePath, string.Empty);
    }

    private static bool HasSwitch(string[] args, string name)
    {
        return args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    }
}

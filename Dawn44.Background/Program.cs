using Dawn44.Core;
using System;
using System.Linq;

namespace Dawn44.Background;

/// <summary>
/// The headless resident: no window, no tray icon, no console, no OSD. It exists to do exactly one
/// thing — move the DAC volume when the configured shortcuts are pressed.
/// </summary>
/// <remarks>
/// The shortcut bindings are read once at startup, which is correct rather than lazy: the two modes
/// are mutually exclusive, so the GUI cannot be open to change them while this process runs, and a
/// switch back picks up whatever was saved.
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

        // Parks the main thread. GetAsyncKeyState polling needs no window and no message pump, which
        // is what lets this process exist without any UI stack at all.
        ModeArbitration.WaitForExitRequest();

        watcher.Stop();
        volume.Stop();
        ModeArbitration.Release();
        DiagnosticLog.Write("Background mode exited on request.");
        return 0;
    }

    private static bool HasSwitch(string[] args, string name)
    {
        return args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    }
}

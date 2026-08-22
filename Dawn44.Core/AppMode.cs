namespace Dawn44.Core;

/// <summary>
/// Which of the two mutually exclusive executables the user wants resident.
/// </summary>
/// <remarks>
/// The two modes cannot share a process: once WinUI initializes, the XAML runtime, the Windows App
/// SDK and the compositor stay loaded for the lifetime of the process, which is why the tray-only
/// GUI still holds ~138MB private. So "switch mode" always means "start the other executable and
/// exit", arbitrated through <see cref="ModeArbitration"/>.
/// </remarks>
public enum AppMode
{
    /// <summary>The WinUI window with the full control surface.</summary>
    Gui,

    /// <summary>The headless resident that only serves the volume shortcuts.</summary>
    Background,
}

using System;
using System.IO;

namespace Dawn44.Core;

/// <summary>
/// Appends to <c>%LOCALAPPDATA%\Dawn4.4 Control\background.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// The background executable has no window, no tray icon and no console, so a failure there is
/// completely silent: the shortcuts simply stop working and there is nothing to look at. This log is
/// the only diagnostic channel it has, so it stays deliberately dumb — plain text, append-only, no
/// dependencies, and every operation swallows its own exceptions because logging must never be the
/// thing that takes the resident down.
/// </para>
/// <para>
/// Only exceptions and state transitions are written, not every keypress, so the file stays tiny in
/// normal use. <see cref="MaxBytes"/> caps it anyway: once exceeded the file is rolled over to
/// <c>background.log.old</c>, keeping at most two generations on disk.
/// </para>
/// </remarks>
public static class DiagnosticLog
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static readonly string LogFilePath = Path.Combine(SettingsStore.SettingsDirectory, "background.log");

    private static readonly string PreviousLogFilePath = LogFilePath + ".old";

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.SettingsDirectory);
                RollOverIfNeeded();
                File.AppendAllText(
                    LogFilePath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // A log that cannot be written must not break the thing it is logging about.
        }
    }

    public static void Write(string message, Exception exception)
    {
        Write($"{message}{Environment.NewLine}{exception}");
    }

    private static void RollOverIfNeeded()
    {
        var file = new FileInfo(LogFilePath);
        if (!file.Exists || file.Length < MaxBytes)
        {
            return;
        }

        File.Delete(PreviousLogFilePath);
        File.Move(LogFilePath, PreviousLogFilePath);
    }
}

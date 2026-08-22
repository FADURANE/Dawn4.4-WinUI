using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Dawn44.Core;

/// <summary>
/// Decides which of the two executables — the WinUI window or the headless resident — owns the
/// device and the shortcuts, and hands ownership over on request.
/// </summary>
/// <remarks>
/// <para>
/// A named mutex would be the obvious mechanism and is the wrong one here. <c>RunAsAdmin</c> makes
/// the resident process high integrity, and a named kernel object created by a high-integrity
/// process usually cannot be opened from medium integrity (no <c>SYNCHRONIZE</c>/<c>MODIFY_STATE</c>),
/// which is exactly the direction a mode switch travels. <c>Process.Kill</c> is barred for the same
/// reason. Files under <c>%LOCALAPPDATA%</c> have no mandatory label — their DACL is granted to the
/// user — so a file handshake works in both directions regardless of integrity level:
/// </para>
/// <list type="bullet">
/// <item><c>running.json</c> — written by the owner, deleted when it exits cleanly.</item>
/// <item><c>.exit-request</c> — dropped by the newcomer; the owner deletes it and exits.</item>
/// </list>
/// <para>
/// Nothing here ever force-kills. If the owner does not release within
/// <see cref="ReleaseTimeoutMs"/> the newcomer logs and gives up, because two processes sharing the
/// HID handle and the poll loop is worse than a failed mode switch.
/// </para>
/// <para>
/// The existing <c>Local\Dawn44Control.SingleInstance</c> mutex stays as the cheap same-mode
/// duplicate check, but it is no longer what arbitrates between the modes.
/// </para>
/// </remarks>
public static class ModeArbitration
{
    private const int ReleaseTimeoutMs = 5000;
    private const int ReleasePollMs = 100;
    private const int ExitRequestPollMs = 500;

    /// <summary>Tolerance when matching a recorded start time against a live process.</summary>
    private const double StartTimeToleranceSeconds = 2;

    public static readonly string RunningFilePath = Path.Combine(SettingsStore.SettingsDirectory, "running.json");

    public static readonly string ExitRequestFilePath = Path.Combine(SettingsStore.SettingsDirectory, ".exit-request");

    /// <summary>
    /// The owner recorded in <c>running.json</c>. <see cref="StartedAt"/> guards against pid reuse:
    /// without it an unrelated process that inherited the pid would look like a live owner forever.
    /// </summary>
    public readonly record struct RunningState(int Pid, AppMode Mode, DateTimeOffset? StartedAt);

    /// <summary>
    /// Claims ownership for <paramref name="mode"/>, asking the other mode to step aside first.
    /// </summary>
    public static TakeOverResult TakeOver(AppMode mode)
    {
        var existing = ReadRunningState();

        switch (Decide(existing, mode, IsAlive))
        {
            case TakeOverDecision.YieldToSameMode:
                return TakeOverResult.SameModeAlreadyRunning;

            case TakeOverDecision.WaitForOtherMode:
                RequestExit();
                if (!WaitForRelease())
                {
                    DiagnosticLog.Write(
                        $"{mode} start abandoned: the {existing!.Value.Mode} owner (pid {existing.Value.Pid}) "
                        + $"did not release within {ReleaseTimeoutMs}ms.");
                    ClearExitRequest();
                    return TakeOverResult.OtherModeDidNotRelease;
                }

                DiagnosticLog.Write($"{mode} took over from {existing!.Value.Mode} (pid {existing.Value.Pid}).");
                break;
        }

        // Either a sentinel left over from a handover that already completed, or the one just
        // honoured above. Left in place it would make this process exit the moment it starts.
        ClearExitRequest();
        WriteRunningState(new RunningState(Environment.ProcessId, mode, GetCurrentProcessStartTime()));
        return TakeOverResult.Acquired;
    }

    /// <summary>Deletes <c>running.json</c>, but only when this process is the recorded owner.</summary>
    public static void Release()
    {
        try
        {
            var state = ReadRunningState();
            if (state is not null && state.Value.Pid != Environment.ProcessId)
            {
                return;
            }

            File.Delete(RunningFilePath);
        }
        catch
        {
            // Nothing useful to do; a stale file is handled by the pid liveness check on next start.
        }
    }

    /// <summary>Asks the current owner, whichever mode it is, to shut down.</summary>
    public static void RequestExit()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.SettingsDirectory);
            File.WriteAllText(ExitRequestFilePath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // Without the sentinel the newcomer simply times out and refuses to start, which is the
            // intended failure direction.
        }
    }

    /// <summary>
    /// The owner-side half of the handshake. Cheap enough to fold into an existing poll loop at a
    /// few hundred milliseconds; it is a single file-existence check.
    /// </summary>
    public static bool IsExitRequested()
    {
        try
        {
            return File.Exists(ExitRequestFilePath);
        }
        catch
        {
            return false;
        }
    }

    public static void ClearExitRequest()
    {
        try
        {
            File.Delete(ExitRequestFilePath);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Blocks until another process asks this one to exit. The headless resident parks its main
    /// thread here, which is why it needs no window and no message loop.
    /// </summary>
    public static void WaitForExitRequest(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsExitRequested())
            {
                ClearExitRequest();
                return;
            }

            // Doubles as the sleep: an unsignalable token just times out every poll interval.
            if (cancellationToken.WaitHandle.WaitOne(ExitRequestPollMs))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The four cases, kept free of I/O so they can be tested without a second process: no owner or
    /// a dead one means run; the same mode means step aside; the other mode means ask it to leave.
    /// </summary>
    internal static TakeOverDecision Decide(
        RunningState? existing,
        AppMode desiredMode,
        Func<RunningState, bool> isAlive)
    {
        if (existing is null)
        {
            return TakeOverDecision.Run;
        }

        var state = existing.Value;

        // Our own record, left behind by an earlier TakeOver in this process.
        if (state.Pid == Environment.ProcessId)
        {
            return TakeOverDecision.Run;
        }

        if (!isAlive(state))
        {
            return TakeOverDecision.Run;
        }

        return state.Mode == desiredMode
            ? TakeOverDecision.YieldToSameMode
            : TakeOverDecision.WaitForOtherMode;
    }

    private static bool WaitForRelease()
    {
        var deadline = Environment.TickCount64 + ReleaseTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(ReleasePollMs);

            var state = ReadRunningState();
            if (state is null || !IsAlive(state.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Errs towards "alive". Wrongly deciding the owner is gone starts a second resident that fights
    /// over the HID handle and the shortcuts; wrongly deciding it is alive only costs a five-second
    /// wait before a clean refusal.
    /// </summary>
    internal static bool IsAlive(RunningState state)
    {
        if (state.Pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(state.Pid);
            if (state.StartedAt is null)
            {
                return true;
            }

            try
            {
                var actual = new DateTimeOffset(process.StartTime);
                return Math.Abs((actual - state.StartedAt.Value).TotalSeconds) < StartTimeToleranceSeconds;
            }
            catch
            {
                // Reading the start time of a higher-integrity process can be denied.
                return true;
            }
        }
        catch (ArgumentException)
        {
            // No process carries that id any more.
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static DateTimeOffset? GetCurrentProcessStartTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime);
        }
        catch
        {
            return null;
        }
    }

    private static RunningState? ReadRunningState()
    {
        try
        {
            if (!File.Exists(RunningFilePath))
            {
                return null;
            }

            return TryParseRunningState(File.ReadAllText(RunningFilePath), out var state) ? state : null;
        }
        catch
        {
            // Unreadable or half-written: treat as no owner, the liveness check is the real guard.
            return null;
        }
    }

    private static void WriteRunningState(RunningState state)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.SettingsDirectory);
            File.WriteAllText(RunningFilePath, SerializeRunningState(state));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Could not write running.json.", ex);
        }
    }

    /// <summary>
    /// Hand-written with <see cref="Utf8JsonWriter"/> rather than <c>JsonSerializer</c>: the headless
    /// executable is published with NativeAOT, where the reflection-based serializer is not trim-safe.
    /// </summary>
    internal static string SerializeRunningState(RunningState state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Pid", state.Pid);
            writer.WriteString("Mode", state.Mode == AppMode.Background ? "Background" : "Gui");
            if (state.StartedAt is not null)
            {
                writer.WriteString("StartedAt", state.StartedAt.Value.ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// A missing or unrecognised <c>Mode</c> reads as <see cref="AppMode.Gui"/>; a missing
    /// <c>StartedAt</c> only turns off the pid-reuse guard. Only the pid is mandatory, because
    /// without it there is nothing to check liveness against.
    /// </summary>
    internal static bool TryParseRunningState(string json, out RunningState state)
    {
        state = default;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("Pid", out var pidElement)
                || pidElement.ValueKind != JsonValueKind.Number
                || !pidElement.TryGetInt32(out var pid))
            {
                return false;
            }

            var mode = SettingsStore.ParseMode(
                root.TryGetProperty("Mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
                    ? modeElement.GetString()
                    : null);

            DateTimeOffset? startedAt = null;
            if (root.TryGetProperty("StartedAt", out var startedElement)
                && startedElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    startedElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedStartedAt))
            {
                startedAt = parsedStartedAt;
            }

            state = new RunningState(pid, mode, startedAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Outcome of <see cref="ModeArbitration.TakeOver"/>; anything but
/// <see cref="Acquired"/> means this process must exit.</summary>
public enum TakeOverResult
{
    Acquired,
    SameModeAlreadyRunning,
    OtherModeDidNotRelease,
}

internal enum TakeOverDecision
{
    Run,
    YieldToSameMode,
    WaitForOtherMode,
}

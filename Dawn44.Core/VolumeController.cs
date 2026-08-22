using System;
using System.Threading;

namespace Dawn44.Core;

/// <summary>
/// Applies relative volume steps for the headless mode, from a verified value, on a dedicated thread.
/// </summary>
/// <remarks>
/// <para>
/// This is not <see cref="VolumeWriteQueue"/>, which ramps toward an absolute target so that a slider
/// drag sounds smooth. The shortcut path has the opposite problem: there is no OSD in the background
/// mode, so the user only has their ears.
/// </para>
/// <para>
/// <b>Never write from a value that has not been verified.</b> A relative step is only as good as the
/// value it starts from, and raw 0 means "no attenuation", so display 60 — maximum — is exactly what a
/// garbled or zeroed response decodes to. One bad reading turns the next keypress into a write of 60 on
/// a 4.4mm balanced output. So the device is re-read whenever the value is older than
/// <see cref="TrustWindowMs"/>, a reading that disagrees with what we last knew has to be confirmed by
/// a second read, and a step with nothing trustworthy to start from is dropped rather than guessed at.
/// Within a burst the value stays trusted — each successful write tells us what the device now holds —
/// so the read cost falls once at the start of a burst rather than on every keypress.
/// </para>
/// <para>
/// <b>Allocate nothing per keypress.</b> The first version was async: <c>Task.Delay</c> between writes
/// and a <c>Task.Run</c> per HID command, so every 28ms of a held shortcut cost a task, a timer queue
/// node, a cancellation registration and a state machine. In a process whose whole heap is a couple of
/// megabytes that churn is what made memory climb while the volume moved — the same defect the poll
/// loop had with <c>Task.Delay(15)</c> when idle. The worker is now a plain thread that parks on a
/// <see cref="ManualResetEventSlim"/> and calls the synchronous device methods directly, and once a
/// burst settles it hands back whatever the burst did grow through <see cref="ProcessFootprint.Trim"/>.
/// </para>
/// <para>
/// <see cref="Change"/> is called from the hotkey poll thread and neither blocks nor allocates.
/// </para>
/// </remarks>
public sealed class VolumeController
{
    private const int WriteIntervalMs = 28;
    private const int RefreshIdleMs = 1000;
    private const int DeviceRetryMs = 500;

    /// <summary>How long a reading or a successful write is trusted before the device is read again.</summary>
    private const int TrustWindowMs = 1500;

    /// <summary>
    /// A backlog is applied a couple of steps at a time instead of in one write. Holding the shortcut
    /// repeats every 80ms while the worker writes every 28ms, so this never throttles real input; what
    /// it prevents is a queue that built up behind a slow device turning into one audible jump.
    /// </summary>
    private const int MaxStepPerWrite = 2;

    /// <summary>How far a fresh reading may sit from the last known value before it needs a second one.</summary>
    private const int CorroborationTolerance = 2;

    /// <summary>
    /// Rate limit for <see cref="FootprintObserved"/>. Reported after a burst rather than on a timer, so
    /// the log shows the footprint next to the work that moved it, and never while genuinely idle.
    /// </summary>
    private const int FootprintIntervalMs = 300000;

    private readonly DawnHidDevice _device;
    private readonly object _gate = new();

    private ManualResetEventSlim? _wake;
    private ManualResetEventSlim? _stop;
    private Thread? _worker;
    private int _pendingDelta;
    private int? _volume;
    private int? _lastKnown;
    private long _verifiedAtMs;
    private long _footprintAtMs;
    private bool _refreshDue;

    public VolumeController(DawnHidDevice device)
    {
        _device = device;
    }

    /// <summary>Raised after each successful write, for logging. Runs on the worker thread.</summary>
    public Action<int>? VolumeApplied { get; set; }

    /// <summary>Raised for unexpected exceptions in the worker; the worker keeps running.</summary>
    public Action<Exception>? Faulted { get; set; }

    /// <summary>Raised when a reading is rejected as untrustworthy and the step is dropped.</summary>
    public Action<string>? ReadRejected { get; set; }

    /// <summary>
    /// Raised after a burst settles, at most every <see cref="FootprintIntervalMs"/>, with the line from
    /// <see cref="ProcessFootprint.Describe"/>. The resident has no other way to show where its memory
    /// went — see the remarks on that method.
    /// </summary>
    public Action<string>? FootprintObserved { get; set; }

    public void Start()
    {
        Stop();

        // spinCount 0 on both: this thread's job is to park, so spinning first would only burn CPU.
        var wake = new ManualResetEventSlim(false, 0);
        var stop = new ManualResetEventSlim(false, 0);

        // Materialised once, because WaitHandle.WaitAny needs an array and this is the idle path. Passed
        // down the call chain rather than held in a field, so a Stop() racing with the worker cannot
        // leave it reading a nulled-out reference.
        var wakeOrStop = new[] { wake.WaitHandle, stop.WaitHandle };
        var worker = new Thread(() => Run(wake, stop, wakeOrStop))
        {
            IsBackground = true,
            Name = "Dawn44 volume worker",
        };

        _wake = wake;
        _stop = stop;
        _worker = worker;
        worker.Start();
    }

    public void Stop()
    {
        var wake = _wake;
        var stop = _stop;
        var worker = _worker;
        _wake = null;
        _stop = null;
        _worker = null;

        if (stop is null || wake is null)
        {
            return;
        }

        stop.Set();

        // Only released once the worker is known to be out of its wait; disposing under a live Wait
        // would fault that thread. A Stop() from a callback runs on the worker and cannot join itself.
        if (worker is null || worker == Thread.CurrentThread || worker.Join(2000))
        {
            wake.Dispose();
            stop.Dispose();
        }
    }

    /// <summary>Queues a relative step. Safe to call from the hotkey poll thread.</summary>
    public void Change(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        lock (_gate)
        {
            _pendingDelta += delta;
        }

        // Unconditional and idempotent: a set event that the worker has not looked at yet costs
        // nothing, and the bookkeeping needed to avoid it is how wake-ups get lost.
        _wake?.Set();
    }

    private void Run(ManualResetEventSlim wake, ManualResetEventSlim stop, WaitHandle[] wakeOrStop)
    {
        // Seeded once up front, off the key path, so the first keypress has a last-known value to
        // corroborate its reading against and needs one read rather than two. Startup is also the most
        // allocation-heavy moment this process has — it enumerates every HID device on the machine — so
        // it is the first thing worth handing back.
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }

        ProcessFootprint.Trim();

        while (!stop.IsSet)
        {
            try
            {
                if (!Step(wake, stop, wakeOrStop))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                // A thread of our own, so an escaping exception would take the process down.
                Faulted?.Invoke(ex);
                if (stop.Wait(DeviceRetryMs))
                {
                    return;
                }
            }
        }
    }

    /// <summary><see langword="false"/> ends the worker loop.</summary>
    private bool Step(ManualResetEventSlim wake, ManualResetEventSlim stop, WaitHandle[] wakeOrStop)
    {
        int delta;
        lock (_gate)
        {
            delta = _pendingDelta;
            _pendingDelta = 0;
        }

        if (delta == 0)
        {
            return WaitForWork(wake, stop, wakeOrStop);
        }

        if (!EnsureTrustedVolume())
        {
            // Nothing trustworthy to step from. Dropping the press is the safe failure: writing a
            // relative step onto a guess is what put 60 on a balanced output once already, and the
            // user's next press retries anyway.
            return !stop.Wait(DeviceRetryMs);
        }

        // Only part of a backlog is applied per write; the rest goes back on the queue, which the loop
        // keeps draining at WriteIntervalMs without needing another wake-up.
        var step = StepToApply(delta);
        if (step != delta)
        {
            lock (_gate)
            {
                _pendingDelta += delta - step;
            }
        }

        var next = DawnProtocol.Clamp(_volume!.Value + step, 0, DawnProtocol.MaxVolume);
        if (next != _volume)
        {
            if (_device.TrySetVolume(next))
            {
                // A write the device accepted is itself a verification: we know what it holds now.
                Accept(next);
                VolumeApplied?.Invoke(next);
            }
            else
            {
                // In practice this is an unplug. Forget the value so the next press re-reads.
                Forget();
            }
        }

        _refreshDue = true;
        return !stop.Wait(WriteIntervalMs);
    }

    /// <summary>
    /// Parks until a keypress arrives. Once a burst has happened the wait is bounded, so the value gets
    /// resynced and the footprint trimmed <see cref="RefreshIdleMs"/> after the last write; otherwise it
    /// is unbounded and the process is genuinely idle.
    /// </summary>
    private bool WaitForWork(ManualResetEventSlim wake, ManualResetEventSlim stop, WaitHandle[] wakeOrStop)
    {
        var signalled = WaitHandle.WaitAny(wakeOrStop, _refreshDue ? RefreshIdleMs : Timeout.Infinite);
        if (signalled == 1)
        {
            return false;
        }

        if (signalled != WaitHandle.WaitTimeout)
        {
            // Reset before the caller reads _pendingDelta, so a Change that lands in between leaves the
            // event set and costs one extra pass rather than being lost.
            wake.Reset();
            return true;
        }

        _refreshDue = false;
        Refresh();
        ProcessFootprint.Trim();
        ReportFootprint();
        return true;
    }

    /// <summary>
    /// Reports the footprint after the trim, so the number in the log is the one the process settles at
    /// rather than its high-water mark.
    /// </summary>
    private void ReportFootprint()
    {
        var observer = FootprintObserved;
        if (observer is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_footprintAtMs != 0 && now - _footprintAtMs < FootprintIntervalMs)
        {
            return;
        }

        _footprintAtMs = now;
        observer(ProcessFootprint.Describe());
    }

    /// <summary>
    /// Everything below here runs only on the worker thread, so the volume state needs no lock.
    /// </summary>
    /// <returns><see langword="true"/> when a trustworthy volume is held afterwards.</returns>
    private bool EnsureTrustedVolume()
    {
        if (_volume is not null && Environment.TickCount64 - _verifiedAtMs <= TrustWindowMs)
        {
            return true;
        }

        return Refresh();
    }

    /// <summary>
    /// Reads the device, and decides whether to believe what came back.
    /// </summary>
    /// <remarks>
    /// A reading close to the last known value is taken as is. One that jumps has to be confirmed by a
    /// second read, because the failure this class exists to prevent looks exactly like a plausible
    /// value: raw 0 decodes to display 60, so a zeroed or garbled payload byte reads back as maximum
    /// volume. An unconfirmed jump is discarded, which costs the user one keypress.
    /// </remarks>
    private bool Refresh()
    {
        var first = ReadVolume();
        if (first is null)
        {
            Forget();
            return false;
        }

        if (!ReadingNeedsCorroboration(_lastKnown, first.Value))
        {
            Accept(first.Value);
            return true;
        }

        var second = ReadVolume();
        if (second != first)
        {
            ReadRejected?.Invoke(
                $"Volume read {first} was not corroborated (second read {second?.ToString() ?? "failed"}, " +
                $"last known {_lastKnown?.ToString() ?? "none"}); the step was dropped.");
            Forget();
            return false;
        }

        Accept(first.Value);
        return true;
    }

    /// <summary>
    /// Whether a fresh reading sits far enough from the last known value that a second, agreeing read
    /// is required before anything is written from it.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the rule can be tested without a device attached; it is the one
    /// piece of this class that decides whether a suspicious value reaches the DAC.
    /// </remarks>
    internal static bool ReadingNeedsCorroboration(int? lastKnown, int reading)
    {
        if (lastKnown is int previous)
        {
            return Math.Abs(previous - reading) > CorroborationTolerance;
        }

        return true;
    }

    /// <summary>How much of an accumulated delta one write is allowed to apply.</summary>
    internal static int StepToApply(int delta)
    {
        return Math.Sign(delta) * Math.Min(Math.Abs(delta), MaxStepPerWrite);
    }

    /// <summary>
    /// One read, reduced to either a volume inside the valid range or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="DawnHidDevice"/> reports an undecodable raw byte as -1 rather than 0, precisely so
    /// that it cannot be mistaken here for a real display volume.
    /// </remarks>
    private int? ReadVolume()
    {
        try
        {
            var volume = _device.TryReadState()?.Volume;
            return volume is >= 0 and <= DawnProtocol.MaxVolume ? volume : null;
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
            return null;
        }
    }

    /// <summary>Records a volume we have just confirmed, by reading it or by writing it.</summary>
    private void Accept(int volume)
    {
        _volume = volume;
        _lastKnown = volume;
        _verifiedAtMs = Environment.TickCount64;
    }

    /// <summary>
    /// Stops trusting the current value. <see cref="_lastKnown"/> is deliberately kept: it is no longer
    /// good enough to write from, but it is still the best thing to check the next reading against.
    /// </summary>
    private void Forget()
    {
        _volume = null;
    }




}

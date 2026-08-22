using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dawn44.Core;

/// <summary>
/// Applies relative volume steps for the headless mode, from a verified in-memory value.
/// </summary>
/// <remarks>
/// <para>
/// This is not <see cref="VolumeWriteQueue"/>, which exists to make a slider drag sound smooth by
/// ramping toward an absolute target. The shortcut path has the opposite problem: there is no OSD in
/// the background mode, so the user only has their ears.
/// </para>
/// <para>
/// The original version cached the volume indefinitely and never read the device on the key path, to
/// keep the first press of a burst from waiting on a 150-500ms read. That was the wrong trade. A
/// relative step is only as good as the value it starts from, and raw 0 means "no attenuation", so
/// display 60 — maximum — is exactly what a garbled or zeroed response decodes to. One bad reading
/// therefore turns the next keypress into a write of 60 on a 4.4mm balanced output. Now the device is
/// re-read whenever the value is older than <see cref="TrustWindowMs"/>, a reading that disagrees with
/// what we last knew has to be confirmed by a second read, and a step with nothing trustworthy to
/// start from is dropped rather than guessed at.
/// </para>
/// <para>
/// Within a burst the value stays trusted — each successful write tells us what the device is now — so
/// the read cost is paid once at the start of a burst rather than per keypress.
/// </para>
/// <para>
/// <see cref="Change"/> is called from the poll thread and never blocks: it accumulates a delta and
/// signals the worker.
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

    private readonly DawnHidDevice _device;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private int _pendingDelta;
    private bool _signalled;
    private int? _volume;
    private int? _lastKnown;
    private long _verifiedAtMs;
    private bool _refreshDue;

    public VolumeController(DawnHidDevice device)
    {
        _device = device;
    }

    /// <summary>Raised after each successful write, for logging.</summary>
    public Action<int>? VolumeApplied { get; set; }

    /// <summary>Raised for unexpected exceptions in the worker; the worker keeps running.</summary>
    public Action<Exception>? Faulted { get; set; }

    /// <summary>Raised when a reading is rejected as untrustworthy and the step is dropped.</summary>
    public Action<string>? ReadRejected { get; set; }

    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => RunAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
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
            if (_signalled)
            {
                return;
            }

            _signalled = true;
        }

        _signal.Release();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Seeded once up front, off the key path, so the first keypress has a last-known value to
        // corroborate its reading against and needs only one read rather than two.
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await StepAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
                await DelayAsync(DeviceRetryMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary><see langword="false"/> ends the worker loop.</summary>
    private async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        int delta;
        lock (_gate)
        {
            delta = _pendingDelta;
            _pendingDelta = 0;
        }

        if (delta == 0)
        {
            return await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await EnsureTrustedVolumeAsync(cancellationToken).ConfigureAwait(false))
        {
            // Nothing trustworthy to step from. Dropping the press is the safe failure: writing a
            // relative step onto a guess is what put 60 on a balanced output once already, and the
            // user's next press will retry anyway.
            await DelayAsync(DeviceRetryMs, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Only part of a backlog is applied per write; the rest goes back on the queue so the loop
        // keeps draining it at WriteIntervalMs.
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
            if (await _device.TrySetVolumeAsync(next, cancellationToken).ConfigureAwait(false))
            {
                // A write that the device accepted is itself a verification: we now know what it holds.
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
        await DelayAsync(WriteIntervalMs, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Parks until a keypress arrives. Once a burst has happened, the wait is bounded so the cache
    /// gets resynced <see cref="RefreshIdleMs"/> after the last write; otherwise it is unbounded and
    /// the process is genuinely idle.
    /// </summary>
    private async Task<bool> WaitForWorkAsync(CancellationToken cancellationToken)
    {
        bool signalled;
        try
        {
            signalled = await _signal
                .WaitAsync(_refreshDue ? RefreshIdleMs : Timeout.Infinite, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (signalled)
        {
            lock (_gate)
            {
                _signalled = false;
            }

            return true;
        }

        _refreshDue = false;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// <see cref="_volume"/>, <see cref="_lastKnown"/> and <see cref="_verifiedAtMs"/> are only ever
    /// touched by the worker task, so they need no lock.
    /// </summary>
    /// <returns><see langword="true"/> when a trustworthy volume is held afterwards.</returns>
    private async Task<bool> EnsureTrustedVolumeAsync(CancellationToken cancellationToken)
    {
        if (_volume is not null && Environment.TickCount64 - _verifiedAtMs <= TrustWindowMs)
        {
            return true;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
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
    private async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        var first = await ReadVolumeAsync(cancellationToken).ConfigureAwait(false);
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

        var second = await ReadVolumeAsync(cancellationToken).ConfigureAwait(false);
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
        return lastKnown is not int previous || Math.Abs(previous - reading) > CorroborationTolerance;
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
    private async Task<int?> ReadVolumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _device.TryReadStateAsync(cancellationToken).ConfigureAwait(false);
            var volume = state?.Volume;
            return volume is >= 0 and <= DawnProtocol.MaxVolume ? volume : null;
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

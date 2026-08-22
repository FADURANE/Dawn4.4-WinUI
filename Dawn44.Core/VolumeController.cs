using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dawn44.Core;

/// <summary>
/// Applies relative volume steps for the headless mode, from an in-memory cache.
/// </summary>
/// <remarks>
/// <para>
/// This is not <see cref="VolumeWriteQueue"/>, which exists to make a slider drag sound smooth by
/// ramping toward an absolute target. The shortcut path has the opposite problem: there is no OSD in
/// the background mode, so the user only has their ears, and the 150–500ms a device read costs would
/// be plainly audible as lag on the first press of a burst. So the current volume is cached and a
/// keypress only ever does clamp-and-write.
/// </para>
/// <para>
/// The cache is refreshed on a background thread once a burst has been idle for
/// <see cref="RefreshIdleMs"/>, and dropped outright when a write fails, so an unplug or a change
/// made elsewhere is picked up without ever delaying a keypress.
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

    private readonly DawnHidDevice _device;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private int _pendingDelta;
    private bool _signalled;
    private int? _volume;
    private bool _refreshDue;

    public VolumeController(DawnHidDevice device)
    {
        _device = device;
    }

    /// <summary>Raised after each successful write, for logging.</summary>
    public Action<int>? VolumeApplied { get; set; }

    /// <summary>Raised for unexpected exceptions in the worker; the worker keeps running.</summary>
    public Action<Exception>? Faulted { get; set; }

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
        // Seeded once up front so the very first keypress is a bare write.
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

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

        if (_volume is null && !await RefreshAsync(cancellationToken).ConfigureAwait(false))
        {
            // No device: drop the step rather than queue it, so a burst pressed while the dongle is
            // out does not all land at once when it comes back.
            await DelayAsync(DeviceRetryMs, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var next = DawnProtocol.Clamp(_volume!.Value + delta, 0, DawnProtocol.MaxVolume);
        if (next != _volume)
        {
            if (await _device.TrySetVolumeAsync(next, cancellationToken).ConfigureAwait(false))
            {
                _volume = next;
                VolumeApplied?.Invoke(next);
            }
            else
            {
                // In practice this is an unplug. Drop the cache so the next press re-reads.
                _volume = null;
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
    /// <see cref="_volume"/> is only ever touched by the worker task, so it needs no lock.
    /// </summary>
    /// <returns><see langword="true"/> when a volume is cached afterwards.</returns>
    private async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _device.TryReadStateAsync(cancellationToken).ConfigureAwait(false);
            _volume = state?.Volume;
            return _volume is not null;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _volume = null;
            Faulted?.Invoke(ex);
            return false;
        }
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

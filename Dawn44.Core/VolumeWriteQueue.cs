using System;
using System.Threading.Tasks;

namespace Dawn44.Core;

/// <summary>
/// Paces volume writes to the device and ramps toward the latest target.
/// </summary>
/// <remarks>
/// <para>
/// A slider drag produces far more values than the DAC can absorb, and jumping straight to the
/// final value sounds like a step rather than a fade. So the queue keeps only the newest target and
/// walks toward it at most <see cref="MaxStepPerWrite"/> per write, one write every
/// <see cref="WriteIntervalMs"/>. Both numbers were tuned against real hardware.
/// </para>
/// <para>
/// Callbacks fire on a worker thread; a UI caller must marshal them.
/// </para>
/// </remarks>
public sealed class VolumeWriteQueue
{
    private const int WriteIntervalMs = 28;
    private const int MaxStepPerWrite = 2;

    private readonly DawnHidDevice _device;
    private readonly object _gate = new();

    private int? _queuedVolume;
    private int? _lastAppliedVolume;
    private bool _isLoopActive;

    public VolumeWriteQueue(DawnHidDevice device)
    {
        _device = device;
    }

    /// <summary>Raised with the applied volume once the queue has settled on the requested target.</summary>
    public event Action<int>? TargetReached;

    /// <summary>Raised when a write failed, which in practice means the dongle was unplugged.</summary>
    public event Action? WriteFailed;

    /// <summary>Raised for unexpected exceptions. <see cref="OperationCanceledException"/> is filtered out.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Seeds the ramp origin from a freshly read device state so the first drag does not jump.</summary>
    public void SetLastApplied(int volume)
    {
        lock (_gate)
        {
            _lastAppliedVolume = volume;
        }
    }

    /// <summary>Drops any pending work. Call this when the device goes away.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _queuedVolume = null;
            _isLoopActive = false;
            _lastAppliedVolume = null;
        }
    }

    public void Enqueue(int volume)
    {
        lock (_gate)
        {
            _queuedVolume = DawnProtocol.Clamp(volume, 0, DawnProtocol.MaxVolume);
            if (_isLoopActive)
            {
                return;
            }

            _isLoopActive = true;
        }

        _ = Task.Run(RunLoopAsync);
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                int targetVolume;
                int volumeToApply;
                lock (_gate)
                {
                    if (_queuedVolume is null)
                    {
                        _isLoopActive = false;
                        return;
                    }

                    targetVolume = _queuedVolume.Value;
                    volumeToApply = DawnProtocol.MoveToward(
                        _lastAppliedVolume ?? targetVolume,
                        targetVolume,
                        MaxStepPerWrite);
                }

                if (_lastAppliedVolume != volumeToApply)
                {
                    if (!await WriteStepAsync(targetVolume, volumeToApply).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        if (_queuedVolume == targetVolume)
                        {
                            _queuedVolume = null;
                        }
                    }
                }

                await Task.Delay(WriteIntervalMs).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _queuedVolume = null;
                _isLoopActive = false;
            }

            if (ex is not OperationCanceledException)
            {
                Faulted?.Invoke(ex);
            }
        }
    }

    /// <returns><see langword="false"/> when the loop must stop because the device went away.</returns>
    private async Task<bool> WriteStepAsync(int targetVolume, int volumeToApply)
    {
        var applied = await _device.TrySetVolumeAsync(volumeToApply).ConfigureAwait(false);
        if (!applied)
        {
            lock (_gate)
            {
                _queuedVolume = null;
                _isLoopActive = false;
            }

            WriteFailed?.Invoke();
            return false;
        }

        var reachedTarget = false;
        lock (_gate)
        {
            _lastAppliedVolume = volumeToApply;
            if (_queuedVolume == targetVolume && volumeToApply == targetVolume)
            {
                _queuedVolume = null;
                reachedTarget = true;
            }
        }

        if (reachedTarget)
        {
            TargetReached?.Invoke(volumeToApply);
        }

        return true;
    }
}

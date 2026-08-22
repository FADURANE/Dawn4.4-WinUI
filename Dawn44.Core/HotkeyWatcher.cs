using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Dawn44.Core;

/// <summary>
/// Watches the two volume shortcuts by polling <c>GetAsyncKeyState</c>.
/// </summary>
/// <remarks>
/// <para>
/// Polling replaced <c>RegisterHotKey</c>/<c>WM_HOTKEY</c> in v1.0.17 and that choice is not
/// negotiable: <c>GetAsyncKeyState</c> reads the kernel's async key-state table, which is populated
/// before any <c>WH_KEYBOARD_LL</c> hook chain runs, so shortcuts keep working while a game such as
/// Star Citizen (EasyAntiCheat) owns the foreground and swallows input. It also needs no window and
/// no message loop, which is what lets the headless background mode exist without an HWND at all.
/// </para>
/// <para>
/// The bindings are supplied as delegates rather than values so the GUI keeps re-reading them every
/// tick — a shortcut changed in Settings takes effect without restarting the watcher.
/// </para>
/// <para>
/// Callbacks are raised on the poll thread. A UI caller must marshal them itself. A callback that
/// throws is reported through <see cref="CallbackFaulted"/> and the loop keeps polling, because a
/// dead poll loop would silently cost the background mode the only thing it does.
/// </para>
/// </remarks>
public sealed class HotkeyWatcher
{
    private const int PollMs = 15;
    private const int RepeatDelayMs = 400;
    private const int RepeatIntervalMs = 80;

    private readonly Func<HotkeySetting> _volumeUp;
    private readonly Func<HotkeySetting> _volumeDown;
    private readonly Action _onVolumeUp;
    private readonly Action _onVolumeDown;

    private CancellationTokenSource? _cts;

    /// <summary>Raised when a shortcut callback throws; the poll loop carries on regardless.</summary>
    public Action<Exception>? CallbackFaulted { get; set; }

    public HotkeyWatcher(
        Func<HotkeySetting> volumeUp,
        Func<HotkeySetting> volumeDown,
        Action onVolumeUp,
        Action onVolumeDown)
    {
        _volumeUp = volumeUp;
        _volumeDown = volumeDown;
        _onVolumeUp = onVolumeUp;
        _onVolumeDown = onVolumeDown;
    }

    /// <summary>Restarts the poll loop, cancelling any previous one.</summary>
    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        bool upHeld = false, downHeld = false;
        int upHeldMs = 0, downHeldMs = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var upNow = IsComboActive(_volumeUp());
            var downNow = IsComboActive(_volumeDown());

            if (upNow)
            {
                if (!upHeld)
                {
                    upHeldMs = 0;
                    Raise(_onVolumeUp);
                }
                else
                {
                    upHeldMs += PollMs;
                    if (ShouldRepeat(upHeldMs))
                    {
                        Raise(_onVolumeUp);
                    }
                }
            }

            upHeld = upNow;

            if (downNow)
            {
                if (!downHeld)
                {
                    downHeldMs = 0;
                    Raise(_onVolumeDown);
                }
                else
                {
                    downHeldMs += PollMs;
                    if (ShouldRepeat(downHeldMs))
                    {
                        Raise(_onVolumeDown);
                    }
                }
            }

            downHeld = downNow;
        }
    }

    private void Raise(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            CallbackFaulted?.Invoke(ex);
        }
    }

    /// <summary>
    /// Hold-to-repeat: nothing for the first 400ms, then one step every 80ms. The modulo is against
    /// <see cref="PollMs"/> because the accumulator advances in 15ms steps and will not land exactly
    /// on a multiple of the interval.
    /// </summary>
    internal static bool ShouldRepeat(int heldMs)
    {
        return heldMs >= RepeatDelayMs && (heldMs - RepeatDelayMs) % RepeatIntervalMs < PollMs;
    }

    internal static bool IsComboActive(HotkeySetting hotkey)
    {
        if (hotkey.Vk == 0)
        {
            return false;
        }

        if (!IsVkDown((int)hotkey.Vk))
        {
            return false;
        }

        if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0 && !IsVkDown(HotkeyVirtualKeys.Control))
        {
            return false;
        }

        if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0 && !IsVkDown(HotkeyVirtualKeys.Menu))
        {
            return false;
        }

        if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0 && !IsVkDown(HotkeyVirtualKeys.Shift))
        {
            return false;
        }

        if ((hotkey.Modifiers & HotkeyModifiers.Win) != 0
            && !IsVkDown(HotkeyVirtualKeys.LeftWin)
            && !IsVkDown(HotkeyVirtualKeys.RightWin))
        {
            return false;
        }

        return true;
    }

    private static bool IsVkDown(int vk)
    {
        return (GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

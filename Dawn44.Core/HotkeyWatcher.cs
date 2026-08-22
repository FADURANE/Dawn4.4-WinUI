using System;
using System.Runtime.InteropServices;
using System.Threading;

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
/// Callbacks are raised on the poll thread, a dedicated background thread of its own. A UI caller
/// must marshal them itself. A callback that throws is reported through <see cref="CallbackFaulted"/>
/// and the loop keeps polling, because a dead poll loop would silently cost the background mode the
/// only thing it does.
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

    private ManualResetEventSlim? _stopSignal;
    private Thread? _pollThread;

    /// <summary>
    /// Raised when a shortcut callback, or reading a binding, throws; the poll loop carries on
    /// regardless.
    /// </summary>
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

        // spinCount 0: this thread's whole job is to park for 15ms at a time, so spinning first would
        // only burn CPU.
        var stopSignal = new ManualResetEventSlim(false, 0);
        var thread = new Thread(() => PollLoop(stopSignal))
        {
            IsBackground = true,
            Name = "Dawn44 hotkey poll",
        };

        _stopSignal = stopSignal;
        _pollThread = thread;
        thread.Start();
    }

    public void Stop()
    {
        var stopSignal = _stopSignal;
        var thread = _pollThread;
        _stopSignal = null;
        _pollThread = null;

        if (stopSignal is null)
        {
            return;
        }

        stopSignal.Set();

        // Disposing while the poll thread could still be inside Wait would fault that thread, so the
        // handle is only released once the thread is known to have finished. Stop() called from a
        // shortcut callback runs on the poll thread itself, which cannot join itself.
        if (thread is null || thread == Thread.CurrentThread || thread.Join(1000))
        {
            stopSignal.Dispose();
        }
    }

    /// <summary>
    /// A dedicated thread blocking on a wait handle, rather than an <c>await Task.Delay</c> loop, and
    /// not for tidiness: at 15ms each delay allocated a promise, a timer node and a cancellation
    /// registration, measured at about 1.3MB/min of garbage. In the GUI that disappears into the XAML
    /// heap, but the headless resident is meant to sit at single-digit megabytes for days, and its
    /// working set climbed steadily for as long as it ran. This version allocates nothing per tick.
    /// </summary>
    private void PollLoop(ManualResetEventSlim stopSignal)
    {
        bool upHeld = false, downHeld = false;
        int upHeldMs = 0, downHeldMs = 0;

        while (!stopSignal.Wait(PollMs))
        {
            // A binding provider that throws must not take the thread down with it, which on a
            // dedicated thread would mean the whole process rather than one silent task.
            try
            {
                Tick(ref upHeld, ref upHeldMs, _volumeUp, _onVolumeUp);
                Tick(ref downHeld, ref downHeldMs, _volumeDown, _onVolumeDown);
            }
            catch (Exception ex)
            {
                CallbackFaulted?.Invoke(ex);
            }
        }
    }

    private void Tick(ref bool held, ref int heldMs, Func<HotkeySetting> binding, Action callback)
    {
        if (!IsComboActive(binding()))
        {
            held = false;
            return;
        }

        if (!held)
        {
            held = true;
            heldMs = 0;
            Raise(callback);
            return;
        }

        heldMs += PollMs;
        if (ShouldRepeat(heldMs))
        {
            Raise(callback);
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

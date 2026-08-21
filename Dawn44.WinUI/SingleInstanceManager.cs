using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dawn44.WinUI;

internal static class SingleInstanceManager
{
    private const string MutexName = @"Local\Dawn44Control.SingleInstance";
    private const string ShowExistingWindowMessageName = "Dawn44Control.ShowExistingWindow";
    private const int AsfwAny = -1;
    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private static Mutex? _mutex;

    public static uint ShowExistingWindowMessage { get; } = RegisterWindowMessage(ShowExistingWindowMessageName);

    public static bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            _mutex = mutex;
            return true;
        }

        mutex.Dispose();
        return false;
    }

    /// <summary>
    /// Gives up ownership of the single-instance mutex so a replacement process (for example an
    /// elevated relaunch) can acquire it. Without this the replacement sees the mutex still held
    /// by the process that spawned it, treats itself as a duplicate, and exits immediately.
    /// </summary>
    public static void Release()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; disposing below is still the right cleanup.
        }

        _mutex.Dispose();
        _mutex = null;
    }

    public static void NotifyExistingInstance()
    {
        if (ShowExistingWindowMessage != 0)
        {
            AllowSetForegroundWindow(AsfwAny);
            PostMessage(HwndBroadcast, ShowExistingWindowMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}

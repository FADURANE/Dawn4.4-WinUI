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

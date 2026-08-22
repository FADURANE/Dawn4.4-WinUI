using Dawn44.Core;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dawn44.Background;

/// <summary>Why the tray host's message loop returned.</summary>
internal enum TrayExit
{
    /// <summary>Another mode asked this process to step aside.</summary>
    HandoverRequested,

    /// <summary>The user picked window mode, or double-clicked the icon.</summary>
    SwitchToGui,

    /// <summary>The user picked Exit.</summary>
    UserExit,
}

/// <summary>
/// The resident's tray icon, and with it the only window this process owns: <c>Shell_NotifyIcon</c>
/// needs an HWND to deliver its callbacks to, so there has to be one.
/// </summary>
/// <remarks>
/// <para>
/// <c>HWND_MESSAGE</c> is what keeps this compatible with the single requirement background mode
/// exists for. A message-only window is never activated, never appears in Alt+Tab and cannot take
/// focus, so nothing here can drop a full-screen game to the desktop. The shortcuts still come from
/// <c>GetAsyncKeyState</c> polling on its own thread rather than from this window — no hook and no
/// <c>RegisterHotKey</c>, which is what makes them survive anti-cheat.
/// </para>
/// <para>
/// The message loop also carries the handover poll as a 500ms <c>WM_TIMER</c>, so replacing
/// <c>ModeArbitration.WaitForExitRequest</c> with a window costs no thread either way.
/// </para>
/// <para>
/// Structures here are blittable — fixed char buffers rather than <c>ByValTStr</c>, function pointers
/// rather than delegates — because this assembly is compiled by ILC, and blittable signatures are the
/// ones that need no marshalling stub at all.
/// </para>
/// </remarks>
internal sealed unsafe class TrayHost
{
    private const int TrayIconId = 1;
    private const int ExitPollTimerId = 1;
    private const int ExitPollIntervalMs = 500;

    private const int MenuIdWindowMode = 1;
    private const int MenuIdBackgroundMode = 2;
    private const int MenuIdExit = 3;

    private static TrayHost? _current;

    private readonly uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private IntPtr _iconHandle;
    private bool _iconAdded;
    private TrayExit _exit = TrayExit.HandoverRequested;

    public TrayHost()
    {
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    }

    /// <summary>
    /// Creates the window and the icon, then pumps messages until something asks this process to stop.
    /// Returns what that something was.
    /// </summary>
    public TrayExit Run()
    {
        _current = this;
        _hwnd = CreateMessageWindow();
        if (_hwnd == IntPtr.Zero)
        {
            // No window means no icon and no timer, so fall back to the loop the resident used before
            // it had either. The shortcuts are unaffected; only the icon is lost.
            DiagnosticLog.Write(
                $"Tray icon unavailable: window creation failed with {Marshal.GetLastWin32Error()}. "
                + "Continuing without it.");
            ModeArbitration.WaitForExitRequest();
            return TrayExit.HandoverRequested;
        }

        SendIcon(NimAdd);
        SetTimer(_hwnd, new IntPtr(ExitPollTimerId), ExitPollIntervalMs, IntPtr.Zero);

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            DispatchMessageW(ref message);
        }

        KillTimer(_hwnd, new IntPtr(ExitPollTimerId));
        SendIcon(NimDelete);
        DestroyWindow(_hwnd);
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        _hwnd = IntPtr.Zero;
        _current = null;
        return _exit;
    }

    private IntPtr CreateMessageWindow()
    {
        var moduleHandle = GetModuleHandleW(IntPtr.Zero);

        // Never freed: the string has to outlive the window class, and there is exactly one of each for
        // the life of the process.
        var classNamePtr = Marshal.StringToHGlobalUni("Dawn44BackgroundTray");
        var windowClass = default(WndClassW);
        windowClass.lpfnWndProc =
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc;
        windowClass.hInstance = moduleHandle;
        windowClass.lpszClassName = classNamePtr;

        if (RegisterClassW(ref windowClass) == 0
            && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
        {
            return IntPtr.Zero;
        }

        return CreateWindowExW(
            0,
            classNamePtr,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);
    }

    private void SendIcon(int message)
    {
        if (message == NimDelete && !_iconAdded)
        {
            return;
        }

        var data = default(NotifyIconDataW);
        data.cbSize = sizeof(NotifyIconDataW);
        data.hWnd = _hwnd;
        data.uID = TrayIconId;
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmTrayIcon;
        data.hIcon = GetIconHandle();
        CopyTip(&data, Strings.Text("Tooltip"));

        var succeeded = Shell_NotifyIconW(message, &data);
        _iconAdded = message != NimDelete && succeeded;
        if (!succeeded && message == NimAdd)
        {
            DiagnosticLog.Write($"Tray icon could not be added: {Marshal.GetLastWin32Error()}.");
        }
    }

    private static void CopyTip(NotifyIconDataW* data, string value)
    {
        var length = Math.Min(value.Length, TipCapacity - 1);
        for (var index = 0; index < length; index++)
        {
            data->szTip[index] = value[index];
        }

        data->szTip[length] = '\0';
    }

    /// <summary>
    /// The installed icon, falling back to the system default so a missing asset costs the icon's
    /// appearance rather than the icon itself.
    /// </summary>
    private IntPtr GetIconHandle()
    {
        if (_iconHandle != IntPtr.Zero)
        {
            return _iconHandle;
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Dawn44Control.ico");
        if (File.Exists(iconPath))
        {
            _iconHandle = LoadImageW(
                IntPtr.Zero,
                iconPath,
                ImageIcon,
                0,
                0,
                LrLoadFromFile | LrDefaultSize);
        }

        return _iconHandle != IntPtr.Zero
            ? _iconHandle
            : LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
    }

    private void ShowMenu()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MfString | MfDisabled, 0, Strings.Text("TrayModeTitle"));

            // Mode is written by whichever executable owns the session, so the check mark is both what
            // is running and what the next logon starts. In this process it is always background mode;
            // it is read rather than assumed so the two menus cannot end up disagreeing.
            var mode = SettingsStore.GetMode();
            AppendMenuW(
                menu,
                MfString | (mode == AppMode.Gui ? MfChecked : 0u),
                MenuIdWindowMode,
                Strings.Text("ModeGui"));
            AppendMenuW(
                menu,
                MfString | (mode == AppMode.Background ? MfChecked : 0u),
                MenuIdBackgroundMode,
                Strings.Text("ModeBackground"));
            AppendMenuW(menu, MfSeparator, 0, string.Empty);
            AppendMenuW(menu, MfString, MenuIdExit, Strings.Text("Exit"));

            // Both calls are required of a tray menu: without the foreground claim it never receives the
            // click that dismisses it, and the WM_NULL is what makes it close on the first click
            // elsewhere instead of the second.
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            HandleMenuCommand(command);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleMenuCommand(int command)
    {
        switch (command)
        {
            case MenuIdWindowMode:
                _exit = TrayExit.SwitchToGui;
                PostQuitMessage(0);
                break;

            case MenuIdBackgroundMode:
                // Already the mode running, and honoured anyway: it is the one action that repairs a
                // logon entry left pointing at the window by an install or a repair. Off this thread,
                // because it can shell out to schtasks and the message loop must not stall on it.
                Program.ClaimBackgroundModeAsync();
                break;

            case MenuIdExit:
                _exit = TrayExit.UserExit;
                PostQuitMessage(0);
                break;
        }
    }

    /// <summary>
    /// A function pointer rather than a delegate, so ILC needs no reverse marshalling stub. There is
    /// exactly one window, so the single static <see cref="_current"/> is enough to get from the HWND
    /// back to the instance.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var host = _current;
        if (host is not null && host.HandleMessage(message, lParam))
        {
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Returns whether the message was handled here.</summary>
    private bool HandleMessage(uint message, IntPtr lParam)
    {
        if (message == WmTimer)
        {
            // The owner-side half of the file handshake, on the loop that already exists rather than
            // on a thread of its own.
            if (ModeArbitration.IsExitRequested())
            {
                ModeArbitration.ClearExitRequest();
                _exit = TrayExit.HandoverRequested;
                PostQuitMessage(0);
            }

            return true;
        }

        if (message == WmTrayIcon)
        {
            // The notification is in the low word of lParam; the high word is the icon id.
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            if (notification == WmRButtonUp)
            {
                ShowMenu();
            }
            else if (notification == WmLButtonDblClk)
            {
                _exit = TrayExit.SwitchToGui;
                PostQuitMessage(0);
            }

            return true;
        }

        // Explorer restarted and took every tray icon with it, this one included.
        if (message != 0 && message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            SendIcon(NimAdd);
            return true;
        }

        return false;
    }

    // Win32 constants, structures and imports. Names and values match the GUI's proven tray code; the
    // difference is that the structures here are blittable so ILC emits no marshalling stub.
    private const int TipCapacity = 128;
    private const int InfoCapacity = 256;
    private const int InfoTitleCapacity = 64;

    private const int ErrorClassAlreadyExists = 1410;

    private const uint WmNull = 0x0000;
    private const uint WmTimer = 0x0113;
    private const uint WmTrayIcon = 0x0400 + 44;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;

    private const int NimAdd = 0x00000000;
    private const int NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const int IdiApplication = 32512;

    private const uint MfString = 0x00000000;
    private const uint MfDisabled = 0x00000002;
    private const uint MfChecked = 0x00000008;
    private const uint MfSeparator = 0x00000800;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    /// <summary>
    /// <c>HWND_MESSAGE</c>. Passed as the parent, this is what makes the window message-only: no
    /// activation, no Alt+Tab entry, no focus to steal from a full-screen game.
    /// </summary>
    private static readonly IntPtr HwndMessage = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
    }

    /// <summary>
    /// The full <c>NOTIFYICONDATAW</c>: only the first few fields are used, but
    /// <see cref="cbSize"/> is <c>sizeof</c> this type and the shell validates it, so every field has
    /// to be present at the right offset. Fixed buffers instead of <c>ByValTStr</c> keep it blittable.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconDataW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[TipCapacity];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[InfoCapacity];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[InfoTitleCapacity];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int dwMessage, NotifyIconDataW* lpData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        IntPtr lpClassName,
        IntPtr lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr hinst,
        string lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

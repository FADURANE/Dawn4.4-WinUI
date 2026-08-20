using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace Dawn44.WinUI;

public sealed class VolumeOsdWindow : Window
{
    private const int OsdWidth = 288;
    private const int OsdHeight = 70;
    private const int BottomOffset = 84;
    private const int HideDelayMs = 1250;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopMost = 0x00000008;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private static readonly IntPtr HwndTopMost = new(-1);

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly TextBlock _valueText;
    private readonly ProgressBar _progressBar;
    private CancellationTokenSource? _hideCts;

    public VolumeOsdWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        Title = "Dawn4.4 Volume";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        _appWindow.Resize(new SizeInt32(OsdWidth, OsdHeight));
        ApplyWindowStyles();
        ApplyRoundedWindowCorners();

        _valueText = new TextBlock
        {
            Text = "--",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x1F, 0x1F, 0x1F)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 60,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = BuildContent();
        ShowWindow(_hwnd, SwHide);
    }

    public void ShowVolume(int volume, string title, Window owner)
    {
        var value = Math.Clamp(volume, 0, 60);
        _valueText.Text = value.ToString();
        _progressBar.Value = value;

        MoveNearWindowsVolumeFlyout(owner);
        ShowWindow(_hwnd, SwShowNoActivate);
        SetWindowPos(_hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        ScheduleHide();
    }

    public void CloseOsd()
    {
        _hideCts?.Cancel();
        _hideCts?.Dispose();
        _hideCts = null;
        Close();
    }

    private Grid BuildContent()
    {
        var root = new Grid
        {
            Padding = new Thickness(18, 0, 16, 0),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xF3, 0xF3, 0xF3, 0xF3)),
        };

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE767",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 20,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x1F, 0x1F, 0x1F)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
        };
        root.Children.Add(icon);

        Grid.SetColumn(_progressBar, 1);
        root.Children.Add(_progressBar);

        Grid.SetColumn(_valueText, 2);
        _valueText.Margin = new Thickness(14, 0, 0, 0);
        root.Children.Add(_valueText);

        var clipBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Child = root,
        };

        return new Grid
        {
            Children =
            {
                clipBorder,
            },
        };
    }

    private void MoveNearWindowsVolumeFlyout(Window owner)
    {
        var ownerWindowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(owner));
        var displayArea = DisplayArea.GetFromWindowId(ownerWindowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + (workArea.Width - OsdWidth) / 2;
        var y = workArea.Y + workArea.Height - OsdHeight - BottomOffset;
        _appWindow.Move(new PointInt32(x, y));
    }

    private void ApplyWindowStyles()
    {
        var style = GetWindowLongPtr(_hwnd, GwlExStyle);
        SetWindowLongPtr(
            _hwnd,
            GwlExStyle,
            new IntPtr(style.ToInt64() | WsExToolWindow | WsExTopMost | WsExNoActivate));
    }

    private void ApplyRoundedWindowCorners()
    {
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(
            _hwnd,
            DwmwaWindowCornerPreference,
            ref preference,
            Marshal.SizeOf<int>());
    }

    private void ScheduleHide()
    {
        _hideCts?.Cancel();
        _hideCts?.Dispose();
        _hideCts = new CancellationTokenSource();
        var token = _hideCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(HideDelayMs, token);
                if (!token.IsCancellationRequested)
                {
                    DispatcherQueue.TryEnqueue(() => ShowWindow(_hwnd, SwHide));
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}

using Dawn44.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

// Windows.System, imported above, carries a DispatcherQueueTimer of its own, so the plain namespace
// import would make the type name ambiguous. The Window.DispatcherQueue property this timer comes
// from is the Microsoft.UI one.
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Dawn44.WinUI;

public sealed partial class MainWindow : Window
{
    private static readonly Guid HidClassGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const int DefaultWindowWidth = 660;
    private const int DefaultWindowHeight = 1460;

    /// <summary>
    /// How often the other mode's handover request is checked for. A single file-existence test on
    /// the UI dispatcher, which already has a message pump running, so this costs no thread of its
    /// own.
    /// </summary>
    private const int ExitRequestPollMs = 500;
    private const int HotkeyVolumeUp = 0x4441;
    private const int HotkeyVolumeDown = 0x4442;
    // Aliases for Dawn44.Core so the ~20 call sites below stay untouched.
    private const uint ModAlt = HotkeyModifiers.Alt;
    private const uint ModControl = HotkeyModifiers.Control;
    private const uint ModShift = HotkeyModifiers.Shift;
    private const uint ModWin = HotkeyModifiers.Win;
    private const uint ModAltControl = HotkeyModifiers.AltControl;
    private const uint VkUp = HotkeyVirtualKeys.Up;
    private const uint VkDown = HotkeyVirtualKeys.Down;
    private const int VkShift = HotkeyVirtualKeys.Shift;
    private const int VkControl = HotkeyVirtualKeys.Control;
    private const int VkMenu = HotkeyVirtualKeys.Menu;
    private const int VkLWin = HotkeyVirtualKeys.LeftWin;
    private const int VkRWin = HotkeyVirtualKeys.RightWin;
    private const int WmNull = 0x0000;
    private const int WmHotkey = 0x0312;
    private const int WmSize = 0x0005;
    private const int WmDeviceChange = 0x0219;
    private const int WmTrayIcon = 0x0400 + 44;
    private const int SizeMinimized = 1;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevtypDeviceInterface = 0x00000005;
    private const uint DeviceNotifyWindowHandle = 0x00000000;
    private const int DeviceArrivalRefreshDelayMs = 900;
    private const int DeviceRemovalRefreshDelayMs = 200;
    private const int DevBroadcastDeviceInterfaceNameOffset = 28;
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const uint ImageIcon = 1;
    private const int IconDefaultSize = 0;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int IdiApplication = 32512;
    private const int TrayMenuVolumeUp = 1003;
    private const int TrayMenuVolumeDown = 1004;
    private const int TrayMenuMute = 1005;
    private const int TrayMenuGainLow = 1010;
    private const int TrayMenuGainHigh = 1011;
    private const int TrayMenuLedOn = 1020;
    private const int TrayMenuLedOff = 1021;
    private const int TrayMenuFilterBase = 1030;
    private const int TrayMenuRestore = 1001;
    private const int TrayMenuExit = 1002;
    private const int TrayMenuModeGui = 1040;
    private const int TrayMenuModeBackground = 1041;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint MfString = 0x00000000;
    private const uint MfDisabled = 0x00000002;
    private const uint MfChecked = 0x00000008;
    private const uint MfSeparator = 0x00000800;
    // Only the keys still named at a call site are aliased; the rest are reached through the typed
    // SettingsStore accessors.
    private const string BackgroundImageTokenKey = SettingsStore.BackgroundImageTokenKey;
    private const string BackgroundImageNameKey = SettingsStore.BackgroundImageNameKey;
    private const string BackgroundZoomKey = SettingsStore.BackgroundZoomKey;
    private const string BackgroundOffsetXKey = SettingsStore.BackgroundOffsetXKey;
    private const string BackgroundOffsetYKey = SettingsStore.BackgroundOffsetYKey;

    private readonly DawnHidDevice _device = new();
    private readonly VolumeWriteQueue _volumeWriteQueue;
    private readonly HotkeyWatcher _hotkeyWatcher;
    private readonly DispatcherQueueTimer _exitRequestTimer;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly SubclassProc _subclassProc;
    private IntPtr _trayIconHandle;
    private IntPtr _deviceNotificationHandle;
    private CancellationTokenSource? _deviceChangeRefreshCts;
    private VolumeOsdWindow? _volumeOsdWindow;
    private bool _trayIconVisible;
    private bool _isLoading;
    private bool _isApplying;
    private bool _isExiting;
    private bool _isLoadingSettings;
    private bool _isLoadingBackgroundAdjustment = true;
    private bool _isDeviceConnected;
    private bool _hasCompletedInitialRefresh;
    private string? _cachedBackgroundPath;
    private string? _cachedBackgroundName;
    private bool _startMinimizedToTray;
    private bool _isHiddenToTray;
    private string _language = "en";
    private HotkeyCaptureTarget _hotkeyCaptureTarget = HotkeyCaptureTarget.None;

    private enum HotkeyCaptureTarget
    {
        None,
        VolumeUp,
        VolumeDown,
    }

    public MainWindow(bool startMinimizedToTray = false)
    {
        InitializeComponent();
        _volumeWriteQueue = new VolumeWriteQueue(_device);
        _volumeWriteQueue.TargetReached += OnVolumeTargetReached;
        _volumeWriteQueue.WriteFailed += OnVolumeWriteFailed;
        _volumeWriteQueue.Faulted += OnVolumeWriteFaulted;
        // Delegates, not values: a shortcut changed in Settings takes effect on the next tick.
        _hotkeyWatcher = new HotkeyWatcher(
            SettingsStore.GetVolumeUpHotkey,
            SettingsStore.GetVolumeDownHotkey,
            () => ChangeVolumeBy(1),
            () => ChangeVolumeBy(-1));
        _startMinimizedToTray = startMinimizedToTray;
        // Set before LoadSettingsUi so the background loader knows not to decode into a hidden window.
        _isHiddenToTray = startMinimizedToTray;
        _isLoadingBackgroundAdjustment = false;
        ExtendsContentIntoTitleBar = true;

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _subclassProc = WindowSubclassProc;
        TrySetAppIcon();
        ResizeWindow(DefaultWindowWidth, DefaultWindowHeight);
        PositionWindowNearRight();
        LoadSettingsUi();
        RegisterHidDeviceNotifications();
        InitializeTrayIcon();
        RegisterHotkeys();
        SetWindowSubclass(_hwnd, _subclassProc, UIntPtr.Zero, UIntPtr.Zero);
        _appWindow.Closing += AppWindow_Closing;

        // The owner half of the mode handshake. A named event would be the obvious mechanism and is
        // the wrong one across integrity levels, so the resident asks by dropping a file and this
        // window watches for it — see ModeArbitration.
        _exitRequestTimer = DispatcherQueue.CreateTimer();
        _exitRequestTimer.Interval = TimeSpan.FromMilliseconds(ExitRequestPollMs);
        _exitRequestTimer.Tick += ExitRequestTimer_Tick;
        _exitRequestTimer.Start();

        _ = RefreshAsync();
        if (_startMinimizedToTray)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                HideToTray();
                _startMinimizedToTray = false;
            });
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void AdjustBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        EnterBackgroundAdjustmentMode();
    }

    private void DoneBackgroundAdjustmentButton_Click(object sender, RoutedEventArgs e)
    {
        BackgroundAdjustPanel.Visibility = Visibility.Collapsed;
    }

    private void ClearCloseDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCloseBehavior("Ask");
        LoadSettingsUi();
        ShowStatus(InfoBarSeverity.Success, Text("Settings"), Text("CloseBehaviorReset"));
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        _hotkeyCaptureTarget = string.Equals(button.Tag?.ToString(), "Down", StringComparison.Ordinal)
            ? HotkeyCaptureTarget.VolumeDown
            : HotkeyCaptureTarget.VolumeUp;
        button.Content = Text("PressShortcut");
        button.Focus(FocusState.Programmatic);
        ShowStatus(InfoBarSeverity.Informational, Text("GlobalShortcuts"), Text("PressShortcutHint"));
    }

    private void ShortcutButton_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_hotkeyCaptureTarget == HotkeyCaptureTarget.None)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            _hotkeyCaptureTarget = HotkeyCaptureTarget.None;
            UpdateShortcutButtons();
            return;
        }

        if (!TryBuildHotkeySetting(e.Key, out var hotkey))
        {
            ShowStatus(InfoBarSeverity.Warning, Text("GlobalShortcuts"), Text("ShortcutNeedsModifier"));
            return;
        }

        if (_hotkeyCaptureTarget == HotkeyCaptureTarget.VolumeUp)
        {
            SettingsStore.SaveVolumeUpHotkey(hotkey);
        }
        else
        {
            SettingsStore.SaveVolumeDownHotkey(hotkey);
        }

        _hotkeyCaptureTarget = HotkeyCaptureTarget.None;
        RegisterHotkeys();
        UpdateShortcutButtons();
        ShowStatus(InfoBarSeverity.Success, Text("GlobalShortcuts"), Text("ShortcutUpdated"));
    }

    private void CloseBehaviorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (CloseBehaviorBox.SelectedItem is ComboBoxItem item && item.Tag is string behavior)
        {
            SaveCloseBehavior(behavior);
        }
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (LanguageBox.SelectedItem is ComboBoxItem item && item.Tag is string language)
        {
            SaveLanguage(language);
            _language = NormalizeLanguage(language);
            ApplyLanguage();
            ShowStatus(InfoBarSeverity.Success, Text("Settings"), Text("LanguageUpdated"));
        }
    }

    private async void ChooseBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        SaveSetting(BackgroundImageTokenKey, file.Path);
        SaveSetting(BackgroundImageNameKey, file.Name);
        ResetBackgroundAdjustmentSettings();
        LoadBackgroundAdjustmentUi();
        await ApplyBackgroundImageAsync(file.Path, file.Name);
        SettingsOverlay.Visibility = Visibility.Collapsed;
        EnterBackgroundAdjustmentMode();
        ShowStatus(InfoBarSeverity.Success, Text("Settings"), Text("BackgroundUpdated"));
    }

    private void ClearBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        ClearBackgroundImageSetting();
        ResetBackgroundAdjustmentSettings();
        LoadBackgroundAdjustmentUi();
        CustomBackgroundImage.Source = null;
        CustomBackgroundImage.Visibility = Visibility.Collapsed;
        BackgroundAdjustPanel.Visibility = Visibility.Collapsed;
        AdjustBackgroundButton.Visibility = Visibility.Collapsed;
        BackgroundPathText.Text = Text("NoCustomBackground");
        ShowStatus(InfoBarSeverity.Success, Text("Settings"), Text("BackgroundCleared"));
    }

    private void BackgroundAdjustmentSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isLoadingBackgroundAdjustment || !AreBackgroundAdjustmentControlsReady())
        {
            return;
        }

        SaveBackgroundAdjustment();
        ApplyBackgroundAdjustment();
    }

    private void ResetBackgroundAdjustmentButton_Click(object sender, RoutedEventArgs e)
    {
        ResetBackgroundAdjustmentSettings();
        LoadBackgroundAdjustmentUi();
        ShowStatus(InfoBarSeverity.Success, Text("Settings"), Text("BackgroundAdjustmentReset"));
    }

    private void ResizeLockSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveResizeLocked(ResizeLockSwitch.IsOn);
        ApplyResizeLockFromSettings();
        ShowStatus(InfoBarSeverity.Success, Text("Settings"), ResizeLockSwitch.IsOn ? Text("WindowSizeLocked") : Text("WindowSizeUnlocked"));
    }

    private void HotkeyOsdSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveHotkeyOsdEnabled(HotkeyOsdSwitch.IsOn);
        ShowStatus(InfoBarSeverity.Success, Text("Settings"),
            HotkeyOsdSwitch.IsOn ? Text("HotkeyOsdEnabled") : Text("HotkeyOsdDisabled"));
    }

    private async void AdminSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;

        SaveRunAsAdmin(AdminSwitch.IsOn);

        if (AdminSwitch.IsOn)
        {
            if (IsCurrentProcessElevated())
            {
                // Now elevated, so the logon task can be (re)created at the highest run level.
                await Task.Run(() => ApplyStartupRegistration(GetStartupEnabled()));
                ShowStatus(InfoBarSeverity.Success, Text("Settings"), Text("RunAsAdminAlready"));
            }
            else
            {
                // The elevated instance re-applies startup registration during its own startup.
                RestartAsAdmin();
            }
        }
        else
        {
            // Back to a plain Run entry; the elevated logon task is no longer wanted.
            await Task.Run(() => ApplyStartupRegistration(GetStartupEnabled()));
            ShowStatus(InfoBarSeverity.Informational, Text("Settings"), Text("RunAsAdminDisabled"));
        }
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        var enabled = StartupSwitch.IsOn;
        SaveStartupEnabled(enabled);
        var applied = await Task.Run(() => ApplyStartupRegistration(enabled));
        ShowStatus(
            applied ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            Text("Startup"),
            applied
                ? (enabled ? Text("StartupEnabled") : Text("StartupDisabled"))
                : Text("StartupFailed"));
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (VolumeText is null)
        {
            return;
        }

        var volume = (int)Math.Round(e.NewValue);
        VolumeText.Text = volume.ToString();

        if (_isLoading || !_isDeviceConnected)
        {
            return;
        }

        QueueVolumeWrite(volume);
    }

    private async void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _isApplying || !_isDeviceConnected || FilterBox.SelectedIndex < 0)
        {
            return;
        }

        await RunDeviceActionAsync(() => _device.TrySetFilterAsync(FilterBox.SelectedIndex), Text("FilterUpdated"));
    }

    private async void GainButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _isApplying || !_isDeviceConnected || GainButtons.SelectedIndex < 0)
        {
            return;
        }

        await RunDeviceActionAsync(() => _device.TrySetGainAsync(GainButtons.SelectedIndex), Text("GainUpdated"));
    }

    private async void LedButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _isApplying || !_isDeviceConnected || LedButtons.SelectedIndex < 0)
        {
            return;
        }

        await RunDeviceActionAsync(() => _device.TrySetLedAsync(LedButtons.SelectedIndex), Text("LedUpdated"));
    }

    private async Task RefreshAsync()
    {
        if (_isApplying)
        {
            return;
        }

        _isApplying = true;
        _isLoading = true;
        SetBusy(true);
        try
        {
            var state = await _device.TryReadStateAsync();
            if (state is null)
            {
                var wasConnected = _isDeviceConnected;
                SetDeviceConnected(false);
                ShowDeviceDisconnectedStatus();
                if (_hasCompletedInitialRefresh && wasConnected)
                {
                    ShowTrayNotification(Text("NotConnected"), Text("DeviceDisconnected"));
                }
                return;
            }

            var wasDisconnected = !_isDeviceConnected;
            SetDeviceConnected(true);
            ApplyStateToUi(state);
            ShowStatus(InfoBarSeverity.Success, Text("Connected"), Text("DeviceReady"));
            if (_hasCompletedInitialRefresh && wasDisconnected)
            {
                ShowTrayNotification(Text("Connected"), Text("DeviceConnected"));
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, Text("NotReady"), ex.Message);
        }
        finally
        {
            _isApplying = false;
            _isLoading = false;
            _hasCompletedInitialRefresh = true;
            SetBusy(false);
        }
    }

    private void QueueDeviceChangeRefresh(bool removed)
    {
        _deviceChangeRefreshCts?.Cancel();
        _deviceChangeRefreshCts?.Dispose();
        _deviceChangeRefreshCts = new CancellationTokenSource();
        var token = _deviceChangeRefreshCts.Token;

        _ = RefreshAfterDeviceChangeAsync(removed ? DeviceRemovalRefreshDelayMs : DeviceArrivalRefreshDelayMs, token);
    }

    private async Task RefreshAfterDeviceChangeAsync(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
            if (!token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(async () => await RefreshAsync());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void QueueVolumeWrite(int volume)
    {
        if (!_isDeviceConnected)
        {
            ShowStatus(InfoBarSeverity.Warning, Text("NotConnected"), Text("ConnectBeforeChanging"));
            return;
        }

        _volumeWriteQueue.Enqueue(volume);
    }

    // The queue raises these on a worker thread, so every one of them marshals to the UI thread.
    private void OnVolumeTargetReached(int volume)
    {
        DispatcherQueue.TryEnqueue(() =>
            ShowStatus(InfoBarSeverity.Success, Text("Applied"), string.Format(Text("VolumeApplied"), volume)));
    }

    private void OnVolumeWriteFailed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetDeviceConnected(false);
            ShowDeviceDisconnectedStatus();
        });
    }

    private void OnVolumeWriteFaulted(Exception ex)
    {
        DispatcherQueue.TryEnqueue(() => ShowStatus(InfoBarSeverity.Error, Text("VolumeFailed"), ex.Message));
    }

    private async Task RunDeviceActionAsync(Func<Task<bool>> action, string? successMessage)
    {
        if (_isApplying)
        {
            return;
        }

        _isApplying = true;
        SetBusy(true);
        try
        {
            var applied = await action();
            if (!applied)
            {
                SetDeviceConnected(false);
                ShowDeviceDisconnectedStatus();
                return;
            }

            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                ShowStatus(InfoBarSeverity.Success, Text("Applied"), successMessage);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, Text("NotReady"), ex.Message);
        }
        finally
        {
            _isApplying = false;
            SetBusy(false);
        }
    }

    private void ApplyStateToUi(DawnDeviceState state)
    {
        if (state.Volume >= 0)
        {
            VolumeSlider.Value = state.Volume;
            VolumeText.Text = state.Volume.ToString();
            _volumeWriteQueue.SetLastApplied(state.Volume);
        }

        if (state.Filter >= 0)
        {
            FilterBox.SelectedIndex = Clamp(state.Filter, 0, 4);
        }

        if (state.Gain >= 0)
        {
            GainButtons.SelectedIndex = Clamp(state.Gain, 0, 1);
        }

        if (state.Led >= 0)
        {
            LedButtons.SelectedIndex = Clamp(state.Led, 0, 2);
        }
    }

    private void ChangeVolumeBy(int delta)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDeviceConnected)
            {
                ShowStatus(InfoBarSeverity.Warning, Text("NotConnected"), Text("ConnectBeforeChanging"));
                return;
            }

            var next = Clamp((int)Math.Round(VolumeSlider.Value) + delta, 0, 60);
            VolumeSlider.Value = next;
            VolumeText.Text = next.ToString();
            QueueVolumeWrite(next);
            if (GetHotkeyOsdEnabled())
            {
                ShowVolumeOsd(next);
            }
        });
    }

    private void SetVolumeDirect(int volume)
    {
        if (!_isDeviceConnected)
        {
            ShowStatus(InfoBarSeverity.Warning, Text("NotConnected"), Text("ConnectBeforeChanging"));
            return;
        }

        var next = Clamp(volume, 0, 60);
        VolumeSlider.Value = next;
        VolumeText.Text = next.ToString();
        QueueVolumeWrite(next);
        if (GetHotkeyOsdEnabled())
        {
            ShowVolumeOsd(next);
        }
    }

    private void ShowVolumeOsd(int volume)
    {
        _volumeOsdWindow ??= new VolumeOsdWindow();
        _volumeOsdWindow.ShowVolume(volume, Text("Volume"), this);
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting)
        {
            CleanupNativeResources();
            return;
        }

        var behavior = GetCloseBehavior();
        if (behavior == "Tray")
        {
            args.Cancel = true;
            HideToTray();
            return;
        }

        if (behavior == "Exit")
        {
            _isExiting = true;
            CleanupNativeResources();
            return;
        }

        args.Cancel = true;
        var choice = await ShowCloseChoiceDialogAsync();
        if (choice == "Tray")
        {
            HideToTray();
        }
        else if (choice == "Exit")
        {
            ExitApplication();
        }
    }

    private async Task<string?> ShowCloseChoiceDialogAsync()
    {
        var remember = new CheckBox { Content = Text("RememberChoice") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Text("CloseQuestion"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(remember);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Text("CloseDawn"),
            Content = panel,
            PrimaryButtonText = Text("MinimizeToTray"),
            SecondaryButtonText = Text("ExitApp"),
            CloseButtonText = Text("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        var behavior = result switch
        {
            ContentDialogResult.Primary => "Tray",
            ContentDialogResult.Secondary => "Exit",
            _ => null,
        };

        if (behavior is not null && remember.IsChecked == true)
        {
            SaveCloseBehavior(behavior);
            LoadSettingsUi();
        }

        return behavior;
    }

    private void InitializeTrayIcon()
    {
        UpdateTrayIcon(NimAdd);
        _trayIconVisible = true;
    }

    private void ShowTrayNotification(string title, string message)
    {
        if (!_trayIconVisible)
        {
            return;
        }

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifInfo,
            szInfoTitle = title,
            szInfo = message,
            uTimeoutOrVersion = 3000,
        };
        try
        {
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch
        {
            // Tray notification support is best-effort and should never block startup.
        }
    }

    private void HideToTray()
    {
        _isHiddenToTray = true;

        // Release background image memory while minimized to tray.
        _cachedBackgroundPath = GetBackgroundImageToken();
        _cachedBackgroundName = GetBackgroundImageName();
        CustomBackgroundImage.Source = null;
        _appWindow.Hide();
    }

    private async void ShowFromTray()
    {
        _isHiddenToTray = false;
        _appWindow.Show();
        ShowWindow(_hwnd, SwRestore);
        ShowWindow(_hwnd, SwShow);
        if (GetResizeLocked())
        {
            _appWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
        }

        SetForegroundWindow(_hwnd);
        Activate();

        // Restore background image if it was cached
        if (CustomBackgroundImage.Source is null && !string.IsNullOrWhiteSpace(_cachedBackgroundPath))
        {
            try
            {
                await ApplyBackgroundImageAsync(_cachedBackgroundPath, _cachedBackgroundName ?? System.IO.Path.GetFileName(_cachedBackgroundPath));
            }
            catch
            {
                // Ignore errors on restore; user can re-select if needed
            }
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        CleanupNativeResources();
        Close();
    }

    /// <summary>
    /// Another mode is starting and wants this process gone. The sentinel is cleared here rather than
    /// left to the newcomer, so one that dies mid-handover cannot leave a file behind that makes the
    /// next GUI exit the moment it starts.
    /// </summary>
    private void ExitRequestTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isExiting || !ModeArbitration.IsExitRequested())
        {
            return;
        }

        ModeArbitration.ClearExitRequest();
        DiagnosticLog.Write("GUI mode exiting: another mode asked to take over.");
        ExitApplication();
    }

    /// <summary>
    /// Switches which executable the user wants resident. Selecting the mode already in effect is
    /// still worth honouring — it rewrites the logon entry, which is the one thing that can be out of
    /// step after an install or a repair.
    /// </summary>
    private void SwitchMode(AppMode mode)
    {
        if (mode == AppMode.Gui)
        {
            SettingsStore.SaveMode(AppMode.Gui);
            _ = Task.Run(() => ApplyStartupRegistration(GetStartupEnabled()));
            ShowFromTray();
            ShowStatus(InfoBarSeverity.Success, Text("TrayModeTitle"), Text("ModeGuiApplied"));
            return;
        }

        var exePath = ModeExecutable.Resolve(AppMode.Background);
        if (exePath is null)
        {
            // A development run, or an install from before the resident shipped. Say so instead of
            // writing a mode whose executable does not exist.
            ShowFromTray();
            ShowStatus(InfoBarSeverity.Warning, Text("TrayModeTitle"), Text("BackgroundExeMissing"));
            return;
        }

        SettingsStore.SaveMode(AppMode.Background);

        // Re-registered before the handover, and synchronously: after this process exits there is
        // nobody left to fix a logon entry that still points at the window.
        ApplyStartupRegistration(GetStartupEnabled());

        if (!ModeExecutable.TryStart(exePath, ModeExecutable.ArgumentsFor(AppMode.Background)))
        {
            SettingsStore.SaveMode(AppMode.Gui);
            ApplyStartupRegistration(GetStartupEnabled());
            ShowFromTray();
            ShowStatus(InfoBarSeverity.Error, Text("TrayModeTitle"), Text("ModeSwitchFailed"));
            return;
        }

        // Nothing has been torn down until the resident is confirmed started, so a failed launch
        // leaves a fully working window. From here the resident is parked in its own TakeOver waiting
        // for running.json to disappear, which CleanupNativeResources does by releasing it.
        DiagnosticLog.Write("GUI mode handing over to the background resident.");
        _isExiting = true;
        CleanupNativeResources();
        SingleInstanceManager.Release();
        Close();
    }

    private void ShowTrayMenu()
    {
        if (!GetCursorPos(out var point))
        {
            ShowFromTray();
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            ShowFromTray();
            return;
        }

        try
        {
            AppendMenu(menu, MfString, TrayMenuRestore, Text("Restore"));
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(menu, MfString, TrayMenuMute, Text("TrayMute"));
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(menu, MfString | MfDisabled, 0, Text("TrayGainTitle"));
            AppendMenu(menu, MfString, TrayMenuGainLow, Text("GainLow"));
            AppendMenu(menu, MfString, TrayMenuGainHigh, Text("GainHigh"));
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(menu, MfString | MfDisabled, 0, Text("TrayLedTitle"));
            AppendMenu(menu, MfString, TrayMenuLedOn, Text("LedOn"));
            AppendMenu(menu, MfString, TrayMenuLedOff, Text("LedOff"));
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(menu, MfString | MfDisabled, 0, Text("TrayFilterTitle"));
            AppendMenu(menu, MfString, TrayMenuFilterBase + 0, Text("FilterFastLowLatency"));
            AppendMenu(menu, MfString, TrayMenuFilterBase + 1, Text("FilterFastPhase"));
            AppendMenu(menu, MfString, TrayMenuFilterBase + 2, Text("FilterSlowLowLatency"));
            AppendMenu(menu, MfString, TrayMenuFilterBase + 3, Text("FilterSlowPhase"));
            AppendMenu(menu, MfString, TrayMenuFilterBase + 4, Text("FilterNos"));
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(menu, MfString | MfDisabled, 0, Text("TrayModeTitle"));

            // This menu only exists in the GUI process, so the window is always the mode running right
            // now and is labelled as such. The check mark answers the other question — which executable
            // the next logon starts — because that is the only one the two items can change.
            var startupMode = SettingsStore.GetMode();
            AppendMenu(
                menu,
                MfString | (startupMode == AppMode.Gui ? MfChecked : 0u),
                TrayMenuModeGui,
                Text("ModeGui") + Text("ModeCurrentSuffix"));
            AppendMenu(
                menu,
                MfString | (startupMode == AppMode.Background ? MfChecked : 0u),
                TrayMenuModeBackground,
                Text("ModeBackground"));
            AppendMenu(menu, MfSeparator, 0, string.Empty);
            AppendMenu(menu, MfString, TrayMenuExit, Text("Exit"));
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (command == TrayMenuRestore)
            {
                ShowFromTray();
            }
            else if (command == TrayMenuExit)
            {
                ExitApplication();
            }
            else
            {
                HandleTrayCommand(command);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleTrayCommand(int command)
    {
        switch (command)
        {
            case TrayMenuVolumeUp:
                ChangeVolumeBy(1);
                break;
            case TrayMenuVolumeDown:
                ChangeVolumeBy(-1);
                break;
            case TrayMenuMute:
                SetVolumeDirect(0);
                break;
            case TrayMenuModeGui:
                SwitchMode(AppMode.Gui);
                break;
            case TrayMenuModeBackground:
                SwitchMode(AppMode.Background);
                break;
            case TrayMenuGainLow:
                _ = RunTrayDeviceActionAsync(() => _device.TrySetGainAsync(0), () => GainButtons.SelectedIndex = 0, Text("GainUpdated"));
                break;
            case TrayMenuGainHigh:
                _ = RunTrayDeviceActionAsync(() => _device.TrySetGainAsync(1), () => GainButtons.SelectedIndex = 1, Text("GainUpdated"));
                break;
            case TrayMenuLedOn:
                _ = RunTrayDeviceActionAsync(() => _device.TrySetLedAsync(0), () => LedButtons.SelectedIndex = 0, Text("LedUpdated"));
                break;
            case TrayMenuLedOff:
                _ = RunTrayDeviceActionAsync(() => _device.TrySetLedAsync(2), () => LedButtons.SelectedIndex = 2, Text("LedUpdated"));
                break;
            case var filterCommand when filterCommand >= TrayMenuFilterBase && filterCommand < TrayMenuFilterBase + 5:
                var filter = command - TrayMenuFilterBase;
                _ = RunTrayDeviceActionAsync(() => _device.TrySetFilterAsync(filter), () => FilterBox.SelectedIndex = filter, Text("FilterUpdated"));
                break;
        }
    }

    private async Task RunTrayDeviceActionAsync(Func<Task<bool>> action, Action updateUi, string successMessage)
    {
        if (!_isDeviceConnected)
        {
            ShowStatus(InfoBarSeverity.Warning, Text("NotConnected"), Text("ConnectBeforeChanging"));
            return;
        }

        var applied = await action();
        if (!applied)
        {
            SetDeviceConnected(false);
            ShowDeviceDisconnectedStatus();
            return;
        }

        var wasApplying = _isApplying;
        _isApplying = true;
        try
        {
            updateUi();
        }
        finally
        {
            _isApplying = wasApplying;
        }

        ShowStatus(InfoBarSeverity.Success, Text("Applied"), successMessage);
    }
    private void UpdateTrayIcon(int message)
    {
        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = GetTrayIconHandle(),
            szTip = "Dawn4.4 Control",
        };
        Shell_NotifyIcon(message, ref data);
    }

    private IntPtr GetTrayIconHandle()
    {
        var iconPath = GetAppIconPath();
        if (_trayIconHandle == IntPtr.Zero && System.IO.File.Exists(iconPath))
        {
            _trayIconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, IconDefaultSize, IconDefaultSize, LrLoadFromFile | LrDefaultSize);
        }

        return _trayIconHandle != IntPtr.Zero
            ? _trayIconHandle
            : LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
    }

    private static string GetAppIconPath()
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Dawn44Control.ico");
    }

    private void TrySetAppIcon()
    {
        var iconPath = GetAppIconPath();
        if (!System.IO.File.Exists(iconPath))
        {
            return;
        }

        try
        {
            _appWindow.SetIcon(iconPath);
        }
        catch
        {
            // Missing or unreadable icon assets should not stop the controller from opening.
        }
    }

    private void RegisterHotkeys()
    {
        // Unregister legacy RegisterHotKey (kept as fallback for non-hook scenarios)
        UnregisterHotKey(_hwnd, HotkeyVolumeUp);
        UnregisterHotKey(_hwnd, HotkeyVolumeDown);

        // Start GetAsyncKeyState polling — bypasses low-level keyboard hooks (e.g. EasyAntiCheat)
        _hotkeyWatcher.Start();
    }

    private void RegisterHidDeviceNotifications()
    {
        var filter = new DevBroadcastDeviceInterface
        {
            dbcc_size = Marshal.SizeOf<DevBroadcastDeviceInterface>(),
            dbcc_devicetype = DbtDevtypDeviceInterface,
            dbcc_classguid = HidClassGuid,
        };

        _deviceNotificationHandle = RegisterDeviceNotification(_hwnd, ref filter, DeviceNotifyWindowHandle);
    }

    private void CleanupNativeResources()
    {
        _exitRequestTimer.Stop();
        _deviceChangeRefreshCts?.Cancel();
        _deviceChangeRefreshCts?.Dispose();
        _deviceChangeRefreshCts = null;
        _volumeOsdWindow?.CloseOsd();
        _volumeOsdWindow = null;
        if (_deviceNotificationHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_deviceNotificationHandle);
            _deviceNotificationHandle = IntPtr.Zero;
        }

        UnregisterHotKey(_hwnd, HotkeyVolumeUp);
        UnregisterHotKey(_hwnd, HotkeyVolumeDown);
        _hotkeyWatcher.Stop();
        RemoveWindowSubclass(_hwnd, _subclassProc, UIntPtr.Zero);
        if (_trayIconVisible)
        {
            UpdateTrayIcon(NimDelete);
            _trayIconVisible = false;
        }

        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }

        // Last, and only if this process is the recorded owner: whoever is waiting to take over is
        // watching for running.json to disappear, and should not see it go while the shortcuts and the
        // device notifications above are still live.
        ModeArbitration.Release();
    }

    private IntPtr WindowSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData)
    {
        if (SingleInstanceManager.ShowExistingWindowMessage != 0 && message == SingleInstanceManager.ShowExistingWindowMessage)
        {
            DispatcherQueue.TryEnqueue(ShowFromTray);
        }
        else if (message == WmHotkey)
        {
            var hotkeyId = wParam.ToInt32();
            if (hotkeyId == HotkeyVolumeUp)
            {
                ChangeVolumeBy(1);
                return IntPtr.Zero;
            }

            if (hotkeyId == HotkeyVolumeDown)
            {
                ChangeVolumeBy(-1);
                return IntPtr.Zero;
            }
        }
        else if (message == WmTrayIcon)
        {
            var trayMessage = lParam.ToInt32();
            if (trayMessage == WmLButtonUp)
            {
                DispatcherQueue.TryEnqueue(ShowFromTray);
                return IntPtr.Zero;
            }

            if (trayMessage == WmRButtonUp)
            {
                DispatcherQueue.TryEnqueue(ShowTrayMenu);
                return IntPtr.Zero;
            }
        }
        else if (message == WmDeviceChange)
        {
            var deviceEvent = wParam.ToInt32();
            if (deviceEvent == DbtDeviceArrival)
            {
                if (IsDawnDeviceChange(lParam))
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ShowStatus(InfoBarSeverity.Informational, Text("CheckingDevice"), Text("ReadingState"));
                        QueueDeviceChangeRefresh(removed: false);
                    });
                }
            }
            else if (deviceEvent == DbtDeviceRemoveComplete)
            {
                if (IsDawnDeviceChange(lParam))
                {
                    DispatcherQueue.TryEnqueue(() => QueueDeviceChangeRefresh(removed: true));
                }
            }
        }
        else if (message == WmSize && wParam.ToInt32() == SizeMinimized)
        {
            DispatcherQueue.TryEnqueue(HideToTray);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static bool IsDawnDeviceChange(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var header = Marshal.PtrToStructure<DevBroadcastHeader>(lParam);
            if (header.dbch_devicetype != DbtDevtypDeviceInterface || header.dbch_size <= DevBroadcastDeviceInterfaceNameOffset)
            {
                return false;
            }

            var name = Marshal.PtrToStringUni(IntPtr.Add(lParam, DevBroadcastDeviceInterfaceNameOffset));
            return IsDawnDevicePath(name);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDawnDevicePath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.Contains("vid_2fc6", StringComparison.OrdinalIgnoreCase)
            && path.Contains("pid_f067", StringComparison.OrdinalIgnoreCase);
    }

    private void LoadSettingsUi()
    {
        _isLoadingSettings = true;
        try
        {
            _language = GetLanguage();
            LanguageBox.SelectedIndex = _language == "zh" ? 1 : 0;

            var behavior = GetCloseBehavior();
            CloseBehaviorBox.SelectedIndex = behavior switch
            {
                "Tray" => 1,
                "Exit" => 2,
                _ => 0,
            };

            ResizeLockSwitch.IsOn = GetResizeLocked();
            StartupSwitch.IsOn = GetStartupEnabled();
            HotkeyOsdSwitch.IsOn = GetHotkeyOsdEnabled();
            AdminSwitch.IsOn = GetRunAsAdmin();
            BackgroundPathText.Text = GetBackgroundImageName() ?? Text("NoCustomBackground");
            AdjustBackgroundButton.Visibility = GetBackgroundImageName() is null ? Visibility.Collapsed : Visibility.Visible;
            LoadBackgroundAdjustmentUi();
            UpdateShortcutButtons();
        }
        finally
        {
            _isLoadingSettings = false;
        }

        ApplyLanguage();
        ApplyResizeLockFromSettings();
        // Startup registration can spawn schtasks, so keep it off the window construction path.
        _ = Task.Run(() => ApplyStartupRegistration(GetStartupEnabled()));
        _ = LoadBackgroundImageFromSettingsAsync();
    }

    private void EnterBackgroundAdjustmentMode()
    {
        if (CustomBackgroundImage.Source is null)
        {
            ShowStatus(InfoBarSeverity.Warning, Text("Settings"), Text("NoCustomBackground"));
            return;
        }

        SettingsOverlay.Visibility = Visibility.Collapsed;
        BackgroundAdjustPanel.Visibility = Visibility.Visible;
    }

    // Everything below forwards to Dawn44.Core.SettingsStore, which is the single reader/writer for
    // settings.json and is shared with the headless background executable. The one-line wrappers are
    // kept so the ~90 call sites in this file stay as they were.
    private static string? GetCloseBehavior() => SettingsStore.GetCloseBehavior();

    private static void SaveCloseBehavior(string behavior) => SettingsStore.SaveCloseBehavior(behavior);

    private static bool GetStartupEnabled() => SettingsStore.GetStartupEnabled();

    private static void SaveStartupEnabled(bool enabled) => SettingsStore.SaveStartupEnabled(enabled);

    private static bool GetHotkeyOsdEnabled() => SettingsStore.GetHotkeyOsdEnabled();

    private static void SaveHotkeyOsdEnabled(bool enabled) => SettingsStore.SaveHotkeyOsdEnabled(enabled);

    private static bool GetRunAsAdmin() => SettingsStore.GetRunAsAdmin();

    private static void SaveRunAsAdmin(bool enabled) => SettingsStore.SaveRunAsAdmin(enabled);

    private static bool IsCurrentProcessElevated() => Elevation.IsCurrentProcessElevated();

    private void RestartAsAdmin()
    {
        // Hand the single-instance mutex over first: the elevated child starts while this process
        // is still alive, and would otherwise treat itself as a duplicate and exit immediately.
        SingleInstanceManager.Release();

        // The window is visible right now, so do not carry --tray into the restarted instance.
        if (Elevation.TryRestartAsAdmin(Environment.GetCommandLineArgs().Skip(1), App.TraySwitch))
        {
            ExitApplication();
            return;
        }

        // User cancelled the UAC prompt — keep running and revert the toggle.
        SingleInstanceManager.TryAcquire();
        _isLoadingSettings = true;
        AdminSwitch.IsOn = false;
        _isLoadingSettings = false;
        SaveRunAsAdmin(false);
    }

    private static string GetLanguage() => SettingsStore.GetLanguage();

    private static void SaveLanguage(string language) => SettingsStore.SaveLanguage(language);

    private static string NormalizeLanguage(string? language) => SettingsStore.NormalizeLanguage(language);

    private static bool GetResizeLocked() => SettingsStore.GetResizeLocked();

    private static void SaveResizeLocked(bool locked) => SettingsStore.SaveResizeLocked(locked);

    private static string? GetBackgroundImageToken() => SettingsStore.GetBackgroundImageToken();

    private static string? GetBackgroundImageName() => SettingsStore.GetBackgroundImageName();

    private static double GetDoubleSetting(string key, double defaultValue)
        => SettingsStore.GetDoubleSetting(key, defaultValue);

    private static void SaveDoubleSetting(string key, double value)
        => SettingsStore.SaveDoubleSetting(key, value);

    private static void SaveSetting(string key, string value) => SettingsStore.SaveSetting(key, value);

    private static void RemoveSetting(string key) => SettingsStore.RemoveSetting(key);

    private void LoadBackgroundAdjustmentUi()
    {
        if (!AreBackgroundAdjustmentControlsReady())
        {
            return;
        }

        _isLoadingBackgroundAdjustment = true;
        try
        {
            BackgroundZoomSlider.Value = GetDoubleSetting(BackgroundZoomKey, 1);
            BackgroundHorizontalSlider.Value = GetDoubleSetting(BackgroundOffsetXKey, 0);
            BackgroundVerticalSlider.Value = GetDoubleSetting(BackgroundOffsetYKey, 0);
        }
        finally
        {
            _isLoadingBackgroundAdjustment = false;
        }

        ApplyBackgroundAdjustment();
    }

    private void SaveBackgroundAdjustment()
    {
        if (!AreBackgroundAdjustmentControlsReady())
        {
            return;
        }

        SaveDoubleSetting(BackgroundZoomKey, BackgroundZoomSlider.Value);
        SaveDoubleSetting(BackgroundOffsetXKey, BackgroundHorizontalSlider.Value);
        SaveDoubleSetting(BackgroundOffsetYKey, BackgroundVerticalSlider.Value);
    }

    private void ResetBackgroundAdjustmentSettings()
    {
        SaveDoubleSetting(BackgroundZoomKey, 1);
        SaveDoubleSetting(BackgroundOffsetXKey, 0);
        SaveDoubleSetting(BackgroundOffsetYKey, 0);
    }

    private void ApplyBackgroundAdjustment()
    {
        if (!AreBackgroundAdjustmentControlsReady())
        {
            return;
        }

        CustomBackgroundTransform.ScaleX = BackgroundZoomSlider.Value;
        CustomBackgroundTransform.ScaleY = BackgroundZoomSlider.Value;
        CustomBackgroundTransform.TranslateX = BackgroundHorizontalSlider.Value;
        CustomBackgroundTransform.TranslateY = BackgroundVerticalSlider.Value;
    }

    private bool AreBackgroundAdjustmentControlsReady()
    {
        return BackgroundZoomSlider is not null
            && BackgroundHorizontalSlider is not null
            && BackgroundVerticalSlider is not null
            && CustomBackgroundTransform is not null;
    }

    private async Task LoadBackgroundImageFromSettingsAsync()
    {
        var path = GetBackgroundImageToken();
        if (string.IsNullOrWhiteSpace(path))
        {
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundAdjustPanel.Visibility = Visibility.Collapsed;
            AdjustBackgroundButton.Visibility = Visibility.Collapsed;
            BackgroundPathText.Text = Text("NoCustomBackground");
            return;
        }

        try
        {
            // On a --tray launch this runs while the window is already hidden. Decoding the
            // background then would leave a full-resolution bitmap resident for the whole
            // session, so remember it and let ShowFromTray load it on first restore instead.
            if (_isHiddenToTray)
            {
                _cachedBackgroundPath = path;
                _cachedBackgroundName = GetBackgroundImageName();
                BackgroundPathText.Text = _cachedBackgroundName ?? System.IO.Path.GetFileName(path);
                AdjustBackgroundButton.Visibility = Visibility.Visible;
                return;
            }

            await ApplyBackgroundImageAsync(path, GetBackgroundImageName() ?? System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundAdjustPanel.Visibility = Visibility.Collapsed;
            AdjustBackgroundButton.Visibility = Visibility.Collapsed;
            BackgroundPathText.Text = Text("BackgroundUnavailable");
            ShowStatus(InfoBarSeverity.Warning, Text("Settings"), $"{Text("BackgroundUnavailable")}: {ex.Message}");
        }
    }

    private async Task ApplyBackgroundImageAsync(string path, string displayName)
    {
        using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        CustomBackgroundImage.Source = bitmap;
        CustomBackgroundImage.Visibility = Visibility.Visible;
        AdjustBackgroundButton.Visibility = Visibility.Visible;
        ApplyBackgroundAdjustment();
        BackgroundPathText.Text = displayName;
    }

    private static void ClearBackgroundImageSetting()
    {
        RemoveSetting(BackgroundImageTokenKey);
        RemoveSetting(BackgroundImageNameKey);
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        SetDeviceControlsEnabled(!busy && _isDeviceConnected);
    }

    private void SetDeviceConnected(bool connected)
    {
        _isDeviceConnected = connected;
        if (!connected)
        {
            _volumeWriteQueue.Reset();
        }

        SetDeviceControlsEnabled(connected && !_isApplying && !_isLoading);
    }

    private void SetDeviceControlsEnabled(bool enabled)
    {
        VolumeSlider.IsEnabled = enabled;
        FilterBox.IsEnabled = enabled;
        GainButtons.IsEnabled = enabled;
        LedButtons.IsEnabled = enabled;
    }

    private void UpdateShortcutButtons()
    {
        ShortcutUpLabelText.Text = Text("ShortcutUpLabel");
        ShortcutDownLabelText.Text = Text("ShortcutDownLabel");
        ShortcutUpButton.Content = _hotkeyCaptureTarget == HotkeyCaptureTarget.VolumeUp
            ? Text("PressShortcut")
            : FormatHotkey(SettingsStore.GetVolumeUpHotkey());
        ShortcutDownButton.Content = _hotkeyCaptureTarget == HotkeyCaptureTarget.VolumeDown
            ? Text("PressShortcut")
            : FormatHotkey(SettingsStore.GetVolumeDownHotkey());
    }

    private bool TryBuildHotkeySetting(VirtualKey key, out HotkeySetting hotkey)
    {
        hotkey = default;
        var vk = (uint)key;
        if (vk is VkShift or VkControl or VkMenu or VkLWin or VkRWin)
        {
            return false;
        }

        var modifiers = GetCurrentHotkeyModifiers();
        if (modifiers == 0)
        {
            return false;
        }

        hotkey = new HotkeySetting(modifiers, vk);
        return true;
    }

    private static uint GetCurrentHotkeyModifiers()
    {
        uint modifiers = 0;
        if (IsKeyDown(VkControl))
        {
            modifiers |= ModControl;
        }

        if (IsKeyDown(VkMenu))
        {
            modifiers |= ModAlt;
        }

        if (IsKeyDown(VkShift))
        {
            modifiers |= ModShift;
        }

        if (IsKeyDown(VkLWin) || IsKeyDown(VkRWin))
        {
            modifiers |= ModWin;
        }

        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetKeyState(virtualKey) & 0x8000) != 0;
    }

    private static string FormatHotkey(HotkeySetting hotkey)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((hotkey.Modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((hotkey.Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((hotkey.Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((hotkey.Modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatVirtualKey(hotkey.Vk));
        return string.Join(" + ", parts);
    }

    private static string FormatVirtualKey(uint vk)
    {
        return vk switch
        {
            VkUp => "Up",
            VkDown => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
            _ => ((VirtualKey)vk).ToString(),
        };
    }

    private void ShowDeviceDisconnectedStatus()
    {
        ShowStatus(InfoBarSeverity.Warning, Text("NotConnected"), Text("DeviceNotFound"));
    }

    private void ApplyLanguage()
    {
        var wasLoading = _isLoading;
        _isLoading = true;
        var gainIndex = GainButtons.SelectedIndex;
        var ledIndex = LedButtons.SelectedIndex;

        SubtitleText.Text = Text("Subtitle");
        SettingsButton.Content = Text("Settings");
        VolumeTitleText.Text = Text("Volume");
        VolumeSubtitleText.Text = Text("VolumeSubtitle");
        FilterTitleText.Text = Text("Filter");
        FilterFastLowLatencyItem.Content = Text("FilterFastLowLatency");
        FilterFastPhaseItem.Content = Text("FilterFastPhase");
        FilterSlowLowLatencyItem.Content = Text("FilterSlowLowLatency");
        FilterSlowPhaseItem.Content = Text("FilterSlowPhase");
        FilterNosItem.Content = Text("FilterNos");
        GainTitleText.Text = Text("Gain");
        GainButtons.Items[0] = Text("GainLow");
        GainButtons.Items[1] = Text("GainHigh");
        GainButtons.SelectedIndex = gainIndex;
        LedTitleText.Text = Text("Led");
        LedButtons.Items[0] = Text("LedOn");
        LedButtons.Items[1] = Text("LedTemporaryOff");
        LedButtons.Items[2] = Text("LedOff");
        LedButtons.SelectedIndex = ledIndex;
        RefreshButton.Content = Text("Refresh");
        SettingsTitleText.Text = Text("Settings");
        CloseSettingsButton.Content = Text("Close");
        LanguageTitleText.Text = Text("Language");
        CloseBehaviorTitleText.Text = Text("CloseBehavior");
        CloseAskItem.Content = Text("AskEveryTime");
        CloseTrayItem.Content = Text("MinimizeToTray");
        CloseExitItem.Content = Text("ExitApp");
        ClearCloseDefaultButton.Content = Text("ClearDefaultChoice");
        WindowBackgroundTitleText.Text = Text("WindowBackground");
        ChooseBackgroundButton.Content = Text("ChooseImage");
        ClearBackgroundButton.Content = Text("Clear");
        BackgroundAdjustTitleText.Text = Text("BackgroundAdjustment");
        AdjustBackgroundButton.Content = Text("AdjustImage");
        DoneBackgroundAdjustmentButton.Content = Text("Done");
        BackgroundZoomText.Text = Text("BackgroundZoom");
        BackgroundHorizontalText.Text = Text("BackgroundHorizontal");
        BackgroundVerticalText.Text = Text("BackgroundVertical");
        ResetBackgroundAdjustmentButton.Content = Text("ResetBackgroundAdjustment");
        WindowSizeTitleText.Text = Text("WindowSize");
        ResizeLockSwitch.Header = Text("LockWindowResizing");
        ResizeLockSwitch.OnContent = Text("Locked");
        ResizeLockSwitch.OffContent = Text("Unlocked");
        StartupTitleText.Text = Text("Startup");
        StartupSwitch.Header = Text("StartWithWindows");
        StartupSwitch.OnContent = Text("Enabled");
        StartupSwitch.OffContent = Text("Disabled");
        HotkeyOsdTitleText.Text = Text("HotkeyOsdTitle");
        HotkeyOsdSwitch.Header = Text("HotkeyOsdHeader");
        HotkeyOsdSwitch.OnContent = Text("Enabled");
        HotkeyOsdSwitch.OffContent = Text("Disabled");
        RunAsAdminTitleText.Text = Text("RunAsAdminTitle");
        AdminSwitch.Header = Text("RunAsAdminHeader");
        AdminSwitch.OnContent = Text("Enabled");
        AdminSwitch.OffContent = Text("Disabled");
        ShortcutsTitleText.Text = Text("GlobalShortcuts");
        UpdateShortcutButtons();

        if (GetBackgroundImageName() is null)
        {
            BackgroundPathText.Text = Text("NoCustomBackground");
        }

        _isLoading = wasLoading;
    }

    private string Text(string key)
    {
        var zh = _language == "zh";
        return key switch
        {
            "Subtitle" => zh ? "水月雨 USB DAC 控制" : "Moondrop USB DAC Control",
            "Settings" => zh ? "设置" : "Settings",
            "Volume" => zh ? "音量" : "Volume",
            "VolumeSubtitle" => zh ? "实时设备音量" : "Live device volume",
            "Filter" => zh ? "滤波器" : "Filter",
            "FilterFastLowLatency" => zh ? "快速滚降 低延迟" : "Fast Roll-Off Low Latency",
            "FilterFastPhase" => zh ? "快速滚降 相位补偿" : "Fast Roll-Off Phase Compensated",
            "FilterSlowLowLatency" => zh ? "慢速滚降 低延迟" : "Slow Roll-Off Low Latency",
            "FilterSlowPhase" => zh ? "慢速滚降 相位补偿" : "Slow Roll-Off Phase Compensated",
            "FilterNos" => zh ? "非过采样" : "Non-Oversampling",
            "Gain" => zh ? "增益" : "Gain",
            "GainLow" => zh ? "低" : "Low",
            "GainHigh" => zh ? "高" : "High",
            "Led" => zh ? "指示灯" : "LED",
            "LedOn" => zh ? "开启" : "On",
            "LedTemporaryOff" => zh ? "临时关闭" : "Temporary Off",
            "LedOff" => zh ? "关闭" : "Off",
            "Refresh" => zh ? "刷新" : "Refresh",
            "Close" => zh ? "关闭" : "Close",
            "Language" => zh ? "语言" : "Language",
            "CloseBehavior" => zh ? "关闭按钮行为" : "Close button behavior",
            "AskEveryTime" => zh ? "每次询问" : "Ask every time",
            "MinimizeToTray" => zh ? "最小化到托盘" : "Minimize to tray",
            "ExitApp" => zh ? "退出应用" : "Exit app",
            "ClearDefaultChoice" => zh ? "清除默认选择" : "Clear default choice",
            "WindowBackground" => zh ? "窗口背景" : "Window background",
            "ChooseImage" => zh ? "选择图片" : "Choose image",
            "Clear" => zh ? "清除" : "Clear",
            "NoCustomBackground" => zh ? "未设置自定义背景" : "No custom background",
            "BackgroundUnavailable" => zh ? "背景图片不可用" : "Background image unavailable",
            "WindowSize" => zh ? "窗口大小" : "Window size",
            "LockWindowResizing" => zh ? "锁定窗口大小" : "Lock window resizing",
            "Locked" => zh ? "已锁定" : "Locked",
            "Unlocked" => zh ? "已解锁" : "Unlocked",
            "Startup" => zh ? "开机启动" : "Startup",
            "StartWithWindows" => zh ? "随 Windows 启动" : "Start with Windows",
            "Enabled" => zh ? "已启用" : "Enabled",
            "Disabled" => zh ? "已禁用" : "Disabled",
            "StartupEnabled" => zh ? "开机启动已启用。" : "Start with Windows enabled.",
            "StartupDisabled" => zh ? "开机启动已禁用。" : "Start with Windows disabled.",
            "StartupFailed" => zh ? "开机启动设置失败。" : "Startup setting failed.",
            "GlobalShortcuts" => zh ? "全局快捷键" : "Global shortcuts",
            "ShortcutUpLabel" => zh ? "音量 +1" : "Volume +1",
            "ShortcutDownLabel" => zh ? "音量 -1" : "Volume -1",
            "PressShortcut" => zh ? "按下快捷键..." : "Press shortcut...",
            "PressShortcutHint" => zh ? "按下包含 Ctrl、Alt、Shift 或 Win 的组合键，Esc 取消。" : "Press a shortcut with Ctrl, Alt, Shift, or Win. Esc cancels.",
            "ShortcutNeedsModifier" => zh ? "快捷键需要包含 Ctrl、Alt、Shift 或 Win。" : "Shortcut needs Ctrl, Alt, Shift, or Win.",
            "ShortcutUpdated" => zh ? "快捷键已更新。" : "Shortcut updated.",
            "ShortcutRegisterFailed" => zh ? "快捷键注册失败，可能已被其他程序占用" : "Shortcut registration failed, possibly already used by another app",
            "HotkeyOsdTitle" => zh ? "快捷键音量提示" : "Shortcut volume popup",
            "HotkeyOsdHeader" => zh ? "快捷键调音量时显示弹窗" : "Show popup when using shortcuts",
            "HotkeyOsdEnabled" => zh ? "快捷键音量弹窗已启用。" : "Shortcut volume popup enabled.",
            "HotkeyOsdDisabled" => zh ? "快捷键音量弹窗已禁用。" : "Shortcut volume popup disabled.",
            "RunAsAdminTitle" => zh ? "以管理员身份运行" : "Run as administrator",
            "RunAsAdminHeader" => zh ? "以管理员权限运行，修复部分游戏中快捷键失效的问题" : "Run with admin rights to fix hotkeys in some games",
            "RunAsAdminEnabled" => zh ? "正在以管理员身份重启..." : "Restarting as administrator...",
            "RunAsAdminDisabled" => zh ? "下次启动将以普通权限运行。" : "Will launch without admin rights next time.",
            "RunAsAdminAlready" => zh ? "当前已以管理员身份运行。" : "Already running as administrator.",
            "Ready" => zh ? "就绪" : "Ready",
            "ReadingState" => zh ? "正在读取 Dawn 4.4 状态..." : "Reading Dawn 4.4 state...",
            "CheckingDevice" => zh ? "正在检测设备" : "Checking device",
            "Connected" => zh ? "已连接" : "Connected",
            "DeviceReady" => zh ? "Dawn 4.4 已就绪。" : "Dawn 4.4 is ready.",
            "DeviceConnected" => zh ? "Dawn 4.4 已连接。" : "Dawn 4.4 connected.",
            "DeviceDisconnected" => zh ? "Dawn 4.4 已断开。" : "Dawn 4.4 disconnected.",
            "NotConnected" => zh ? "未连接" : "Not connected",
            "DeviceNotFound" => zh ? "未找到 Dawn 4.4 HID 接口。" : "Dawn 4.4 HID interface was not found.",
            "ConnectBeforeChanging" => zh ? "请先连接 Dawn 4.4 再更改设置。" : "Connect Dawn 4.4 before changing settings.",
            "Applied" => zh ? "已应用" : "Applied",
            "FilterUpdated" => zh ? "滤波器已更新。" : "Filter updated.",
            "GainUpdated" => zh ? "增益已更新。" : "Gain updated.",
            "LedUpdated" => zh ? "指示灯已更新。" : "LED updated.",
            "VolumeApplied" => zh ? "音量 {0}" : "Volume {0}",
            "VolumeFailed" => zh ? "音量设置失败" : "Volume failed",
            "NotReady" => zh ? "未就绪" : "Not ready",
            "CloseBehaviorReset" => zh ? "关闭行为已重置。" : "Close behavior reset.",
            "LanguageUpdated" => zh ? "语言已更新。" : "Language updated.",
            "BackgroundUpdated" => zh ? "背景图片已更新。" : "Background image updated.",
            "BackgroundCleared" => zh ? "背景图片已清除。" : "Background image cleared.",
            "BackgroundAdjustment" => zh ? "图片调整" : "Image adjustment",
            "AdjustImage" => zh ? "调整图片" : "Adjust image",
            "Done" => zh ? "完成" : "Done",
            "BackgroundZoom" => zh ? "缩放" : "Zoom",
            "BackgroundHorizontal" => zh ? "水平位置" : "Horizontal position",
            "BackgroundVertical" => zh ? "垂直位置" : "Vertical position",
            "ResetBackgroundAdjustment" => zh ? "重置调整" : "Reset adjustment",
            "BackgroundAdjustmentReset" => zh ? "背景图片调整已重置。" : "Background image adjustment reset.",
            "WindowSizeLocked" => zh ? "窗口大小已锁定。" : "Window size locked.",
            "WindowSizeUnlocked" => zh ? "窗口大小已解锁。" : "Window size unlocked.",
            "RememberChoice" => zh ? "记住我的选择" : "Remember my choice",
            "CloseQuestion" => zh ? "关闭窗口时，Dawn 4.4 应该怎么做？" : "What should Dawn 4.4 do when you close the window?",
            "CloseDawn" => zh ? "关闭 Dawn 4.4" : "Close Dawn 4.4",
            "Cancel" => zh ? "取消" : "Cancel",
            "Restore" => zh ? "还原" : "Restore",
            "TrayVolumeUp" => zh ? "音量 +1" : "Volume +1",
            "TrayVolumeDown" => zh ? "音量 -1" : "Volume -1",
            "TrayMute" => zh ? "静音" : "Mute",
            "TrayGainTitle" => zh ? "增益" : "Gain",
            "TrayLedTitle" => zh ? "LED" : "LED",
            "TrayFilterTitle" => zh ? "滤波器" : "Filter",
            "TrayModeTitle" => zh ? "启动模式" : "Startup mode",
            "ModeGui" => zh ? "窗口模式" : "Window mode",
            "ModeBackground" => zh ? "后台模式（仅快捷键）" : "Background mode (shortcuts only)",
            "ModeCurrentSuffix" => zh ? "（当前）" : " (current)",
            "ModeGuiApplied" => zh
                ? "已设为窗口模式，开机自启将启动此窗口。"
                : "Window mode set; auto-start will launch this window.",
            "ModeSwitchFailed" => zh
                ? "后台进程启动失败，已保持窗口模式。"
                : "The background process would not start; staying in window mode.",
            "BackgroundExeMissing" => zh
                ? "此目录下没有 Dawn44.Background.exe，无法切换到后台模式。"
                : "Dawn44.Background.exe is not installed here, so background mode is unavailable.",
            "Exit" => zh ? "退出" : "Exit",
            _ => key,
        };
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void ResizeWindow(int width, int height)
    {
        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void PositionWindowNearRight()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + Math.Max(24, workArea.Width - DefaultWindowWidth - 80);
        var y = workArea.Y + Math.Max(24, (workArea.Height - DefaultWindowHeight) / 2);
        _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private void ApplyResizeLockFromSettings()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = !GetResizeLocked();
        }
    }

    private static bool ApplyStartupRegistration(bool enabled)
    {
        // The logon entry has to name the executable for the mode the user chose, not whichever one
        // happens to be applying it — this is the step that is easy to miss, and missing it means a
        // switch to background mode still starts the window at the next logon. Null means "this
        // process", which is both the answer for GUI mode and the fallback when the resident is not
        // installed beside us.
        var mode = SettingsStore.GetMode();
        var exePath = mode == AppMode.Background ? ModeExecutable.Resolve(AppMode.Background) : null;
        if (mode == AppMode.Background && exePath is null)
        {
            mode = AppMode.Gui;
        }

        // Run-as-administrator makes the scheduled task the preferred mechanism, because an elevated
        // app cannot auto-start from the HKCU Run key without a logon-time UAC prompt that nobody
        // answers.
        return StartupRegistration.Apply(
            enabled,
            exePath,
            ModeExecutable.ArgumentsFor(mode),
            SettingsStore.GetRunAsAdmin());
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return DawnProtocol.Clamp(value, minimum, maximum);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastHeader
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastDeviceInterface
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, ref DevBroadcastDeviceInterface notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc subclassProc, UIntPtr subclassId, UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc subclassProc, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}












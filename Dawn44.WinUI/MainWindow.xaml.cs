using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace Dawn44.WinUI;

public sealed partial class MainWindow : Window
{
    private static readonly string SettingsDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dawn4.4 Control");
    private static readonly string SettingsFilePath = System.IO.Path.Combine(SettingsDirectory, "settings.json");
    private static Dictionary<string, string>? _settingsCache;
    private static readonly Guid HidClassGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const int DefaultWindowWidth = 660;
    private const int DefaultWindowHeight = 1460;
    private const int HotkeyVolumeUp = 0x4441;
    private const int HotkeyVolumeDown = 0x4442;
    private const int VolumeWriteIntervalMs = 28;
    private const int MaxVolumeStepPerWrite = 2;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModAltControl = ModAlt | ModControl;
    private const uint VkUp = 0x26;
    private const uint VkDown = 0x28;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
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
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
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
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint MfString = 0x00000000;
    private const uint MfDisabled = 0x00000002;
    private const uint MfSeparator = 0x00000800;
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryName = "Dawn4.4 Control";
    private const string CloseBehaviorKey = "CloseBehavior";
    private const string BackgroundImageTokenKey = "BackgroundImageToken";
    private const string BackgroundImageNameKey = "BackgroundImageName";
    private const string BackgroundZoomKey = "BackgroundZoom";
    private const string BackgroundOffsetXKey = "BackgroundOffsetX";
    private const string BackgroundOffsetYKey = "BackgroundOffsetY";
    private const string ResizeLockedKey = "ResizeLocked";
    private const string LanguageKey = "Language";
    private const string HotkeyVolumeUpModifiersKey = "HotkeyVolumeUpModifiers";
    private const string HotkeyVolumeUpVkKey = "HotkeyVolumeUpVk";
    private const string HotkeyVolumeDownModifiersKey = "HotkeyVolumeDownModifiers";
    private const string HotkeyVolumeDownVkKey = "HotkeyVolumeDownVk";
    private const string StartupEnabledKey = "StartupEnabled";

    private readonly DawnHidDevice _device = new();
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly SubclassProc _subclassProc;
    private readonly object _volumeWriteLock = new();
    private int? _queuedVolume;
    private int? _lastAppliedVolume;
    private IntPtr _trayIconHandle;
    private IntPtr _deviceNotificationHandle;
    private CancellationTokenSource? _deviceChangeRefreshCts;
    private VolumeOsdWindow? _volumeOsdWindow;
    private bool _isVolumeWriteLoopActive;
    private bool _trayIconVisible;
    private bool _isLoading;
    private bool _isApplying;
    private bool _isExiting;
    private bool _isLoadingSettings;
    private bool _isLoadingBackgroundAdjustment = true;
    private bool _isDeviceConnected;
    private bool _hasCompletedInitialRefresh;
    private bool _startMinimizedToTray;
    private string _language = "en";
    private HotkeyCaptureTarget _hotkeyCaptureTarget = HotkeyCaptureTarget.None;

    private enum HotkeyCaptureTarget
    {
        None,
        VolumeUp,
        VolumeDown,
    }

    private readonly record struct HotkeySetting(uint Modifiers, uint Vk);

    public MainWindow(bool startMinimizedToTray = false)
    {
        InitializeComponent();
        _startMinimizedToTray = startMinimizedToTray;
        _isLoadingBackgroundAdjustment = false;
        ExtendsContentIntoTitleBar = true;

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _subclassProc = WindowSubclassProc;
        ResizeWindow(DefaultWindowWidth, DefaultWindowHeight);
        PositionWindowNearRight();
        LoadSettingsUi();
        RegisterHidDeviceNotifications();
        InitializeTrayIcon();
        RegisterHotkeys();
        SetWindowSubclass(_hwnd, _subclassProc, UIntPtr.Zero, UIntPtr.Zero);
        _appWindow.Closing += AppWindow_Closing;

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
            SaveHotkey(HotkeyVolumeUpModifiersKey, HotkeyVolumeUpVkKey, hotkey);
        }
        else
        {
            SaveHotkey(HotkeyVolumeDownModifiersKey, HotkeyVolumeDownVkKey, hotkey);
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

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        SaveStartupEnabled(StartupSwitch.IsOn);
        var applied = ApplyStartupRegistration(StartupSwitch.IsOn);
        ShowStatus(
            applied ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            Text("Startup"),
            applied
                ? (StartupSwitch.IsOn ? Text("StartupEnabled") : Text("StartupDisabled"))
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

        if (removed)
        {
            SetDeviceConnected(false);
            ShowDeviceDisconnectedStatus();
            ShowTrayNotification(Text("NotConnected"), Text("DeviceDisconnected"));
        }

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

        lock (_volumeWriteLock)
        {
            _queuedVolume = Clamp(volume, 0, 60);
            if (_isVolumeWriteLoopActive)
            {
                return;
            }

            _isVolumeWriteLoopActive = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    int targetVolume;
                    int volumeToApply;
                    lock (_volumeWriteLock)
                    {
                        if (_queuedVolume is null)
                        {
                            _isVolumeWriteLoopActive = false;
                            return;
                        }

                        targetVolume = _queuedVolume.Value;
                        volumeToApply = MoveToward(_lastAppliedVolume ?? targetVolume, targetVolume, MaxVolumeStepPerWrite);
                    }

                    if (_lastAppliedVolume != volumeToApply)
                    {
                        var applied = await _device.TrySetVolumeAsync(volumeToApply);
                        if (!applied)
                        {
                            lock (_volumeWriteLock)
                            {
                                _queuedVolume = null;
                                _isVolumeWriteLoopActive = false;
                            }

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                SetDeviceConnected(false);
                                ShowDeviceDisconnectedStatus();
                            });
                            return;
                        }

                        var reachedTarget = false;
                        lock (_volumeWriteLock)
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
                            DispatcherQueue.TryEnqueue(() => ShowStatus(InfoBarSeverity.Success, Text("Applied"), string.Format(Text("VolumeApplied"), volumeToApply)));
                        }
                    }
                    else
                    {
                        lock (_volumeWriteLock)
                        {
                            if (_queuedVolume == targetVolume)
                            {
                                _queuedVolume = null;
                            }
                        }
                    }

                    await Task.Delay(VolumeWriteIntervalMs);
                }
            }
            catch (Exception ex)
            {
                lock (_volumeWriteLock)
                {
                    _queuedVolume = null;
                    _isVolumeWriteLoopActive = false;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ex is OperationCanceledException)
                    {
                        return;
                    }

                    ShowStatus(InfoBarSeverity.Error, Text("VolumeFailed"), ex.Message);
                });
            }
        });
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
            _lastAppliedVolume = state.Volume;
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
            ShowVolumeOsd(next);
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
        ShowVolumeOsd(next);
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
        _appWindow.Hide();
    }

    private void ShowFromTray()
    {
        _appWindow.Show();
        ShowWindow(_hwnd, SwRestore);
        ShowWindow(_hwnd, SwShow);
        if (GetResizeLocked())
        {
            _appWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
        }

        SetForegroundWindow(_hwnd);
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        CleanupNativeResources();
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
        return _trayIconHandle != IntPtr.Zero
            ? _trayIconHandle
            : LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
    }

    private void RegisterHotkeys()
    {
        UnregisterHotKey(_hwnd, HotkeyVolumeUp);
        UnregisterHotKey(_hwnd, HotkeyVolumeDown);

        var up = GetVolumeUpHotkey();
        var down = GetVolumeDownHotkey();
        var upRegistered = RegisterHotKey(_hwnd, HotkeyVolumeUp, up.Modifiers, up.Vk);
        var upError = Marshal.GetLastWin32Error();
        var downRegistered = RegisterHotKey(_hwnd, HotkeyVolumeDown, down.Modifiers, down.Vk);
        var downError = Marshal.GetLastWin32Error();

        if (!upRegistered || !downRegistered)
        {
            var failed = new List<string>();
            if (!upRegistered)
            {
                failed.Add($"{Text("ShortcutUpLabel")} ({FormatHotkey(up)})");
            }

            if (!downRegistered)
            {
                failed.Add($"{Text("ShortcutDownLabel")} ({FormatHotkey(down)})");
            }

            var error = !upRegistered ? upError : downError;
            ShowStatus(
                InfoBarSeverity.Warning,
                Text("GlobalShortcuts"),
                $"{Text("ShortcutRegisterFailed")}: {string.Join(", ", failed)} ({error})");
        }
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
    }

    private IntPtr WindowSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData)
    {
        if (message == WmHotkey)
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
                DispatcherQueue.TryEnqueue(() =>
                {
                    ShowStatus(InfoBarSeverity.Informational, Text("CheckingDevice"), Text("ReadingState"));
                    QueueDeviceChangeRefresh(removed: false);
                });
            }
            else if (deviceEvent == DbtDeviceRemoveComplete)
            {
                DispatcherQueue.TryEnqueue(() => QueueDeviceChangeRefresh(removed: true));
            }
        }
        else if (message == WmSize && wParam.ToInt32() == SizeMinimized)
        {
            DispatcherQueue.TryEnqueue(HideToTray);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
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
        ApplyStartupRegistration(GetStartupEnabled());
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

    private static string? GetCloseBehavior()
    {
        return GetStringSetting(CloseBehaviorKey, "Ask");
    }

    private static void SaveCloseBehavior(string behavior)
    {
        SaveSetting(CloseBehaviorKey, behavior);
    }

    private static bool GetStartupEnabled()
    {
        return GetBoolSetting(StartupEnabledKey, false);
    }

    private static void SaveStartupEnabled(bool enabled)
    {
        SaveSetting(StartupEnabledKey, enabled ? "true" : "false");
    }

    private static HotkeySetting GetVolumeUpHotkey()
    {
        return GetHotkey(HotkeyVolumeUpModifiersKey, HotkeyVolumeUpVkKey, new HotkeySetting(ModAltControl, VkUp));
    }

    private static HotkeySetting GetVolumeDownHotkey()
    {
        return GetHotkey(HotkeyVolumeDownModifiersKey, HotkeyVolumeDownVkKey, new HotkeySetting(ModAltControl, VkDown));
    }

    private static HotkeySetting GetHotkey(string modifiersKey, string vkKey, HotkeySetting defaultValue)
    {
        var modifiers = GetUIntSetting(modifiersKey, defaultValue.Modifiers);
        var vk = GetUIntSetting(vkKey, defaultValue.Vk);

        return modifiers == 0 || vk == 0
            ? defaultValue
            : new HotkeySetting(modifiers, vk);
    }

    private static uint ConvertSettingToUInt32(string? value, uint defaultValue)
    {
        return uint.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static void SaveHotkey(string modifiersKey, string vkKey, HotkeySetting hotkey)
    {
        SaveSetting(modifiersKey, hotkey.Modifiers.ToString());
        SaveSetting(vkKey, hotkey.Vk.ToString());
    }

    private static string GetLanguage()
    {
        return NormalizeLanguage(GetStringSetting(LanguageKey, null));
    }

    private static void SaveLanguage(string language)
    {
        SaveSetting(LanguageKey, NormalizeLanguage(language));
    }

    private static string NormalizeLanguage(string? language)
    {
        return string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }

    private static bool GetResizeLocked()
    {
        return GetBoolSetting(ResizeLockedKey, true);
    }

    private static void SaveResizeLocked(bool locked)
    {
        SaveSetting(ResizeLockedKey, locked ? "true" : "false");
    }

    private static string? GetBackgroundImageToken()
    {
        return GetStringSetting(BackgroundImageTokenKey, null);
    }

    private static string? GetBackgroundImageName()
    {
        return GetStringSetting(BackgroundImageNameKey, null);
    }

    private static double GetDoubleSetting(string key, double defaultValue)
    {
        var value = GetStringSetting(key, null);
        return double.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static void SaveDoubleSetting(string key, double value)
    {
        SaveSetting(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? GetStringSetting(string key, string? defaultValue)
    {
        return GetSettings().TryGetValue(key, out var value) ? value : defaultValue;
    }

    private static bool GetBoolSetting(string key, bool defaultValue)
    {
        var value = GetStringSetting(key, null);
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static uint GetUIntSetting(string key, uint defaultValue)
    {
        return ConvertSettingToUInt32(GetStringSetting(key, null), defaultValue);
    }

    private static void SaveSetting(string key, string value)
    {
        var settings = GetSettings();
        settings[key] = value;
        SaveSettings();
    }

    private static void RemoveSetting(string key)
    {
        var settings = GetSettings();
        if (settings.Remove(key))
        {
            SaveSettings();
        }
    }

    private static Dictionary<string, string> GetSettings()
    {
        if (_settingsCache is not null)
        {
            return _settingsCache;
        }

        try
        {
            if (System.IO.File.Exists(SettingsFilePath))
            {
                var json = System.IO.File.ReadAllText(SettingsFilePath);
                _settingsCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                return _settingsCache;
            }
        }
        catch
        {
            // Corrupt settings should not prevent the controller from opening.
        }

        _settingsCache = new();
        return _settingsCache;
    }

    private static void SaveSettings()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(GetSettings(), new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Settings persistence is best-effort; device control should continue working.
        }
    }

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
            lock (_volumeWriteLock)
            {
                _queuedVolume = null;
                _isVolumeWriteLoopActive = false;
                _lastAppliedVolume = null;
            }
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
            : FormatHotkey(GetVolumeUpHotkey());
        ShortcutDownButton.Content = _hotkeyCaptureTarget == HotkeyCaptureTarget.VolumeDown
            ? Text("PressShortcut")
            : FormatHotkey(GetVolumeDownHotkey());
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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                key.SetValue(RunRegistryName, $"\"{exePath}\" --tray");
            }
            else
            {
                key.DeleteValue(RunRegistryName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Min(Math.Max(value, minimum), maximum);
    }

    private static int MoveToward(int current, int target, int maximumStep)
    {
        if (current == target)
        {
            return target;
        }

        var delta = target - current;
        if (Math.Abs(delta) <= maximumStep)
        {
            return target;
        }

        return current + Math.Sign(delta) * maximumStep;
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
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












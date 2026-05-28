using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace OpenFinger.Control;

public partial class MainWindow
{
    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsRunValueName = "OpenFinger.Control";
    private const string WindowsStartupArgument = "--startup";
    private static readonly TimeSpan HiddenTrayRefreshInterval = TimeSpan.FromSeconds(10);

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private bool _allowClose;
    private bool _shellLoaded;
    private bool _startHiddenOnLaunch;
    private bool _isHiddenToTray;
    private bool _trayReducedLoadActive;
    private bool _suspendRawLogUiUpdates;
    private string _currentPageKey = UiPageCatalog.Home;
    private string _lastDeviceNotificationSignature = string.Empty;

    private enum ClientNotificationKind
    {
        Device,
        Flash,
        Driver
    }

    public void ConfigureStartupMode(bool startHiddenOnLaunch)
    {
        _startHiddenOnLaunch = startHiddenOnLaunch;
        if (!startHiddenOnLaunch)
        {
            return;
        }

        ShowActivated = false;
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
    }

    private void InitializeShell()
    {
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this, _config.Ui.ThemeMode);
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplyThemeFromConfig();
        ApplyWindowSettingsFromConfig();
        ApplyInitialNavigationFromConfig();
    }

    private void CompleteShellStartup()
    {
        _shellLoaded = true;
        EnsureTrayIcon();
        UpdateTrayIconVisibility();
        if (_startHiddenOnLaunch)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => HideToTray(showNotification: false)));
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized || _isHiddenToTray)
        {
            return;
        }

        if (_config.Ui.Window.RememberBounds)
        {
            SaveWindowPlacement();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            SaveWindowPlacement();
            DisposeTrayIcon();
            return;
        }

        var action = UiCloseActionCatalog.Normalize(_config.Ui.Tray.CloseButtonAction);
        if (string.Equals(action, UiCloseActionCatalog.Close, StringComparison.OrdinalIgnoreCase))
        {
            SaveWindowPlacement();
            DisposeTrayIcon();
            return;
        }

        if (string.Equals(action, UiCloseActionCatalog.Tray, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            HideToTray(showNotification: true);
            return;
        }

        e.Cancel = true;
        var dialog = new CloseBehaviorDialog
        {
            Owner = this
        };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case CloseBehaviorChoice.Close:
                if (dialog.RememberChoice)
                {
                    SetCloseButtonAction(UiCloseActionCatalog.Close, showStatus: false);
                }

                RequestExitFromUi();
                break;
            case CloseBehaviorChoice.Tray:
                if (dialog.RememberChoice)
                {
                    SetCloseButtonAction(UiCloseActionCatalog.Tray, showStatus: false);
                }

                HideToTray(showNotification: true);
                break;
            default:
                break;
        }
    }

    private void RequestExitFromUi()
    {
        _allowClose = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
    }

    private void ApplyWindowSettingsFromConfig()
    {
        if (!_config.Ui.Window.RememberBounds)
        {
            return;
        }

        Width = Math.Max(MinWidth, _config.Ui.Window.Width);
        Height = Math.Max(MinHeight, _config.Ui.Window.Height);

        if (IsUsableHorizontal(_config.Ui.Window.Left, Width))
        {
            Left = _config.Ui.Window.Left;
        }

        if (IsUsableVertical(_config.Ui.Window.Top, Height))
        {
            Top = _config.Ui.Window.Top;
        }

        if (_config.Ui.Window.WasMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsUsableHorizontal(double value, double span)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || span <= 0)
        {
            return false;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        return value + 120 >= virtualLeft && value <= virtualRight - 120;
    }

    private static bool IsUsableVertical(double value, double span)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || span <= 0)
        {
            return false;
        }

        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        return value + 120 >= virtualTop && value <= virtualBottom - 120;
    }

    private void SaveWindowPlacement()
    {
        if (!_config.Ui.Window.RememberBounds)
        {
            return;
        }

        var sourceState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        var bounds = sourceState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

        _config.Ui.Window.Width = Math.Max(MinWidth, bounds.Width);
        _config.Ui.Window.Height = Math.Max(MinHeight, bounds.Height);
        if (!double.IsNaN(bounds.Left) && !double.IsInfinity(bounds.Left))
        {
            _config.Ui.Window.Left = bounds.Left;
        }

        if (!double.IsNaN(bounds.Top) && !double.IsInfinity(bounds.Top))
        {
            _config.Ui.Window.Top = bounds.Top;
        }
        _config.Ui.Window.WasMaximized = sourceState == WindowState.Maximized;
        _configStore.Save(_config);
    }

    private void ApplyInitialNavigationFromConfig()
    {
        var page = _config.Ui.Navigation.RememberLastPage
            ? _config.Ui.Navigation.LastPage
            : _config.Ui.Navigation.LaunchPage;
        page = UiPageCatalog.NormalizePage(page);

        switch (page)
        {
            case UiPageCatalog.Status:
                NavigateToStatus();
                break;
            case UiPageCatalog.Devices:
                NavigateToDevices();
                break;
            case UiPageCatalog.Firmware:
                NavigateToFirmware();
                break;
            case UiPageCatalog.Calibration:
                NavigateToCalibration();
                break;
            case UiPageCatalog.SteamVr:
                NavigateToDiagnostics();
                break;
            case UiPageCatalog.Settings:
                NavigateToSettings();
                break;
            case UiPageCatalog.About:
                NavigateToAbout();
                break;
            default:
                NavigateToHome();
                break;
        }
    }

    private void RememberActivePage(UIElement activePage)
    {
        _currentPageKey = ResolvePageKey(activePage);
        if (!_config.Ui.Navigation.RememberLastPage
            || string.Equals(_config.Ui.Navigation.LastPage, _currentPageKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _config.Ui.Navigation.LastPage = _currentPageKey;
        _configStore.Save(_config);
    }

    private string ResolvePageKey(UIElement activePage)
    {
        if (ReferenceEquals(activePage, StatusPageView))
        {
            return UiPageCatalog.Status;
        }

        if (ReferenceEquals(activePage, DevicesPageView))
        {
            return UiPageCatalog.Devices;
        }

        if (ReferenceEquals(activePage, FirmwarePageView))
        {
            return UiPageCatalog.Firmware;
        }

        if (ReferenceEquals(activePage, CalibrationPageView))
        {
            return UiPageCatalog.Calibration;
        }

        if (ReferenceEquals(activePage, DiagnosticsPageView))
        {
            return UiPageCatalog.SteamVr;
        }

        if (ReferenceEquals(activePage, SettingsPageView))
        {
            return UiPageCatalog.Settings;
        }

        if (ReferenceEquals(activePage, AboutPageView))
        {
            return UiPageCatalog.About;
        }

        return UiPageCatalog.Home;
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.BeginInvoke(() => RestoreFromTray()));
        _trayMenu.Items.Add("打开状态页", null, (_, _) => Dispatcher.BeginInvoke(() => RestoreFromTray(UiPageCatalog.Status)));
        _trayMenu.Items.Add("打开设置", null, (_, _) => Dispatcher.BeginInvoke(() => RestoreFromTray(UiPageCatalog.Settings)));
        _trayMenu.Items.Add("关于 OpenFinger", null, (_, _) => Dispatcher.BeginInvoke(() => RestoreFromTray(UiPageCatalog.About)));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出 OpenFinger", null, (_, _) => Dispatcher.BeginInvoke(() => ExitFromTrayMenu()));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "OpenFinger",
            Icon = CreateTrayIcon(),
            Visible = false,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => RestoreFromTray());
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
            {
                return Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? Drawing.SystemIcons.Application;
            }
        }
        catch
        {
        }

        return Drawing.SystemIcons.Application;
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
    }

    private void UpdateTrayIconVisibility()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var mode = UiTrayVisibilityCatalog.Normalize(_config.Ui.Tray.Visibility);
        _trayIcon.Visible = string.Equals(mode, UiTrayVisibilityCatalog.Always, StringComparison.OrdinalIgnoreCase)
            || _isHiddenToTray;
    }

    private void HideToTray(bool showNotification)
    {
        EnsureTrayIcon();
        _isHiddenToTray = true;
        ShowInTaskbar = false;
        Hide();
        ApplyHiddenLoadPolicy(true);
        UpdateTrayIconVisibility();
        if (showNotification)
        {
            MaybeShowTrayNotification("OpenFinger", "窗口已进入托盘，仍会保持核心连接。", ClientNotificationKind.Device, Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray(string? pageKey = null)
    {
        _isHiddenToTray = false;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _config.Ui.Window.WasMaximized ? WindowState.Maximized : WindowState.Normal;
        }

        Activate();
        if (!string.IsNullOrWhiteSpace(pageKey))
        {
            NavigateToPage(pageKey);
        }

        ApplyHiddenLoadPolicy(false);
        UpdateTrayIconVisibility();
        _ = RefreshAllAsync(forceSerialProbe: true);
    }

    private void ExitFromTrayMenu()
    {
        SaveWindowPlacement();
        _allowClose = true;
        Close();
    }

    private void ApplyHiddenLoadPolicy(bool hiddenToTray)
    {
        if (hiddenToTray && _config.Ui.Tray.ReduceLoadWhenHidden)
        {
            _suspendRawLogUiUpdates = true;
            _refreshTimer.Interval = HiddenTrayRefreshInterval;
            _portAgeTimer.Stop();
            _runtimeUiTimer.Stop();
            _trayReducedLoadActive = true;
            return;
        }

        if (!_trayReducedLoadActive)
        {
            return;
        }

        _suspendRawLogUiUpdates = false;
        _refreshTimer.Interval = DeviceRefreshInterval;
        if (_shellLoaded)
        {
            _portAgeTimer.Start();
            _runtimeUiTimer.Start();
        }

        _trayReducedLoadActive = false;
    }

    private void NavigateToPage(string pageKey)
    {
        switch (UiPageCatalog.NormalizePage(pageKey))
        {
            case UiPageCatalog.Status:
                NavigateToStatus();
                break;
            case UiPageCatalog.Devices:
                NavigateToDevices();
                break;
            case UiPageCatalog.Firmware:
                NavigateToFirmware();
                break;
            case UiPageCatalog.Calibration:
                NavigateToCalibration();
                break;
            case UiPageCatalog.SteamVr:
                NavigateToDiagnostics();
                break;
            case UiPageCatalog.Settings:
                NavigateToSettings();
                break;
            case UiPageCatalog.About:
                NavigateToAbout();
                break;
            default:
                NavigateToHome();
                break;
        }
    }

    private void MaybeShowTrayNotification(string title, string message, ClientNotificationKind kind, Forms.ToolTipIcon icon)
    {
        if (_trayIcon is null || !_config.Ui.Notifications.EnableTrayNotifications)
        {
            return;
        }

        if (!_trayIcon.Visible)
        {
            return;
        }

        if (kind == ClientNotificationKind.Device && !_config.Ui.Notifications.DeviceEvents)
        {
            return;
        }

        if (kind == ClientNotificationKind.Flash && !_config.Ui.Notifications.FlashResults)
        {
            return;
        }

        if (kind == ClientNotificationKind.Driver && !_config.Ui.Notifications.DriverResults)
        {
            return;
        }

        try
        {
            _trayIcon.ShowBalloonTip(4000, title, message, icon);
        }
        catch
        {
        }
    }

    private void NotifyDeviceSummaryChanged()
    {
        var signature = $"{_vm.Devices.Count}|{_vm.Devices.Count(item => item.WifiStatus.Contains("已连接", StringComparison.OrdinalIgnoreCase) || item.WifiStatus.Contains("在线", StringComparison.OrdinalIgnoreCase))}|{_vm.Devices.Count(item => item.Online)}";
        if (string.IsNullOrWhiteSpace(_lastDeviceNotificationSignature))
        {
            _lastDeviceNotificationSignature = signature;
            return;
        }

        if (string.Equals(_lastDeviceNotificationSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _lastDeviceNotificationSignature = signature;
        MaybeShowTrayNotification("OpenFinger 设备状态", _vm.StatusLine, ClientNotificationKind.Device, Forms.ToolTipIcon.Info);
    }

    private static string BuildWindowsStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("无法确认当前程序路径。");
        }

        return $"\"{processPath}\" {WindowsStartupArgument}";
    }

    private static bool IsStartupLaunch()
    {
        return Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, WindowsStartupArgument, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyWindowsStartupRegistration(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的启动项注册表。");

        if (!enabled)
        {
            key.DeleteValue(WindowsRunValueName, false);
            return;
        }

        key.SetValue(WindowsRunValueName, BuildWindowsStartupCommand());
    }

    public void SetCloseButtonAction(string value, bool showStatus = true)
    {
        _config.Ui.Tray.CloseButtonAction = UiCloseActionCatalog.Normalize(value);
        _configStore.Save(_config);
        RefreshUiFromState();
        if (showStatus)
        {
            SetPinnedStatusLine($"关闭按钮行为已改为：{UiCloseActionCatalog.Options.First(item => item.Value == _config.Ui.Tray.CloseButtonAction).Label}。", 4);
        }
    }

    public void SetTrayVisibilityMode(string value)
    {
        _config.Ui.Tray.Visibility = UiTrayVisibilityCatalog.Normalize(value);
        _configStore.Save(_config);
        UpdateTrayIconVisibility();
        RefreshUiFromState();
        SetPinnedStatusLine($"托盘图标显示方式已更新。", 4);
    }

    public void SetReduceLoadWhenHidden(bool enabled)
    {
        _config.Ui.Tray.ReduceLoadWhenHidden = enabled;
        _configStore.Save(_config);
        if (_isHiddenToTray)
        {
            ApplyHiddenLoadPolicy(true);
        }

        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "进入托盘后会降低界面刷新负载。" : "进入托盘后将保持原有刷新节奏。", 4);
    }

    public void SetRememberWindowBounds(bool enabled)
    {
        _config.Ui.Window.RememberBounds = enabled;
        if (enabled)
        {
            SaveWindowPlacement();
        }
        else
        {
            _configStore.Save(_config);
        }

        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "已启用窗口位置与大小记忆。" : "已关闭窗口位置与大小记忆。", 4);
    }

    public void ResetClosePromptBehavior()
    {
        _config.Ui.Tray.CloseButtonAction = UiCloseActionCatalog.Ask;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine("下次点关闭按钮时会重新询问。", 4);
    }

    public void SetEnableWindowsStartup(bool enabled)
    {
        try
        {
            ApplyWindowsStartupRegistration(enabled);
            _config.Ui.Tray.EnableWindowsStartup = enabled;
            if (!enabled)
            {
                _config.Ui.Tray.StartHiddenOnWindowsStartup = false;
            }

            _configStore.Save(_config);
            RefreshUiFromState();
            SetPinnedStatusLine(enabled ? "已开启 Windows 开机自启。" : "已关闭 Windows 开机自启。", 4);
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"设置 Windows 开机自启失败: {ex.Message}", 8);
        }
    }

    public void SetStartHiddenOnWindowsStartup(bool enabled)
    {
        _config.Ui.Tray.StartHiddenOnWindowsStartup = _config.Ui.Tray.EnableWindowsStartup && enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(_config.Ui.Tray.StartHiddenOnWindowsStartup ? "已开启“开机自启时直接进托盘”。" : "已关闭“开机自启时直接进托盘”。", 4);
    }

    public void SetLaunchPage(string pageKey)
    {
        _config.Ui.Navigation.LaunchPage = UiPageCatalog.NormalizePage(pageKey);
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine($"启动默认页面已改为：{UiPageCatalog.GetLabel(_config.Ui.Navigation.LaunchPage)}。", 4);
    }

    public void SetRememberLastPage(bool enabled)
    {
        _config.Ui.Navigation.RememberLastPage = enabled;
        if (enabled)
        {
            _config.Ui.Navigation.LastPage = _currentPageKey;
        }

        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "下次启动会记住上次停留页面。" : "下次启动将使用固定默认页面。", 4);
    }

    public void SetEnableTrayNotifications(bool enabled)
    {
        _config.Ui.Notifications.EnableTrayNotifications = enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "已开启托盘提示。" : "已关闭托盘提示。", 4);
    }

    public void SetDeviceNotificationsEnabled(bool enabled)
    {
        _config.Ui.Notifications.DeviceEvents = enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "已开启设备事件提示。" : "已关闭设备事件提示。", 4);
    }

    public void SetFlashNotificationsEnabled(bool enabled)
    {
        _config.Ui.Notifications.FlashResults = enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "已开启刷写结果提示。" : "已关闭刷写结果提示。", 4);
    }

    public void SetDriverNotificationsEnabled(bool enabled)
    {
        _config.Ui.Notifications.DriverResults = enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "已开启驱动结果提示。" : "已关闭驱动结果提示。", 4);
    }


    public void SetThemeMode(string value)
    {
        var normalized = UiThemeCatalog.Normalize(value);
        _config.Ui.ThemeMode = normalized;
        _configStore.Save(_config);
        ApplyThemeFromConfig();
        RefreshUiFromState();
        SetPinnedStatusLine($"已切换为 {ThemeManager.DescribeResolved(normalized)} 主题。", 4);
    }

    private void ApplyThemeFromConfig()
    {
        ThemeManager.ApplyThemeMode(_config.Ui.ThemeMode);
        ThemeManager.ApplyWindowChrome(this, _config.Ui.ThemeMode);
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!string.Equals(_config.Ui.ThemeMode, UiThemeCatalog.System, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ApplyThemeFromConfig();
            RefreshUiFromState();
        }));
    }

    public void OpenConfigDirectory()
    {
        var directory = Path.GetDirectoryName(_configStore.PathValue);
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetPinnedStatusLine("没有找到配置目录。", 6);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private SettingsDashboardState BuildSettingsDashboardState()
    {
        return new SettingsDashboardState
        {
            ShowAdvanced = _config.Ui.ShowAdvanced,
            CloseButtonAction = _config.Ui.Tray.CloseButtonAction,
            TrayVisibility = _config.Ui.Tray.Visibility,
            ReduceLoadWhenHidden = _config.Ui.Tray.ReduceLoadWhenHidden,
            RememberWindowBounds = _config.Ui.Window.RememberBounds,
            RememberLastPage = _config.Ui.Navigation.RememberLastPage,
            LaunchPage = _config.Ui.Navigation.LaunchPage,
            EnableWindowsStartup = _config.Ui.Tray.EnableWindowsStartup,
            StartHiddenOnWindowsStartup = _config.Ui.Tray.StartHiddenOnWindowsStartup,
            EnableTrayNotifications = _config.Ui.Notifications.EnableTrayNotifications,
            EnableDeviceNotifications = _config.Ui.Notifications.DeviceEvents,
            EnableFlashNotifications = _config.Ui.Notifications.FlashResults,
            EnableDriverNotifications = _config.Ui.Notifications.DriverResults,
            ThemeMode = _config.Ui.ThemeMode,
            ConfigPath = _configStore.PathValue
        };
    }
}

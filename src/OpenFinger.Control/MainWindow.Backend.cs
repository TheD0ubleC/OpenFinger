using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace OpenFinger.Control;

public partial class MainWindow
{
    private const int FingerTestRuntimePort = 39004;
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PortAgeRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RuntimeUiRefreshInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan SerialProbeInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SerialStatusCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RuntimePresentFreshFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RuntimeStaleAfter = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan TrackingDisableGrace = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan RawLogUiUpdateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DashboardUiRefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecentRuntimeForSerialSkip = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan WifiReachabilityCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeviceHeartbeatFreshFor = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ProcessStatusRefreshInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PortInventoryCacheTtl = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan FirmwarePortReadyTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan FullDeviceRefreshWhileRuntimeActive = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan VisiblePageRefreshWhileRuntimeActive = TimeSpan.FromSeconds(12);
    private const int MaxServiceLogLines = 160;
    private const int MaxRuntimeLogLines = 120;
    private static readonly string[] FingerNames = ["thumb", "index", "middle", "ring", "pinky"];
    private static readonly StatusVisual OfflineVisual = new(CreateBrush("#94A3B8"), CreateBrush("#F1F5F9"), CreateBrush("#64748B"));
    private static readonly StatusVisual ConnectedVisual = new(CreateBrush("#3B82F6"), CreateBrush("#EFF6FF"), CreateBrush("#2563EB"));
    private static readonly StatusVisual SuccessVisual = new(CreateBrush("#10B981"), CreateBrush("#ECFDF5"), CreateBrush("#059669"));
    private static readonly StatusVisual WarningVisual = new(CreateBrush("#F59E0B"), CreateBrush("#FFFBEB"), CreateBrush("#D97706"));
    private static readonly StatusVisual DangerVisual = new(CreateBrush("#EF4444"), CreateBrush("#FEF2F2"), CreateBrush("#DC2626"));

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int phyAddrLen);

    private sealed class RuntimeSideCache
    {
        public DateTime LastSeenUtc { get; set; } = DateTime.MinValue;
        public bool TrackingEnabled { get; set; } = true;
        public DateTime TrackingDisabledSinceUtc { get; set; } = DateTime.MinValue;
        public int[] Raws { get; } = new int[5];
        public bool[] PacketActive { get; } = new bool[5];
        public double[] FilteredBends { get; } = new double[5];
        public bool[] FilterInitialized { get; } = new bool[5];
        public int JoystickRawX { get; set; } = -1;
        public int JoystickRawY { get; set; } = -1;
        public bool? JoystickPressed { get; set; }
    }

    private sealed class StatusVisual
    {
        public StatusVisual(Brush accent, Brush background, Brush text)
        {
            Accent = accent;
            Background = background;
            Text = text;
        }

        public Brush Accent { get; }
        public Brush Background { get; }
        public Brush Text { get; }
    }

    private sealed class DeviceHeartbeatSnapshot
    {
        public required string SourceIp { get; init; }
        public required SerialStatusDto Status { get; init; }
        public required DateTime SeenUtc { get; init; }
    }

    private sealed class SerialProbeResult
    {
        public required string Port { get; init; }
        public SerialStatusDto? Status { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private sealed class ReachabilityProbeResult
    {
        public required string Ip { get; init; }
        public required bool Reachable { get; init; }
        public required DateTime SeenUtc { get; init; }
    }

    private sealed class ProcessStatusSnapshot
    {
        public bool SteamVrRunning { get; init; }
        public bool VrServerRunning { get; init; }
        public bool BridgeRunning { get; init; }
        public bool LegacyServiceRunning { get; init; }
    }

    private sealed class SteamVrDriverSnapshot
    {
        public bool RuntimeDetected { get; init; }
        public bool ToolAvailable { get; init; }
        public bool FilesReady { get; init; }
        public bool Registered { get; init; }
        public bool IsLatest { get; init; }
        public bool HasMultipleRegistrations { get; init; }
        public string RuntimePath { get; init; } = string.Empty;
        public string ConfigPath { get; init; } = string.Empty;
        public string ToolPath { get; init; } = string.Empty;
        public string ExpectedDriverPath { get; init; } = string.Empty;
        public string RegisteredDriverPath { get; init; } = string.Empty;
        public IReadOnlyList<string> RegisteredPaths { get; init; } = Array.Empty<string>();
        public string CurrentBuildText { get; init; } = "--";
        public string InstalledBuildText { get; init; } = "--";
    }

    private readonly MainVm _vm = new();
    private readonly OpenFingerConfigStore _configStore = new();
    private readonly FirmwareTools _firmwareTools = new(FirmwareTools.ResolveRepositoryRoot());
    private readonly DispatcherTimer _refreshTimer = new(DispatcherPriority.Background) { Interval = DeviceRefreshInterval };
    private readonly DispatcherTimer _portAgeTimer = new(DispatcherPriority.Background) { Interval = PortAgeRefreshInterval };
    private readonly DispatcherTimer _runtimeUiTimer = new(DispatcherPriority.Background) { Interval = RuntimeUiRefreshInterval };
    private readonly ConcurrentQueue<string> _runtimeLines = new();
    private readonly Dictionary<string, DateTime> _udpSeenByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _runtimeSeenBySide = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _trackingEnabledBySide = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _trackingDisabledSinceBySide = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerialStatusDto> _serialStatusByPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _serialStatusSeenByPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _wifiReachableByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _wifiReachableSeenByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceHeartbeatSnapshot> _heartbeatByDeviceKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _firmwarePortArrivedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownFirmwarePorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _serviceLogLines = new();
    private readonly object _runtimeCacheLock = new();
    private readonly object _latestRuntimePacketLock = new();
    private readonly SemaphoreSlim _portInventoryLock = new(1, 1);
    private readonly Dictionary<string, RuntimeSideCache> _runtimeCacheBySide = new(StringComparer.OrdinalIgnoreCase)
    {
        ["left"] = new(),
        ["right"] = new()
    };

    private OpenFingerAppConfig _config = new();
    private UdpRuntimeMonitor? _udpMonitor;
    private RuntimeFramePublisher? _runtimePublisher;
    private RuntimeFramePublisher? _fingerTestPublisher;
    private bool _firmwareBusy;
    private bool _refreshDevicesBusy;
    private bool _deviceActionBusy;
    private bool _suppressFingerConfigSave;
    private bool _suppressSelectedDeviceEvents;
    private bool _suppressFirmwareReportRateEvents;
    private bool _suspendFirmwareSelectionSync;
    private bool _firmwarePortsInitialized;
    private int _runtimeUiDirty;
    private DateTime _statusLinePinnedUntilUtc = DateTime.MinValue;
    private DateTime _lastSerialProbeUtc = DateTime.MinValue;
    private DateTime _lastRawLogUiUpdateUtc = DateTime.MinValue;
    private DateTime _lastDashboardUiRefreshUtc = DateTime.MinValue;
    private DateTime _lastProcessStatusRefreshUtc = DateTime.MinValue;
    private DateTime _lastPortInventoryRefreshUtc = DateTime.MinValue;
    private DateTime _lastFullDeviceRefreshUtc = DateTime.MinValue;
    private DateTime _latestRuntimePacketUtc = DateTime.MinValue;
    private string _lastFirmwareSelectionDeviceId = string.Empty;
    private string _lastAppliedFirmwareDefaultsPackageId = string.Empty;
    private string _lastAppliedFirmwareDefaultsTarget = string.Empty;
    private string _latestRuntimeSourceIp = string.Empty;
    private string _latestRuntimeSide = "right";
    private int _latestRuntimeMask;
    private bool? _latestRuntimeTrackingEnabled;
    private int? _latestRuntimeJoystickRawX;
    private int? _latestRuntimeJoystickRawY;
    private bool? _latestRuntimeJoystickPressed;
    private bool _firmwareReportRateUserOverride;
    private IReadOnlyList<string> _cachedAvailablePorts = Array.Empty<string>();
    private SteamVrDriverSnapshot _steamVrDriverSnapshot = new();

    private void InitializeBackend()
    {
        DataContext = _vm;
        _config = _configStore.Load();
        LoadControllerStylesFromSharedConfig();
        ThemeManager.ApplyThemeMode(_config.Ui.ThemeMode);

        foreach (var finger in FingerNames)
        {
            _vm.LeftFingers.Add(new FingerRuntimeVm { Name = finger, DisplayName = GetFingerDisplayName(finger) });
            _vm.RightFingers.Add(new FingerRuntimeVm { Name = finger, DisplayName = GetFingerDisplayName(finger) });
        }

        AttachFingerHandlers("left", _vm.LeftFingers);
        AttachFingerHandlers("right", _vm.RightFingers);
        ApplyFingerConfigState();
        CalibrationPageView.ApplyAlgorithmTuning(_config.AlgorithmTuning);
        CalibrationPageView.ApplyJoystickSettings(_config.Joystick);
        CalibrationPageView.ApplyPoseOffsets(_config.PoseOffsets);

        foreach (var option in FirmwareTargetCatalog.TargetOptions)
        {
            _vm.FirmwareTargetOptions.Add(option);
        }

        foreach (var option in FirmwareTargetCatalog.SourceOptions)
        {
            _vm.FirmwareSourceOptions.Add(option);
        }

        foreach (var option in FirmwareTargetCatalog.ReportRateOptions)
        {
            _vm.FirmwareReportRateOptions.Add(option);
        }

        foreach (var option in Esp32C3PinCatalog.TrackingSwitchModes)
        {
            _vm.FirmwareTrackingSwitchModes.Add(option);
        }

        LoadFirmwareSettingsIntoVm();
        _vm.PropertyChanged += ViewModel_PropertyChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _portAgeTimer.Tick += (_, _) => RefreshFirmwarePortAgeLabels();
        _runtimeUiTimer.Tick += (_, _) => FlushRuntimeUi();

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        SyncUdpMonitorMode();
        SetAdvancedMode(_config.Ui.ShowAdvanced);
        RefreshUiFromState();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = BeginStartupIntroAsync();

        if (StopServiceIfRunning(out var stopMessage) && !string.IsNullOrWhiteSpace(stopMessage))
        {
            AppendLog(stopMessage);
            SetPinnedStatusLine(stopMessage, 5);
        }

        await RefreshFirmwareCatalogAsync();
        await RefreshPortsAsync();
        await RefreshDevicesAsync(forceSerialProbe: true);
        _refreshTimer.Start();
        _portAgeTimer.Start();
        _runtimeUiTimer.Start();
        CompleteShellStartup();
        if (_config.Ui.Updates.CheckOnStartup)
        {
            _ = CheckForUpdatesAsync(userInitiated: false);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _portAgeTimer.Stop();
        _runtimeUiTimer.Stop();
        StopUdpMonitor();
        StopRuntimePublisher();
        StopFingerTestPublisher();
        DisposeTrayIcon();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainVm.SelectedDevice))
        {
            if (_suppressSelectedDeviceEvents)
            {
                return;
            }

            ApplySelectedDeviceState();
            RefreshUiFromState();
            return;
        }

        if (e.PropertyName == nameof(MainVm.FirmwareTarget))
        {
            _firmwareReportRateUserOverride = false;
            RefreshFirmwareTargetOptions(normalizeSelection: true);
            ApplyPackageSelectionDefaults(forcePackageDefaults: true, forceReportRate: true);
            RefreshUiFromState();
            return;
        }

        if (e.PropertyName == nameof(MainVm.FirmwareSource))
        {
            _config.Firmware.PreferredSource = _vm.FirmwareSource;
            _configStore.Save(_config);
            return;
        }

        if (e.PropertyName == nameof(MainVm.FirmwareReportRateHz))
        {
            if (!_suppressFirmwareReportRateEvents)
            {
                _firmwareReportRateUserOverride = true;
            }

            _config.Firmware.ReportRateHz = Math.Clamp(_vm.FirmwareReportRateHz, 10, 240);
            _configStore.Save(_config);
            RefreshUiFromState();
            return;
        }
    }

    public async Task RefreshAllAsync(bool forceSerialProbe = false)
    {
        await RefreshDevicesAsync(forceSerialProbe: forceSerialProbe);
        await RefreshPortsAsync();
    }

    public async Task RefreshFirmwarePortsOnlyAsync()
    {
        await RefreshPortsAsync();
        RefreshUiFromState();
    }

    public void SelectDevice(DeviceVm? device)
    {
        _vm.SelectedDevice = device;
    }

    public void NotifyRoleEdited(DeviceVm? device)
    {
        if (device is null)
        {
            return;
        }

        var normalizedRole = NormalizeRoleForUi(device.Role, _vm.Role);
        if (!string.Equals(device.Role, normalizedRole, StringComparison.OrdinalIgnoreCase))
        {
            device.Role = normalizedRole;
        }

        if (_vm.SelectedDevice?.Id == device.Id)
        {
            _vm.Role = normalizedRole;
        }

        if (PersistDeviceRole(device, normalizedRole))
        {
            SetPinnedStatusLine($"已将 {device.DisplayName} 设置为{(normalizedRole == "left" ? "左手" : "右手")}。", 4);
        }
    }

    public async Task StartSteamVrAsync()
    {
        try
        {
            var notes = new List<string>();
            if (EnsureBridgeRunning(out var bridgeMessage) && !string.IsNullOrWhiteSpace(bridgeMessage))
            {
                notes.Add(bridgeMessage);
            }

            if (!TrySetSteamVrForwardControllerInputs(true, out var forwardMessage))
            {
                SetPinnedStatusLine(forwardMessage);
                return;
            }

            notes.Add(forwardMessage);
            Process.Start(new ProcessStartInfo
            {
                FileName = "steam://run/250820",
                UseShellExecute = true
            });
            notes.Add("已请求启动 SteamVR。");
            SetPinnedStatusLine(string.Join(" ", notes), 5);
            await Task.Delay(1200);
            UpdateProcessStatus();
            RefreshUiFromState();
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"启动 SteamVR 失败: {ex.Message}");
        }
    }

    public async Task RestartSteamVrAsync()
    {
        try
        {
            StopSteamVrProcesses();
            SetPinnedStatusLine("正在重启 SteamVR...", 4);
            await Task.Delay(1200);
            UpdateProcessStatus();
            RefreshUiFromState();
            await StartSteamVrAsync();
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"重启 SteamVR 失败: {ex.Message}", 8);
        }
    }

    public async Task StartBridgeAsync()
    {
        try
        {
            if (!EnsureBridgeRunning(out var message))
            {
                SetPinnedStatusLine(message);
                return;
            }

            SetPinnedStatusLine(string.IsNullOrWhiteSpace(message) ? "bridge 已在运行。" : message, 4);
            await Task.Delay(400);
            UpdateProcessStatus();
            RefreshUiFromState();
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"启动 bridge 失败: {ex.Message}");
        }
    }

    public Task StopLegacyServiceAsync()
    {
        try
        {
            if (!StopServiceIfRunning(out var message))
            {
                SetPinnedStatusLine(message);
                return Task.CompletedTask;
            }

            SetPinnedStatusLine(string.IsNullOrWhiteSpace(message) ? "legacy service 已停用。" : message, 4);
            UpdateProcessStatus();
            RefreshUiFromState();
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"停用 service 失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void RepairConfig()
    {
        _config = _configStore.Load();
        _configStore.Save(_config);
        ApplyFingerConfigState();
        CalibrationPageView.ApplyAlgorithmTuning(_config.AlgorithmTuning);
        CalibrationPageView.ApplyJoystickSettings(_config.Joystick);
        CalibrationPageView.ApplyPoseOffsets(_config.PoseOffsets);
        RefreshDisplayedBends(resetFilters: true);
        PublishRuntimeFrame();
        SetPinnedStatusLine("已执行五指配置修正。", 4);
        RefreshUiFromState();
    }

    public void OpenConfig()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _configStore.PathValue,
            UseShellExecute = true
        });
    }

    public async Task ProvisionSelectedDeviceAsync(string wifiPassword)
    {
        if (_deviceActionBusy)
        {
            return;
        }

        try
        {
            SetDeviceActionBusy(true);
            await SendProvisionAsync(wifiPassword);
            await RefreshDevicesAsync(forceSerialProbe: true);
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine(ex.Message);
        }
        finally
        {
            SetDeviceActionBusy(false);
        }
    }

    public async Task IdentifySelectedDeviceAsync()
    {
        if (_deviceActionBusy)
        {
            return;
        }

        try
        {
            SetDeviceActionBusy(true);
            await SendIdentifyAsync();
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine(ex.Message);
        }
        finally
        {
            SetDeviceActionBusy(false);
        }
    }

    public async Task SaveSelectedRoleAsync()
    {
        if (_deviceActionBusy)
        {
            return;
        }

        try
        {
            SetDeviceActionBusy(true);
            await SendRoleAsync();
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine(ex.Message);
        }
        finally
        {
            SetDeviceActionBusy(false);
        }
    }

    public async Task ForgetSelectedDeviceAsync()
    {
        if (_deviceActionBusy)
        {
            return;
        }

        if (_vm.SelectedDevice is null)
        {
            SetPinnedStatusLine("先选择设备。");
            return;
        }

        try
        {
            SetDeviceActionBusy(true);
            var removedDisplayName = _vm.SelectedDevice.DisplayName;
            if (RemoveKnownDevice(_vm.SelectedDevice))
            {
                RemoveVisibleDeviceCard(_vm.SelectedDevice);
                SetPinnedStatusLine($"已删除设备记录：{removedDisplayName}", 4);
            }
            else
            {
                SetPinnedStatusLine("这台设备当前没有已保存记录可删除。", 4);
            }

            await RefreshDevicesAsync(forceSerialProbe: true);
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine(ex.Message);
        }
        finally
        {
            SetDeviceActionBusy(false);
        }
    }

    public void ClearAllLogs()
    {
        _serviceLogLines.Clear();
        _vm.ServiceLog = string.Empty;
        _vm.RawPacketLog = string.Empty;
        _vm.FirmwareOutput = string.Empty;
        _vm.FirmwareFriendlyOutput = string.Empty;
        while (_runtimeLines.TryDequeue(out _))
        {
        }

        RefreshUiFromState();
    }

    public async Task StartFirmwareFlowAsync()
    {
        await RefreshPortsAsync();
        if (string.IsNullOrWhiteSpace(_vm.FirmwarePort))
        {
            SetPinnedStatusLine("请先在固件页选择刷写串口。", 4);
            return;
        }

        var package = ResolveSelectedFirmwarePackage();
        if (package is null)
        {
            SetPinnedStatusLine("请先选择一个可用的固件包。", 4);
            return;
        }

        var selectedTarget = _vm.FirmwareTarget;
        var selectedPackage = _vm.SelectedFirmwarePackage;
        bool? result;
        _suspendFirmwareSelectionSync = true;
        try
        {
            var dialog = new FirmwareSetupDialog(_vm, package.Summary, package.BootHint)
            {
                Owner = this
            };

            result = dialog.ShowDialog();

            if (result != true)
            {
                SetPinnedStatusLine("已取消刷写。", 3);
                return;
            }

            _vm.FirmwareTarget = selectedTarget;
            _vm.SelectedFirmwarePackage = selectedPackage ?? package;
            _vm.FirmwareThumbPin = dialog.ThumbPin;
            _vm.FirmwareIndexPin = dialog.IndexPin;
            _vm.FirmwareMiddlePin = dialog.MiddlePin;
            _vm.FirmwareRingPin = dialog.RingPin;
            _vm.FirmwarePinkyPin = dialog.PinkyPin;
            _vm.FirmwareTrackingSwitchPin = dialog.TrackingSwitchPin;
            _vm.FirmwareTrackingSwitchMode = dialog.TrackingSwitchMode;
            _vm.FirmwareJoystickVrxPin = dialog.JoystickVrxPin;
            _vm.FirmwareJoystickVryPin = dialog.JoystickVryPin;
            _vm.FirmwareJoystickSwPin = dialog.JoystickSwPin;
            _vm.FirmwareBatteryAdcPin = dialog.BatteryAdcPin;
            _vm.FirmwareBatteryChargePin = dialog.BatteryChargePin;
            RefreshFirmwareTargetOptions(normalizeSelection: false);
        }
        finally
        {
            _suspendFirmwareSelectionSync = false;
        }

        RefreshUiFromState();
        await FlashSelectedFirmwarePackageAsync(package);
    }

    public async Task RefreshFirmwareCatalogAsync(bool forceReload = false)
    {
        try
        {
            if (forceReload)
            {
                _vm.FirmwareCatalogStatus = "正在检查固件包...";
                RefreshUiFromState();
            }

            _config.Firmware.PreferredSource = _vm.FirmwareSource;
            _config.Firmware.ExternalPackagePath = _vm.FirmwareExternalPackagePath;
            _config.Firmware.OnlineCatalogUrl = _vm.FirmwareOnlineCatalogUrl;
            _configStore.Save(_config);

            var manifests = await FirmwareCatalogService.LoadCatalogAsync(
                _vm.FirmwareSource,
                _vm.FirmwareExternalPackagePath,
                _vm.FirmwareOnlineCatalogUrl);

            _vm.FirmwarePackages.Clear();
            foreach (var manifest in manifests)
            {
                _vm.FirmwarePackages.Add(FirmwareCatalogService.ToPackageVm(manifest, _vm.FirmwareSource));
            }

            _vm.FirmwareCatalogStatus = _vm.FirmwarePackages.Count > 0
                ? $"已找到 {_vm.FirmwarePackages.Count} 个固件包。"
                : "当前来源没有可用固件包。";

            SelectRecommendedFirmwarePackage();
            ApplyPackageSelectionDefaults(forcePackageDefaults: true, forceReportRate: true);
        }
        catch (Exception ex)
        {
            _vm.FirmwareCatalogStatus = $"固件包检查失败：{ex.Message}";
        }
        finally
        {
            RefreshUiFromState();
        }
    }

    public void BrowseFirmwarePackageManifest()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择固件包 manifest.json",
            Filter = "manifest.json|manifest.json|JSON 文件|*.json",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _vm.FirmwareExternalPackagePath = dialog.FileName;
        _vm.FirmwareSource = "external";
        _ = RefreshFirmwareCatalogAsync(forceReload: true);
    }

    public void NotifyFirmwarePackageSelectionChanged()
    {
        _firmwareReportRateUserOverride = false;
        ApplyPackageSelectionDefaults(forcePackageDefaults: true, forceReportRate: true);
        RefreshUiFromState();
    }

    public async Task ReverifySelectedFirmwareAsync()
    {
        if (string.IsNullOrWhiteSpace(_vm.FirmwarePort))
        {
            SetPinnedStatusLine("请先选择需要验证的串口。", 4);
            return;
        }

        try
        {
            SetFirmwareBusy(true, "正在读取设备固件信息...");
            AppendFriendlyFirmwareLine($"正在验证 {_vm.FirmwarePort} 上的设备信息...");
            var payload = await FirmwareToolClient.RunJsonAsync("verify", "--port", _vm.FirmwarePort);
            if (!(payload["ok"]?.GetValue<bool>() ?? false))
            {
                var fallbackMessage = payload["message"]?.GetValue<string>() ?? "设备没有返回固件状态。";
                throw new InvalidOperationException(await ResolveFirmwareVerificationFailureMessageAsync(_vm.FirmwarePort, fallbackMessage));
            }

            UpdateFirmwareDetectionFromToolPayload(payload);
            AppendFriendlyFirmwareLine($"验证完成：{BuildDetectedFirmwareSummary()}");
            SetPinnedStatusLine("设备固件信息已更新。", 4);
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"读取设备固件信息失败：{ex.Message}", 6);
        }
        finally
        {
            SetFirmwareBusy(false);
            RefreshUiFromState();
        }
    }

    public void SetAdvancedMode(bool enabled)
    {
        _vm.ShowAdvanced = enabled;
        _config.Ui.ShowAdvanced = enabled;
        _configStore.Save(_config);
        FirmwarePageView.SetAdvancedMode(enabled);
        StatusPageView.SetAdvancedMode(enabled);
        DiagnosticsPageView.SetAdvancedMode(enabled);
        RefreshUiFromState();
    }

    public Task StartHandCalibrationAsync(string side)
    {
        var normalizedSide = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? "left" : "right";
        var fingers = normalizedSide == "left" ? _vm.LeftFingers : _vm.RightFingers;
        var handConfig = normalizedSide == "left" ? _config.Hands.Left : _config.Hands.Right;
        var timerWasEnabled = _refreshTimer.IsEnabled;
        var portTimerWasEnabled = _portAgeTimer.IsEnabled;
        if (timerWasEnabled)
        {
            _refreshTimer.Stop();
        }

        if (portTimerWasEnabled)
        {
            _portAgeTimer.Stop();
        }

        try
        {
            var window = new HandCalibrationDialog(normalizedSide, fingers, handConfig)
            {
                Owner = this
            };

            if (window.ShowDialog() == true)
            {
                _config = _configStore.Load();
                var targetHand = normalizedSide == "left" ? _config.Hands.Left : _config.Hands.Right;
                foreach (var result in window.Results)
                {
                    if (!targetHand.Fingers.TryGetValue(result.FingerName, out var finger))
                    {
                        finger = new FingerConfig();
                        targetHand.Fingers[result.FingerName] = finger;
                    }

                    finger.CalibratedOpenRaw = result.OpenRaw;
                    finger.CalibratedClosedRaw = result.ClosedRaw;
                    finger.CenterRaw = (result.OpenRaw + result.ClosedRaw) / 2;
                    finger.Direction = result.ClosedRaw >= result.OpenRaw ? "positive" : "negative";
                }

                SyncKnownCalibrationStates();
                _configStore.Save(_config);
                RefreshDisplayedBends(resetFilters: true);
                PublishRuntimeFrame();
                SetPinnedStatusLine($"{(normalizedSide == "left" ? "左手" : "右手")}校准已保存，运行时会自动应用。", 6);
                RefreshUiFromState();
            }
        }
        finally
        {
            if (timerWasEnabled)
            {
                _refreshTimer.Start();
            }

            if (portTimerWasEnabled)
            {
                _portAgeTimer.Start();
            }
        }

        return Task.CompletedTask;
    }

    public async Task StartSyncCalibrationAsync()
    {
        var openCapturePrompt = MessageBox.Show(
            "请先把双手完全张开并保持稳定，然后点击“确定”记录张开值。",
            "OpenFinger 校准",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (openCapturePrompt != MessageBoxResult.OK)
        {
            SetPinnedStatusLine("已取消校准。", 3);
            return;
        }

        var openLeft = CaptureFingerSnapshot(_vm.LeftFingers);
        var openRight = CaptureFingerSnapshot(_vm.RightFingers);

        var closeCapturePrompt = MessageBox.Show(
            "现在请把双手尽量握拳并保持稳定，然后点击“确定”记录握拳值。",
            "OpenFinger 校准",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (closeCapturePrompt != MessageBoxResult.OK)
        {
            SetPinnedStatusLine("已取消校准。", 3);
            return;
        }

        var closeLeft = CaptureFingerSnapshot(_vm.LeftFingers);
        var closeRight = CaptureFingerSnapshot(_vm.RightFingers);

        PersistCalibration("left", openLeft, closeLeft);
        PersistCalibration("right", openRight, closeRight);
        SyncKnownCalibrationStates();

        _configStore.Save(_config);
        RefreshDisplayedBends(resetFilters: true);
        PublishRuntimeFrame();
        SetPinnedStatusLine("双手校准已保存，运行时会自动应用。", 6);
        RefreshUiFromState();
    }

    public void ResetCalibration()
    {
        foreach (var hand in new[] { _config.Hands.Left, _config.Hands.Right })
        {
            foreach (var finger in hand.Fingers.Values)
            {
                finger.CalibratedOpenRaw = -1;
                finger.CalibratedClosedRaw = -1;
                finger.CenterRaw = 2048;
                finger.Direction = "auto";
            }
        }

        SyncKnownCalibrationStates();
        _configStore.Save(_config);
        RefreshDisplayedBends(resetFilters: true);
        ResetObservedRanges(_vm.LeftFingers);
        ResetObservedRanges(_vm.RightFingers);
        SetPinnedStatusLine("已重置双手校准。", 4);
        RefreshUiFromState();
    }

    public void UpdateAlgorithmTuning(AlgorithmTuningConfig tuning)
    {
        _config.AlgorithmTuning.SensitivityLevel = Math.Clamp(Math.Round(tuning.SensitivityLevel), 1, 3);
        _config.AlgorithmTuning.AntiShakeLevel = Math.Clamp(Math.Round(tuning.AntiShakeLevel), 1, 3);
        _config.AlgorithmTuning.SmoothingAlpha = Math.Clamp(tuning.SmoothingAlpha, 0.05, 1.0);
        _config.AlgorithmTuning.DeadzonePercent = Math.Clamp(tuning.DeadzonePercent, 0, 15);
        _config.AlgorithmTuning.KalmanQ = Math.Clamp(tuning.KalmanQ, 0.001, 0.1);

        foreach (var hand in new[] { _config.Hands.Left, _config.Hands.Right })
        {
            foreach (var fingerName in FingerNames)
            {
                if (!hand.Fingers.TryGetValue(fingerName, out var fingerConfig))
                {
                    fingerConfig = new FingerConfig();
                    hand.Fingers[fingerName] = fingerConfig;
                }

                fingerConfig.SmoothingAlpha = _config.AlgorithmTuning.SmoothingAlpha;
                fingerConfig.Deadzone = _config.AlgorithmTuning.DeadzonePercent / 100.0;
            }
        }

        _configStore.Save(_config);
        RefreshDisplayedBends(resetFilters: true);
        PublishRuntimeFrame();
        RefreshUiFromState();
    }

    public void UpdateJoystickSettings(string side, string axisMode, string clickAction, string orientation, double deadzonePercent)
    {
        var settings = GetJoystickSettings(side);
        settings.SteamVrAxisMode = JoystickSteamVrCatalog.IsValidAxisMode(axisMode)
            ? axisMode
            : JoystickSteamVrCatalog.AxisJoystick;
        settings.SteamVrClickAction = JoystickSteamVrCatalog.IsValidClickAction(clickAction)
            ? clickAction
            : JoystickSteamVrCatalog.ClickJoystick;
        settings.Orientation = JoystickOrientationCatalog.IsValid(orientation)
            ? orientation
            : JoystickOrientationCatalog.Normal;
        settings.DeadzonePercent = Math.Clamp(deadzonePercent, 0, 40);
        PersistJoystickSettings();
    }

    public bool CaptureJoystickCenter(string side)
    {
        var cache = SnapshotRuntimeCache(side);
        if (cache.JoystickRawX < 0 && cache.JoystickRawY < 0)
        {
            SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}当前没有摇杆原始数据，无法记录中心点。", 4);
            return false;
        }

        var settings = GetJoystickSettings(side);
        settings.CenterRawX = cache.JoystickRawX >= 0 ? cache.JoystickRawX : settings.CenterRawX;
        settings.CenterRawY = cache.JoystickRawY >= 0 ? cache.JoystickRawY : settings.CenterRawY;
        PersistJoystickSettings();
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}摇杆中心已记录。", 4);
        return true;
    }

    public Task<bool> AutoCalibrateJoystickDirectionAsync(string side)
    {
        var cache = SnapshotRuntimeCache(side);
        if (cache.JoystickRawX < 0 || cache.JoystickRawY < 0)
        {
            SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}当前没有可用摇杆数据，先确认摇杆已经接好并正在输入。", 4);
            return Task.FromResult(false);
        }

        var dialog = new JoystickDirectionCalibrationDialog(side, () =>
        {
            var snapshot = SnapshotRuntimeCache(side);
            return new JoystickDirectionCalibrationDialog.RawJoystickSnapshot(
                snapshot.JoystickRawX,
                snapshot.JoystickRawY,
                snapshot.JoystickRawX >= 0 && snapshot.JoystickRawY >= 0);
        })
        {
            Owner = this
        };

        var result = dialog.ShowDialog() == true;
        if (!result)
        {
            return Task.FromResult(false);
        }

        var settings = GetJoystickSettings(side);
        settings.CenterRawX = dialog.ResultCenterRawX;
        settings.CenterRawY = dialog.ResultCenterRawY;
        settings.Orientation = dialog.ResultOrientation;
        PersistJoystickSettings();

        var orientationLabel = JoystickOrientationCatalog.Options.FirstOrDefault(option =>
            string.Equals(option.Value, dialog.ResultOrientation, StringComparison.OrdinalIgnoreCase))?.Label ?? dialog.ResultOrientation;
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}摇杆方向已自动校准为“{orientationLabel}”。", 4);
        return Task.FromResult(true);
    }

    public void ResetJoystickCalibration(string side)
    {
        var settings = GetJoystickSettings(side);
        settings.CenterRawX = -1;
        settings.CenterRawY = -1;
        settings.Orientation = JoystickOrientationCatalog.Normal;
        settings.DeadzonePercent = 8;
        PersistJoystickSettings();
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}摇杆校准已恢复默认。", 4);
    }

    public JoystickHandSettings CloneJoystickSettings(string side)
    {
        var settings = GetJoystickSettings(side);
        return new JoystickHandSettings
        {
            SteamVrAxisMode = settings.SteamVrAxisMode,
            SteamVrClickAction = settings.SteamVrClickAction,
            CenterRawX = settings.CenterRawX,
            CenterRawY = settings.CenterRawY,
            Orientation = settings.Orientation,
            DeadzonePercent = settings.DeadzonePercent
        };
    }

    private void PersistJoystickSettings()
    {
        _configStore.Save(_config);
        CalibrationPageView.ApplyJoystickSettings(_config.Joystick);
        PublishRuntimeFrame();
        RefreshUiFromState();
    }

    public void UpdatePoseOffset(string side, ControllerPoseOffsetConfig offset)
    {
        var target = GetPoseOffset(side);
        target.PositionX = Math.Clamp(double.IsFinite(offset.PositionX) ? offset.PositionX : 0.0, -1.0, 1.0);
        target.PositionY = Math.Clamp(double.IsFinite(offset.PositionY) ? offset.PositionY : 0.0, -1.0, 1.0);
        target.PositionZ = Math.Clamp(double.IsFinite(offset.PositionZ) ? offset.PositionZ : 0.0, -1.0, 1.0);
        target.RotationPitch = Math.Clamp(double.IsFinite(offset.RotationPitch) ? offset.RotationPitch : 0.0, -180.0, 180.0);
        target.RotationYaw = Math.Clamp(double.IsFinite(offset.RotationYaw) ? offset.RotationYaw : 0.0, -180.0, 180.0);
        target.RotationRoll = Math.Clamp(double.IsFinite(offset.RotationRoll) ? offset.RotationRoll : 0.0, -180.0, 180.0);

        _configStore.Save(_config);
        CalibrationPageView.ApplyPoseOffsets(_config.PoseOffsets);
        PublishRuntimeFrame();
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")} 6DoF 偏移已保存。", 3);
    }

    public void ResetPoseOffset(string side)
    {
        var target = GetPoseOffset(side);
        target.PositionX = 0;
        target.PositionY = 0;
        target.PositionZ = 0;
        target.RotationPitch = 0;
        target.RotationYaw = 0;
        target.RotationRoll = 0;

        _configStore.Save(_config);
        CalibrationPageView.ApplyPoseOffsets(_config.PoseOffsets);
        PublishRuntimeFrame();
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")} 6DoF 偏移已重置。", 3);
    }

    public ControllerPoseOffsetConfig ClonePoseOffset(string side)
    {
        var offset = GetPoseOffset(side);
        return new ControllerPoseOffsetConfig
        {
            PositionX = offset.PositionX,
            PositionY = offset.PositionY,
            PositionZ = offset.PositionZ,
            RotationPitch = offset.RotationPitch,
            RotationYaw = offset.RotationYaw,
            RotationRoll = offset.RotationRoll
        };
    }

    private ControllerPoseOffsetConfig GetPoseOffset(string side)
    {
        _config.PoseOffsets ??= new ControllerPoseOffsetsConfig();
        _config.PoseOffsets.Left ??= new ControllerPoseOffsetConfig();
        _config.PoseOffsets.Right ??= new ControllerPoseOffsetConfig();
        return IsLeftSide(side) ? _config.PoseOffsets.Left : _config.PoseOffsets.Right;
    }

    private JoystickHandSettings GetJoystickSettings(string side)
    {
        return IsLeftSide(side) ? _config.Joystick.Left : _config.Joystick.Right;
    }

    private static bool IsLeftSide(string side)
    {
        return string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> CaptureFingerSnapshot(IReadOnlyList<FingerRuntimeVm> fingers)
    {
        var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var finger in fingers)
        {
            snapshot[finger.Name] = finger.Raw;
        }

        return snapshot;
    }

    private void PersistCalibration(string side, IReadOnlyDictionary<string, int> openRaw, IReadOnlyDictionary<string, int> closedRaw)
    {
        var hand = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? _config.Hands.Left : _config.Hands.Right;
        foreach (var fingerName in new[] { "thumb", "index", "middle", "ring", "pinky" })
        {
            if (!hand.Fingers.TryGetValue(fingerName, out var fingerConfig))
            {
                fingerConfig = new FingerConfig();
                hand.Fingers[fingerName] = fingerConfig;
            }

            var openValue = openRaw.TryGetValue(fingerName, out var capturedOpen) ? capturedOpen : -1;
            var closedValue = closedRaw.TryGetValue(fingerName, out var capturedClosed) ? capturedClosed : -1;
            if (openValue < 0 || closedValue < 0 || openValue == closedValue)
            {
                continue;
            }

            fingerConfig.CalibratedOpenRaw = openValue;
            fingerConfig.CalibratedClosedRaw = closedValue;
            fingerConfig.CenterRaw = (openValue + closedValue) / 2;
            fingerConfig.Direction = closedValue >= openValue ? "positive" : "negative";
        }
    }

    private async Task RefreshDevicesAsync(bool ignoreFirmwareBusy = false, bool forceSerialProbe = false)
    {
        if (_refreshDevicesBusy)
        {
            return;
        }

        _refreshDevicesBusy = true;
        try
        {
            if (_firmwareBusy && !ignoreFirmwareBusy)
            {
                return;
            }

            var selectedDevice = CloneDeviceIdentity(_vm.SelectedDevice);
            var selectedId = selectedDevice?.Id;
            var devices = new List<DiscoveryDevice>();
            var nowUtc = DateTime.UtcNow;
            var shouldRefreshProcess = forceSerialProbe
                || (nowUtc - _lastProcessStatusRefreshUtc) >= ProcessStatusRefreshInterval;
            var processTask = shouldRefreshProcess
                ? Task.Run(ReadProcessStatusSnapshot)
                : null;
            var availablePorts = await GetAvailablePortsSnapshotAsync(nowUtc, forceSerialProbe);
            var hasRecentRuntimeData = HasRecentRuntimeData(nowUtc);
            if (ShouldDeferFullDeviceRefresh(nowUtc, forceSerialProbe, hasRecentRuntimeData))
            {
                return;
            }

            var shouldProbeSerialNow = forceSerialProbe
                || _lastSerialProbeUtc == DateTime.MinValue
                || (!hasRecentRuntimeData && (nowUtc - _lastSerialProbeUtc) >= SerialProbeInterval);

            if (shouldProbeSerialNow)
            {
                _lastSerialProbeUtc = nowUtc;
            }

            PruneSerialStatusCache(nowUtc);
            PruneHeartbeatCache(nowUtc);
            var serialCacheSnapshot = _serialStatusByPort.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var serialSeenSnapshot = _serialStatusSeenByPort.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var portProbeResults = availablePorts.Count == 0
                ? Array.Empty<SerialProbeResult>()
                : await Task.WhenAll(availablePorts.Select(port =>
                    ProbeSerialPortAsync(port, shouldProbeSerialNow, nowUtc, serialCacheSnapshot, serialSeenSnapshot)));

            if (processTask is not null)
            {
                ApplyProcessStatusSnapshot(await processTask);
                _lastProcessStatusRefreshUtc = nowUtc;
            }

            foreach (var probe in portProbeResults)
            {
                if (!string.IsNullOrWhiteSpace(probe.ErrorMessage))
                {
                    AppendLog(probe.ErrorMessage!);
                }

                if (probe.Status is null)
                {
                    continue;
                }

                CacheSerialStatus(probe.Port, probe.Status, nowUtc);
            }

            var allowNetworkReachabilityProbe = !hasRecentRuntimeData
                || DevicesPageView.Visibility == Visibility.Visible
                || FirmwarePageView.Visibility == Visibility.Visible
                || StatusPageView.Visibility == Visibility.Visible
                || DiagnosticsPageView.Visibility == Visibility.Visible;
            var reachabilityCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var probe in portProbeResults)
            {
                if (probe.Status is null)
                {
                    continue;
                }

                var savedStaIp = FindSavedStaIp(probe.Status.Mac, probe.Status.Device, probe.Port);
                var heartbeatStatus = GetRecentHeartbeatStatus(probe.Status.Mac, probe.Status.Device, PreferStaIp(probe.Status.StaIp, savedStaIp), nowUtc);
                var resolvedStaIp = PreferStaIp(heartbeatStatus?.StaIp, PreferStaIp(probe.Status.StaIp, savedStaIp));
                if (!string.IsNullOrWhiteSpace(resolvedStaIp))
                {
                    reachabilityCandidates.Add(resolvedStaIp);
                }
            }

            foreach (var saved in _config.Devices)
            {
                var heartbeatStatus = GetRecentHeartbeatStatus(saved.Mac, saved.Name, saved.StaIp, nowUtc);
                var cachedStatus = heartbeatStatus ?? GetSavedDeviceCachedStatus(saved, nowUtc);
                var savedStaIp = PreferStaIp(cachedStatus?.StaIp, saved.StaIp);
                if (!string.IsNullOrWhiteSpace(savedStaIp))
                {
                    reachabilityCandidates.Add(savedStaIp);
                }
            }

            var wifiReachableSnapshot = _wifiReachableByIp.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var wifiReachableSeenSnapshot = _wifiReachableSeenByIp.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var reachabilityResults = await ProbeReachabilityBatchAsync(
                reachabilityCandidates,
                nowUtc,
                wifiReachableSnapshot,
                wifiReachableSeenSnapshot,
                allowNetworkReachabilityProbe);

            foreach (var probe in reachabilityResults.Values)
            {
                _wifiReachableByIp[probe.Ip] = probe.Reachable;
                _wifiReachableSeenByIp[probe.Ip] = probe.SeenUtc;
            }

            foreach (var probe in portProbeResults)
            {
                var status = probe.Status;
                if (status is null)
                {
                    continue;
                }

                try
                {
                    var savedStaIp = FindSavedStaIp(status.Mac, status.Device, probe.Port);
                    var fallbackRole = FindSavedRole(status.Mac, status.Device);
                    var resolvedRole = NormalizeRoleForUi(PreferRole(status.Role ?? "unknown", fallbackRole), fallbackRole);
                    var heartbeatStatus = GetRecentHeartbeatStatus(status.Mac, status.Device, PreferStaIp(status.StaIp, savedStaIp), nowUtc);
                    var resolvedStaIp = PreferStaIp(heartbeatStatus?.StaIp, PreferStaIp(status.StaIp, savedStaIp));
                    var runtimeTracking = true;
                    var hasRecentRuntime = CanUseRoleRuntimeFallback(resolvedRole)
                        && TryGetRecentRuntimeTrackingState(resolvedRole, nowUtc, out runtimeTracking);
                    var wifiActive = hasRecentRuntime || IsUdpActive(resolvedStaIp) || (heartbeatStatus is not null && IsSerialStatusStreaming(heartbeatStatus));
                    var wifiReachable = wifiActive
                        || (heartbeatStatus is not null && IsSerialStatusWifiConnected(heartbeatStatus))
                        || GetReachabilityValue(reachabilityResults, resolvedStaIp);
                    var wifiConnected = IsSerialStatusWifiConnected(status)
                        || (heartbeatStatus is not null && IsSerialStatusWifiConnected(heartbeatStatus))
                        || wifiReachable;
                    var detailStatus = heartbeatStatus ?? status;
                    var detailMessage = BuildDeviceMessage(detailStatus, wifiConnected, wifiActive);
                    devices.Add(new DiscoveryDevice
                    {
                        Id = string.IsNullOrWhiteSpace(status.Mac) ? $"usb:{probe.Port}" : $"mac:{status.Mac}",
                        DisplayName = status.Device ?? $"openfinger-{probe.Port}",
                        SerialPort = probe.Port,
                        Mac = status.Mac ?? string.Empty,
                        StaIp = resolvedStaIp,
                        Role = resolvedRole,
                        State = status.State ?? "online",
                        Message = detailMessage,
                        UdpPort = status.UdpPort,
                        AdcMask = status.AdcMask,
                        BoardTarget = string.IsNullOrWhiteSpace(status.BoardTarget) ? string.Empty : FirmwareTargetCatalog.NormalizeTarget(status.BoardTarget),
                        FirmwareVersion = status.FirmwareVersion ?? string.Empty,
                        ReportHz = status.ReportHz,
                        ThumbPin = status.ThumbPin,
                        IndexPin = status.IndexPin,
                        MiddlePin = status.MiddlePin,
                        RingPin = status.RingPin,
                        PinkyPin = status.PinkyPin,
                        JoystickVrxPin = status.JoystickVrxPin,
                        JoystickVryPin = status.JoystickVryPin,
                        JoystickSwPin = status.JoystickSwPin,
                        BatteryAvailable = status.BatteryAvailable,
                        BatteryPercent = status.BatteryPercent,
                        BatteryMillivolts = status.BatteryMillivolts,
                        BatteryChargingKnown = status.BatteryChargingKnown,
                        BatteryCharging = status.BatteryCharging,
                        TrackingEnabled = hasRecentRuntime ? runtimeTracking : status.TrackingEnabled ?? true,
                        WifiConnected = wifiConnected,
                        WifiActive = wifiActive,
                        UsbConnected = true,
                        Online = wifiConnected || wifiActive,
                        LastSeenUtc = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    if (ShouldLogSerialFailure(probe.Port, ex))
                    {
                        AppendLog($"Serial {probe.Port}: {ex.Message}");
                    }
                }
            }

            foreach (var saved in _config.Devices)
            {
                if (devices.Any(item => SamePhysical(item, saved)))
                {
                    continue;
                }

                var preferredRole = NormalizeRoleForUi(saved.SavedRole, saved.PreferredRole);
                var heartbeatStatus = GetRecentHeartbeatStatus(saved.Mac, saved.Name, saved.StaIp, nowUtc);
                var cachedStatus = heartbeatStatus ?? GetSavedDeviceCachedStatus(saved, nowUtc);
                var savedStaIp = PreferStaIp(cachedStatus?.StaIp, saved.StaIp);
                var runtimeTracking = true;
                var hasRecentRuntime = CanUseRoleRuntimeFallback(preferredRole)
                    && TryGetRecentRuntimeTrackingState(preferredRole, nowUtc, out runtimeTracking);
                var wifiActive = hasRecentRuntime || IsUdpActive(savedStaIp) || (heartbeatStatus is not null && IsSerialStatusStreaming(heartbeatStatus));
                var wifiConnected = wifiActive
                    || (cachedStatus is not null && IsSerialStatusWifiConnected(cachedStatus))
                    || GetReachabilityValue(reachabilityResults, savedStaIp);
                var usbConnected = !string.IsNullOrWhiteSpace(saved.SerialPort)
                    && availablePorts.Contains(saved.SerialPort, StringComparer.OrdinalIgnoreCase);
                var detailMessage = cachedStatus is not null
                    ? BuildDeviceMessage(cachedStatus, wifiConnected, wifiActive)
                    : wifiConnected
                        ? "已连接到 Wi-Fi，等待运行时数据"
                        : usbConnected
                            ? "USB 已连接，可写入配置"
                            : "已记录设备";

                devices.Add(new DiscoveryDevice
                {
                    Id = !string.IsNullOrWhiteSpace(saved.Mac) ? $"mac:{saved.Mac}" : $"saved:{saved.Name}",
                    DisplayName = string.IsNullOrWhiteSpace(saved.Name) ? "OpenFinger" : saved.Name,
                    SerialPort = saved.SerialPort,
                    Mac = saved.Mac,
                    StaIp = savedStaIp,
                    Role = NormalizeRoleForUi(PreferRole(cachedStatus?.Role ?? "unknown", preferredRole), preferredRole),
                    State = wifiActive ? "wifi_active" : wifiConnected ? "wifi_connected" : usbConnected ? "usb_connected" : "offline",
                    Message = detailMessage,
                    UdpPort = saved.UdpPort,
                    AdcMask = saved.AdcMask,
                    BoardTarget = !string.IsNullOrWhiteSpace(cachedStatus?.BoardTarget)
                        ? FirmwareTargetCatalog.NormalizeTarget(cachedStatus.BoardTarget)
                        : string.IsNullOrWhiteSpace(saved.BoardTarget)
                            ? string.Empty
                            : FirmwareTargetCatalog.NormalizeTarget(saved.BoardTarget),
                    FirmwareVersion = !string.IsNullOrWhiteSpace(cachedStatus?.FirmwareVersion) ? cachedStatus.FirmwareVersion! : saved.FirmwareVersion,
                    ReportHz = cachedStatus?.ReportHz > 0 ? cachedStatus.ReportHz : saved.ReportHz,
                    ThumbPin = cachedStatus?.ThumbPin >= 0 ? cachedStatus.ThumbPin : saved.ThumbPin,
                    IndexPin = cachedStatus?.IndexPin >= 0 ? cachedStatus.IndexPin : saved.IndexPin,
                    MiddlePin = cachedStatus?.MiddlePin >= 0 ? cachedStatus.MiddlePin : saved.MiddlePin,
                    RingPin = cachedStatus?.RingPin >= 0 ? cachedStatus.RingPin : saved.RingPin,
                    PinkyPin = cachedStatus?.PinkyPin >= 0 ? cachedStatus.PinkyPin : saved.PinkyPin,
                    JoystickVrxPin = cachedStatus?.JoystickVrxPin >= 0 ? cachedStatus.JoystickVrxPin : saved.JoystickVrxPin,
                    JoystickVryPin = cachedStatus?.JoystickVryPin >= 0 ? cachedStatus.JoystickVryPin : saved.JoystickVryPin,
                    JoystickSwPin = cachedStatus?.JoystickSwPin >= 0 ? cachedStatus.JoystickSwPin : saved.JoystickSwPin,
                    BatteryAvailable = cachedStatus?.BatteryAvailable ?? false,
                    BatteryPercent = cachedStatus?.BatteryPercent ?? -1,
                    BatteryMillivolts = cachedStatus?.BatteryMillivolts ?? -1,
                    BatteryChargingKnown = cachedStatus?.BatteryChargingKnown ?? false,
                    BatteryCharging = cachedStatus?.BatteryCharging ?? false,
                    TrackingEnabled = hasRecentRuntime ? runtimeTracking : cachedStatus?.TrackingEnabled ?? true,
                    WifiConnected = wifiConnected,
                    WifiActive = wifiActive,
                    UsbConnected = usbConnected,
                    Online = wifiConnected || wifiActive || usbConnected,
                    LastSeenUtc = ResolveSavedDeviceLastSeenUtc(saved.SerialPort, savedStaIp, saved.Mac, saved.Name, preferredRole, nowUtc)
                });
            }

            foreach (var heartbeat in GetRecentHeartbeats(nowUtc))
            {
                if (devices.Any(item => SamePhysical(item, heartbeat)))
                {
                    continue;
                }

                var heartbeatIp = PreferStaIp(heartbeat.Status.StaIp, heartbeat.SourceIp);
                var resolvedRole = NormalizeRoleForUi(heartbeat.Status.Role, FindSavedRole(heartbeat.Status.Mac, heartbeat.Status.Device));
                var runtimeTracking = true;
                var hasRecentRuntime = CanUseRoleRuntimeFallback(resolvedRole)
                    && TryGetRecentRuntimeTrackingState(resolvedRole, nowUtc, out runtimeTracking);
                var wifiActive = hasRecentRuntime || IsUdpActive(heartbeatIp) || IsSerialStatusStreaming(heartbeat.Status);
                var wifiConnected = wifiActive || IsSerialStatusWifiConnected(heartbeat.Status);
                devices.Add(new DiscoveryDevice
                {
                    Id = !string.IsNullOrWhiteSpace(heartbeat.Status.Mac) ? $"mac:{heartbeat.Status.Mac}" : $"ip:{heartbeatIp}",
                    DisplayName = string.IsNullOrWhiteSpace(heartbeat.Status.Device) ? "OpenFinger" : heartbeat.Status.Device!,
                    SerialPort = string.Empty,
                    Mac = heartbeat.Status.Mac ?? string.Empty,
                    StaIp = heartbeatIp,
                    Role = resolvedRole,
                    State = wifiActive ? "wifi_active" : wifiConnected ? "wifi_connected" : "offline",
                    Message = BuildDeviceMessage(heartbeat.Status, wifiConnected, wifiActive),
                    UdpPort = heartbeat.Status.UdpPort,
                    AdcMask = heartbeat.Status.AdcMask,
                    BoardTarget = string.IsNullOrWhiteSpace(heartbeat.Status.BoardTarget) ? string.Empty : FirmwareTargetCatalog.NormalizeTarget(heartbeat.Status.BoardTarget),
                    FirmwareVersion = heartbeat.Status.FirmwareVersion ?? string.Empty,
                    ReportHz = heartbeat.Status.ReportHz,
                    ThumbPin = heartbeat.Status.ThumbPin,
                    IndexPin = heartbeat.Status.IndexPin,
                    MiddlePin = heartbeat.Status.MiddlePin,
                    RingPin = heartbeat.Status.RingPin,
                    PinkyPin = heartbeat.Status.PinkyPin,
                    JoystickVrxPin = heartbeat.Status.JoystickVrxPin,
                    JoystickVryPin = heartbeat.Status.JoystickVryPin,
                    JoystickSwPin = heartbeat.Status.JoystickSwPin,
                    BatteryAvailable = heartbeat.Status.BatteryAvailable,
                    BatteryPercent = heartbeat.Status.BatteryPercent,
                    BatteryMillivolts = heartbeat.Status.BatteryMillivolts,
                    BatteryChargingKnown = heartbeat.Status.BatteryChargingKnown,
                    BatteryCharging = heartbeat.Status.BatteryCharging,
                    TrackingEnabled = hasRecentRuntime ? runtimeTracking : heartbeat.Status.TrackingEnabled ?? true,
                    WifiConnected = wifiConnected,
                    WifiActive = wifiActive,
                    UsbConnected = false,
                    Online = wifiConnected || wifiActive,
                    LastSeenUtc = heartbeat.SeenUtc
                });
            }

            var ordered = MergeDevices(devices)
                .OrderByDescending(item => item.Online)
                .ThenByDescending(item => item.WifiActive)
                .ThenByDescending(item => item.UsbConnected)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var deviceSnapshots = ordered.Select(CreateDeviceVm).ToList();

            _suppressSelectedDeviceEvents = true;
            try
            {
                SyncDeviceCollection(deviceSnapshots);

                _vm.SelectedDevice = ResolveSelectedDeviceAfterRefresh(selectedId, selectedDevice) ?? _vm.Devices.FirstOrDefault();
            }
            finally
            {
                _suppressSelectedDeviceEvents = false;
            }

            ApplySelectedDeviceState();

            UpdateFirmwarePortOptions(availablePorts);
            if (!hasRecentRuntimeData)
            {
                RefreshDisplayedBends();
                PublishRuntimeFrame();
            }

            if (!_deviceActionBusy && !_firmwareBusy && DateTime.UtcNow >= _statusLinePinnedUntilUtc)
            {
                var wifiCount = ordered.Count(item => item.WifiConnected);
                var streamingCount = ordered.Count(item => item.WifiActive);
                _vm.StatusLine = $"设备 {ordered.Count} 台 | 串口 {availablePorts.Count} 个 | Wi-Fi 已连接 {wifiCount} 台 | 运行时在线 {streamingCount} 台";
            }

            NotifyDeviceSummaryChanged();

            _lastFullDeviceRefreshUtc = nowUtc;
        }
        finally
        {
            _refreshDevicesBusy = false;
            RefreshUiFromState();
        }
    }

    private DeviceVm CreateDeviceVm(DiscoveryDevice device)
    {
        var fingerModuleCount = CountFingerModules(device.ThumbPin, device.IndexPin, device.MiddlePin, device.RingPin, device.PinkyPin);
        return new DeviceVm
        {
            Id = device.Id,
            DisplayName = device.DisplayName,
            Transport = device.Transport,
            SerialPort = device.SerialPort,
            Mac = device.Mac,
            StaIp = device.StaIp,
            Role = device.Role,
            Status = BuildStatusLabel(device),
            Detail = BuildDetailText(device),
            WifiStatus = device.WifiActive ? "Wi-Fi 在线" : device.WifiConnected ? "Wi-Fi 已连接" : "Wi-Fi 未连接",
            UsbStatus = device.UsbConnected
                ? $"USB 已连接{(string.IsNullOrWhiteSpace(device.SerialPort) ? string.Empty : $" ({device.SerialPort})")}"
                : "USB 未连接",
            Online = device.Online,
            IsUsbPreferred = device.UsbPreferred,
            UdpPort = device.UdpPort,
            AdcMask = device.AdcMask,
            BoardTarget = string.IsNullOrWhiteSpace(device.BoardTarget) ? "未识别" : FirmwareTargetCatalog.Get(device.BoardTarget).Label,
            FirmwareVersion = device.FirmwareVersion,
            ReportHz = device.ReportHz,
            ThumbPin = device.ThumbPin,
            IndexPin = device.IndexPin,
            MiddlePin = device.MiddlePin,
            RingPin = device.RingPin,
            PinkyPin = device.PinkyPin,
            JoystickVrxPin = device.JoystickVrxPin,
            JoystickVryPin = device.JoystickVryPin,
            JoystickSwPin = device.JoystickSwPin,
            FingerModuleCount = fingerModuleCount,
            FingerModuleSummary = fingerModuleCount > 0 ? $"{fingerModuleCount} 个可用手指模块" : "模块数未知",
            JoystickConnectionText = BuildDeviceJoystickSummary(device),
            BatteryAvailable = device.BatteryAvailable,
            BatteryPercent = device.BatteryPercent,
            BatteryMillivolts = device.BatteryMillivolts,
            BatteryChargingKnown = device.BatteryChargingKnown,
            BatteryCharging = device.BatteryCharging,
            BatterySummary = BuildBatterySummary(device.BatteryAvailable, device.BatteryPercent, device.BatteryChargingKnown, device.BatteryCharging),
            BatteryVoltageText = BuildBatteryVoltageText(device.BatteryAvailable, device.BatteryMillivolts),
            CalibrationState = ResolveCalibrationStateForRole(device.Role),
            LastSeenTransport = ResolveLastSeenTransport(device),
            LastSeenText = BuildLastSeenText(device)
        };
    }

    private void SyncDeviceCollection(IReadOnlyList<DeviceVm> snapshots)
    {
        var sameShape = _vm.Devices.Count == snapshots.Count;
        if (sameShape)
        {
            for (var index = 0; index < snapshots.Count; index++)
            {
                if (!string.Equals(_vm.Devices[index].Id, snapshots[index].Id, StringComparison.OrdinalIgnoreCase))
                {
                    sameShape = false;
                    break;
                }
            }
        }

        if (!sameShape)
        {
            _vm.Devices.Clear();
            foreach (var snapshot in snapshots)
            {
                _vm.Devices.Add(snapshot);
            }

            return;
        }

        for (var index = 0; index < snapshots.Count; index++)
        {
            ApplyDeviceSnapshot(_vm.Devices[index], snapshots[index]);
        }
    }

    private static void ApplyDeviceSnapshot(DeviceVm target, DeviceVm snapshot)
    {
        target.DisplayName = snapshot.DisplayName;
        target.Transport = snapshot.Transport;
        target.SerialPort = snapshot.SerialPort;
        target.Mac = snapshot.Mac;
        target.StaIp = snapshot.StaIp;
        target.Role = NormalizeRoleForUi(snapshot.Role, target.Role);
        target.Status = snapshot.Status;
        target.Detail = snapshot.Detail;
        target.WifiStatus = snapshot.WifiStatus;
        target.UsbStatus = snapshot.UsbStatus;
        target.Online = snapshot.Online;
        target.IsUsbPreferred = snapshot.IsUsbPreferred;
        target.UdpPort = snapshot.UdpPort;
        target.AdcMask = snapshot.AdcMask;
        target.BoardTarget = snapshot.BoardTarget;
        target.FirmwareVersion = snapshot.FirmwareVersion;
        target.ReportHz = snapshot.ReportHz;
        target.ThumbPin = snapshot.ThumbPin;
        target.IndexPin = snapshot.IndexPin;
        target.MiddlePin = snapshot.MiddlePin;
        target.RingPin = snapshot.RingPin;
        target.PinkyPin = snapshot.PinkyPin;
        target.JoystickVrxPin = snapshot.JoystickVrxPin;
        target.JoystickVryPin = snapshot.JoystickVryPin;
        target.JoystickSwPin = snapshot.JoystickSwPin;
        target.FingerModuleCount = snapshot.FingerModuleCount;
        target.FingerModuleSummary = snapshot.FingerModuleSummary;
        target.JoystickConnectionText = snapshot.JoystickConnectionText;
        target.BatteryAvailable = snapshot.BatteryAvailable;
        target.BatteryPercent = snapshot.BatteryPercent;
        target.BatteryMillivolts = snapshot.BatteryMillivolts;
        target.BatteryChargingKnown = snapshot.BatteryChargingKnown;
        target.BatteryCharging = snapshot.BatteryCharging;
        target.BatterySummary = snapshot.BatterySummary;
        target.BatteryVoltageText = snapshot.BatteryVoltageText;
        target.CalibrationState = snapshot.CalibrationState;
        target.LastSeenTransport = snapshot.LastSeenTransport;
        target.LastSeenText = snapshot.LastSeenText;
    }

    private string BuildDeviceJoystickSummary(DiscoveryDevice device)
    {
        var configured = device.JoystickVrxPin >= 0 || device.JoystickVryPin >= 0 || device.JoystickSwPin >= 0;
        if (!configured)
        {
            return "未连接摇杆";
        }

        if (CanUseRoleRuntimeFallback(device.Role)
            && TryGetRecentRuntimeJoystickState(device.Role, DateTime.UtcNow, out var hasLiveInput)
            && hasLiveInput)
        {
            return "摇杆在线";
        }

        return "已配置摇杆";
    }

    private static string BuildBatterySummary(bool available, int percent, bool chargingKnown, bool charging)
    {
        if (!available || percent < 0)
        {
            return "未接入电量检测";
        }

        if (chargingKnown)
        {
            return charging ? $"{percent}% · 充电中" : $"{percent}% · 未充电";
        }

        return $"{percent}%";
    }

    private static string BuildBatteryVoltageText(bool available, int millivolts)
    {
        if (!available || millivolts <= 0)
        {
            return "--";
        }

        return $"{millivolts / 1000.0:0.00} V";
    }

    private static int CountFingerModules(params int[] pins)
    {
        return pins.Where(pin => pin >= 0).Distinct().Count();
    }

    internal async Task RefreshPortsAsync(bool ignoreFirmwareBusy = false)
    {
        if (_firmwareBusy && !ignoreFirmwareBusy)
        {
            return;
        }

        var availablePorts = await GetAvailablePortsSnapshotAsync(DateTime.UtcNow, forceRefresh: true);
        UpdateFirmwarePortOptions(availablePorts);
        RefreshUiFromState();
    }

    private void UpdateFirmwarePortOptions(IReadOnlyList<string> availablePorts)
    {
        var nowUtc = DateTime.UtcNow;
        var currentSet = new HashSet<string>(availablePorts, StringComparer.OrdinalIgnoreCase);

        if (!_firmwarePortsInitialized)
        {
            _knownFirmwarePorts.Clear();
            foreach (var port in availablePorts)
            {
                _knownFirmwarePorts.Add(port);
            }

            _firmwarePortsInitialized = true;
        }
        else
        {
            foreach (var newPort in currentSet.Except(_knownFirmwarePorts, StringComparer.OrdinalIgnoreCase))
            {
                _firmwarePortArrivedUtc[newPort] = nowUtc;
            }

            foreach (var removedPort in _knownFirmwarePorts.Except(currentSet, StringComparer.OrdinalIgnoreCase).ToList())
            {
                _firmwarePortArrivedUtc.Remove(removedPort);
            }

            _knownFirmwarePorts.Clear();
            foreach (var port in availablePorts)
            {
                _knownFirmwarePorts.Add(port);
            }
        }

        foreach (var expired in _firmwarePortArrivedUtc
                     .Where(item => (nowUtc - item.Value) > TimeSpan.FromSeconds(30))
                     .Select(item => item.Key)
                     .ToList())
        {
            _firmwarePortArrivedUtc.Remove(expired);
        }

        var previousPort = _vm.FirmwarePort;
        var preferredPort = _vm.SelectedDevice?.SerialPort;

        if (FirmwarePortInventoryMatches(availablePorts))
        {
            RefreshFirmwarePortAgeLabels();
            return;
        }

        _vm.FirmwarePorts.Clear();
        foreach (var port in availablePorts)
        {
            _vm.FirmwarePorts.Add(new FirmwarePortOption
            {
                Value = port,
                Label = BuildFirmwarePortLabel(port, nowUtc)
            });
        }

        if (ContainsFirmwarePort(previousPort))
        {
            _vm.FirmwarePort = previousPort;
        }
        else if (!string.IsNullOrWhiteSpace(preferredPort) && ContainsFirmwarePort(preferredPort))
        {
            _vm.FirmwarePort = preferredPort;
        }
        else if (availablePorts.Count == 1)
        {
            _vm.FirmwarePort = availablePorts[0];
        }
        else if (!string.IsNullOrWhiteSpace(previousPort))
        {
            _vm.FirmwarePort = string.Empty;
        }
    }

    private bool FirmwarePortInventoryMatches(IReadOnlyList<string> availablePorts)
    {
        if (_vm.FirmwarePorts.Count != availablePorts.Count)
        {
            return false;
        }

        for (var index = 0; index < availablePorts.Count; index++)
        {
            if (!string.Equals(_vm.FirmwarePorts[index].Value, availablePorts[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void RefreshFirmwarePortAgeLabels()
    {
        if (_vm.FirmwarePorts.Count == 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var option in _vm.FirmwarePorts)
        {
            var nextLabel = BuildFirmwarePortLabel(option.Value, nowUtc);
            if (!string.Equals(option.Label, nextLabel, StringComparison.Ordinal))
            {
                option.Label = nextLabel;
            }
        }
    }

    private string BuildFirmwarePortLabel(string port, DateTime nowUtc)
    {
        if (_firmwarePortArrivedUtc.TryGetValue(port, out var arrivedUtc))
        {
            var seconds = (int)Math.Floor((nowUtc - arrivedUtc).TotalSeconds);
            if (seconds <= 30)
            {
                return $"{port}（{Math.Max(1, seconds)}秒前接入）";
            }
        }

        return port;
    }

    private bool ContainsFirmwarePort(string? port)
    {
        return !string.IsNullOrWhiteSpace(port)
            && _vm.FirmwarePorts.Any(item => string.Equals(item.Value, port, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadFirmwareSettingsIntoVm()
    {
        _vm.FirmwareTarget = FirmwareTargetCatalog.NormalizeTarget(_config.Firmware.Target);
        _vm.FirmwareSource = string.IsNullOrWhiteSpace(_config.Firmware.PreferredSource) ? "bundled" : _config.Firmware.PreferredSource;
        SetFirmwareReportRateSilently(_config.Firmware.ReportRateHz, markUserOverride: _config.Firmware.ReportRateHz > 0);
        _vm.FirmwareVersionTag = _config.Firmware.VersionTag;
        _vm.FirmwareExternalPackagePath = _config.Firmware.ExternalPackagePath ?? string.Empty;
        _vm.FirmwareOnlineCatalogUrl = _config.Firmware.OnlineCatalogUrl ?? string.Empty;
        _vm.FirmwareThumbPin = _config.Firmware.ThumbPin;
        _vm.FirmwareIndexPin = _config.Firmware.IndexPin;
        _vm.FirmwareMiddlePin = _config.Firmware.MiddlePin;
        _vm.FirmwareRingPin = _config.Firmware.RingPin;
        _vm.FirmwarePinkyPin = _config.Firmware.PinkyPin;
        _vm.FirmwareTrackingSwitchPin = _config.Firmware.TrackingSwitchPin;
        _vm.FirmwareTrackingSwitchMode = _config.Firmware.TrackingSwitchMode;
        _vm.FirmwareJoystickVrxPin = _config.Firmware.JoystickVrxPin;
        _vm.FirmwareJoystickVryPin = _config.Firmware.JoystickVryPin;
        _vm.FirmwareJoystickSwPin = _config.Firmware.JoystickSwPin;
        _vm.FirmwareBatteryAdcPin = _config.Firmware.BatteryAdcPin;
        _vm.FirmwareBatteryChargePin = _config.Firmware.BatteryChargePin;
        RefreshFirmwareTargetOptions(normalizeSelection: true);
    }

    private void RefreshFirmwareTargetOptions(bool normalizeSelection)
    {
        var definition = FirmwareTargetCatalog.Get(_vm.FirmwareTarget);

        _vm.FirmwareAdcPinOptions.Clear();
        foreach (var pin in definition.AdcPins)
        {
            _vm.FirmwareAdcPinOptions.Add(new FirmwarePinOption { Value = pin, Label = $"GPIO{pin}" });
        }

        _vm.FirmwareSwitchPinOptions.Clear();
        foreach (var pin in definition.TrackingSwitchPins)
        {
            _vm.FirmwareSwitchPinOptions.Add(new FirmwarePinOption { Value = pin, Label = pin < 0 ? "不使用追踪开关" : $"GPIO{pin}" });
        }

        _vm.FirmwareOptionalAdcPinOptions.Clear();
        _vm.FirmwareOptionalAdcPinOptions.Add(new FirmwarePinOption { Value = -1, Label = "不使用摇杆轴" });
        foreach (var pin in definition.AdcPins)
        {
            _vm.FirmwareOptionalAdcPinOptions.Add(new FirmwarePinOption { Value = pin, Label = $"GPIO{pin}" });
        }

        _vm.FirmwareOptionalSwitchPinOptions.Clear();
        _vm.FirmwareOptionalSwitchPinOptions.Add(new FirmwarePinOption { Value = -1, Label = "不使用摇杆按键" });
        foreach (var pin in definition.TrackingSwitchPins.Where(pin => pin >= 0))
        {
            _vm.FirmwareOptionalSwitchPinOptions.Add(new FirmwarePinOption { Value = pin, Label = $"GPIO{pin}" });
        }

        if (!normalizeSelection)
        {
            return;
        }

        var defaults = FirmwareTargetCatalog.CreateDefaultConfig(definition.Value);
        var adcPins = new[] { _vm.FirmwareThumbPin, _vm.FirmwareIndexPin, _vm.FirmwareMiddlePin, _vm.FirmwareRingPin, _vm.FirmwarePinkyPin };
        if (adcPins.Any(pin => !FirmwareTargetCatalog.IsValidAdcPin(definition.Value, pin)))
        {
            _vm.FirmwareThumbPin = defaults.ThumbPin;
            _vm.FirmwareIndexPin = defaults.IndexPin;
            _vm.FirmwareMiddlePin = defaults.MiddlePin;
            _vm.FirmwareRingPin = defaults.RingPin;
            _vm.FirmwarePinkyPin = defaults.PinkyPin;
        }

        if (!FirmwareTargetCatalog.IsValidTrackingSwitchPin(definition.Value, _vm.FirmwareTrackingSwitchPin))
        {
            _vm.FirmwareTrackingSwitchPin = defaults.TrackingSwitchPin;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalAdcPin(definition.Value, _vm.FirmwareJoystickVrxPin))
        {
            _vm.FirmwareJoystickVrxPin = defaults.JoystickVrxPin;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalAdcPin(definition.Value, _vm.FirmwareJoystickVryPin))
        {
            _vm.FirmwareJoystickVryPin = defaults.JoystickVryPin;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalSwitchPin(definition.Value, _vm.FirmwareJoystickSwPin))
        {
            _vm.FirmwareJoystickSwPin = defaults.JoystickSwPin;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalAdcPin(definition.Value, _vm.FirmwareBatteryAdcPin))
        {
            _vm.FirmwareBatteryAdcPin = defaults.BatteryAdcPin;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalSwitchPin(definition.Value, _vm.FirmwareBatteryChargePin))
        {
            _vm.FirmwareBatteryChargePin = defaults.BatteryChargePin;
        }

        var usedAdcPins = new HashSet<int> { _vm.FirmwareThumbPin, _vm.FirmwareIndexPin, _vm.FirmwareMiddlePin, _vm.FirmwareRingPin, _vm.FirmwarePinkyPin };
        if (_vm.FirmwareTrackingSwitchPin >= 0 && usedAdcPins.Contains(_vm.FirmwareTrackingSwitchPin))
        {
            _vm.FirmwareTrackingSwitchPin = -1;
            _vm.FirmwareTrackingSwitchMode = "disabled";
        }

        if (_vm.FirmwareJoystickVrxPin >= 0 && usedAdcPins.Contains(_vm.FirmwareJoystickVrxPin))
        {
            _vm.FirmwareJoystickVrxPin = -1;
        }

        if (_vm.FirmwareJoystickVrxPin >= 0 && _vm.FirmwareJoystickVrxPin == _vm.FirmwareTrackingSwitchPin)
        {
            _vm.FirmwareJoystickVrxPin = -1;
        }

        if (_vm.FirmwareJoystickVryPin >= 0
            && (usedAdcPins.Contains(_vm.FirmwareJoystickVryPin)
                || _vm.FirmwareJoystickVryPin == _vm.FirmwareTrackingSwitchPin
                || _vm.FirmwareJoystickVryPin == _vm.FirmwareJoystickVrxPin))
        {
            _vm.FirmwareJoystickVryPin = -1;
        }

        if (_vm.FirmwareJoystickSwPin >= 0
            && (_vm.FirmwareJoystickSwPin == _vm.FirmwareTrackingSwitchPin
                || _vm.FirmwareJoystickSwPin == _vm.FirmwareJoystickVrxPin
                || _vm.FirmwareJoystickSwPin == _vm.FirmwareJoystickVryPin
                || usedAdcPins.Contains(_vm.FirmwareJoystickSwPin)))
        {
            _vm.FirmwareJoystickSwPin = -1;
        }

        if (_vm.FirmwareBatteryAdcPin >= 0
            && (usedAdcPins.Contains(_vm.FirmwareBatteryAdcPin)
                || _vm.FirmwareBatteryAdcPin == _vm.FirmwareJoystickVrxPin
                || _vm.FirmwareBatteryAdcPin == _vm.FirmwareJoystickVryPin))
        {
            _vm.FirmwareBatteryAdcPin = -1;
        }

        if (_vm.FirmwareBatteryChargePin >= 0
            && (_vm.FirmwareBatteryChargePin == _vm.FirmwareTrackingSwitchPin
                || _vm.FirmwareBatteryChargePin == _vm.FirmwareJoystickVrxPin
                || _vm.FirmwareBatteryChargePin == _vm.FirmwareJoystickVryPin
                || _vm.FirmwareBatteryChargePin == _vm.FirmwareJoystickSwPin
                || _vm.FirmwareBatteryChargePin == _vm.FirmwareBatteryAdcPin
                || usedAdcPins.Contains(_vm.FirmwareBatteryChargePin)))
        {
            _vm.FirmwareBatteryChargePin = -1;
        }

        if (!Esp32C3PinCatalog.IsValidTrackingSwitchMode(_vm.FirmwareTrackingSwitchMode))
        {
            _vm.FirmwareTrackingSwitchMode = defaults.TrackingSwitchMode;
        }

        if (_vm.FirmwareReportRateHz <= 0)
        {
            SetFirmwareReportRateSilently(definition.DefaultReportRateHz, markUserOverride: false);
        }
    }

    private void SetFirmwareReportRateSilently(int reportRateHz, bool markUserOverride)
    {
        var normalized = Math.Clamp(reportRateHz <= 0 ? 30 : reportRateHz, 10, 240);
        _suppressFirmwareReportRateEvents = true;
        try
        {
            _vm.FirmwareReportRateHz = normalized;
        }
        finally
        {
            _suppressFirmwareReportRateEvents = false;
        }

        _firmwareReportRateUserOverride = markUserOverride;
    }

    private void AttachFingerHandlers(string side, ObservableCollection<FingerRuntimeVm> fingers)
    {
        foreach (var finger in fingers)
        {
            var capturedFinger = finger;
            finger.PropertyChanged += (_, args) =>
            {
                if (_suppressFingerConfigSave || args.PropertyName != nameof(FingerRuntimeVm.Active))
                {
                    return;
                }

                SaveFingerEnabledState(side, capturedFinger);
            };
        }
    }

    private void SaveFingerEnabledState(string side, FingerRuntimeVm finger)
    {
        var hand = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? _config.Hands.Left : _config.Hands.Right;
        if (!hand.Fingers.TryGetValue(finger.Name, out var fingerConfig))
        {
            fingerConfig = new FingerConfig();
            hand.Fingers[finger.Name] = fingerConfig;
        }

        fingerConfig.Enabled = finger.Active;
        _configStore.Save(_config);
        PublishRuntimeFrame();
        SetPinnedStatusLine($"{(side == "left" ? "左手" : "右手")}{finger.DisplayName}{(finger.Active ? "已启用" : "已关闭")}。", 3);
    }

    private void ApplyFingerConfigState()
    {
        ApplyFingerConfigState("left", _vm.LeftFingers);
        ApplyFingerConfigState("right", _vm.RightFingers);
    }

    private void ApplyFingerConfigState(string side, ObservableCollection<FingerRuntimeVm> fingers)
    {
        var hand = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? _config.Hands.Left : _config.Hands.Right;
        _suppressFingerConfigSave = true;
        try
        {
            foreach (var finger in fingers)
            {
                if (hand.Fingers.TryGetValue(finger.Name, out var fingerConfig))
                {
                    finger.Active = fingerConfig.Enabled;
                    finger.CalibratedOpenRaw = fingerConfig.CalibratedOpenRaw;
                    finger.CalibratedClosedRaw = fingerConfig.CalibratedClosedRaw;
                }
                else
                {
                    finger.Active = true;
                    finger.CalibratedOpenRaw = -1;
                    finger.CalibratedClosedRaw = -1;
                }
            }
        }
        finally
        {
            _suppressFingerConfigSave = false;
        }
    }

    private bool TryPrepareFirmwareConfig(out FirmwareConfig firmwareConfig, out string error)
    {
        var target = FirmwareTargetCatalog.NormalizeTarget(_vm.FirmwareTarget);
        var definition = FirmwareTargetCatalog.Get(target);
        firmwareConfig = new FirmwareConfig
        {
            Target = target,
            ReportRateHz = Math.Clamp(_vm.FirmwareReportRateHz, 10, 240),
            VersionTag = _vm.SelectedFirmwarePackage?.Version ?? _config.Firmware.VersionTag,
            ThumbPin = _vm.FirmwareThumbPin,
            IndexPin = _vm.FirmwareIndexPin,
            MiddlePin = _vm.FirmwareMiddlePin,
            RingPin = _vm.FirmwareRingPin,
            PinkyPin = _vm.FirmwarePinkyPin,
            TrackingSwitchPin = _vm.FirmwareTrackingSwitchPin,
            TrackingSwitchMode = _vm.FirmwareTrackingSwitchMode,
            JoystickVrxPin = _vm.FirmwareJoystickVrxPin,
            JoystickVryPin = _vm.FirmwareJoystickVryPin,
            JoystickSwPin = _vm.FirmwareJoystickSwPin,
            BatteryAdcPin = _vm.FirmwareBatteryAdcPin,
            BatteryChargePin = _vm.FirmwareBatteryChargePin
        };

        var adcPins = new[]
        {
            firmwareConfig.ThumbPin,
            firmwareConfig.IndexPin,
            firmwareConfig.MiddlePin,
            firmwareConfig.RingPin,
            firmwareConfig.PinkyPin
        };

        if (string.IsNullOrWhiteSpace(_vm.FirmwarePort))
        {
            error = "请先选择刷写串口。";
            return false;
        }

        if (adcPins.Any(pin => !FirmwareTargetCatalog.IsValidAdcPin(target, pin)))
        {
            error = $"GPIO 选择超出 {definition.Label} 的 ADC 范围，请重新检查五根手指。";
            return false;
        }

        if (!FirmwareTargetCatalog.IsValidTrackingSwitchPin(target, firmwareConfig.TrackingSwitchPin))
        {
            error = "追踪开关引脚选择无效。";
            return false;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalAdcPin(target, firmwareConfig.JoystickVrxPin)
            || !FirmwareTargetCatalog.IsValidOptionalAdcPin(target, firmwareConfig.JoystickVryPin))
        {
            error = $"摇杆 VRX / VRY 选择超出 {definition.Label} 的 ADC 范围。";
            return false;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalSwitchPin(target, firmwareConfig.JoystickSwPin))
        {
            error = "摇杆按键 GPIO 选择无效。";
            return false;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalAdcPin(target, firmwareConfig.BatteryAdcPin))
        {
            error = $"电池检测 ADC 超出 {definition.Label} 的 ADC 范围。";
            return false;
        }

        if (!FirmwareTargetCatalog.IsValidOptionalSwitchPin(target, firmwareConfig.BatteryChargePin))
        {
            error = "充电检测 GPIO 选择无效。";
            return false;
        }

        if (!Esp32C3PinCatalog.IsValidTrackingSwitchMode(firmwareConfig.TrackingSwitchMode))
        {
            error = "追踪开关模式无效。";
            return false;
        }

        if (firmwareConfig.TrackingSwitchPin < 0)
        {
            firmwareConfig.TrackingSwitchMode = "disabled";
        }
        else if (adcPins.Contains(firmwareConfig.TrackingSwitchPin))
        {
            error = "追踪开关引脚不能和手指 ADC 引脚重复。";
            return false;
        }

        if (firmwareConfig.JoystickVrxPin >= 0
            && (adcPins.Contains(firmwareConfig.JoystickVrxPin)
                || firmwareConfig.JoystickVrxPin == firmwareConfig.TrackingSwitchPin))
        {
            error = firmwareConfig.JoystickVrxPin == firmwareConfig.TrackingSwitchPin
                ? "摇杆 VRX 不能和追踪开关 GPIO 重复。"
                : "摇杆 VRX 不能和手指 ADC 引脚重复。";
            return false;
        }

        if (firmwareConfig.JoystickVryPin >= 0
            && (adcPins.Contains(firmwareConfig.JoystickVryPin)
                || firmwareConfig.JoystickVryPin == firmwareConfig.TrackingSwitchPin
                || firmwareConfig.JoystickVryPin == firmwareConfig.JoystickVrxPin))
        {
            error = firmwareConfig.JoystickVryPin == firmwareConfig.JoystickVrxPin
                ? "摇杆 VRX 和 VRY 不能使用同一个 ADC GPIO。"
                : firmwareConfig.JoystickVryPin == firmwareConfig.TrackingSwitchPin
                    ? "摇杆 VRY 不能和追踪开关 GPIO 重复。"
                    : "摇杆 VRY 不能和手指 ADC 引脚重复。";
            return false;
        }

        if (firmwareConfig.JoystickSwPin >= 0
            && (adcPins.Contains(firmwareConfig.JoystickSwPin)
                || firmwareConfig.JoystickSwPin == firmwareConfig.TrackingSwitchPin
                || firmwareConfig.JoystickSwPin == firmwareConfig.JoystickVrxPin
                || firmwareConfig.JoystickSwPin == firmwareConfig.JoystickVryPin))
        {
            error = "摇杆按键 GPIO 不能和已有 ADC / 开关 GPIO 重复。";
            return false;
        }

        if (firmwareConfig.BatteryAdcPin >= 0
            && (adcPins.Contains(firmwareConfig.BatteryAdcPin)
                || firmwareConfig.BatteryAdcPin == firmwareConfig.JoystickVrxPin
                || firmwareConfig.BatteryAdcPin == firmwareConfig.JoystickVryPin))
        {
            error = "电池检测 ADC 不能和手指或摇杆轴 GPIO 重复。";
            return false;
        }

        if (firmwareConfig.BatteryChargePin >= 0
            && (adcPins.Contains(firmwareConfig.BatteryChargePin)
                || firmwareConfig.BatteryChargePin == firmwareConfig.TrackingSwitchPin
                || firmwareConfig.BatteryChargePin == firmwareConfig.JoystickVrxPin
                || firmwareConfig.BatteryChargePin == firmwareConfig.JoystickVryPin
                || firmwareConfig.BatteryChargePin == firmwareConfig.JoystickSwPin
                || firmwareConfig.BatteryChargePin == firmwareConfig.BatteryAdcPin))
        {
            error = "充电检测 GPIO 不能和已有 ADC / 开关 GPIO 重复。";
            return false;
        }

        _config.Firmware = firmwareConfig;
        _config.Firmware.PreferredSource = _vm.FirmwareSource;
        _config.Firmware.ExternalPackagePath = _vm.FirmwareExternalPackagePath;
        _config.Firmware.OnlineCatalogUrl = _vm.FirmwareOnlineCatalogUrl;
        _config.Firmware.LastPackageId = _vm.SelectedFirmwarePackage?.Id ?? _config.Firmware.LastPackageId;
        _configStore.Save(_config);
        _vm.FirmwareVersionTag = firmwareConfig.VersionTag;
        error = string.Empty;
        return true;
    }

    private async Task FlashSelectedFirmwarePackageAsync(FirmwarePackageVm package)
    {
        try
        {
            if (!TryPrepareFirmwareConfig(out var firmwareConfig, out var error))
            {
                SetPinnedStatusLine(error, 4);
                return;
            }

            ResetFirmwareLogs();
            SetFirmwareBusy(true, "正在准备固件刷写...");
            AppendFriendlyFirmwareLine($"已选择固件包：{package.Summary}");
            AppendFriendlyFirmwareLine($"目标串口：{_vm.FirmwarePort}");

            var flashPayload = await FirmwareToolClient.RunJsonAsync(
                "flash-package",
                "--port",
                _vm.FirmwarePort,
                "--manifest",
                package.ManifestPath);

            AppendFirmwareToolPayload(flashPayload);
            if (!flashPayload["ok"]?.GetValue<bool>() ?? false)
            {
                throw new InvalidOperationException(flashPayload["message"]?.GetValue<string>() ?? "固件包刷写失败。");
            }

            var usedPort = flashPayload["port"]?.GetValue<string>() ?? _vm.FirmwarePort;
            if (!string.IsNullOrWhiteSpace(usedPort))
            {
                _vm.FirmwarePort = usedPort;
            }

            AppendFriendlyFirmwareLine("固件写入完成，等待设备重启...");
            await WaitForDeviceAfterFlashAsync();

            if (ShouldSkipPostFlashRuntimeConfig(package, firmwareConfig))
            {
                AppendFriendlyFirmwareLine("当前选择与 S3 内置固件默认值一致，已跳过刷后运行配置写入。");
            }
            else
            {
                SetFirmwareBusy(true, "正在写入设备运行配置...");
                await ApplyRuntimeFirmwareConfigAsync(firmwareConfig);
            }

            SetFirmwareBusy(true, "正在验证固件版本...");
            var verifyPayload = await WaitForFirmwareVerificationAsync();
            AppendFirmwareToolPayload(verifyPayload);
            if (!verifyPayload["ok"]?.GetValue<bool>() ?? false)
            {
                var fallbackMessage = verifyPayload["message"]?.GetValue<string>() ?? "固件验证失败。";
                throw new InvalidOperationException(await ResolveFirmwareVerificationFailureMessageAsync(_vm.FirmwarePort, fallbackMessage));
            }

            UpdateFirmwareDetectionFromToolPayload(verifyPayload);
            await RefreshDevicesAsync(ignoreFirmwareBusy: true, forceSerialProbe: true);
            SetPinnedStatusLine($"刷写完成：{BuildDetectedFirmwareSummary()}", 6);
            AppendFriendlyFirmwareLine($"刷写完成：{BuildDetectedFirmwareSummary()}");
            MaybeShowTrayNotification("OpenFinger 刷写完成", BuildDetectedFirmwareSummary(), ClientNotificationKind.Flash, Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            var message = FormatFirmwareFailure("固件刷写", ex);
            SetPinnedStatusLine(message, 8);
            AppendFriendlyFirmwareLine(message);
            MaybeShowTrayNotification("OpenFinger 刷写失败", message, ClientNotificationKind.Flash, Forms.ToolTipIcon.Error);
        }
        finally
        {
            SetFirmwareBusy(false);
            RefreshUiFromState();
        }
    }

    private void OnPacketReceived(string sourceIp, int[] raws, int mask, bool? trackingEnabled, int? joystickRawX, int? joystickRawY, bool? joystickPressed)
    {
        var nowUtc = DateTime.UtcNow;
        var side = ResolveSideForSourceIp(sourceIp);
        UpdateRuntimeCache(side, raws, mask, trackingEnabled, joystickRawX, joystickRawY, joystickPressed, nowUtc);
        PublishRuntimeFrame();
        lock (_latestRuntimePacketLock)
        {
            _latestRuntimeSourceIp = sourceIp;
            _latestRuntimeSide = side;
            _latestRuntimeMask = mask;
            _latestRuntimeTrackingEnabled = trackingEnabled;
            _latestRuntimeJoystickRawX = joystickRawX;
            _latestRuntimeJoystickRawY = joystickRawY;
            _latestRuntimeJoystickPressed = joystickPressed;
            _latestRuntimePacketUtc = nowUtc;
        }

        var trackingText = trackingEnabled.HasValue ? $" tracking={(trackingEnabled.Value ? 1 : 0)}" : string.Empty;
        var joystickText = joystickRawX.HasValue || joystickRawY.HasValue || joystickPressed.HasValue
            ? $" joy=({(joystickRawX?.ToString() ?? "-")},{(joystickRawY?.ToString() ?? "-")},{(joystickPressed.HasValue ? (joystickPressed.Value ? "1" : "0") : "-")})"
            : string.Empty;
        var line = $"[{nowUtc.ToLocalTime():HH:mm:ss}] {sourceIp} mask={mask}{trackingText}{joystickText} raw={string.Join(",", raws)}";
        _runtimeLines.Enqueue(line);
        while (_runtimeLines.Count > MaxRuntimeLogLines)
        {
            _runtimeLines.TryDequeue(out _);
        }

        Interlocked.Exchange(ref _runtimeUiDirty, 1);
    }

    private void OnHeartbeatReceived(string sourceIp, SerialStatusDto status)
    {
        var nowUtc = DateTime.UtcNow;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            CacheHeartbeat(sourceIp, status, nowUtc);
            UpdateKnownDeviceFromHeartbeat(sourceIp, status);
            RequestDashboardRefresh(nowUtc, forceRefresh: true);
        }));
    }

    private async Task SendProvisionAsync(string wifiPassword)
    {
        if (_vm.SelectedDevice is null)
        {
            SetPinnedStatusLine("先选择设备。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.Ssid))
        {
            SetPinnedStatusLine("Wi-Fi 名称不能为空。");
            return;
        }

        var configuredHostIp = string.IsNullOrWhiteSpace(_vm.HostIp) ? "auto" : _vm.HostIp.Trim();
        var hostIp = OpenFingerWire.ResolveHostIp(configuredHostIp, _vm.SelectedDevice.StaIp);
        if (string.IsNullOrWhiteSpace(hostIp))
        {
            SetPinnedStatusLine("没有找到可用的本机局域网 IP。");
            return;
        }

        var udpPort = int.TryParse(_vm.UdpPort, out var parsedUdp) ? parsedUdp : 39001;
        var adcMask = int.TryParse(_vm.AdcMask, out var parsedMask) ? parsedMask : 31;
        var command = OpenFingerWire.BuildProvisionCommand(_vm.Ssid, wifiPassword, hostIp, udpPort, adcMask, _vm.Role);
        var transportLabel = GetSelectedCommandTransportLabel();

        if (IsSelectedUsbUsable())
        {
            await OpenFingerWire.SendSerialCommandAsync(_vm.SelectedDevice.SerialPort, command);
        }
        else
        {
            SetPinnedStatusLine("当前设备没有可用的 USB 写入通道。请先连接 USB。");
            return;
        }

        SaveKnownDevice(_vm.SelectedDevice, configuredHostIp, udpPort, adcMask, _vm.Role);
        AppendLog($"已通过 {transportLabel} 发送配网命令: {command}");
        SetPinnedStatusLine($"已通过 {transportLabel} 发送配网命令，正在等待设备确认...");

        var ack = await WaitForSelectedDeviceStatusAsync(status =>
            string.Equals(status.State, "connecting", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "connected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "connected_streaming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "streaming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "error", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(6));

        if (ack is null)
        {
            SetPinnedStatusLine("已发送配置，但设备没有回状态。请检查 USB 串口是否稳定。");
            return;
        }

        if (string.Equals(ack.State, "error", StringComparison.OrdinalIgnoreCase))
        {
            SetPinnedStatusLine($"设备返回错误: {DescribeDeviceStatus(ack)}");
            return;
        }

        if (string.Equals(ack.State, "connecting", StringComparison.OrdinalIgnoreCase))
        {
            SetPinnedStatusLine($"设备已开始连接 Wi-Fi: {DescribeDeviceStatus(ack)}");
            var final = await WaitForSelectedDeviceStatusAsync(status =>
                string.Equals(status.State, "connected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.State, "connected_streaming", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.State, "streaming", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.State, "error", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(30));

            SetPinnedStatusLine(final is null
                ? "设备已开始连接 Wi-Fi，但 30 秒内没有确认结果。"
                : string.Equals(final.State, "error", StringComparison.OrdinalIgnoreCase)
                    ? $"Wi-Fi 连接失败: {DescribeDeviceStatus(final)}"
                    : $"Wi-Fi 连接成功: {DescribeDeviceStatus(final)}");
            return;
        }

        SetPinnedStatusLine($"设备已接受配置: {DescribeDeviceStatus(ack)}");
    }

    private async Task SendRoleAsync()
    {
        if (_vm.SelectedDevice is null)
        {
            SetPinnedStatusLine("先选择设备。");
            return;
        }

        var command = OpenFingerWire.BuildRoleCommand(_vm.Role);
        var transportLabel = GetSelectedCommandTransportLabel();
        if (IsSelectedUsbUsable())
        {
            await OpenFingerWire.SendSerialCommandAsync(_vm.SelectedDevice.SerialPort, command);
        }
        else
        {
            SetPinnedStatusLine("当前设备没有可用的 USB 写入通道。请先连接 USB。");
            return;
        }

        SaveKnownDevice(_vm.SelectedDevice, _config.Runtime.HostIp, _vm.SelectedDevice.UdpPort, _vm.SelectedDevice.AdcMask, _vm.Role);
        AppendLog($"已通过 {transportLabel} 发送角色命令: {_vm.Role}");
        SetPinnedStatusLine($"已通过 {transportLabel} 发送左右手设置，正在等待设备确认...");
        var status = await WaitForSelectedDeviceStatusAsync(item =>
            string.Equals(item.State, "configured", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.State, "streaming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.State, "connected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.State, "connected_streaming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.State, "error", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5));
        SetPinnedStatusLine(status is null
            ? "已发送左右手设置，但没有收到确认。"
            : string.Equals(status.State, "error", StringComparison.OrdinalIgnoreCase)
                ? $"左右手设置失败: {DescribeDeviceStatus(status)}"
                : $"左右手已更新: {DescribeDeviceStatus(status)}");
    }

    private async Task SendIdentifyAsync()
    {
        if (_vm.SelectedDevice is null)
        {
            SetPinnedStatusLine("先选择设备。");
            return;
        }

        var transportLabel = GetSelectedCommandTransportLabel();
        if (IsSelectedUsbUsable())
        {
            await OpenFingerWire.SendSerialCommandAsync(_vm.SelectedDevice.SerialPort, "OFIDENT");
        }
        else
        {
            SetPinnedStatusLine("当前设备没有可用的 USB 识别通道。请先连接 USB。");
            return;
        }

        AppendLog($"已通过 {transportLabel} 发送识别命令。");
        SetPinnedStatusLine($"已通过 {transportLabel} 发送识别命令，正在等待设备确认...");
        var status = await WaitForSelectedDeviceStatusAsync(item =>
            string.Equals(item.State, "identify", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.State, "error", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(4));
        SetPinnedStatusLine(status is null
            ? "已发送识别命令，但设备没有确认。"
            : string.Equals(status.State, "error", StringComparison.OrdinalIgnoreCase)
                ? $"识别失败: {DescribeDeviceStatus(status)}"
                : $"识别灯已触发: {DescribeDeviceStatus(status)}");
    }

    private async Task<SerialStatusDto?> QuerySelectedDeviceStatusAsync()
    {
        if (_vm.SelectedDevice is null || string.IsNullOrWhiteSpace(_vm.SelectedDevice.SerialPort))
        {
            return null;
        }

        try
        {
            var status = await OpenFingerWire.QuerySerialStatusAsync(_vm.SelectedDevice.SerialPort);
            if (status is not null)
            {
                CacheSerialStatus(_vm.SelectedDevice.SerialPort, status, DateTime.UtcNow);
                UpdateKnownDeviceFromStatus(_vm.SelectedDevice, status);
                return status;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"USB 状态查询失败: {ex.Message}");
        }

        return null;
    }

    private async Task<SerialStatusDto?> WaitForSelectedDeviceStatusAsync(Func<SerialStatusDto, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        SerialStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await QuerySelectedDeviceStatusAsync();
            if (last is not null)
            {
                AppendLog($"设备状态: {last.State ?? "-"} | {last.Message ?? "-"}");
                if (predicate(last))
                {
                    return last;
                }
            }

            await Task.Delay(600);
        }

        return last;
    }

    private async Task WaitForDeviceAfterFlashAsync()
    {
        _vm.StatusLine = "刷写完成，等待设备重启并重新识别...";
        var initialPort = _vm.FirmwarePort;
        var readyPort = await WaitForFirmwarePortReadyAsync(initialPort, FirmwarePortReadyTimeout);
        if (!string.Equals(initialPort, readyPort, StringComparison.OrdinalIgnoreCase))
        {
            AppendFriendlyFirmwareLine($"设备重启后串口已切换到 {readyPort}。");
        }

        _vm.FirmwarePort = readyPort;
    }

    private async Task ApplyRuntimeFirmwareConfigAsync(FirmwareConfig firmwareConfig)
    {
        if (string.IsNullOrWhiteSpace(_vm.FirmwarePort))
        {
            throw new InvalidOperationException("没有可用的设备串口，无法写入运行配置。");
        }

        var hostIp = OpenFingerWire.ResolveHostIp(_config.Runtime.HostIp, _vm.SelectedDevice?.StaIp);
        var command = OpenFingerWire.BuildRuntimeConfigCommand(
            firmwareConfig,
            string.IsNullOrWhiteSpace(_vm.Role) ? _vm.SelectedDevice?.Role : _vm.Role,
            hostIp,
            int.TryParse(_vm.UdpPort, out var udpPort) ? udpPort : _config.Runtime.DeviceUdpPort);

        AppendFriendlyFirmwareLine("正在应用回报率、手型和 GPIO 预设...");
        var readyPort = await WaitForFirmwarePortReadyAsync(_vm.FirmwarePort, TimeSpan.FromSeconds(10));
        await SendRuntimeConfigWithRetryAsync(readyPort, command);
        _vm.FirmwarePort = readyPort;
        await Task.Delay(500);
        AppendFriendlyFirmwareLine("运行配置已发送，开始回读确认。");
    }

    private static bool ShouldSkipPostFlashRuntimeConfig(FirmwarePackageVm package, FirmwareConfig firmwareConfig)
    {
        if (!string.Equals(FirmwareTargetCatalog.NormalizeTarget(firmwareConfig.Target), FirmwareTargetCatalog.Esp32S3, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var manifest = FirmwareCatalogService.LoadManifestOrThrow(package.ManifestPath);
            var profile = manifest.DefaultProfile;
            if (profile is null)
            {
                return false;
            }

            return string.Equals(FirmwareTargetCatalog.NormalizeTarget(manifest.Target), FirmwareTargetCatalog.Esp32S3, StringComparison.OrdinalIgnoreCase)
                && (manifest.ReportRateHz <= 0 || manifest.ReportRateHz == firmwareConfig.ReportRateHz)
                && profile.ThumbPin == firmwareConfig.ThumbPin
                && profile.IndexPin == firmwareConfig.IndexPin
                && profile.MiddlePin == firmwareConfig.MiddlePin
                && profile.RingPin == firmwareConfig.RingPin
                && profile.PinkyPin == firmwareConfig.PinkyPin
                && profile.TrackingSwitchPin == firmwareConfig.TrackingSwitchPin
                && profile.JoystickVrxPin == firmwareConfig.JoystickVrxPin
                && profile.JoystickVryPin == firmwareConfig.JoystickVryPin
                && profile.JoystickSwPin == firmwareConfig.JoystickSwPin
                && string.Equals(profile.TrackingSwitchMode ?? "disabled", firmwareConfig.TrackingSwitchMode ?? "disabled", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonObject> WaitForFirmwareVerificationAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(18);
        string? lastMessage = null;
        string currentPort = _vm.FirmwarePort;

        while (DateTime.UtcNow < deadline)
        {
            currentPort = await WaitForFirmwarePortReadyAsync(currentPort, TimeSpan.FromSeconds(6));
            _vm.FirmwarePort = currentPort;

            JsonObject payload;
            try
            {
                payload = await FirmwareToolClient.RunJsonAsync("verify", "--port", currentPort);
            }
            catch (Exception ex) when (IsTransientFirmwareVerifyFailure(ex.Message))
            {
                lastMessage = ex.Message;
                await Task.Delay(900);
                continue;
            }

            if (payload["ok"]?.GetValue<bool>() == true)
            {
                return payload;
            }

            lastMessage = payload["message"]?.GetValue<string>() ?? "设备没有返回状态。";
            if (!IsTransientFirmwareVerifyFailure(lastMessage))
            {
                return payload;
            }

            await Task.Delay(900);
        }

        var fallbackMessage = lastMessage ?? "设备在规定时间内没有返回固件状态。";
        throw new InvalidOperationException(await ResolveFirmwareVerificationFailureMessageAsync(currentPort, fallbackMessage));
    }

    private async Task<string> ResolveFirmwareVerificationFailureMessageAsync(string port, string fallbackMessage)
    {
        var bootloaderMessage = await TryDiagnoseBootloaderModeAsync(port);
        return string.IsNullOrWhiteSpace(bootloaderMessage) ? fallbackMessage : bootloaderMessage;
    }

    private async Task<string?> TryDiagnoseBootloaderModeAsync(string port)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            return null;
        }

        try
        {
            var payload = await FirmwareToolClient.RunJsonAsync("bootloader-info", "--port", port);
            if (!(payload["ok"]?.GetValue<bool>() ?? false))
            {
                return null;
            }

            AppendFriendlyFirmwareLine($"诊断结果：{port} 当前仍能被芯片 ROM 下载器识别，应用没有真正启动。");
            return "固件已经刷入，但设备重启后仍停在芯片 ROM 下载模式，没有进入 OpenFinger 应用。对 ESP32-S3 原生 USB 板子，这既可能是 BOOT/GPIO0 被拉低，也可能是刷写后复位方式不对，芯片没有真正跳回应用。我已经改成更适合 USB Serial/JTAG 的复位方式；如果仍失败，再检查 BOOT 按键和 GPIO0 是否被意外拉低。";
        }
        catch
        {
            return null;
        }
    }

    private void UpdateProcessStatus()
    {
        _steamVrDriverSnapshot = ReadSteamVrDriverSnapshot();
        ApplyProcessStatusSnapshot(ReadProcessStatusSnapshot());
    }

    private static ProcessStatusSnapshot ReadProcessStatusSnapshot()
    {
        return new ProcessStatusSnapshot
        {
            SteamVrRunning = Process.GetProcessesByName("vrmonitor").Any(),
            VrServerRunning = Process.GetProcessesByName("vrserver").Any(),
            BridgeRunning = Process.GetProcessesByName("openfinger_controller_bridge").Any(),
            LegacyServiceRunning = Process.GetProcessesByName("openfinger_service").Any()
        };
    }

    private static void StopSteamVrProcesses()
    {
        foreach (var processName in new[] { "vrmonitor", "vrserver", "vrcompositor", "vrdashboard" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
                catch
                {
                }
            }
        }
    }

    private void ApplyProcessStatusSnapshot(ProcessStatusSnapshot snapshot)
    {
        _vm.SteamVrStatus = snapshot.SteamVrRunning ? "SteamVR: 运行中" : "SteamVR: 未运行";
        _vm.VrServerStatus = snapshot.VrServerRunning ? "vrserver: 运行中" : "vrserver: 未运行";
        _vm.BridgeStatus = snapshot.BridgeRunning ? "bridge: 已启动" : "bridge: 未启动";
        _vm.ServiceStatus = snapshot.LegacyServiceRunning ? "service: 冲突中" : "service: 已停用";
    }

    private void ApplySelectedDeviceState()
    {
        if (_vm.SelectedDevice is null)
        {
            _lastFirmwareSelectionDeviceId = string.Empty;
            _vm.FirmwareDetectedTarget = "未识别";
            _vm.FirmwareDetectedVersion = "--";
            _vm.FirmwareDetectedReportRate = "--";
            SelectRecommendedFirmwarePackage();
            return;
        }

        _vm.Role = NormalizeRoleForUi(_vm.SelectedDevice.Role, _vm.Role);
        if (!string.Equals(_vm.SelectedDevice.Role, _vm.Role, StringComparison.OrdinalIgnoreCase))
        {
            _vm.SelectedDevice.Role = _vm.Role;
        }
        _vm.UdpPort = _vm.SelectedDevice.UdpPort.ToString();
        var resolvedTarget = ResolveFirmwareTargetForDevice(_vm.SelectedDevice);
        _vm.FirmwareDetectedTarget = FirmwareTargetCatalog.Get(resolvedTarget).Label;
        _vm.FirmwareDetectedVersion = string.IsNullOrWhiteSpace(_vm.SelectedDevice.FirmwareVersion) ? "--" : _vm.SelectedDevice.FirmwareVersion;
        _vm.FirmwareDetectedReportRate = _vm.SelectedDevice.ReportHz > 0 ? $"{_vm.SelectedDevice.ReportHz} Hz" : "--";

        if (_suspendFirmwareSelectionSync)
        {
            return;
        }

        var currentDeviceId = _vm.SelectedDevice.Id ?? string.Empty;
        var isNewDeviceSelection = !string.Equals(_lastFirmwareSelectionDeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase);
        if (!isNewDeviceSelection)
        {
            return;
        }

        _lastFirmwareSelectionDeviceId = currentDeviceId;
        _firmwareReportRateUserOverride = false;
        if (!string.Equals(_vm.FirmwareTarget, resolvedTarget, StringComparison.OrdinalIgnoreCase))
        {
            _vm.FirmwareTarget = resolvedTarget;
            return;
        }

        SelectRecommendedFirmwarePackage();
        ApplyPackageSelectionDefaults(forcePackageDefaults: true, forceReportRate: true);
    }

    private void RefreshUiFromState()
    {
        var leftDevice = ResolveDeviceForSide("left");
        var rightDevice = ResolveDeviceForSide("right");
        var homeState = BuildHomeDashboardState(leftDevice, rightDevice);
        var allowVisiblePageUpdates = !(_isHiddenToTray && _config.Ui.Tray.ReduceLoadWhenHidden);
        UpdateChromeStatus(homeState, leftDevice, rightDevice);

        if (allowVisiblePageUpdates && HomePageView.Visibility == Visibility.Visible)
        {
            HomePageView.UpdateDashboard(homeState);
        }

        if (allowVisiblePageUpdates && FirmwarePageView.Visibility == Visibility.Visible)
        {
            FirmwarePageView.UpdateDashboard(BuildFirmwareDashboardState());
        }

        if (allowVisiblePageUpdates && CalibrationPageView.Visibility == Visibility.Visible)
        {
            CalibrationPageView.RefreshFingerCards(_vm.LeftFingers, _vm.RightFingers, _vm.LeftJoystick, _vm.RightJoystick);
        }

        if (allowVisiblePageUpdates && GesturePageView.Visibility == Visibility.Visible)
        {
            GesturePageView.UpdateDashboard(BuildGestureDashboardState());
        }

        if (allowVisiblePageUpdates && StatusPageView.Visibility == Visibility.Visible)
        {
            StatusPageView.UpdateStatus(BuildDiagnosticsDashboardState(leftDevice, rightDevice));
        }

        if (allowVisiblePageUpdates && DiagnosticsPageView.Visibility == Visibility.Visible)
        {
            DiagnosticsPageView.UpdateDiagnostics(BuildDiagnosticsDashboardState(leftDevice, rightDevice));
        }

        if (allowVisiblePageUpdates && SettingsPageView.Visibility == Visibility.Visible)
        {
            SettingsPageView.UpdateSettings(BuildSettingsDashboardState());
        }
    }

    private string BuildPendingFirmwareSummary()
    {
        var package = ResolveSelectedFirmwarePackage();
        if (package is not null)
        {
            return $"{package.DisplayName} · {package.Version} · {_vm.FirmwareReportRateHz} Hz";
        }

        var label = FirmwareTargetCatalog.Get(_vm.FirmwareTarget).Label;
        return $"{label} · {_vm.FirmwareReportRateHz} Hz";
    }

    private string BuildDetectedFirmwareSummary()
    {
        if (_vm.SelectedDevice is null
            && string.IsNullOrWhiteSpace(_vm.FirmwareDetectedTarget)
            && string.IsNullOrWhiteSpace(_vm.FirmwareDetectedVersion))
        {
            return "未选择设备";
        }

        return $"{_vm.FirmwareDetectedTarget} · {_vm.FirmwareDetectedVersion} · {_vm.FirmwareDetectedReportRate}";
    }

    private HomeDashboardState BuildHomeDashboardState(DeviceVm? leftDevice, DeviceVm? rightDevice)
    {
        var left = BuildDeviceReadinessState("左手", leftDevice);
        var right = BuildDeviceReadinessState("右手", rightDevice);
        var connectedDevices = _vm.Devices.Where(IsDeviceConnected).ToList();
        var readyDevices = new[] { leftDevice, rightDevice }.Count(IsDeviceReadyForUse);
        var overall = readyDevices > 0
            ? new StatusBadge(readyDevices >= 2 ? "系统可直接使用" : "已准备好一只手", UiTone.Success)
            : connectedDevices.Count > 0
                ? new StatusBadge("设备已连接，仍需完成设置", UiTone.Info)
                : new StatusBadge("等待连接第一只设备", UiTone.Neutral);

        var nextActionTitle = "连接第一只设备";
        var nextActionDescription = "前往设备页搜索硬件，确认左手/右手归属后继续。";
        var primaryActionKey = "devices";
        var primaryActionLabel = "连接设备";
        var secondaryActionKey = "diagnostics";
        var secondaryActionLabel = "打开诊断";

        if (connectedDevices.Count > 0 && connectedDevices.Any(device => string.IsNullOrWhiteSpace(device.FirmwareVersion)))
        {
            nextActionTitle = "检查并刷写固件";
            nextActionDescription = "设备已经接入，但还没有可靠的固件信息。建议先完成固件刷写。";
            primaryActionKey = "firmware";
            primaryActionLabel = "打开固件刷写";
            secondaryActionKey = "devices";
            secondaryActionLabel = "返回设备页";
        }
        else if (connectedDevices.Count > 0 && connectedDevices.Any(device => !IsCalibrationStateReady(device.CalibrationState)))
        {
            nextActionTitle = "完成校准";
            nextActionDescription = "固件和连接已基本就绪，下一步请记录张开值与握拳值。";
            primaryActionKey = "calibration";
            primaryActionLabel = "开始校准";
            secondaryActionKey = "firmware";
            secondaryActionLabel = "查看固件";
        }
        else if (readyDevices > 0 && !IsSteamVrDriverReady(_steamVrDriverSnapshot))
        {
            nextActionTitle = _steamVrDriverSnapshot.Registered ? "更新 SteamVR 驱动" : "安装 SteamVR 驱动";
            nextActionDescription = _steamVrDriverSnapshot.Registered
                ? "检测到当前 SteamVR 驱动不是这次发行版自带的版本，建议先更新驱动再进入使用环境。"
                : "当前还没有把 OpenFinger 驱动注册到 SteamVR，先安装驱动再继续。";
            primaryActionKey = "driver";
            primaryActionLabel = _steamVrDriverSnapshot.Registered ? "更新驱动" : "安装驱动";
            secondaryActionKey = "diagnostics";
            secondaryActionLabel = "查看诊断";
        }
        else if (readyDevices > 0 && !TrimStatusPrefix(_vm.SteamVrStatus).Contains("运行中", StringComparison.OrdinalIgnoreCase))
        {
            nextActionTitle = "启动 SteamVR";
            nextActionDescription = "硬件侧已经准备完成，打开使用环境后即可进入日常使用状态。";
            primaryActionKey = "steamvr";
            primaryActionLabel = "启动 SteamVR";
            secondaryActionKey = "diagnostics";
            secondaryActionLabel = "查看环境";
        }
        else if (readyDevices > 0)
        {
            nextActionTitle = "系统可直接使用";
            nextActionDescription = "设备、固件、校准和运行环境都已达到可用状态。";
            primaryActionKey = "calibration";
            primaryActionLabel = "查看校准";
            secondaryActionKey = "devices";
            secondaryActionLabel = "管理设备";
        }

        return new HomeDashboardState
        {
            Overall = overall,
            NextActionTitle = nextActionTitle,
            NextActionDescription = nextActionDescription,
            PrimaryActionKey = primaryActionKey,
            PrimaryActionLabel = primaryActionLabel,
            SecondaryActionKey = secondaryActionKey,
            SecondaryActionLabel = secondaryActionLabel,
            Left = left,
            Right = right
        };
    }

    private DeviceReadinessState BuildDeviceReadinessState(string title, DeviceVm? device)
    {
        if (device is null)
        {
            return new DeviceReadinessState
            {
                Title = title,
                Connection = new StatusBadge("未连接", UiTone.Neutral),
                Firmware = new StatusBadge("未识别", UiTone.Neutral),
                Calibration = new StatusBadge("待校准", UiTone.Warning),
                Usage = new StatusBadge("未就绪", UiTone.Neutral),
                Detail = "还没有识别到这只手的设备。",
                Meta = "等待接入"
            };
        }

        var connection = device.Online && device.WifiStatus.Contains("在线", StringComparison.OrdinalIgnoreCase)
            ? new StatusBadge("实时在线", UiTone.Success)
            : IsDeviceConnected(device)
                ? new StatusBadge("已连接", UiTone.Info)
                : new StatusBadge("离线", UiTone.Neutral);

        var firmware = string.IsNullOrWhiteSpace(device.FirmwareVersion)
            ? new StatusBadge("未识别", UiTone.Warning)
            : new StatusBadge(device.FirmwareVersion, UiTone.Success);

        var calibrationReady = IsCalibrationStateReady(device.CalibrationState);
        var calibration = calibrationReady
            ? new StatusBadge("已校准", UiTone.Success)
            : new StatusBadge("待校准", UiTone.Warning);

        var usage = IsDeviceReadyForUse(device)
            ? new StatusBadge("可使用", UiTone.Success)
            : IsDeviceConnected(device)
                ? new StatusBadge("待完成设置", UiTone.Warning)
                : new StatusBadge("未就绪", UiTone.Neutral);

        return new DeviceReadinessState
        {
            Title = title,
            Connection = connection,
            Firmware = firmware,
            Calibration = calibration,
            Usage = usage,
            Detail = string.IsNullOrWhiteSpace(device.Detail) ? device.ConnectionSummary : device.Detail,
            Meta = string.IsNullOrWhiteSpace(device.LastSeenText) ? "等待通信" : device.LastSeenText
        };
    }

    private DiagnosticsDashboardState BuildDiagnosticsDashboardState(DeviceVm? leftDevice, DeviceVm? rightDevice)
    {
        var conflict = _vm.ServiceStatus.Contains("冲突", StringComparison.OrdinalIgnoreCase);
        var bridgeRunning = _vm.BridgeStatus.Contains("已启动", StringComparison.OrdinalIgnoreCase);
        var steamVrRunning = _vm.SteamVrStatus.Contains("运行中", StringComparison.OrdinalIgnoreCase);
        var anyOnline = IsDeviceStreaming(leftDevice) || IsDeviceStreaming(rightDevice);
        var anyConnected = IsDeviceConnected(leftDevice) || IsDeviceConnected(rightDevice);

        var kitBadge = conflict
            ? new StatusBadge("发现冲突", UiTone.Danger)
            : bridgeRunning
                ? new StatusBadge("已就绪", UiTone.Success)
                : new StatusBadge("未启动", UiTone.Warning);

        var steamVrBadge = steamVrRunning
            ? new StatusBadge("运行中", UiTone.Success)
            : new StatusBadge("未运行", UiTone.Neutral);

        var deviceCommBadge = anyOnline
            ? new StatusBadge("实时在线", UiTone.Success)
            : anyConnected
                ? new StatusBadge("已连接", UiTone.Info)
                : new StatusBadge("未检测到设备", UiTone.Neutral);

        var driverBadge = !_steamVrDriverSnapshot.RuntimeDetected || !_steamVrDriverSnapshot.ToolAvailable
            ? new StatusBadge("未找到运行时", UiTone.Warning)
            : !_steamVrDriverSnapshot.FilesReady
                ? new StatusBadge("当前发行版缺少驱动", UiTone.Warning)
                : !_steamVrDriverSnapshot.Registered
                    ? new StatusBadge("未安装", UiTone.Warning)
                    : _steamVrDriverSnapshot.HasMultipleRegistrations
                        ? new StatusBadge("注册冲突", UiTone.Danger)
                        : _steamVrDriverSnapshot.IsLatest
                            ? new StatusBadge("已安装且最新", UiTone.Success)
                            : new StatusBadge("已安装但需更新", UiTone.Warning);

        return new DiagnosticsDashboardState
        {
            OpenFingerKit = kitBadge,
            SteamVr = steamVrBadge,
            DeviceComm = deviceCommBadge,
            Driver = driverBadge,
            DriverActionLabel = IsSteamVrDriverReady(_steamVrDriverSnapshot) ? "重新安装驱动" : "安装/更新驱动",
            DriverInstalled = _steamVrDriverSnapshot.Registered,
            OpenFingerKitDetail = conflict
                ? "检测到旧版 service 仍在运行，建议保持由 OpenFinger.Control 接管。"
                : bridgeRunning
                    ? "OpenFingerKit 关键组件已启动。"
                    : "关键桥接组件尚未启动，可在此页手动启动。",
            SteamVrDetail = steamVrRunning
                ? "SteamVR 已运行，可以直接进入使用流程。"
                : "SteamVR 当前未运行，只有在需要进入使用环境时再启动即可。",
            DeviceCommDetail = anyOnline
                ? "至少一只设备正在持续回传运行时数据。"
                : anyConnected
                    ? "已经识别到设备连接，但运行时数据还没有开始流动。"
                    : "当前没有检测到可通信的设备。",
            DriverDetail = BuildSteamVrDriverDetail(_steamVrDriverSnapshot),
            FriendlyLog = BuildFriendlyLogText(),
            RawLog = BuildCombinedLogText(),
            ShowAdvanced = _vm.ShowAdvanced,
            ControllerStyle = BuildControllerStyleDashboardState()
        };
    }

    private FirmwareDashboardState BuildFirmwareDashboardState()
    {
        var package = ResolveSelectedFirmwarePackage();
        var currentDeviceTitle = _vm.SelectedDevice?.DisplayName ?? "未选择设备";
        var currentDeviceDetail = _vm.SelectedDevice is null
            ? "可以直接选择串口刷写，也可以先在设备页选择目标设备。"
            : $"{_vm.SelectedDevice.Status} · {_vm.SelectedDevice.ConnectionSummary}";

        return new FirmwareDashboardState
        {
            SelectedDeviceTitle = currentDeviceTitle,
            SelectedDeviceDetail = currentDeviceDetail,
            SourceStatus = _vm.FirmwareCatalogStatus,
            CurrentFirmwareText = BuildDetectedFirmwareSummary(),
            TargetFirmwareText = BuildPendingFirmwareSummary(),
            RecommendationText = BuildFirmwareRecommendationText(package),
            BootHint = package?.BootHint ?? FirmwareTargetCatalog.Get(_vm.FirmwareTarget).BootHint,
            ProgressText = _vm.StatusLine,
            Busy = _firmwareBusy,
            ShowAdvanced = _vm.ShowAdvanced
        };
    }

    private string BuildFirmwareRecommendationText(FirmwarePackageVm? package)
    {
        if (package is null)
        {
            return "没有匹配到可用固件包";
        }

        var deviceTarget = ResolveFirmwareTargetForDevice(_vm.SelectedDevice);
        if (_vm.SelectedDevice is not null && string.Equals(deviceTarget, package.Target, StringComparison.OrdinalIgnoreCase))
        {
            return $"已为当前设备匹配：{package.DisplayName}";
        }

        return $"当前选择：{package.DisplayName}";
    }

    private string ResolveFirmwareTargetForDevice(DeviceVm? device)
    {
        if (device is null)
        {
            return FirmwareTargetCatalog.NormalizeTarget(_config.Firmware.Target);
        }

        if (!string.IsNullOrWhiteSpace(device.BoardTarget))
        {
            return FirmwareTargetCatalog.NormalizeTarget(device.BoardTarget);
        }

        var saved = _config.Devices.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(device.Mac) && string.Equals(item.Mac, device.Mac, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(device.SerialPort) && string.Equals(item.SerialPort, device.SerialPort, StringComparison.OrdinalIgnoreCase))
            || string.Equals(item.Name, device.DisplayName, StringComparison.OrdinalIgnoreCase));
        if (saved is not null && !string.IsNullOrWhiteSpace(saved.BoardTarget))
        {
            return FirmwareTargetCatalog.NormalizeTarget(saved.BoardTarget);
        }

        return FirmwareTargetCatalog.NormalizeTarget(_config.Firmware.Target);
    }

    private void UpdateChromeStatus(HomeDashboardState state, DeviceVm? leftDevice, DeviceVm? rightDevice)
    {
        StatusReadyDot.Fill = UiTonePalette.Accent(state.Overall.Tone);
        StatusReadyText.Text = state.Overall.Text;
        StatusReadyText.Foreground = UiTonePalette.Text(state.Overall.Tone);

        CompactLeftBadgeBorder.Background = UiTonePalette.Background(state.Left.Usage.Tone);
        CompactLeftStatusText.Text = state.Left.Usage.Text;
        CompactLeftStatusText.Foreground = UiTonePalette.Text(state.Left.Usage.Tone);

        CompactRightBadgeBorder.Background = UiTonePalette.Background(state.Right.Usage.Tone);
        CompactRightStatusText.Text = state.Right.Usage.Text;
        CompactRightStatusText.Foreground = UiTonePalette.Text(state.Right.Usage.Tone);
    }

    private static Brush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private static string TrimStatusPrefix(string? status)
    {
        return (status ?? string.Empty)
            .Replace("service: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("SteamVR: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("bridge: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("vrserver: ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string GetDeviceStatusText(DeviceVm? device)
    {
        return string.IsNullOrWhiteSpace(device?.Status) ? "离线" : device.Status;
    }

    private static string GetDeviceHeadline(DeviceVm? device)
    {
        return string.IsNullOrWhiteSpace(device?.ConnectionSummary) ? "未检测到设备" : device.ConnectionSummary;
    }

    private static string GetWifiStateText(DeviceVm? device)
    {
        if (device is null)
        {
            return "--";
        }

        if (device.WifiStatus.Contains("在线", StringComparison.OrdinalIgnoreCase)
            || device.WifiStatus.Contains("已连接", StringComparison.OrdinalIgnoreCase))
        {
            return "已连接";
        }

        return "未连接";
    }

    private string GetPacketRateText(DeviceVm? device)
    {
        return IsDeviceStreaming(device) ? $"{_config.Runtime.PublishHz} Hz" : "-- Hz";
    }

    private static string GetFirmwareStateText(DeviceVm? device)
    {
        if (device is null)
        {
            return "--";
        }

        var status = GetDeviceStatusText(device);
        if (status.Contains("追踪关闭", StringComparison.OrdinalIgnoreCase))
        {
            return "追踪关闭";
        }

        if (status.Contains("在线", StringComparison.OrdinalIgnoreCase))
        {
            return "在线";
        }

        if (device.UsbStatus.Contains("已连接", StringComparison.OrdinalIgnoreCase)
            || device.WifiStatus.Contains("已连接", StringComparison.OrdinalIgnoreCase))
        {
            return "待机";
        }

        return "离线";
    }

    private static bool IsDeviceStreaming(DeviceVm? device)
    {
        var status = GetDeviceStatusText(device);
        return status.Contains("在线", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeviceConnected(DeviceVm? device)
    {
        if (device is null)
        {
            return false;
        }

        return IsDeviceStreaming(device)
            || device.WifiStatus.Contains("已连接", StringComparison.OrdinalIgnoreCase)
            || device.WifiStatus.Contains("在线", StringComparison.OrdinalIgnoreCase)
            || device.UsbStatus.Contains("已连接", StringComparison.OrdinalIgnoreCase);
    }

    private static StatusVisual ResolveDeviceVisual(DeviceVm? device)
    {
        var status = GetDeviceStatusText(device);
        if (status.Contains("追踪关闭", StringComparison.OrdinalIgnoreCase))
        {
            return WarningVisual;
        }

        if (status.Contains("在线", StringComparison.OrdinalIgnoreCase))
        {
            return SuccessVisual;
        }

        if (status.Contains("Wi-Fi 已连接", StringComparison.OrdinalIgnoreCase)
            || status.Contains("USB 已连接", StringComparison.OrdinalIgnoreCase)
            || IsDeviceConnected(device))
        {
            return ConnectedVisual;
        }

        return OfflineVisual;
    }

    private static StatusVisual ResolveWifiVisual(DeviceVm? device)
    {
        if (device is null)
        {
            return OfflineVisual;
        }

        if (device.WifiStatus.Contains("在线", StringComparison.OrdinalIgnoreCase))
        {
            return SuccessVisual;
        }

        if (device.WifiStatus.Contains("已连接", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectedVisual;
        }

        return OfflineVisual;
    }

    private static StatusVisual ResolveFirmwareVisual(DeviceVm? device)
    {
        return GetFirmwareStateText(device) switch
        {
            "在线" => SuccessVisual,
            "待机" => ConnectedVisual,
            "追踪关闭" => WarningVisual,
            _ => OfflineVisual
        };
    }

    private static StatusVisual ResolveServiceVisual(string status)
    {
        var trimmed = TrimStatusPrefix(status);
        if (trimmed.Contains("冲突", StringComparison.OrdinalIgnoreCase))
        {
            return DangerVisual;
        }

        if (trimmed.Contains("已停用", StringComparison.OrdinalIgnoreCase))
        {
            return WarningVisual;
        }

        if (trimmed.Contains("已启动", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("运行中", StringComparison.OrdinalIgnoreCase))
        {
            return SuccessVisual;
        }

        return OfflineVisual;
    }

    private static StatusVisual ResolveBridgeVisual(string status)
    {
        var trimmed = TrimStatusPrefix(status);
        if (trimmed.Contains("已启动", StringComparison.OrdinalIgnoreCase))
        {
            return SuccessVisual;
        }

        if (trimmed.Contains("未启动", StringComparison.OrdinalIgnoreCase))
        {
            return OfflineVisual;
        }

        return WarningVisual;
    }

    private static StatusVisual ResolveLegacyVisual(string serviceStatus)
    {
        return serviceStatus.Contains("已停用", StringComparison.OrdinalIgnoreCase)
            ? WarningVisual
            : DangerVisual;
    }

    private static StatusVisual ResolveRuntimeVisual(string status)
    {
        return TrimStatusPrefix(status).Contains("运行中", StringComparison.OrdinalIgnoreCase)
            ? SuccessVisual
            : OfflineVisual;
    }

    private static (StatusVisual Visual, string Text) ResolveOverallStatus(DeviceVm? leftDevice, DeviceVm? rightDevice, StatusVisual serviceVisual, StatusVisual bridgeVisual)
    {
        var activeCount = 0;
        if (IsDeviceStreaming(leftDevice))
        {
            activeCount++;
        }

        if (IsDeviceStreaming(rightDevice))
        {
            activeCount++;
        }

        if (activeCount > 0)
        {
            return (SuccessVisual, activeCount == 2 ? "系统处于双手在线状态" : "系统处于单手在线状态");
        }

        var connectedCount = 0;
        if (IsDeviceConnected(leftDevice))
        {
            connectedCount++;
        }

        if (IsDeviceConnected(rightDevice))
        {
            connectedCount++;
        }

        if (connectedCount > 0)
        {
            return (ConnectedVisual, connectedCount == 2 ? "双手已连接，等待运行时数据" : "已有设备连接，等待运行时数据");
        }

        if (ReferenceEquals(serviceVisual, DangerVisual) || ReferenceEquals(bridgeVisual, WarningVisual))
        {
            return (WarningVisual, "当前没有在线追踪器");
        }

        return (OfflineVisual, "当前没有在线追踪器");
    }

    private string BuildCombinedLogText()
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_vm.ServiceLog))
        {
            builder.AppendLine("[Service]");
            builder.AppendLine(_vm.ServiceLog.Trim());
            builder.AppendLine();
        }

        var runtimeLog = _runtimeLines.IsEmpty
            ? string.Empty
            : string.Join(Environment.NewLine, _runtimeLines.ToArray());
        if (!string.IsNullOrWhiteSpace(runtimeLog))
        {
            builder.AppendLine("[UDP / ADC]");
            builder.AppendLine(runtimeLog.Trim());
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(_vm.FirmwareOutput))
        {
            builder.AppendLine("[Firmware]");
            builder.AppendLine(_vm.FirmwareOutput.Trim());
        }

        return builder.ToString().Trim();
    }

    private string BuildFriendlyLogText()
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_vm.StatusLine))
        {
            builder.AppendLine(_vm.StatusLine.Trim());
        }

        if (!string.IsNullOrWhiteSpace(_vm.LastSeenRuntime))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine(_vm.LastSeenRuntime.Trim());
        }

        if (!string.IsNullOrWhiteSpace(_vm.FirmwareFriendlyOutput))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine(_vm.FirmwareFriendlyOutput.Trim());
        }

        return builder.ToString().Trim();
    }

    private void ResetFirmwareLogs()
    {
        _vm.FirmwareFriendlyOutput = string.Empty;
        _vm.FirmwareOutput = string.Empty;
    }

    private void AppendFriendlyFirmwareLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
        _vm.FirmwareFriendlyOutput = string.IsNullOrWhiteSpace(_vm.FirmwareFriendlyOutput)
            ? line
            : $"{_vm.FirmwareFriendlyOutput}{Environment.NewLine}{line}";
    }

    private void AppendFirmwareToolPayload(JsonObject payload)
    {
        var output = payload["output"]?.GetValue<string>();
        var stderr = payload["stderr"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(output))
        {
            AppendFirmwareOutput(output + Environment.NewLine);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            AppendFirmwareOutput(stderr + Environment.NewLine);
        }

        var message = payload["message"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(message))
        {
            AppendFriendlyFirmwareLine(message);
        }
    }

    private FirmwarePackageVm? ResolveSelectedFirmwarePackage()
    {
        if (_vm.SelectedFirmwarePackage is not null)
        {
            return _vm.SelectedFirmwarePackage;
        }

        return _vm.FirmwarePackages.FirstOrDefault();
    }

    private void SelectRecommendedFirmwarePackage()
    {
        if (_vm.FirmwarePackages.Count == 0)
        {
            _vm.SelectedFirmwarePackage = null;
            return;
        }

        var selected = _vm.SelectedFirmwarePackage;
        if (selected is not null && _vm.FirmwarePackages.Any(item => string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var preferredTarget = ResolveFirmwareTargetForDevice(_vm.SelectedDevice);
        var match = _vm.FirmwarePackages.FirstOrDefault(item => string.Equals(item.Target, preferredTarget, StringComparison.OrdinalIgnoreCase))
            ?? _vm.FirmwarePackages.FirstOrDefault(item => string.Equals(item.Id, _config.Firmware.LastPackageId, StringComparison.OrdinalIgnoreCase))
            ?? _vm.FirmwarePackages.FirstOrDefault();
        _vm.SelectedFirmwarePackage = match;
    }

    private void ApplyPackageSelectionDefaults(bool forcePackageDefaults = false, bool forceReportRate = false)
    {
        var selected = ResolveSelectedFirmwarePackage();
        if (selected is null)
        {
            _lastAppliedFirmwareDefaultsPackageId = string.Empty;
            _lastAppliedFirmwareDefaultsTarget = string.Empty;
            return;
        }

        var normalizedTarget = FirmwareTargetCatalog.NormalizeTarget(selected.Target);
        var packageChanged = !string.Equals(_lastAppliedFirmwareDefaultsPackageId, selected.Id, StringComparison.OrdinalIgnoreCase);
        var targetChanged = !string.Equals(_lastAppliedFirmwareDefaultsTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase);
        var shouldApplyPackageDefaults = forcePackageDefaults || packageChanged;
        var shouldApplyReportRate = packageChanged
            || targetChanged
            || _vm.FirmwareReportRateHz <= 0
            || (forceReportRate && !_firmwareReportRateUserOverride);

        _config.Firmware.LastPackageId = selected.Id;
        _vm.FirmwareTarget = normalizedTarget;
        if (!_vm.ShowAdvanced)
        {
            try
            {
                var manifest = FirmwareCatalogService.LoadManifestOrThrow(selected.ManifestPath);
                if (shouldApplyPackageDefaults && manifest.DefaultProfile is not null)
                {
                    _vm.FirmwareThumbPin = manifest.DefaultProfile.ThumbPin;
                    _vm.FirmwareIndexPin = manifest.DefaultProfile.IndexPin;
                    _vm.FirmwareMiddlePin = manifest.DefaultProfile.MiddlePin;
                    _vm.FirmwareRingPin = manifest.DefaultProfile.RingPin;
                    _vm.FirmwarePinkyPin = manifest.DefaultProfile.PinkyPin;
                    _vm.FirmwareTrackingSwitchPin = manifest.DefaultProfile.TrackingSwitchPin;
                    _vm.FirmwareTrackingSwitchMode = manifest.DefaultProfile.TrackingSwitchMode;
                    _vm.FirmwareJoystickVrxPin = manifest.DefaultProfile.JoystickVrxPin;
                    _vm.FirmwareJoystickVryPin = manifest.DefaultProfile.JoystickVryPin;
                    _vm.FirmwareJoystickSwPin = manifest.DefaultProfile.JoystickSwPin;
                    _vm.FirmwareBatteryAdcPin = manifest.DefaultProfile.BatteryAdcPin;
                    _vm.FirmwareBatteryChargePin = manifest.DefaultProfile.BatteryChargePin;
                }

                if (shouldApplyReportRate)
                {
                    SetFirmwareReportRateSilently(
                        manifest.ReportRateHz > 0
                            ? manifest.ReportRateHz
                            : FirmwareTargetCatalog.Get(selected.Target).DefaultReportRateHz,
                        markUserOverride: false);
                }

                _vm.FirmwareVersionTag = manifest.Version;
            }
            catch
            {
                if (shouldApplyReportRate)
                {
                    SetFirmwareReportRateSilently(
                        selected.ReportRateHz > 0
                            ? selected.ReportRateHz
                            : _vm.FirmwareReportRateHz,
                        markUserOverride: false);
                }

                _vm.FirmwareVersionTag = selected.Version;
            }
        }

        RefreshFirmwareTargetOptions(normalizeSelection: true);
        _lastAppliedFirmwareDefaultsPackageId = selected.Id;
        _lastAppliedFirmwareDefaultsTarget = normalizedTarget;
        _configStore.Save(_config);
    }

    private void UpdateFirmwareDetectionFromToolPayload(JsonObject payload)
    {
        var boardTarget = payload["board_target"]?.GetValue<string>();
        var firmwareVersion = payload["firmware_version"]?.GetValue<string>();
        var reportHz = payload["report_hz"]?.GetValue<int?>() ?? 0;
        var normalizedTarget = string.IsNullOrWhiteSpace(boardTarget)
            ? string.Empty
            : FirmwareTargetCatalog.NormalizeTarget(boardTarget);

        if (!string.IsNullOrWhiteSpace(boardTarget))
        {
            _vm.FirmwareDetectedTarget = FirmwareTargetCatalog.Get(boardTarget).Label;
            if (_vm.SelectedDevice is not null)
            {
                _vm.SelectedDevice.BoardTarget = FirmwareTargetCatalog.Get(boardTarget).Label;
            }
        }

        if (!string.IsNullOrWhiteSpace(firmwareVersion))
        {
            _vm.FirmwareDetectedVersion = firmwareVersion!;
            if (_vm.SelectedDevice is not null)
            {
                _vm.SelectedDevice.FirmwareVersion = firmwareVersion!;
            }
        }

        if (reportHz > 0)
        {
            _vm.FirmwareDetectedReportRate = $"{reportHz} Hz";
            if (_vm.SelectedDevice is not null)
            {
                _vm.SelectedDevice.ReportHz = reportHz;
            }
        }

        if (_suspendFirmwareSelectionSync)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(normalizedTarget)
            && !string.Equals(_vm.FirmwareTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            _vm.FirmwareTarget = normalizedTarget;
            return;
        }

        SelectRecommendedFirmwarePackage();
    }

    private string ResolveCalibrationStateForRole(string? role)
    {
        return string.Equals(role, "left", StringComparison.OrdinalIgnoreCase)
            ? ResolveCalibrationState(_config.Hands.Left)
            : string.Equals(role, "right", StringComparison.OrdinalIgnoreCase)
                ? ResolveCalibrationState(_config.Hands.Right)
                : "待校准";
    }

    private static string ResolveCalibrationState(HandConfig hand)
    {
        foreach (var finger in hand.Fingers.Values)
        {
            if (finger.CalibratedOpenRaw < 0 || finger.CalibratedClosedRaw < 0 || finger.CalibratedOpenRaw == finger.CalibratedClosedRaw)
            {
                return "待校准";
            }
        }

        return "已校准";
    }

    private void SyncKnownCalibrationStates()
    {
        foreach (var device in _config.Devices)
        {
            device.CalibrationState = ResolveCalibrationStateForRole(device.SavedRole);
        }

        foreach (var device in _vm.Devices)
        {
            device.CalibrationState = ResolveCalibrationStateForRole(device.Role);
        }
    }

    private static bool IsCalibrationStateReady(string? state)
    {
        return string.Equals(state, "已校准", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLastSeenTransport(DiscoveryDevice device)
    {
        if (device.WifiActive)
        {
            return "Wi-Fi 实时";
        }

        if (device.WifiConnected)
        {
            return "Wi-Fi";
        }

        if (device.UsbConnected)
        {
            return "USB";
        }

        return "离线";
    }

    private static string BuildLastSeenText(DiscoveryDevice device)
    {
        var transport = ResolveLastSeenTransport(device);
        return transport == "离线"
            ? "当前离线"
            : $"{transport} · {device.LastSeenUtc.ToLocalTime():HH:mm:ss}";
    }

    private static bool IsDeviceReadyForUse(DeviceVm? device)
    {
        return device is not null
            && IsDeviceStreaming(device)
            && !string.IsNullOrWhiteSpace(device.FirmwareVersion)
            && IsCalibrationStateReady(device.CalibrationState);
    }

    private DeviceVm? ResolveDeviceForSide(string side)
    {
        return _vm.Devices.FirstOrDefault(item => string.Equals(item.Role, side, StringComparison.OrdinalIgnoreCase))
            ?? _vm.Devices.FirstOrDefault(item => string.Equals(item.DisplayName, side, StringComparison.OrdinalIgnoreCase));
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _serviceLogLines.Enqueue(line);
        while (_serviceLogLines.Count > MaxServiceLogLines)
        {
            _serviceLogLines.Dequeue();
        }

        _vm.ServiceLog = string.Join(Environment.NewLine, _serviceLogLines.Reverse());
    }

    private void AppendFirmwareOutput(string text)
    {
        Dispatcher.Invoke(() => _vm.FirmwareOutput += text);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshUiFromState));
    }

    private void SetPinnedStatusLine(string message, double seconds = 8)
    {
        _vm.StatusLine = message;
        _statusLinePinnedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
        FirmwarePageView.SetProgressText(message);
        RefreshUiFromState();
    }

    private void SetFirmwareBusy(bool busy, string? progressText = null)
    {
        _firmwareBusy = busy;
        FirmwarePageView.SetProgressState(busy, progressText ?? _vm.StatusLine);
    }

    private void SetDeviceActionBusy(bool busy)
    {
        _deviceActionBusy = busy;
    }

    private bool RemoveKnownDevice(DeviceVm device)
    {
        var removed = _config.Devices.RemoveAll(item =>
            (!string.IsNullOrWhiteSpace(device.Mac) && string.Equals(item.Mac, device.Mac, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(device.SerialPort) && string.Equals(item.SerialPort, device.SerialPort, StringComparison.OrdinalIgnoreCase))
            || string.Equals(item.Name, device.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            _configStore.Save(_config);
            return true;
        }

        return false;
    }

    private void RemoveVisibleDeviceCard(DeviceVm device)
    {
        var matches = _vm.Devices
            .Where(item =>
                (!string.IsNullOrWhiteSpace(device.Mac) && string.Equals(item.Mac, device.Mac, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(device.SerialPort) && string.Equals(item.SerialPort, device.SerialPort, StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.DisplayName, device.DisplayName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            return;
        }

        _suppressSelectedDeviceEvents = true;
        try
        {
            foreach (var match in matches)
            {
                _vm.Devices.Remove(match);
            }

            if (_vm.SelectedDevice is not null && matches.Any(item => string.Equals(item.Id, _vm.SelectedDevice.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _vm.SelectedDevice = _vm.Devices.FirstOrDefault();
            }
        }
        finally
        {
            _suppressSelectedDeviceEvents = false;
        }

        ApplySelectedDeviceState();
        RefreshUiFromState();
    }

    private bool IsSelectedUsbUsable()
    {
        return _vm.SelectedDevice is not null
            && !string.IsNullOrWhiteSpace(_vm.SelectedDevice.SerialPort)
            && _cachedAvailablePorts.Contains(_vm.SelectedDevice.SerialPort, StringComparer.OrdinalIgnoreCase);
    }

    private string GetSelectedCommandTransportLabel()
    {
        if (IsSelectedUsbUsable())
        {
            return $"USB {_vm.SelectedDevice!.SerialPort}";
        }

        return "没有可用的 USB 写入通道";
    }

    private static string DescribeDeviceStatus(SerialStatusDto? status)
    {
        if (status is null)
        {
            return "没有读到设备状态回包。";
        }

        var state = string.IsNullOrWhiteSpace(status.State) ? "unknown" : status.State;
        var message = string.IsNullOrWhiteSpace(status.Message) ? "无附加信息" : status.Message;
        return $"{state} | {message}";
    }

    private string ResolveSideForSourceIp(string sourceIp)
    {
        var heartbeatStatus = GetRecentHeartbeatStatus(null, null, sourceIp, DateTime.UtcNow);
        if (!string.IsNullOrWhiteSpace(heartbeatStatus?.Role))
        {
            return string.Equals(heartbeatStatus.Role, "left", StringComparison.OrdinalIgnoreCase) ? "left" : "right";
        }

        var matched = _config.Devices.FirstOrDefault(item => string.Equals(item.StaIp, sourceIp, StringComparison.OrdinalIgnoreCase));
        var role = matched?.SavedRole ?? matched?.PreferredRole ?? "right";
        return string.Equals(role, "left", StringComparison.OrdinalIgnoreCase) ? "left" : "right";
    }

    private static string GetFingerDisplayName(string finger)
    {
        return finger switch
        {
            "thumb" => "拇指",
            "index" => "食指",
            "middle" => "中指",
            "ring" => "无名指",
            "pinky" => "小指",
            _ => finger
        };
    }

    private void StartUdpMonitor(int port, int forwardPort)
    {
        StopUdpMonitor();
        _udpMonitor = new UdpRuntimeMonitor(port, forwardPort);
        _udpMonitor.PacketReceived += OnPacketReceived;
        _udpMonitor.HeartbeatReceived += OnHeartbeatReceived;
    }

    private bool EnsureUdpMonitor(int port, int forwardPort)
    {
        if (_udpMonitor?.Port == port && _udpMonitor?.ForwardPort == forwardPort)
        {
            return true;
        }

        try
        {
            StartUdpMonitor(port, forwardPort);
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"UDP 监听未启动: {ex.Message}");
            StopUdpMonitor();
            return false;
        }
    }

    private void StopUdpMonitor()
    {
        if (_udpMonitor is null)
        {
            return;
        }

        _udpMonitor.PacketReceived -= OnPacketReceived;
        _udpMonitor.HeartbeatReceived -= OnHeartbeatReceived;
        _udpMonitor.Dispose();
        _udpMonitor = null;
    }

    private void SyncUdpMonitorMode()
    {
        EnsureUdpMonitor(_config.Runtime.DeviceUdpPort, _config.Service.RawInputUdpPort);
        EnsureRuntimePublisher(_config.Runtime.LocalRuntimeUdpPort);
        EnsureFingerTestPublisher(FingerTestRuntimePort);
        PublishRuntimeFrame();
        UpdateProcessStatus();
    }

    private void RefreshProcessStatusIfNeeded(DateTime nowUtc, bool forceRefresh = false)
    {
        if (!forceRefresh && (nowUtc - _lastProcessStatusRefreshUtc) < ProcessStatusRefreshInterval)
        {
            return;
        }

        UpdateProcessStatus();
        _lastProcessStatusRefreshUtc = nowUtc;
    }

    private async Task<IReadOnlyList<string>> GetAvailablePortsSnapshotAsync(DateTime nowUtc, bool forceRefresh = false)
    {
        if (!forceRefresh
            && (nowUtc - _lastPortInventoryRefreshUtc) < PortInventoryCacheTtl)
        {
            return _cachedAvailablePorts;
        }

        await _portInventoryLock.WaitAsync();
        try
        {
            if (!forceRefresh
                && (nowUtc - _lastPortInventoryRefreshUtc) < PortInventoryCacheTtl)
            {
                return _cachedAvailablePorts;
            }

            var ports = await Task.Run(FirmwareTools.EnumeratePorts);
            _cachedAvailablePorts = ports;
            _lastPortInventoryRefreshUtc = DateTime.UtcNow;
            return _cachedAvailablePorts;
        }
        finally
        {
            _portInventoryLock.Release();
        }
    }

    private static SerialStatusDto? GetCachedSerialStatus(
        string port,
        DateTime nowUtc,
        IReadOnlyDictionary<string, SerialStatusDto> statusByPort,
        IReadOnlyDictionary<string, DateTime> seenByPort)
    {
        if (!statusByPort.TryGetValue(port, out var status))
        {
            return null;
        }

        return seenByPort.TryGetValue(port, out var seenAtUtc) && (nowUtc - seenAtUtc) <= SerialStatusCacheTtl
            ? status
            : null;
    }

    private static async Task<SerialProbeResult> ProbeSerialPortAsync(
        string port,
        bool shouldProbeSerialNow,
        DateTime nowUtc,
        IReadOnlyDictionary<string, SerialStatusDto> statusByPort,
        IReadOnlyDictionary<string, DateTime> seenByPort)
    {
        var status = !shouldProbeSerialNow
            ? GetCachedSerialStatus(port, nowUtc, statusByPort, seenByPort)
            : null;
        if (status is not null)
        {
            return new SerialProbeResult
            {
                Port = port,
                Status = status
            };
        }

        if (!shouldProbeSerialNow)
        {
            return new SerialProbeResult
            {
                Port = port
            };
        }

        try
        {
            status = await OpenFingerWire.QuerySerialStatusAsync(port);
            return new SerialProbeResult
            {
                Port = port,
                Status = status
            };
        }
        catch (Exception ex)
        {
            return new SerialProbeResult
            {
                Port = port,
                ErrorMessage = ShouldLogSerialFailure(port, ex) ? $"Serial {port}: {ex.Message}" : null
            };
        }
    }

    private static async Task<Dictionary<string, ReachabilityProbeResult>> ProbeReachabilityBatchAsync(
        IEnumerable<string> candidates,
        DateTime nowUtc,
        IReadOnlyDictionary<string, bool> reachableByIp,
        IReadOnlyDictionary<string, DateTime> seenByIp,
        bool allowActiveProbe)
    {
        var results = new Dictionary<string, ReachabilityProbeResult>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Task<ReachabilityProbeResult>>();

        foreach (var candidate in candidates)
        {
            var normalized = NormalizeStaIp(candidate);
            if (string.IsNullOrWhiteSpace(normalized) || results.ContainsKey(normalized))
            {
                continue;
            }

            if (reachableByIp.TryGetValue(normalized, out var cached)
                && seenByIp.TryGetValue(normalized, out var seenAtUtc)
                && (nowUtc - seenAtUtc) <= WifiReachabilityCacheTtl)
            {
                results[normalized] = new ReachabilityProbeResult
                {
                    Ip = normalized,
                    Reachable = cached,
                    SeenUtc = seenAtUtc
                };
                continue;
            }

            if (!allowActiveProbe)
            {
                results[normalized] = new ReachabilityProbeResult
                {
                    Ip = normalized,
                    Reachable = false,
                    SeenUtc = nowUtc
                };
                continue;
            }

            pending.Add(ProbeReachabilityAsync(normalized));
        }

        if (pending.Count > 0)
        {
            foreach (var result in await Task.WhenAll(pending))
            {
                results[result.Ip] = result;
            }
        }

        return results;
    }

    private static async Task<ReachabilityProbeResult> ProbeReachabilityAsync(string ip)
    {
        var reachable = false;
        try
        {
            using var ping = new Ping();
            reachable = (await ping.SendPingAsync(ip, 250)).Status == IPStatus.Success;
        }
        catch
        {
        }

        if (!reachable)
        {
            reachable = await Task.Run(() => TryResolveArp(ip));
        }

        return new ReachabilityProbeResult
        {
            Ip = ip,
            Reachable = reachable,
            SeenUtc = DateTime.UtcNow
        };
    }

    private static bool GetReachabilityValue(IReadOnlyDictionary<string, ReachabilityProbeResult> results, string? ip)
    {
        var normalized = NormalizeStaIp(ip);
        return !string.IsNullOrWhiteSpace(normalized)
            && results.TryGetValue(normalized, out var probe)
            && probe.Reachable;
    }

    private void RequestDashboardRefresh(DateTime nowUtc, bool forceRefresh = false)
    {
        if (!forceRefresh && (nowUtc - _lastDashboardUiRefreshUtc) < DashboardUiRefreshInterval)
        {
            return;
        }

        _lastDashboardUiRefreshUtc = nowUtc;
        RefreshUiFromState();
    }

    private async Task<string> WaitForFirmwarePortReadyAsync(string? preferredPort, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(600);
            await RefreshPortsAsync(ignoreFirmwareBusy: true);
            await RefreshDevicesAsync(ignoreFirmwareBusy: true, forceSerialProbe: true);
            var nowUtc = DateTime.UtcNow;

            foreach (var candidate in BuildFirmwarePortCandidates(preferredPort))
            {
                try
                {
                    var status = await OpenFingerWire.QuerySerialStatusAsync(candidate);
                    _vm.FirmwarePort = candidate;
                    if (status is not null)
                    {
                        CacheSerialStatus(candidate, status, DateTime.UtcNow);
                    }

                    return candidate;
                }
                catch (Exception ex) when (IsTransientSerialPortUnavailable(ex))
                {
                    lastError = ex;
                }
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException($"设备重启后串口仍未就绪：{lastError.Message}", lastError);
        }

        throw new InvalidOperationException("设备重启后没有等到可用串口。");
    }

    private IEnumerable<string> BuildFirmwarePortCandidates(string? preferredPort)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? port)
        {
            if (string.IsNullOrWhiteSpace(port) || yielded.Contains(port))
            {
                return;
            }

            if (_cachedAvailablePorts.Contains(port, StringComparer.OrdinalIgnoreCase)
                || _vm.FirmwarePorts.Any(item => string.Equals(item.Value, port, StringComparison.OrdinalIgnoreCase)))
            {
                yielded.Add(port);
            }
        }

        Add(preferredPort);
        Add(_vm.FirmwarePort);
        Add(_vm.SelectedDevice?.SerialPort);

        foreach (var option in _vm.FirmwarePorts
                     .OrderByDescending(item => _firmwarePortArrivedUtc.TryGetValue(item.Value, out var arrivedUtc) ? arrivedUtc : DateTime.MinValue)
                     .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase))
        {
            Add(option.Value);
        }

        foreach (var port in _cachedAvailablePorts)
        {
            Add(port);
        }

        return yielded;
    }

    private async Task SendRuntimeConfigWithRetryAsync(string initialPort, string command)
    {
        Exception? lastError = null;
        var port = initialPort;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await OpenFingerWire.SendSerialCommandAsync(port, command);
                return;
            }
            catch (Exception ex) when (IsTransientSerialPortUnavailable(ex))
            {
                lastError = ex;
                AppendFriendlyFirmwareLine($"串口 {port} 正在重连，准备第 {attempt + 1} 次尝试...");
                port = await WaitForFirmwarePortReadyAsync(port, TimeSpan.FromSeconds(6));
            }
        }

        throw new InvalidOperationException(lastError?.Message ?? $"无法重新打开串口 {port}。", lastError);
    }

    private static bool IsTransientSerialPortUnavailable(Exception ex)
    {
        var message = ex.Message;
        return ex is UnauthorizedAccessException
            || message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("The port is closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Could not find file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("The device attached to the system is not functioning", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientFirmwareVerifyFailure(string? message)
    {
        var text = message ?? string.Empty;
        return text.Contains("device did not answer OFSTATUS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("did not answer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)
            || text.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Could not find file", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The device attached to the system is not functioning", StringComparison.OrdinalIgnoreCase);
    }

    private void FlushRuntimeUi()
    {
        if (Interlocked.Exchange(ref _runtimeUiDirty, 0) == 0)
        {
            return;
        }

        string sourceIp;
        string side;
        int mask;
        bool? trackingEnabled;
        int? joystickRawX;
        int? joystickRawY;
        bool? joystickPressed;
        DateTime packetUtc;
        lock (_latestRuntimePacketLock)
        {
            sourceIp = _latestRuntimeSourceIp;
            side = _latestRuntimeSide;
            mask = _latestRuntimeMask;
            trackingEnabled = _latestRuntimeTrackingEnabled;
            joystickRawX = _latestRuntimeJoystickRawX;
            joystickRawY = _latestRuntimeJoystickRawY;
            joystickPressed = _latestRuntimeJoystickPressed;
            packetUtc = _latestRuntimePacketUtc;
        }

        if (string.IsNullOrWhiteSpace(sourceIp) || packetUtc == DateTime.MinValue)
        {
            return;
        }

        _udpSeenByIp[sourceIp] = packetUtc;
        _runtimeSeenBySide[side] = packetUtc;
        if (trackingEnabled.HasValue)
        {
            _trackingEnabledBySide[side] = trackingEnabled.Value;
            if (trackingEnabled.Value)
            {
                _trackingDisabledSinceBySide.Remove(side);
            }
            else if (!_trackingDisabledSinceBySide.ContainsKey(side))
            {
                _trackingDisabledSinceBySide[side] = packetUtc;
            }
        }

        UpdateRuntimeFingerViewModels("left", _vm.LeftFingers);
        UpdateRuntimeFingerViewModels("right", _vm.RightFingers);
        UpdateRuntimeJoystickViewModel("left", _vm.LeftJoystick);
        UpdateRuntimeJoystickViewModel("right", _vm.RightJoystick);

        var joystickText = joystickRawX.HasValue || joystickRawY.HasValue || joystickPressed.HasValue
            ? $"  摇杆={joystickRawX?.ToString() ?? "-"}/{joystickRawY?.ToString() ?? "-"}"
            : string.Empty;
        _vm.LastSeenRuntime = $"最近 UDP: {sourceIp}  mask={mask}{joystickText}  {packetUtc.ToLocalTime():HH:mm:ss}";
        if (!_suspendRawLogUiUpdates
            && DiagnosticsPageView.Visibility == Visibility.Visible
            && (packetUtc - _lastRawLogUiUpdateUtc) >= RawLogUiUpdateInterval)
        {
            _lastRawLogUiUpdateUtc = packetUtc;
            _vm.RawPacketLog = string.Join(Environment.NewLine, _runtimeLines);
        }

        if (CalibrationPageView.Visibility == Visibility.Visible)
        {
            CalibrationPageView.RefreshFingerCards(_vm.LeftFingers, _vm.RightFingers, _vm.LeftJoystick, _vm.RightJoystick);
        }

        RequestDashboardRefresh(packetUtc);
    }

    private bool ShouldDeferFullDeviceRefresh(DateTime nowUtc, bool forceSerialProbe, bool hasRecentRuntimeData)
    {
        if (forceSerialProbe || !hasRecentRuntimeData)
        {
            return false;
        }

        var refreshWindow = DevicesPageView.Visibility == Visibility.Visible
            || FirmwarePageView.Visibility == Visibility.Visible
            || StatusPageView.Visibility == Visibility.Visible
            || DiagnosticsPageView.Visibility == Visibility.Visible
            ? VisiblePageRefreshWhileRuntimeActive
            : FullDeviceRefreshWhileRuntimeActive;

        return _lastFullDeviceRefreshUtc != DateTime.MinValue
            && (nowUtc - _lastFullDeviceRefreshUtc) < refreshWindow;
    }

    private void UpdateRuntimeFingerViewModels(string side, ObservableCollection<FingerRuntimeVm> target)
    {
        var runtimeCache = SnapshotRuntimeCache(side);
        for (var i = 0; i < target.Count && i < FingerNames.Length; i++)
        {
            var raw = runtimeCache.Raws[i];
            var active = runtimeCache.PacketActive[i];
            var finger = target[i];
            finger.Raw = raw;
            finger.Bend = runtimeCache.FilteredBends[i];
            finger.PacketActive = active;
            finger.Name = FingerNames[i];
            finger.DisplayName = GetFingerDisplayName(FingerNames[i]);
            if (active && raw >= 0)
            {
                finger.MinRaw = finger.MinRaw < 0 ? raw : Math.Min(finger.MinRaw, raw);
                finger.MaxRaw = finger.MaxRaw < 0 ? raw : Math.Max(finger.MaxRaw, raw);
            }
        }
    }

    private void UpdateRuntimeJoystickViewModel(string side, JoystickRuntimeVm target)
    {
        var runtimeCache = SnapshotRuntimeCache(side);
        var hasAxis = runtimeCache.JoystickRawX >= 0 || runtimeCache.JoystickRawY >= 0;
        var hasSwitch = runtimeCache.JoystickPressed.HasValue;
        var settings = GetJoystickSettings(side);
        var rawAxisX = NormalizeJoystickAxis(side, true, runtimeCache.JoystickRawX);
        var rawAxisY = NormalizeJoystickAxis(side, false, runtimeCache.JoystickRawY);
        var orientedAxis = JoystickOrientationCatalog.Apply(settings.Orientation, rawAxisX, rawAxisY);
        target.Available = hasAxis || hasSwitch;
        target.RawX = runtimeCache.JoystickRawX;
        target.RawY = runtimeCache.JoystickRawY;
        target.SwitchPressed = runtimeCache.JoystickPressed;
        target.AxisX = Math.Clamp(orientedAxis.X, -1.0, 1.0);
        target.AxisY = Math.Clamp(orientedAxis.Y, -1.0, 1.0);
    }

    private void EnsureRuntimePublisher(int port)
    {
        if (_runtimePublisher is null)
        {
            _runtimePublisher = new RuntimeFramePublisher(port);
            return;
        }

        if (_runtimePublisher.Port != port)
        {
            _runtimePublisher.UpdatePort(port);
        }
    }

    private void StopRuntimePublisher()
    {
        _runtimePublisher?.Dispose();
        _runtimePublisher = null;
    }

    private void EnsureFingerTestPublisher(int port)
    {
        if (_fingerTestPublisher is null)
        {
            _fingerTestPublisher = new RuntimeFramePublisher(port);
            return;
        }

        if (_fingerTestPublisher.Port != port)
        {
            _fingerTestPublisher.UpdatePort(port);
        }
    }

    private void StopFingerTestPublisher()
    {
        _fingerTestPublisher?.Dispose();
        _fingerTestPublisher = null;
    }

    private void UpdateRuntimeCache(string side, int[] raws, int mask, bool? trackingEnabled, int? joystickRawX, int? joystickRawY, bool? joystickPressed, DateTime nowUtc)
    {
        lock (_runtimeCacheLock)
        {
            if (!_runtimeCacheBySide.TryGetValue(side, out var cache))
            {
                cache = new RuntimeSideCache();
                _runtimeCacheBySide[side] = cache;
            }

            cache.LastSeenUtc = nowUtc;
            if (trackingEnabled.HasValue)
            {
                cache.TrackingEnabled = trackingEnabled.Value;
                if (trackingEnabled.Value)
                {
                    cache.TrackingDisabledSinceUtc = DateTime.MinValue;
                }
                else if (cache.TrackingDisabledSinceUtc == DateTime.MinValue)
                {
                    cache.TrackingDisabledSinceUtc = nowUtc;
                }
            }

            for (var i = 0; i < 5; i++)
            {
                cache.Raws[i] = i < raws.Length ? raws[i] : -1;
                cache.PacketActive[i] = (mask & (1 << i)) != 0;
                cache.FilteredBends[i] = ComputeFilteredBend(side, FingerNames[i], cache, i, cache.Raws[i], cache.PacketActive[i]);
            }

            cache.JoystickRawX = joystickRawX ?? -1;
            cache.JoystickRawY = joystickRawY ?? -1;
            cache.JoystickPressed = joystickPressed;
        }
    }

    private RuntimeSideCache SnapshotRuntimeCache(string side)
    {
        lock (_runtimeCacheLock)
        {
            if (!_runtimeCacheBySide.TryGetValue(side, out var cache))
            {
                return new RuntimeSideCache();
            }

            var snapshot = new RuntimeSideCache
            {
                LastSeenUtc = cache.LastSeenUtc,
                TrackingEnabled = cache.TrackingEnabled,
                TrackingDisabledSinceUtc = cache.TrackingDisabledSinceUtc
            };
            Array.Copy(cache.Raws, snapshot.Raws, cache.Raws.Length);
            Array.Copy(cache.PacketActive, snapshot.PacketActive, cache.PacketActive.Length);
            Array.Copy(cache.FilteredBends, snapshot.FilteredBends, cache.FilteredBends.Length);
            Array.Copy(cache.FilterInitialized, snapshot.FilterInitialized, cache.FilterInitialized.Length);
            snapshot.JoystickRawX = cache.JoystickRawX;
            snapshot.JoystickRawY = cache.JoystickRawY;
            snapshot.JoystickPressed = cache.JoystickPressed;
            return snapshot;
        }
    }

    private void PublishRuntimeFrame()
    {
        if (_runtimePublisher is null && _fingerTestPublisher is null)
        {
            return;
        }

        var leftCache = SnapshotRuntimeCache("left");
        var rightCache = SnapshotRuntimeCache("right");
        var leftPresent = ResolveRuntimeHandPresent("left", leftCache);
        var rightPresent = ResolveRuntimeHandPresent("right", rightCache);
        var leftStale = ResolveRuntimeHandStale(leftCache);
        var rightStale = ResolveRuntimeHandStale(rightCache);
        var leftBends = BuildHandBends("left", leftCache);
        var rightBends = BuildHandBends("right", rightCache);
        var leftJoystickState = BuildJoystickRuntimeState("left", leftCache);
        var rightJoystickState = BuildJoystickRuntimeState("right", rightCache);
        var leftGestureButtons = EvaluateGestureButtons("left", leftPresent, leftStale, leftBends);
        var rightGestureButtons = EvaluateGestureButtons("right", rightPresent, rightStale, rightBends);

        _runtimePublisher?.UpdatePoseOffset("left", _config.PoseOffsets.Left);
        _runtimePublisher?.UpdatePoseOffset("right", _config.PoseOffsets.Right);
        _fingerTestPublisher?.UpdatePoseOffset("left", _config.PoseOffsets.Left);
        _fingerTestPublisher?.UpdatePoseOffset("right", _config.PoseOffsets.Right);

        _runtimePublisher?.UpdateHand("left", leftPresent, leftStale, leftBends);
        _runtimePublisher?.UpdateHand("right", rightPresent, rightStale, rightBends);
        _runtimePublisher?.UpdateJoystick("left", leftJoystickState.Available, leftJoystickState.AxisX, leftJoystickState.AxisY, leftJoystickState.Pressed, leftJoystickState.Touched, leftJoystickState.AxisMode, leftJoystickState.ClickAction);
        _runtimePublisher?.UpdateJoystick("right", rightJoystickState.Available, rightJoystickState.AxisX, rightJoystickState.AxisY, rightJoystickState.Pressed, rightJoystickState.Touched, rightJoystickState.AxisMode, rightJoystickState.ClickAction);
        _runtimePublisher?.UpdateVirtualButtons("left", leftGestureButtons.TriggerClick, leftGestureButtons.GripClick, leftGestureButtons.PrimaryClick, leftGestureButtons.SecondaryClick, leftGestureButtons.SystemClick);
        _runtimePublisher?.UpdateVirtualButtons("right", rightGestureButtons.TriggerClick, rightGestureButtons.GripClick, rightGestureButtons.PrimaryClick, rightGestureButtons.SecondaryClick, rightGestureButtons.SystemClick);
        _fingerTestPublisher?.UpdateHand("left", leftPresent, leftStale, leftBends);
        _fingerTestPublisher?.UpdateHand("right", rightPresent, rightStale, rightBends);
        _fingerTestPublisher?.UpdateJoystick("left", leftJoystickState.Available, leftJoystickState.AxisX, leftJoystickState.AxisY, leftJoystickState.Pressed, leftJoystickState.Touched, leftJoystickState.AxisMode, leftJoystickState.ClickAction);
        _fingerTestPublisher?.UpdateJoystick("right", rightJoystickState.Available, rightJoystickState.AxisX, rightJoystickState.AxisY, rightJoystickState.Pressed, rightJoystickState.Touched, rightJoystickState.AxisMode, rightJoystickState.ClickAction);
        _fingerTestPublisher?.UpdateVirtualButtons("left", leftGestureButtons.TriggerClick, leftGestureButtons.GripClick, leftGestureButtons.PrimaryClick, leftGestureButtons.SecondaryClick, leftGestureButtons.SystemClick);
        _fingerTestPublisher?.UpdateVirtualButtons("right", rightGestureButtons.TriggerClick, rightGestureButtons.GripClick, rightGestureButtons.PrimaryClick, rightGestureButtons.SecondaryClick, rightGestureButtons.SystemClick);
    }

    private bool ResolveRuntimeHandPresent(string side, RuntimeSideCache cache)
    {
        if (!cache.TrackingEnabled)
        {
            if (cache.TrackingDisabledSinceUtc != DateTime.MinValue
                && (DateTime.UtcNow - cache.TrackingDisabledSinceUtc) >= TrackingDisableGrace)
            {
                return false;
            }
        }

        if (cache.LastSeenUtc != DateTime.MinValue
            && (DateTime.UtcNow - cache.LastSeenUtc) <= RuntimePresentFreshFor)
        {
            return true;
        }

        for (var i = 0; i < cache.PacketActive.Length && i < cache.Raws.Length; i++)
        {
            if (cache.PacketActive[i] && cache.Raws[i] >= 0)
            {
                return true;
            }
        }

        return _config.Devices.Any(item =>
            (string.Equals(item.SavedRole, side, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.PreferredRole, side, StringComparison.OrdinalIgnoreCase))
            && IsUdpActive(item.StaIp));
    }

    private static bool ResolveRuntimeHandStale(RuntimeSideCache cache)
    {
        return cache.LastSeenUtc == DateTime.MinValue
            || (DateTime.UtcNow - cache.LastSeenUtc) > RuntimeStaleAfter;
    }

    private double ComputeFilteredBend(string side, string fingerName, RuntimeSideCache cache, int index, int raw, bool active)
    {
        if (!active || raw < 0)
        {
            cache.FilterInitialized[index] = false;
            return 0.0;
        }

        var measurement = ComputeMeasuredBend(side, fingerName, raw, active);
        if (!cache.FilterInitialized[index])
        {
            cache.FilteredBends[index] = measurement;
            cache.FilterInitialized[index] = true;
            return measurement;
        }

        var tuning = _config.AlgorithmTuning;
        var fingerConfig = GetFingerConfig(side, fingerName);
        var alpha = fingerConfig?.SmoothingAlpha ?? tuning.SmoothingAlpha;
        var antiShakeLevel = (int)Math.Round(tuning.AntiShakeLevel);
        var deadband = antiShakeLevel switch
        {
            <= 1 => 0.0,
            2 => 0.01,
            _ => 0.02
        };

        var previous = cache.FilteredBends[index];
        var delta = measurement - previous;
        if (Math.Abs(delta) <= deadband)
        {
            return previous;
        }

        var adaptiveGain = Math.Clamp(
            alpha + (tuning.KalmanQ * 4.0) + (Math.Abs(delta) * (0.35 + (tuning.KalmanQ * 6.0))),
            0.02,
            1.0);
        return previous + ((measurement - previous) * adaptiveGain);
    }

    private double ComputeMeasuredBend(string side, string fingerName, int raw, bool active)
    {
        if (!active || raw < 0)
        {
            return 0.0;
        }

        var fingerConfig = GetFingerConfig(side, fingerName);
        var normalized = Math.Clamp(raw / (double)Math.Max(1, _config.AdcMax), 0.0, 1.0);

        if (fingerConfig is not null
            && fingerConfig.CalibratedOpenRaw >= 0
            && fingerConfig.CalibratedClosedRaw >= 0
            && fingerConfig.CalibratedOpenRaw != fingerConfig.CalibratedClosedRaw)
        {
            var denom = fingerConfig.CalibratedClosedRaw - fingerConfig.CalibratedOpenRaw;
            if (Math.Abs(denom) > 0.5)
            {
                normalized = Math.Clamp((raw - fingerConfig.CalibratedOpenRaw) / (double)denom, 0.0, 1.0);
            }
        }

        var tuning = _config.AlgorithmTuning;
        var deadzone = fingerConfig?.Deadzone ?? (tuning.DeadzonePercent / 100.0);
        normalized = ApplyDeadzone(normalized, deadzone);
        normalized = ApplySensitivityCurve(normalized, tuning.SensitivityLevel);
        return Math.Clamp(normalized, 0.0, 1.0);
    }

    private void RefreshDisplayedBends(bool resetFilters = false)
    {
        if (resetFilters)
        {
            ResetRuntimeFilters();
        }

        ApplyFingerConfigState("left", _vm.LeftFingers);
        ApplyFingerConfigState("right", _vm.RightFingers);
        RecomputeDisplayedBendsForSide("left", _vm.LeftFingers);
        RecomputeDisplayedBendsForSide("right", _vm.RightFingers);
    }

    private void RecomputeDisplayedBendsForSide(string side, ObservableCollection<FingerRuntimeVm> fingers)
    {
        var cache = SnapshotRuntimeCache(side);
        for (var i = 0; i < fingers.Count; i++)
        {
            var finger = fingers[i];
            finger.Bend = i < cache.FilteredBends.Length && cache.FilterInitialized[i]
                ? cache.FilteredBends[i]
                : ComputeMeasuredBend(side, finger.Name, finger.Raw, finger.PacketActive);
        }
    }

    private void ResetRuntimeFilters()
    {
        lock (_runtimeCacheLock)
        {
            foreach (var entry in _runtimeCacheBySide)
            {
                Array.Clear(entry.Value.FilterInitialized, 0, entry.Value.FilterInitialized.Length);
                for (var i = 0; i < FingerNames.Length; i++)
                {
                    entry.Value.FilteredBends[i] = ComputeFilteredBend(
                        entry.Key,
                        FingerNames[i],
                        entry.Value,
                        i,
                        entry.Value.Raws[i],
                        entry.Value.PacketActive[i]);
                }
            }
        }
    }

    private FingerConfig? GetFingerConfig(string side, string fingerName)
    {
        var hand = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? _config.Hands.Left : _config.Hands.Right;
        return hand.Fingers.TryGetValue(fingerName, out var fingerConfig) ? fingerConfig : null;
    }

    private static double ApplyDeadzone(double value, double deadzone)
    {
        deadzone = Math.Clamp(deadzone, 0.0, 0.95);
        if (value <= deadzone)
        {
            return 0.0;
        }

        return Math.Clamp((value - deadzone) / (1.0 - deadzone), 0.0, 1.0);
    }

    private static double ApplySensitivityCurve(double value, double sensitivityLevel)
    {
        var exponent = Math.Round(sensitivityLevel) switch
        {
            <= 1 => 1.25,
            >= 3 => 0.78,
            _ => 1.0
        };
        return Math.Pow(Math.Clamp(value, 0.0, 1.0), exponent);
    }

    private (bool Available, double AxisX, double AxisY, bool Pressed, bool Touched, int AxisMode, int ClickAction) BuildJoystickRuntimeState(string side, RuntimeSideCache cache)
    {
        var settings = GetJoystickSettings(side);
        var available = cache.JoystickRawX >= 0 || cache.JoystickRawY >= 0 || cache.JoystickPressed.HasValue;
        var rawAxisX = NormalizeJoystickAxis(side, true, cache.JoystickRawX);
        var rawAxisY = NormalizeJoystickAxis(side, false, cache.JoystickRawY);
        var orientedAxis = JoystickOrientationCatalog.Apply(settings.Orientation, rawAxisX, rawAxisY);
        var axisX = Math.Clamp(orientedAxis.X, -1.0, 1.0);
        var axisY = Math.Clamp(orientedAxis.Y, -1.0, 1.0);
        var pressed = cache.JoystickPressed == true;
        var touched = pressed || Math.Abs(axisX) >= 0.08 || Math.Abs(axisY) >= 0.08;
        return (
            available,
            axisX,
            axisY,
            pressed,
            touched,
            RuntimeJoystickActionCatalog.AxisModeToId(settings.SteamVrAxisMode),
            RuntimeJoystickActionCatalog.ClickActionToId(settings.SteamVrClickAction));
    }

    private double NormalizeJoystickAxis(string side, bool isXAxis, int raw)
    {
        if (raw < 0)
        {
            return 0.0;
        }

        var settings = GetJoystickSettings(side);
        var max = Math.Max(1, _config.AdcMax);
        var center = isXAxis ? settings.CenterRawX : settings.CenterRawY;
        if (center < 0 || center > max)
        {
            center = max / 2;
        }

        double normalized;
        if (raw >= center)
        {
            normalized = (raw - center) / (double)Math.Max(1, max - center);
        }
        else
        {
            normalized = (raw - center) / (double)Math.Max(1, center);
        }

        normalized = Math.Clamp(normalized, -1.0, 1.0);
        var deadzone = Math.Clamp(settings.DeadzonePercent / 100.0, 0.0, 0.4);
        if (Math.Abs(normalized) <= deadzone)
        {
            return 0.0;
        }

        var signed = Math.Sign(normalized);
        var scaled = (Math.Abs(normalized) - deadzone) / Math.Max(0.01, 1.0 - deadzone);
        return Math.Clamp(signed * scaled, -1.0, 1.0);
    }

    private static void ResetObservedRanges(ObservableCollection<FingerRuntimeVm> fingers)
    {
        foreach (var finger in fingers)
        {
            finger.MinRaw = finger.Raw >= 0 ? finger.Raw : -1;
            finger.MaxRaw = finger.Raw >= 0 ? finger.Raw : -1;
        }
    }

    private static string? FindBridgeExecutable()
    {
        foreach (var path in new[]
        {
            Path.Combine(FirmwareTools.ResolveRepositoryRoot(), "build", "Debug", "openfinger_controller_bridge.exe"),
            Path.Combine(FirmwareTools.ResolveRepositoryRoot(), "build", "Release", "openfinger_controller_bridge.exe")
        })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool TrySetSteamVrForwardControllerInputs(bool enabled, out string message)
    {
        message = string.Empty;
        var settingsPath = ResolveSteamVrSettingsPath();
        if (!File.Exists(settingsPath))
        {
            message = "没有找到 steamvr.vrsettings。";
            return false;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
            if (root is null)
            {
                message = "steamvr.vrsettings 解析失败。";
                return false;
            }

            if (root["driver_openfinger"] is not JsonObject section)
            {
                section = new JsonObject();
                root["driver_openfinger"] = section;
            }

            section["forward_controller_inputs"] = enabled;
            File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            message = enabled ? "已开启控制器按键转发。" : "已关闭控制器按键转发。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"更新 SteamVR 设置失败: {ex.Message}";
            return false;
        }
    }

    public async Task UpdateSteamVrDriverAsync()
    {
        var snapshot = ReadSteamVrDriverSnapshot();
        if (!snapshot.RuntimeDetected || !snapshot.ToolAvailable)
        {
            SetPinnedStatusLine("没有找到 SteamVR 运行时或 vrpathreg.exe，暂时无法安装驱动。", 6);
            return;
        }

        if (!snapshot.FilesReady)
        {
            SetPinnedStatusLine("当前发行版里的 SteamVR 驱动文件不完整，先确认 driver_openfinger.dll 已随发行版一起提供。", 6);
            return;
        }

        try
        {
            foreach (var installedPath in snapshot.RegisteredPaths)
            {
                await RunVrPathRegAsync(snapshot.ToolPath, "removedriver", installedPath);
            }

            await RunVrPathRegAsync(snapshot.ToolPath, "adddriver", snapshot.ExpectedDriverPath);
            _steamVrDriverSnapshot = ReadSteamVrDriverSnapshot();
            RefreshUiFromState();

            var restartHint = _vm.SteamVrStatus.Contains("运行中", StringComparison.OrdinalIgnoreCase)
                ? "请重启 SteamVR 让新驱动生效。"
                : "下次启动 SteamVR 时会直接使用这版驱动。";
            SetPinnedStatusLine($"已完成 SteamVR 驱动更新。{restartHint}", 6);
            MaybeShowTrayNotification("SteamVR 驱动已更新", restartHint, ClientNotificationKind.Driver, Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"更新 SteamVR 驱动失败: {ex.Message}", 8);
            MaybeShowTrayNotification("SteamVR 驱动更新失败", ex.Message, ClientNotificationKind.Driver, Forms.ToolTipIcon.Error);
        }
    }

    public async Task RemoveSteamVrDriverAsync()
    {
        var snapshot = ReadSteamVrDriverSnapshot();
        if (!snapshot.RuntimeDetected || !snapshot.ToolAvailable)
        {
            SetPinnedStatusLine("没有找到 SteamVR 运行时或 vrpathreg.exe，暂时无法移除驱动。", 6);
            return;
        }

        if (snapshot.RegisteredPaths.Count == 0)
        {
            SetPinnedStatusLine("当前没有检测到已注册的 OpenFinger SteamVR 驱动。", 4);
            return;
        }

        try
        {
            foreach (var installedPath in snapshot.RegisteredPaths)
            {
                await RunVrPathRegAsync(snapshot.ToolPath, "removedriver", installedPath);
            }

            _steamVrDriverSnapshot = ReadSteamVrDriverSnapshot();
            RefreshUiFromState();

            var restartHint = _vm.SteamVrStatus.Contains("运行中", StringComparison.OrdinalIgnoreCase)
                ? "请重启 SteamVR 让驱动移除生效。"
                : "下次启动 SteamVR 时将不会再加载 OpenFinger 驱动。";
            SetPinnedStatusLine($"已移除 OpenFinger SteamVR 驱动。{restartHint}", 6);
            MaybeShowTrayNotification("SteamVR 驱动已移除", restartHint, ClientNotificationKind.Driver, Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            SetPinnedStatusLine($"移除 SteamVR 驱动失败: {ex.Message}", 8);
            MaybeShowTrayNotification("SteamVR 驱动移除失败", ex.Message, ClientNotificationKind.Driver, Forms.ToolTipIcon.Error);
        }
    }

    private static async Task RunVrPathRegAsync(string vrPathRegExe, string command, string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = vrPathRegExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 vrpathreg。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? $"vrpathreg {command} 失败。" : output);
        }
    }

    private static bool IsSteamVrDriverReady(SteamVrDriverSnapshot snapshot)
    {
        return snapshot.RuntimeDetected
            && snapshot.ToolAvailable
            && snapshot.FilesReady
            && snapshot.Registered
            && snapshot.IsLatest
            && !snapshot.HasMultipleRegistrations;
    }

    private static string BuildSteamVrDriverDetail(SteamVrDriverSnapshot snapshot)
    {
        if (!snapshot.RuntimeDetected)
        {
            return "没有检测到 SteamVR 运行时目录，先确认 SteamVR 已安装。";
        }

        if (!snapshot.ToolAvailable)
        {
            return "找到了 SteamVR 运行时，但没有找到 vrpathreg.exe，暂时无法自动安装驱动。";
        }

        if (!snapshot.FilesReady)
        {
            return $"当前发行版驱动目录不完整：{snapshot.ExpectedDriverPath}";
        }

        if (!snapshot.Registered)
        {
            return $"当前还没有把 OpenFinger 驱动注册到 SteamVR。可安装目录：{snapshot.ExpectedDriverPath}。当前发行版构建：{snapshot.CurrentBuildText}";
        }

        if (snapshot.HasMultipleRegistrations)
        {
            return $"检测到多个 OpenFinger 驱动注册，建议点一次“更新驱动”清理旧目录。当前发现 {snapshot.RegisteredPaths.Count} 个注册。";
        }

        if (!snapshot.IsLatest)
        {
            return $"已注册，但不是当前发行版这版驱动。当前注册：{snapshot.RegisteredDriverPath}。当前发行版：{snapshot.CurrentBuildText}，已注册版本：{snapshot.InstalledBuildText}";
        }

        return $"已安装，且与当前发行版一致。当前目录：{snapshot.RegisteredDriverPath}。驱动构建：{snapshot.InstalledBuildText}";
    }

    private static SteamVrDriverSnapshot ReadSteamVrDriverSnapshot()
    {
        var runtimePath = ResolveSteamVrRuntimePath();
        var configPath = ResolveSteamVrConfigDirectory();
        var toolPath = ResolveVrPathRegExecutable();
        var repositoryRoot = FirmwareTools.ResolveRepositoryRoot();
        var sourceDriverPath = NormalizePathValue(Path.Combine(repositoryRoot, "src", "drivers", "openfinger"));
        var packageDriverPath = NormalizePathValue(Path.Combine(repositoryRoot, "drivers", "openfinger"));
        var expectedDriverPath = IsDriverPackageReady(sourceDriverPath) ? sourceDriverPath : packageDriverPath;
        var filesReady = IsDriverPackageReady(expectedDriverPath);
        var currentBuildText = GetDriverBuildLabel(expectedDriverPath);

        var registeredPaths = ReadRegisteredExternalDrivers()
            .Where(IsOpenFingerDriverPath)
            .Select(NormalizePathValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var registeredDriverPath = registeredPaths.FirstOrDefault() ?? string.Empty;
        var installedBuildText = string.IsNullOrWhiteSpace(registeredDriverPath) ? "--" : GetDriverBuildLabel(registeredDriverPath);
        var currentSignature = filesReady ? BuildDriverSignature(expectedDriverPath) : string.Empty;
        var latest = filesReady
            && registeredPaths.Count == 1
            && registeredPaths.Any(path => string.Equals(BuildDriverSignature(path), currentSignature, StringComparison.OrdinalIgnoreCase));

        return new SteamVrDriverSnapshot
        {
            RuntimeDetected = !string.IsNullOrWhiteSpace(runtimePath),
            ToolAvailable = File.Exists(toolPath),
            FilesReady = filesReady,
            Registered = registeredPaths.Count > 0,
            IsLatest = latest,
            HasMultipleRegistrations = registeredPaths.Count > 1,
            RuntimePath = runtimePath,
            ConfigPath = configPath,
            ToolPath = toolPath,
            ExpectedDriverPath = expectedDriverPath,
            RegisteredDriverPath = registeredDriverPath,
            RegisteredPaths = registeredPaths,
            CurrentBuildText = currentBuildText,
            InstalledBuildText = installedBuildText
        };
    }

    private static string ResolveSteamVrRuntimePath()
    {
        var openVrPaths = TryReadOpenVrPaths();
        return openVrPaths.RuntimePaths.FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveSteamVrConfigDirectory()
    {
        var openVrPaths = TryReadOpenVrPaths();
        return openVrPaths.ConfigPaths.FirstOrDefault() ?? @"A:\Steam\config";
    }

    private static string ResolveSteamVrSettingsPath()
    {
        return Path.Combine(ResolveSteamVrConfigDirectory(), "steamvr.vrsettings");
    }

    private static string ResolveVrPathRegExecutable()
    {
        var runtimePath = ResolveSteamVrRuntimePath();
        foreach (var candidate in new[]
                 {
                     Path.Combine(runtimePath, "bin", "win64", "vrpathreg.exe"),
                     Path.Combine(runtimePath, "bin", "win32", "vrpathreg.exe"),
                     @"A:\Steam\steamapps\common\SteamVR\bin\win64\vrpathreg.exe",
                     @"A:\Steam\steamapps\common\SteamVR\bin\win32\vrpathreg.exe"
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static (IReadOnlyList<string> RuntimePaths, IReadOnlyList<string> ConfigPaths, IReadOnlyList<string> ExternalDrivers) TryReadOpenVrPaths()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "openvr",
                "openvrpaths.vrpath");
            if (!File.Exists(path))
            {
                return (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            }

            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (root is null)
            {
                return (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            }

            return (
                ReadJsonStringArray(root["runtime"]),
                ReadJsonStringArray(root["config"]),
                ReadJsonStringArray(root["external_drivers"]));
        }
        catch
        {
            return (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }
    }

    private static IReadOnlyList<string> ReadRegisteredExternalDrivers()
    {
        return TryReadOpenVrPaths().ExternalDrivers;
    }

    private static IReadOnlyList<string> ReadJsonStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static bool IsOpenFingerDriverPath(string? path)
    {
        var normalized = NormalizePathValue(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var manifestPath = Path.Combine(normalized, "driver.vrdrivermanifest");
        if (!File.Exists(manifestPath))
        {
            return normalized.EndsWith($"{Path.DirectorySeparatorChar}openfinger", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            var name = root?["name"]?.GetValue<string>() ?? string.Empty;
            return string.Equals(name, "openfinger", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return normalized.EndsWith($"{Path.DirectorySeparatorChar}openfinger", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsDriverPackageReady(string driverRoot)
    {
        return File.Exists(Path.Combine(driverRoot, "driver.vrdrivermanifest"))
            && File.Exists(Path.Combine(driverRoot, "resources", "input", "openfinger_profile.json"))
            && File.Exists(Path.Combine(driverRoot, "bin", "win64", "driver_openfinger.dll"));
    }

    private static string BuildDriverSignature(string driverRoot)
    {
        var files = new[]
        {
            Path.Combine(driverRoot, "driver.vrdrivermanifest"),
            Path.Combine(driverRoot, "resources", "input", "openfinger_profile.json"),
            Path.Combine(driverRoot, "bin", "win64", "driver_openfinger.dll")
        };

        if (files.Any(file => !File.Exists(file)))
        {
            return string.Empty;
        }

        using var sha = SHA256.Create();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    private static string GetDriverBuildLabel(string driverRoot)
    {
        var dllPath = Path.Combine(driverRoot, "bin", "win64", "driver_openfinger.dll");
        if (!File.Exists(dllPath))
        {
            return "--";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            if (!string.IsNullOrWhiteSpace(info.FileVersion))
            {
                return info.FileVersion!;
            }
        }
        catch
        {
        }

        return File.GetLastWriteTime(dllPath).ToString("yyyy-MM-dd HH:mm");
    }

    private static string NormalizePathValue(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private bool StopServiceIfRunning(out string message)
    {
        message = string.Empty;
        var processes = Process.GetProcessesByName("openfinger_service");
        if (processes.Length == 0)
        {
            return true;
        }

        foreach (var process in processes)
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(2000);
            }
            catch
            {
            }
        }

        message = "已停用 legacy service，手指运行时由 Control 直接发送到 SteamVR。";
        return true;
    }

    private bool EnsureBridgeRunning(out string message)
    {
        message = string.Empty;
        if (Process.GetProcessesByName("openfinger_controller_bridge").Any())
        {
            return true;
        }

        var exe = FindBridgeExecutable();
        if (string.IsNullOrWhiteSpace(exe))
        {
            message = "没有找到 openfinger_controller_bridge.exe。";
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true
        });
        message = "已启动 bridge。";
        return true;
    }

    private bool IsUdpActive(string? ip)
    {
        return !string.IsNullOrWhiteSpace(ip)
            && _udpSeenByIp.TryGetValue(ip, out var lastSeen)
            && (DateTime.UtcNow - lastSeen) <= TimeSpan.FromSeconds(3);
    }

    private async Task<bool> IsWifiReachableAsync(string? ip, DateTime nowUtc)
    {
        var normalized = NormalizeStaIp(ip);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (_wifiReachableByIp.TryGetValue(normalized, out var cached)
            && _wifiReachableSeenByIp.TryGetValue(normalized, out var seenAtUtc)
            && (nowUtc - seenAtUtc) <= WifiReachabilityCacheTtl)
        {
            return cached;
        }

        var reachable = false;
        try
        {
            using var ping = new Ping();
            reachable = (await ping.SendPingAsync(normalized, 250)).Status == IPStatus.Success;
        }
        catch
        {
        }

        if (!reachable)
        {
            reachable = TryResolveArp(normalized);
        }

        _wifiReachableByIp[normalized] = reachable;
        _wifiReachableSeenByIp[normalized] = DateTime.UtcNow;
        return reachable;
    }

    private static bool TryResolveArp(string ip)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        try
        {
            var bytes = address.GetAddressBytes();
            var destination = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
            var macAddr = new byte[6];
            var length = macAddr.Length;
            return SendARP(destination, 0, macAddr, ref length) == 0 && length > 0 && macAddr.Any(value => value != 0);
        }
        catch
        {
            return false;
        }
    }

    private void CacheHeartbeat(string sourceIp, SerialStatusDto status, DateTime seenUtc)
    {
        var normalizedSourceIp = NormalizeStaIp(sourceIp);
        var identityKey = BuildHeartbeatIdentityKey(status.Mac, status.Device, normalizedSourceIp);
        _heartbeatByDeviceKey[identityKey] = new DeviceHeartbeatSnapshot
        {
            SourceIp = normalizedSourceIp,
            Status = status,
            SeenUtc = seenUtc
        };

        var effectiveStaIp = PreferStaIp(status.StaIp, normalizedSourceIp);
        if (!string.IsNullOrWhiteSpace(effectiveStaIp))
        {
            _wifiReachableByIp[effectiveStaIp] = true;
            _wifiReachableSeenByIp[effectiveStaIp] = seenUtc;
        }
    }

    private void PruneHeartbeatCache(DateTime nowUtc)
    {
        foreach (var key in _heartbeatByDeviceKey.Keys.Where(key => (nowUtc - _heartbeatByDeviceKey[key].SeenUtc) > DeviceHeartbeatFreshFor).ToList())
        {
            _heartbeatByDeviceKey.Remove(key);
        }
    }

    private IReadOnlyList<DeviceHeartbeatSnapshot> GetRecentHeartbeats(DateTime nowUtc)
    {
        return _heartbeatByDeviceKey.Values
            .Where(snapshot => (nowUtc - snapshot.SeenUtc) <= DeviceHeartbeatFreshFor)
            .OrderByDescending(snapshot => snapshot.SeenUtc)
            .ToList();
    }

    private SerialStatusDto? GetRecentHeartbeatStatus(string? mac, string? deviceName, string? staIp, DateTime nowUtc)
    {
        return FindRecentHeartbeat(mac, deviceName, staIp, nowUtc)?.Status;
    }

    private DeviceHeartbeatSnapshot? FindRecentHeartbeat(string? mac, string? deviceName, string? staIp, DateTime nowUtc)
    {
        var normalizedIp = NormalizeStaIp(staIp);
        return _heartbeatByDeviceKey.Values
            .Where(snapshot => (nowUtc - snapshot.SeenUtc) <= DeviceHeartbeatFreshFor)
            .OrderByDescending(snapshot => snapshot.SeenUtc)
            .FirstOrDefault(snapshot =>
                (!string.IsNullOrWhiteSpace(mac) && string.Equals(snapshot.Status.Mac, mac, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(deviceName) && string.Equals(snapshot.Status.Device, deviceName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(normalizedIp) && (
                    string.Equals(snapshot.SourceIp, normalizedIp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(NormalizeStaIp(snapshot.Status.StaIp), normalizedIp, StringComparison.OrdinalIgnoreCase))));
    }

    private static string BuildHeartbeatIdentityKey(string? mac, string? deviceName, string? sourceIp)
    {
        if (!string.IsNullOrWhiteSpace(mac))
        {
            return $"mac:{mac}";
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            return $"name:{deviceName}";
        }

        return $"ip:{NormalizeStaIp(sourceIp)}";
    }

    private static bool IsSerialStatusStreaming(SerialStatusDto status)
    {
        return status.AdcStreaming == true
            || string.Equals(status.State, "connected_streaming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "streaming", StringComparison.OrdinalIgnoreCase);
    }

    private void PruneSerialStatusCache(DateTime nowUtc)
    {
        foreach (var port in _serialStatusSeenByPort.Keys.ToList())
        {
            if ((nowUtc - _serialStatusSeenByPort[port]) <= SerialStatusCacheTtl)
            {
                continue;
            }

            _serialStatusSeenByPort.Remove(port);
            _serialStatusByPort.Remove(port);
        }
    }

    private SerialStatusDto? GetSavedDeviceCachedStatus(KnownDevice saved, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(saved.SerialPort))
        {
            return null;
        }

        var status = GetCachedSerialStatus(saved.SerialPort, nowUtc);
        return status is not null && SerialStatusMatchesKnownDevice(saved, status)
            ? status
            : null;
    }

    private DateTime ResolveSavedDeviceLastSeenUtc(string? serialPort, string? staIp, string? mac, string? deviceName, string? role, DateTime nowUtc)
    {
        var normalizedStaIp = NormalizeStaIp(staIp);
        var heartbeat = FindRecentHeartbeat(mac, deviceName, normalizedStaIp, nowUtc);
        if (heartbeat is not null)
        {
            return heartbeat.SeenUtc;
        }

        var normalizedRole = NormalizeRoleForUi(role);
        if (CanUseRoleRuntimeFallback(normalizedRole)
            && _runtimeSeenBySide.TryGetValue(normalizedRole, out var runtimeSeenAtUtc)
            && (nowUtc - runtimeSeenAtUtc) <= RuntimePresentFreshFor)
        {
            return runtimeSeenAtUtc;
        }

        if (!string.IsNullOrWhiteSpace(normalizedStaIp)
            && _udpSeenByIp.TryGetValue(normalizedStaIp, out var udpSeenAtUtc)
            && (nowUtc - udpSeenAtUtc) <= RuntimePresentFreshFor)
        {
            return udpSeenAtUtc;
        }

        if (!string.IsNullOrWhiteSpace(normalizedStaIp)
            && _wifiReachableByIp.TryGetValue(normalizedStaIp, out var reachable)
            && reachable
            && _wifiReachableSeenByIp.TryGetValue(normalizedStaIp, out var wifiSeenAtUtc)
            && (nowUtc - wifiSeenAtUtc) <= WifiReachabilityCacheTtl)
        {
            return wifiSeenAtUtc;
        }

        if (!string.IsNullOrWhiteSpace(serialPort)
            && _serialStatusSeenByPort.TryGetValue(serialPort, out var serialSeenAtUtc)
            && (nowUtc - serialSeenAtUtc) <= SerialStatusCacheTtl
            && GetSavedDeviceCachedStatus(new KnownDevice
            {
                Name = deviceName ?? string.Empty,
                Mac = mac ?? string.Empty,
                SerialPort = serialPort,
                StaIp = normalizedStaIp
            }, nowUtc) is not null)
        {
            return serialSeenAtUtc;
        }

        return nowUtc;
    }

    private bool HasRecentRuntimeData(DateTime nowUtc)
    {
        return _udpSeenByIp.Values.Any(lastSeen => (nowUtc - lastSeen) <= RecentRuntimeForSerialSkip);
    }

    private bool TryGetRecentRuntimeTrackingState(string? side, DateTime nowUtc, out bool trackingEnabled)
    {
        trackingEnabled = true;
        if (string.IsNullOrWhiteSpace(side) || string.Equals(side, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_trackingEnabledBySide.TryGetValue(side, out trackingEnabled))
        {
            return false;
        }

        return _runtimeSeenBySide.TryGetValue(side, out var lastSeen)
            && (nowUtc - lastSeen) <= RuntimePresentFreshFor;
    }

    private bool TryGetRecentRuntimeJoystickState(string? side, DateTime nowUtc, out bool hasLiveInput)
    {
        hasLiveInput = false;
        if (string.IsNullOrWhiteSpace(side) || string.Equals(side, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_runtimeSeenBySide.TryGetValue(side, out var lastSeen) || (nowUtc - lastSeen) > RuntimePresentFreshFor)
        {
            return false;
        }

        var cache = SnapshotRuntimeCache(side);
        hasLiveInput = cache.JoystickRawX >= 0 || cache.JoystickRawY >= 0 || cache.JoystickPressed.HasValue;
        return true;
    }

    private bool CanUseRoleRuntimeFallback(string? role)
    {
        var normalizedRole = NormalizeRoleForUi(role);
        return _config.Devices.Count(item =>
                   string.Equals(NormalizeRoleForUi(item.SavedRole, item.PreferredRole), normalizedRole, StringComparison.OrdinalIgnoreCase))
               <= 1;
    }

    private SerialStatusDto? GetCachedSerialStatus(string port, DateTime nowUtc)
    {
        if (!_serialStatusByPort.TryGetValue(port, out var status))
        {
            return null;
        }

        return _serialStatusSeenByPort.TryGetValue(port, out var seenAtUtc) && (nowUtc - seenAtUtc) <= SerialStatusCacheTtl
            ? status
            : null;
    }

    private void CacheSerialStatus(string port, SerialStatusDto status, DateTime seenAtUtc)
    {
        _serialStatusByPort[port] = status;
        _serialStatusSeenByPort[port] = seenAtUtc;
    }

    private static bool ShouldLogSerialFailure(string port, Exception ex)
    {
        if (string.Equals(port, "COM1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var message = ex.Message;
        return !message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("Could not find file", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("The device attached to the system is not functioning", StringComparison.OrdinalIgnoreCase);
    }

    private static List<DiscoveryDevice> MergeDevices(IEnumerable<DiscoveryDevice> devices)
    {
        var merged = new Dictionary<string, DiscoveryDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var key = BuildDeviceKey(device);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = device;
                continue;
            }

            existing.DisplayName = PreferText(existing.DisplayName, device.DisplayName);
            existing.SerialPort = PreferText(existing.SerialPort, device.SerialPort);
            existing.Mac = PreferText(existing.Mac, device.Mac);
            existing.StaIp = PreferStaIp(device.StaIp, existing.StaIp);
            existing.Role = PreferRole(existing.Role, device.Role);
            existing.State = device.WifiActive ? "wifi_active" : device.WifiConnected ? "wifi_connected" : PreferText(existing.State, device.State);
            existing.Message = PreferDetail(existing, device);
            existing.UdpPort = device.UdpPort != 0 ? device.UdpPort : existing.UdpPort;
            existing.AdcMask = device.AdcMask != 0 ? device.AdcMask : existing.AdcMask;
            existing.BoardTarget = !string.IsNullOrWhiteSpace(device.BoardTarget) ? device.BoardTarget : existing.BoardTarget;
            existing.FirmwareVersion = !string.IsNullOrWhiteSpace(device.FirmwareVersion) ? device.FirmwareVersion : existing.FirmwareVersion;
            existing.ReportHz = device.ReportHz > 0 ? device.ReportHz : existing.ReportHz;
            existing.WifiConnected |= device.WifiConnected;
            existing.WifiActive |= device.WifiActive;
            existing.UsbConnected |= device.UsbConnected;
            existing.TrackingEnabled &= device.TrackingEnabled;
            existing.Online = existing.WifiConnected || existing.WifiActive || existing.UsbConnected;
            existing.LastSeenUtc = existing.LastSeenUtc > device.LastSeenUtc ? existing.LastSeenUtc : device.LastSeenUtc;
        }

        foreach (var item in merged.Values)
        {
            item.Online = item.WifiConnected || item.WifiActive || item.UsbConnected;
        }

        return merged.Values.ToList();
    }

    private static string BuildDeviceKey(DiscoveryDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.Mac))
        {
            return $"mac:{device.Mac}";
        }

        if (!string.IsNullOrWhiteSpace(device.StaIp))
        {
            return $"ip:{device.StaIp}";
        }

        if (!string.IsNullOrWhiteSpace(device.DisplayName))
        {
            return $"name:{device.DisplayName}";
        }

        if (!string.IsNullOrWhiteSpace(device.SerialPort))
        {
            return $"usb:{device.SerialPort}";
        }

        return $"id:{device.Id}";
    }

    private static string PreferText(string left, string right) => string.IsNullOrWhiteSpace(left) ? right : left;

    private static string PreferRole(string left, string right)
    {
        return string.Equals(left, "unknown", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(right) ? right : left;
    }

    private static string NormalizeRoleForUi(string? primary, string? fallback = null)
    {
        if (string.Equals(primary, "left", StringComparison.OrdinalIgnoreCase))
        {
            return "left";
        }

        if (string.Equals(primary, "right", StringComparison.OrdinalIgnoreCase))
        {
            return "right";
        }

        if (string.Equals(fallback, "left", StringComparison.OrdinalIgnoreCase))
        {
            return "left";
        }

        if (string.Equals(fallback, "right", StringComparison.OrdinalIgnoreCase))
        {
            return "right";
        }

        return "right";
    }

    private DeviceVm? ResolveSelectedDeviceAfterRefresh(string? selectedId, DeviceVm? previous)
    {
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var exact = _vm.Devices.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        if (previous is not null)
        {
            var samePhysical = _vm.Devices.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(previous.Mac) && string.Equals(item.Mac, previous.Mac, StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.DisplayName, previous.DisplayName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(previous.SerialPort)
                    && string.Equals(item.SerialPort, previous.SerialPort, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(previous.DisplayName)
                        || string.Equals(item.DisplayName, previous.DisplayName, StringComparison.OrdinalIgnoreCase))));
            if (samePhysical is not null)
            {
                return samePhysical;
            }
        }

        return null;
    }

    private static DeviceVm? CloneDeviceIdentity(DeviceVm? source)
    {
        if (source is null)
        {
            return null;
        }

        return new DeviceVm
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            SerialPort = source.SerialPort,
            Mac = source.Mac,
            StaIp = source.StaIp,
            Role = source.Role
        };
    }

    private static string NormalizeStaIp(string? staIp)
    {
        if (string.IsNullOrWhiteSpace(staIp))
        {
            return string.Empty;
        }

        var text = staIp.Trim();
        if (!IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return string.Empty;
        }

        return string.Equals(text, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
    }

    private static string PreferStaIp(string? primary, string? fallback)
    {
        var preferred = NormalizeStaIp(primary);
        return !string.IsNullOrWhiteSpace(preferred) ? preferred : NormalizeStaIp(fallback);
    }

    private static bool IsSerialStatusWifiConnected(SerialStatusDto status)
    {
        return status.WifiConnected == true
            || string.Equals(status.State, "connected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "connected_streaming", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.State, "streaming", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(status.Message) && status.Message.Contains("wifi connected", StringComparison.OrdinalIgnoreCase));
    }

    private static string PreferDetail(DiscoveryDevice existing, DiscoveryDevice incoming)
    {
        if (incoming.WifiActive || existing.WifiActive)
        {
            return "运行时数据已接入";
        }

        if (incoming.WifiConnected || existing.WifiConnected)
        {
            return "已连接到 Wi-Fi，等待运行时数据";
        }

        if (incoming.UsbConnected || existing.UsbConnected)
        {
            return "USB 已连接，可写入配置";
        }

        return existing.Message;
    }

    private string BuildDeviceMessage(SerialStatusDto status, bool wifiConnected, bool wifiActive)
    {
        if (!wifiActive && wifiConnected && !string.IsNullOrWhiteSpace(status.HostIp) && !OpenFingerWire.IsLocalHostIp(status.HostIp))
        {
            return $"已连上 Wi-Fi，但当前目标主机是 {status.HostIp}，需要重写网络";
        }

        return string.IsNullOrWhiteSpace(status.Message) ? string.Empty : status.Message!;
    }

    private string BuildStatusLabel(DiscoveryDevice device)
    {
        if (device.WifiActive && !device.TrackingEnabled)
        {
            return "在线（追踪关闭）";
        }

        if (device.WifiActive)
        {
            return "在线";
        }

        if (device.WifiConnected)
        {
            return "Wi-Fi 已连接";
        }

        if (device.UsbConnected)
        {
            return "USB 已连接";
        }

        return "离线";
    }

    private string BuildDetailText(DiscoveryDevice device)
    {
        if (device.WifiActive && !device.TrackingEnabled)
        {
            return "运行时数据已接入，追踪开关当前关闭";
        }

        if (device.WifiActive)
        {
            return "运行时数据已接入";
        }

        if (device.WifiConnected)
        {
            return string.IsNullOrWhiteSpace(device.Message) ? "已连接到 Wi-Fi，等待运行时数据" : device.Message;
        }

        if (device.UsbConnected)
        {
            return "USB 已连接，等待设备启动或写入配置";
        }

        return string.IsNullOrWhiteSpace(device.Message) ? "未检测到 Wi-Fi 活动" : device.Message;
    }

    private string FindSavedRole(string? mac, string? name)
    {
        var saved = FindKnownDevice(mac, name);
        return saved?.SavedRole ?? saved?.PreferredRole ?? "unknown";
    }

    private string FindSavedStaIp(string? mac, string? name, string? serialPort)
    {
        var saved = FindKnownDevice(mac, name, serialPort);
        return NormalizeStaIp(saved?.StaIp);
    }

    private KnownDevice? FindKnownDevice(string? mac, string? name, string? serialPort = null, string? staIp = null)
    {
        if (!string.IsNullOrWhiteSpace(mac))
        {
            var byMac = _config.Devices.FirstOrDefault(item => string.Equals(item.Mac, mac, StringComparison.OrdinalIgnoreCase));
            if (byMac is not null)
            {
                return byMac;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = _config.Devices.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        var normalizedStaIp = NormalizeStaIp(staIp);
        if (!string.IsNullOrWhiteSpace(normalizedStaIp))
        {
            var byIp = _config.Devices.FirstOrDefault(item => string.Equals(item.StaIp, normalizedStaIp, StringComparison.OrdinalIgnoreCase));
            if (byIp is not null)
            {
                return byIp;
            }
        }

        if (!string.IsNullOrWhiteSpace(serialPort))
        {
            var serialMatches = _config.Devices
                .Where(item => string.Equals(item.SerialPort, serialPort, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (serialMatches.Count == 1)
            {
                return serialMatches[0];
            }
        }

        return null;
    }

    private static bool SerialStatusMatchesKnownDevice(KnownDevice saved, SerialStatusDto status)
    {
        if (!string.IsNullOrWhiteSpace(saved.Mac) && !string.IsNullOrWhiteSpace(status.Mac))
        {
            return string.Equals(saved.Mac, status.Mac, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(saved.Name) && !string.IsNullOrWhiteSpace(status.Device))
        {
            return string.Equals(saved.Name, status.Device, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(saved.StaIp) && !string.IsNullOrWhiteSpace(status.StaIp))
        {
            return string.Equals(NormalizeStaIp(saved.StaIp), NormalizeStaIp(status.StaIp), StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(saved.Mac)
            && string.IsNullOrWhiteSpace(saved.Name)
            && string.IsNullOrWhiteSpace(saved.StaIp);
    }

    private static bool SamePhysical(DiscoveryDevice current, KnownDevice saved)
    {
        if (!string.IsNullOrWhiteSpace(current.Mac) && string.Equals(current.Mac, saved.Mac, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(current.DisplayName) && string.Equals(current.DisplayName, saved.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(current.SerialPort)
            && string.Equals(current.SerialPort, saved.SerialPort, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(current.DisplayName)
                || string.IsNullOrWhiteSpace(saved.Name)
                || string.Equals(current.DisplayName, saved.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SamePhysical(DiscoveryDevice current, DeviceHeartbeatSnapshot heartbeat)
    {
        var heartbeatIp = PreferStaIp(heartbeat.Status.StaIp, heartbeat.SourceIp);
        return (!string.IsNullOrWhiteSpace(current.Mac) && string.Equals(current.Mac, heartbeat.Status.Mac, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(current.StaIp) && string.Equals(current.StaIp, heartbeatIp, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(heartbeat.Status.Device) && string.Equals(current.DisplayName, heartbeat.Status.Device, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateKnownDeviceFromHeartbeat(string sourceIp, SerialStatusDto status)
    {
        var normalizedStaIp = PreferStaIp(status.StaIp, sourceIp);
        var displayName = string.IsNullOrWhiteSpace(status.Device) ? "OpenFinger" : status.Device!;
        var normalizedTarget = string.IsNullOrWhiteSpace(status.BoardTarget) ? string.Empty : FirmwareTargetCatalog.NormalizeTarget(status.BoardTarget);
        var role = string.IsNullOrWhiteSpace(status.Role) ? "unknown" : status.Role!;
        var changed = false;
        var existing = FindKnownDevice(status.Mac, displayName, null, normalizedStaIp);

        if (existing is null)
        {
            existing = new KnownDevice
            {
                Name = displayName,
                Mac = status.Mac ?? string.Empty,
                SerialPort = string.Empty,
                PreferredRole = role,
                SavedRole = role,
                UdpPort = status.UdpPort,
                AdcMask = status.AdcMask,
                BoardTarget = normalizedTarget,
                FirmwareVersion = status.FirmwareVersion ?? string.Empty,
                ReportHz = status.ReportHz,
                ThumbPin = status.ThumbPin,
                IndexPin = status.IndexPin,
                MiddlePin = status.MiddlePin,
                RingPin = status.RingPin,
                PinkyPin = status.PinkyPin,
                JoystickVrxPin = status.JoystickVrxPin,
                JoystickVryPin = status.JoystickVryPin,
                JoystickSwPin = status.JoystickSwPin,
                StaIp = normalizedStaIp,
                CalibrationState = ResolveCalibrationStateForRole(role),
                LastSeenTransport = IsSerialStatusStreaming(status) ? "Wi-Fi 实时" : "Wi-Fi"
            };
            _config.Devices.Add(existing);
            changed = true;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(normalizedStaIp) && !string.Equals(existing.StaIp, normalizedStaIp, StringComparison.OrdinalIgnoreCase))
            {
                existing.StaIp = normalizedStaIp;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(status.Mac) && !string.Equals(existing.Mac, status.Mac, StringComparison.OrdinalIgnoreCase))
            {
                existing.Mac = status.Mac!;
                changed = true;
            }

            if (!string.Equals(existing.Name, displayName, StringComparison.OrdinalIgnoreCase))
            {
                existing.Name = displayName;
                changed = true;
            }

            if (!string.Equals(role, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(existing.PreferredRole, role, StringComparison.OrdinalIgnoreCase))
                {
                    existing.PreferredRole = role;
                    changed = true;
                }

                if (!string.Equals(existing.SavedRole, role, StringComparison.OrdinalIgnoreCase))
                {
                    existing.SavedRole = role;
                    changed = true;
                }
            }

            if (status.UdpPort > 0 && existing.UdpPort != status.UdpPort)
            {
                existing.UdpPort = status.UdpPort;
                changed = true;
            }

            if (status.AdcMask != 0 && existing.AdcMask != status.AdcMask)
            {
                existing.AdcMask = status.AdcMask;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(normalizedTarget) && !string.Equals(existing.BoardTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                existing.BoardTarget = normalizedTarget;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(status.FirmwareVersion) && !string.Equals(existing.FirmwareVersion, status.FirmwareVersion, StringComparison.Ordinal))
            {
                existing.FirmwareVersion = status.FirmwareVersion!;
                changed = true;
            }

            if (status.ReportHz > 0 && existing.ReportHz != status.ReportHz)
            {
                existing.ReportHz = status.ReportHz;
                changed = true;
            }

            changed |= UpdateKnownDeviceHardwarePins(existing, status);

            var lastSeenTransport = IsSerialStatusStreaming(status) ? "Wi-Fi 实时" : "Wi-Fi";
            if (!string.Equals(existing.LastSeenTransport, lastSeenTransport, StringComparison.OrdinalIgnoreCase))
            {
                existing.LastSeenTransport = lastSeenTransport;
                changed = true;
            }
        }

        if (changed)
        {
            _configStore.Save(_config);
        }
    }

    private void UpdateKnownDeviceFromStatus(DeviceVm device, SerialStatusDto status)
    {
        var normalizedStaIp = PreferStaIp(status.StaIp, device.StaIp);
        var role = string.IsNullOrWhiteSpace(status.Role) ? device.Role : status.Role!;
        var changed = false;
        var existing = FindKnownDevice(device.Mac, device.DisplayName, device.SerialPort, normalizedStaIp);

        if (existing is null)
        {
            existing = new KnownDevice
            {
                Name = device.DisplayName,
                Mac = device.Mac,
                SerialPort = device.SerialPort,
                PreferredRole = role,
                SavedRole = role,
                UdpPort = status.UdpPort != 0 ? status.UdpPort : device.UdpPort,
                AdcMask = status.AdcMask != 0 ? status.AdcMask : device.AdcMask,
                BoardTarget = string.IsNullOrWhiteSpace(status.BoardTarget) ? string.Empty : FirmwareTargetCatalog.NormalizeTarget(status.BoardTarget),
                FirmwareVersion = status.FirmwareVersion ?? string.Empty,
                ReportHz = status.ReportHz,
                ThumbPin = status.ThumbPin,
                IndexPin = status.IndexPin,
                MiddlePin = status.MiddlePin,
                RingPin = status.RingPin,
                PinkyPin = status.PinkyPin,
                JoystickVrxPin = status.JoystickVrxPin,
                JoystickVryPin = status.JoystickVryPin,
                JoystickSwPin = status.JoystickSwPin,
                StaIp = normalizedStaIp,
                CalibrationState = device.CalibrationState,
                LastSeenTransport = device.LastSeenTransport
            };
            _config.Devices.Add(existing);
            changed = true;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(normalizedStaIp) && !string.Equals(existing.StaIp, normalizedStaIp, StringComparison.OrdinalIgnoreCase))
            {
                existing.StaIp = normalizedStaIp;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(status.Role) && !string.Equals(existing.SavedRole, status.Role, StringComparison.OrdinalIgnoreCase))
            {
                existing.PreferredRole = status.Role!;
                existing.SavedRole = status.Role!;
                changed = true;
            }

            if (status.UdpPort != 0 && existing.UdpPort != status.UdpPort)
            {
                existing.UdpPort = status.UdpPort;
                changed = true;
            }

            if (status.AdcMask != 0 && existing.AdcMask != status.AdcMask)
            {
                existing.AdcMask = status.AdcMask;
                changed = true;
            }

            var normalizedTarget = string.IsNullOrWhiteSpace(status.BoardTarget) ? string.Empty : FirmwareTargetCatalog.NormalizeTarget(status.BoardTarget);
            if (!string.IsNullOrWhiteSpace(normalizedTarget) && !string.Equals(existing.BoardTarget, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                existing.BoardTarget = normalizedTarget;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(status.FirmwareVersion) && !string.Equals(existing.FirmwareVersion, status.FirmwareVersion, StringComparison.Ordinal))
            {
                existing.FirmwareVersion = status.FirmwareVersion!;
                changed = true;
            }

            if (status.ReportHz > 0 && existing.ReportHz != status.ReportHz)
            {
                existing.ReportHz = status.ReportHz;
                changed = true;
            }

            changed |= UpdateKnownDeviceHardwarePins(existing, status);

            if (!string.Equals(existing.CalibrationState, device.CalibrationState, StringComparison.OrdinalIgnoreCase))
            {
                existing.CalibrationState = device.CalibrationState;
                changed = true;
            }

            if (!string.Equals(existing.LastSeenTransport, device.LastSeenTransport, StringComparison.OrdinalIgnoreCase))
            {
                existing.LastSeenTransport = device.LastSeenTransport;
                changed = true;
            }
        }

        device.StaIp = normalizedStaIp;
        if (!string.IsNullOrWhiteSpace(status.Role))
        {
            device.Role = status.Role!;
        }
        if (!string.IsNullOrWhiteSpace(status.BoardTarget))
        {
            device.BoardTarget = FirmwareTargetCatalog.Get(status.BoardTarget).Label;
        }
        if (!string.IsNullOrWhiteSpace(status.FirmwareVersion))
        {
            device.FirmwareVersion = status.FirmwareVersion!;
        }
        if (status.ReportHz > 0)
        {
            device.ReportHz = status.ReportHz;
        }
        device.ThumbPin = status.ThumbPin;
        device.IndexPin = status.IndexPin;
        device.MiddlePin = status.MiddlePin;
        device.RingPin = status.RingPin;
        device.PinkyPin = status.PinkyPin;
        device.JoystickVrxPin = status.JoystickVrxPin;
        device.JoystickVryPin = status.JoystickVryPin;
        device.JoystickSwPin = status.JoystickSwPin;
        device.FingerModuleCount = CountFingerModules(device.ThumbPin, device.IndexPin, device.MiddlePin, device.RingPin, device.PinkyPin);
        device.FingerModuleSummary = device.FingerModuleCount > 0 ? $"{device.FingerModuleCount} 个可用手指模块" : "模块数未知";
        device.BatteryAvailable = status.BatteryAvailable;
        device.BatteryPercent = status.BatteryPercent;
        device.BatteryMillivolts = status.BatteryMillivolts;
        device.BatteryChargingKnown = status.BatteryChargingKnown;
        device.BatteryCharging = status.BatteryCharging;
        device.BatterySummary = BuildBatterySummary(device.BatteryAvailable, device.BatteryPercent, device.BatteryChargingKnown, device.BatteryCharging);
        device.BatteryVoltageText = BuildBatteryVoltageText(device.BatteryAvailable, device.BatteryMillivolts);
        device.JoystickConnectionText = BuildDeviceJoystickSummary(new DiscoveryDevice
        {
            Role = device.Role,
            JoystickVrxPin = device.JoystickVrxPin,
            JoystickVryPin = device.JoystickVryPin,
            JoystickSwPin = device.JoystickSwPin
        });

        if (changed)
        {
            _configStore.Save(_config);
        }
    }

    private static bool UpdateKnownDeviceHardwarePins(KnownDevice device, SerialStatusDto status)
    {
        var changed = false;

        if (status.ThumbPin >= 0 && device.ThumbPin != status.ThumbPin)
        {
            device.ThumbPin = status.ThumbPin;
            changed = true;
        }
        if (status.IndexPin >= 0 && device.IndexPin != status.IndexPin)
        {
            device.IndexPin = status.IndexPin;
            changed = true;
        }
        if (status.MiddlePin >= 0 && device.MiddlePin != status.MiddlePin)
        {
            device.MiddlePin = status.MiddlePin;
            changed = true;
        }
        if (status.RingPin >= 0 && device.RingPin != status.RingPin)
        {
            device.RingPin = status.RingPin;
            changed = true;
        }
        if (status.PinkyPin >= 0 && device.PinkyPin != status.PinkyPin)
        {
            device.PinkyPin = status.PinkyPin;
            changed = true;
        }
        if (status.JoystickVrxPin >= 0 && device.JoystickVrxPin != status.JoystickVrxPin)
        {
            device.JoystickVrxPin = status.JoystickVrxPin;
            changed = true;
        }
        if (status.JoystickVryPin >= 0 && device.JoystickVryPin != status.JoystickVryPin)
        {
            device.JoystickVryPin = status.JoystickVryPin;
            changed = true;
        }
        if (status.JoystickSwPin >= 0 && device.JoystickSwPin != status.JoystickSwPin)
        {
            device.JoystickSwPin = status.JoystickSwPin;
            changed = true;
        }

        return changed;
    }

    private bool PersistDeviceRole(DeviceVm device, string role)
    {
        var normalizedRole = NormalizeRoleForUi(role, device.Role);
        var changed = false;
        var existing = FindKnownDevice(device.Mac, device.DisplayName, device.SerialPort, device.StaIp);

        if (existing is null)
        {
            existing = new KnownDevice
            {
                Name = device.DisplayName,
                Mac = device.Mac,
                SerialPort = device.SerialPort,
                StaIp = device.StaIp,
                PreferredRole = normalizedRole,
                SavedRole = normalizedRole,
                UdpPort = device.UdpPort,
                AdcMask = device.AdcMask,
                BoardTarget = ResolveFirmwareTargetForDevice(device),
                FirmwareVersion = device.FirmwareVersion,
                ReportHz = device.ReportHz,
                ThumbPin = device.ThumbPin,
                IndexPin = device.IndexPin,
                MiddlePin = device.MiddlePin,
                RingPin = device.RingPin,
                PinkyPin = device.PinkyPin,
                JoystickVrxPin = device.JoystickVrxPin,
                JoystickVryPin = device.JoystickVryPin,
                JoystickSwPin = device.JoystickSwPin,
                CalibrationState = device.CalibrationState,
                LastSeenTransport = device.LastSeenTransport
            };
            _config.Devices.Add(existing);
            changed = true;
        }
        else
        {
            if (!string.Equals(existing.PreferredRole, normalizedRole, StringComparison.OrdinalIgnoreCase))
            {
                existing.PreferredRole = normalizedRole;
                changed = true;
            }

            if (!string.Equals(existing.SavedRole, normalizedRole, StringComparison.OrdinalIgnoreCase))
            {
                existing.SavedRole = normalizedRole;
                changed = true;
            }
        }

        if (changed)
        {
            _configStore.Save(_config);
        }

        return changed;
    }

    private void SaveKnownDevice(DeviceVm device, string configuredHostIp, int udpPort, int adcMask, string role)
    {
        var existing = FindKnownDevice(device.Mac, device.DisplayName, device.SerialPort, device.StaIp);
        if (existing is null)
        {
            existing = new KnownDevice();
            _config.Devices.Add(existing);
        }

        existing.Name = device.DisplayName;
        existing.Mac = device.Mac;
        existing.BleAddress = string.Empty;
        existing.SerialPort = device.SerialPort;
        existing.StaIp = PreferStaIp(device.StaIp, existing.StaIp);
        existing.PreferredRole = role;
        existing.SavedRole = role;
        existing.UdpPort = udpPort;
        existing.AdcMask = adcMask;
        existing.BoardTarget = ResolveFirmwareTargetForDevice(device);
        existing.FirmwareVersion = device.FirmwareVersion;
        existing.ReportHz = device.ReportHz > 0 ? device.ReportHz : _config.Firmware.ReportRateHz;
        existing.ThumbPin = device.ThumbPin;
        existing.IndexPin = device.IndexPin;
        existing.MiddlePin = device.MiddlePin;
        existing.RingPin = device.RingPin;
        existing.PinkyPin = device.PinkyPin;
        existing.JoystickVrxPin = device.JoystickVrxPin;
        existing.JoystickVryPin = device.JoystickVryPin;
        existing.JoystickSwPin = device.JoystickSwPin;
        existing.CalibrationState = device.CalibrationState;
        existing.LastSeenTransport = device.LastSeenTransport;
        _config.Runtime.HostIp = string.IsNullOrWhiteSpace(configuredHostIp)
            || string.Equals(configuredHostIp, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : OpenFingerWire.IsLocalHostIp(configuredHostIp) && !OpenFingerWire.IsDeprioritizedLocalIp(configuredHostIp)
                ? configuredHostIp
                : "auto";
        _config.Runtime.DeviceUdpPort = udpPort;
        _configStore.Save(_config);
        SyncUdpMonitorMode();
        _lastProcessStatusRefreshUtc = DateTime.UtcNow;
        _lastPortInventoryRefreshUtc = DateTime.MinValue;
    }

    private string FormatFirmwareFailure(string operationName, Exception ex)
    {
        var output = _vm.FirmwareOutput ?? string.Empty;
        if (ex.Message.Contains("ROM 下载模式", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operationName}失败: {ex.Message}";
        }

        if (output.Contains("could not find espflash.exe", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("could not find espflash.exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operationName}失败: 本机缺少 espflash 刷写组件。请重新打开 OpenFinger.Control 后再试；如果还是失败，再告诉我。";
        }

        if (output.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
            || output.Contains("could not connect to device", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No serial data received", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operationName}失败: 芯片没有进入下载模式，或者进入下载模式后串口号变了。请按住 BOOT，点一下 RESET，松开 BOOT，然后重新刷写。";
        }

        if (output.Contains("manifest referenced missing firmware files", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operationName}失败: 固件包不完整，缺少必要的二进制文件。";
        }

        if (output.Contains("device did not answer OFSTATUS", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("device did not answer OFSTATUS", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operationName}失败: 设备已经刷入，但重启后暂时没有回固件状态。常见原因是串口刚恢复、板子还在重新上电，或者这块板子的运行配置让它短暂失联。现在程序已经会自动重试；如果仍失败，把这次完整日志再发我。";
        }

        if (output.Contains("Could not find file", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The device attached to the system is not functioning", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operationName}失败: 当前串口已经断开、重启或变号了。请重新插拔设备，刷新串口后再试。";
        }

        if (output.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"{operationName}失败: 设备刚刷完时串口还在被系统重新接管，或者串口号已经变了。我已经加了自动等待和重试；如果这次仍失败，刷新串口后再试一次。";
        }

        return $"{operationName}失败: {ex.Message}";
    }
}

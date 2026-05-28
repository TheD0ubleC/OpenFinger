using System.Collections.ObjectModel;

namespace OpenFinger.Control;

public sealed class FirmwareModeOption
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public sealed class FirmwarePinOption
{
    public int Value { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed class FirmwarePortOption
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class FirmwarePackageVm
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Target { get; init; } = FirmwareTargetCatalog.Esp32C3;
    public string Version { get; init; } = string.Empty;
    public int ReportRateHz { get; init; }
    public string BootHint { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
}

public sealed class MainVm : ObservableObject
{
    private DeviceVm? _selectedDevice;
    private FirmwarePackageVm? _selectedFirmwarePackage;
    private string _ssid = string.Empty;
    private string _hostIp = "auto";
    private string _udpPort = "39001";
    private string _adcMask = "31";
    private string _role = "unknown";
    private string _statusLine = "等待设备接入";
    private string _serviceStatus = "service: 未启动";
    private string _bridgeStatus = "bridge: 未启动";
    private string _steamVrStatus = "SteamVR: 未检测";
    private string _vrServerStatus = "vrserver: 未检测";
    private string _lastSeenRuntime = "--";
    private string _rawPacketLog = string.Empty;
    private string _serviceLog = string.Empty;
    private string _firmwareTarget = FirmwareTargetCatalog.Esp32C3;
    private string _firmwareSource = "bundled";
    private string _firmwarePort = string.Empty;
    private string _firmwareCatalogStatus = "等待检查固件包";
    private string _firmwareOutput = string.Empty;
    private string _firmwareFriendlyOutput = string.Empty;
    private string _firmwareOnlineCatalogUrl = string.Empty;
    private string _firmwareExternalPackagePath = string.Empty;
    private string _firmwareVersionTag = OpenFingerVersion.Version;
    private string _firmwareDetectedTarget = "未识别";
    private string _firmwareDetectedVersion = "未识别";
    private string _firmwareDetectedReportRate = "--";
    private int _firmwareReportRateHz = 30;
    private int _firmwareThumbPin;
    private int _firmwareIndexPin = 1;
    private int _firmwareMiddlePin = 2;
    private int _firmwareRingPin = 3;
    private int _firmwarePinkyPin = 4;
    private int _firmwareTrackingSwitchPin = -1;
    private string _firmwareTrackingSwitchMode = "disabled";
    private int _firmwareJoystickVrxPin = -1;
    private int _firmwareJoystickVryPin = -1;
    private int _firmwareJoystickSwPin = -1;
    private int _firmwareBatteryAdcPin = -1;
    private int _firmwareBatteryChargePin = -1;
    private bool _showAdvanced;

    public ObservableCollection<DeviceVm> Devices { get; } = new();
    public ObservableCollection<FingerRuntimeVm> LeftFingers { get; } = new();
    public ObservableCollection<FingerRuntimeVm> RightFingers { get; } = new();
    public JoystickRuntimeVm LeftJoystick { get; } = new();
    public JoystickRuntimeVm RightJoystick { get; } = new();
    public ObservableCollection<FirmwareModeOption> FirmwareTargetOptions { get; } = new();
    public ObservableCollection<FirmwareModeOption> FirmwareSourceOptions { get; } = new();
    public ObservableCollection<FirmwareModeOption> FirmwareReportRateOptions { get; } = new();
    public ObservableCollection<FirmwarePinOption> FirmwareAdcPinOptions { get; } = new();
    public ObservableCollection<FirmwarePinOption> FirmwareSwitchPinOptions { get; } = new();
    public ObservableCollection<FirmwarePinOption> FirmwareOptionalAdcPinOptions { get; } = new();
    public ObservableCollection<FirmwarePinOption> FirmwareOptionalSwitchPinOptions { get; } = new();
    public ObservableCollection<FirmwareModeOption> FirmwareTrackingSwitchModes { get; } = new();
    public ObservableCollection<FirmwarePortOption> FirmwarePorts { get; } = new();
    public ObservableCollection<FirmwarePackageVm> FirmwarePackages { get; } = new();

    public DeviceVm? SelectedDevice { get => _selectedDevice; set => SetProperty(ref _selectedDevice, value); }
    public FirmwarePackageVm? SelectedFirmwarePackage { get => _selectedFirmwarePackage; set => SetProperty(ref _selectedFirmwarePackage, value); }
    public string Ssid { get => _ssid; set => SetProperty(ref _ssid, value); }
    public string HostIp { get => _hostIp; set => SetProperty(ref _hostIp, value); }
    public string UdpPort { get => _udpPort; set => SetProperty(ref _udpPort, value); }
    public string AdcMask { get => _adcMask; set => SetProperty(ref _adcMask, value); }
    public string Role { get => _role; set => SetProperty(ref _role, value); }
    public string StatusLine { get => _statusLine; set => SetProperty(ref _statusLine, value); }
    public string ServiceStatus { get => _serviceStatus; set => SetProperty(ref _serviceStatus, value); }
    public string BridgeStatus { get => _bridgeStatus; set => SetProperty(ref _bridgeStatus, value); }
    public string SteamVrStatus { get => _steamVrStatus; set => SetProperty(ref _steamVrStatus, value); }
    public string VrServerStatus { get => _vrServerStatus; set => SetProperty(ref _vrServerStatus, value); }
    public string LastSeenRuntime { get => _lastSeenRuntime; set => SetProperty(ref _lastSeenRuntime, value); }
    public string RawPacketLog { get => _rawPacketLog; set => SetProperty(ref _rawPacketLog, value); }
    public string ServiceLog { get => _serviceLog; set => SetProperty(ref _serviceLog, value); }
    public string FirmwareTarget { get => _firmwareTarget; set => SetProperty(ref _firmwareTarget, FirmwareTargetCatalog.NormalizeTarget(value)); }
    public string FirmwareSource { get => _firmwareSource; set => SetProperty(ref _firmwareSource, value); }
    public string FirmwarePort { get => _firmwarePort; set => SetProperty(ref _firmwarePort, value); }
    public string FirmwareCatalogStatus { get => _firmwareCatalogStatus; set => SetProperty(ref _firmwareCatalogStatus, value); }
    public string FirmwareOutput { get => _firmwareOutput; set => SetProperty(ref _firmwareOutput, value); }
    public string FirmwareFriendlyOutput { get => _firmwareFriendlyOutput; set => SetProperty(ref _firmwareFriendlyOutput, value); }
    public string FirmwareOnlineCatalogUrl { get => _firmwareOnlineCatalogUrl; set => SetProperty(ref _firmwareOnlineCatalogUrl, value); }
    public string FirmwareExternalPackagePath { get => _firmwareExternalPackagePath; set => SetProperty(ref _firmwareExternalPackagePath, value); }
    public string FirmwareVersionTag { get => _firmwareVersionTag; set => SetProperty(ref _firmwareVersionTag, value); }
    public string FirmwareDetectedTarget { get => _firmwareDetectedTarget; set => SetProperty(ref _firmwareDetectedTarget, value); }
    public string FirmwareDetectedVersion { get => _firmwareDetectedVersion; set => SetProperty(ref _firmwareDetectedVersion, value); }
    public string FirmwareDetectedReportRate { get => _firmwareDetectedReportRate; set => SetProperty(ref _firmwareDetectedReportRate, value); }
    public int FirmwareReportRateHz { get => _firmwareReportRateHz; set => SetProperty(ref _firmwareReportRateHz, value); }
    public int FirmwareThumbPin { get => _firmwareThumbPin; set => SetProperty(ref _firmwareThumbPin, value); }
    public int FirmwareIndexPin { get => _firmwareIndexPin; set => SetProperty(ref _firmwareIndexPin, value); }
    public int FirmwareMiddlePin { get => _firmwareMiddlePin; set => SetProperty(ref _firmwareMiddlePin, value); }
    public int FirmwareRingPin { get => _firmwareRingPin; set => SetProperty(ref _firmwareRingPin, value); }
    public int FirmwarePinkyPin { get => _firmwarePinkyPin; set => SetProperty(ref _firmwarePinkyPin, value); }
    public int FirmwareTrackingSwitchPin { get => _firmwareTrackingSwitchPin; set => SetProperty(ref _firmwareTrackingSwitchPin, value); }
    public string FirmwareTrackingSwitchMode { get => _firmwareTrackingSwitchMode; set => SetProperty(ref _firmwareTrackingSwitchMode, value); }
    public int FirmwareJoystickVrxPin { get => _firmwareJoystickVrxPin; set => SetProperty(ref _firmwareJoystickVrxPin, value); }
    public int FirmwareJoystickVryPin { get => _firmwareJoystickVryPin; set => SetProperty(ref _firmwareJoystickVryPin, value); }
    public int FirmwareJoystickSwPin { get => _firmwareJoystickSwPin; set => SetProperty(ref _firmwareJoystickSwPin, value); }
    public int FirmwareBatteryAdcPin { get => _firmwareBatteryAdcPin; set => SetProperty(ref _firmwareBatteryAdcPin, value); }
    public int FirmwareBatteryChargePin { get => _firmwareBatteryChargePin; set => SetProperty(ref _firmwareBatteryChargePin, value); }
    public bool ShowAdvanced { get => _showAdvanced; set => SetProperty(ref _showAdvanced, value); }
}

public sealed class DeviceVm : ObservableObject
{
    private string _id = string.Empty;
    private string _displayName = "OpenFinger";
    private string _transport = string.Empty;
    private string _serialPort = string.Empty;
    private string _mac = string.Empty;
    private string _staIp = string.Empty;
    private string _role = "unknown";
    private string _savedRole = "unknown";
    private string _status = "未连接";
    private string _detail = string.Empty;
    private string _message = string.Empty;
    private string _wifiStatus = "Wi-Fi: --";
    private string _usbStatus = "USB: --";
    private bool _online;
    private bool _isUsbPreferred;
    private bool _usbConnected;
    private bool _wifiConnected;
    private bool _wifiActive;
    private int _udpPort = 39001;
    private int _adcMask = 31;
    private string _boardTarget = string.Empty;
    private string _firmwareVersion = string.Empty;
    private int _reportHz;
    private int _thumbPin = -1;
    private int _indexPin = -1;
    private int _middlePin = -1;
    private int _ringPin = -1;
    private int _pinkyPin = -1;
    private int _joystickVrxPin = -1;
    private int _joystickVryPin = -1;
    private int _joystickSwPin = -1;
    private int _fingerModuleCount;
    private string _fingerModuleSummary = "模块数未知";
    private string _joystickConnectionText = "摇杆未配置";
    private bool _batteryAvailable;
    private int _batteryPercent = -1;
    private int _batteryMillivolts = -1;
    private bool _batteryChargingKnown;
    private bool _batteryCharging;
    private string _batterySummary = "电池未配置";
    private string _batteryVoltageText = "--";
    private string _calibrationState = "unknown";
    private string _lastSeenTransport = string.Empty;
    private string _lastSeenText = "--";
    private DateTime _lastSeenUtc = DateTime.MinValue;
    private bool _trackingEnabled = true;

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string DisplayName { get => _displayName; set { if (SetProperty(ref _displayName, value)) Raise(nameof(ConnectionSummary)); } }
    public string Transport { get => _transport; set => SetProperty(ref _transport, value); }
    public string SerialPort { get => _serialPort; set { if (SetProperty(ref _serialPort, value)) Raise(nameof(ConnectionSummary)); } }
    public string Mac { get => _mac; set { if (SetProperty(ref _mac, value)) Raise(nameof(ConnectionSummary)); } }
    public string StaIp { get => _staIp; set { if (SetProperty(ref _staIp, value)) Raise(nameof(ConnectionSummary)); } }
    public string Role { get => _role; set => SetProperty(ref _role, value); }
    public string SavedRole { get => _savedRole; set => SetProperty(ref _savedRole, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public string WifiStatus { get => _wifiStatus; set => SetProperty(ref _wifiStatus, value); }
    public string UsbStatus { get => _usbStatus; set => SetProperty(ref _usbStatus, value); }
    public bool Online { get => _online; set => SetProperty(ref _online, value); }
    public bool IsUsbPreferred { get => _isUsbPreferred; set => SetProperty(ref _isUsbPreferred, value); }
    public bool UsbPreferred { get => IsUsbPreferred; set => IsUsbPreferred = value; }
    public bool UsbConnected { get => _usbConnected; set => SetProperty(ref _usbConnected, value); }
    public bool WifiConnected { get => _wifiConnected; set => SetProperty(ref _wifiConnected, value); }
    public bool WifiActive { get => _wifiActive; set => SetProperty(ref _wifiActive, value); }
    public int UdpPort { get => _udpPort; set => SetProperty(ref _udpPort, value); }
    public int AdcMask { get => _adcMask; set => SetProperty(ref _adcMask, value); }
    public string BoardTarget { get => _boardTarget; set => SetProperty(ref _boardTarget, value); }
    public string FirmwareVersion { get => _firmwareVersion; set => SetProperty(ref _firmwareVersion, value); }
    public int ReportHz { get => _reportHz; set => SetProperty(ref _reportHz, value); }
    public int ThumbPin { get => _thumbPin; set => SetProperty(ref _thumbPin, value); }
    public int IndexPin { get => _indexPin; set => SetProperty(ref _indexPin, value); }
    public int MiddlePin { get => _middlePin; set => SetProperty(ref _middlePin, value); }
    public int RingPin { get => _ringPin; set => SetProperty(ref _ringPin, value); }
    public int PinkyPin { get => _pinkyPin; set => SetProperty(ref _pinkyPin, value); }
    public int JoystickVrxPin { get => _joystickVrxPin; set => SetProperty(ref _joystickVrxPin, value); }
    public int JoystickVryPin { get => _joystickVryPin; set => SetProperty(ref _joystickVryPin, value); }
    public int JoystickSwPin { get => _joystickSwPin; set => SetProperty(ref _joystickSwPin, value); }
    public int FingerModuleCount { get => _fingerModuleCount; set => SetProperty(ref _fingerModuleCount, value); }
    public string FingerModuleSummary { get => _fingerModuleSummary; set => SetProperty(ref _fingerModuleSummary, value); }
    public string JoystickConnectionText { get => _joystickConnectionText; set => SetProperty(ref _joystickConnectionText, value); }
    public bool BatteryAvailable { get => _batteryAvailable; set => SetProperty(ref _batteryAvailable, value); }
    public int BatteryPercent { get => _batteryPercent; set => SetProperty(ref _batteryPercent, value); }
    public int BatteryMillivolts { get => _batteryMillivolts; set => SetProperty(ref _batteryMillivolts, value); }
    public bool BatteryChargingKnown { get => _batteryChargingKnown; set => SetProperty(ref _batteryChargingKnown, value); }
    public bool BatteryCharging { get => _batteryCharging; set => SetProperty(ref _batteryCharging, value); }
    public string BatterySummary { get => _batterySummary; set => SetProperty(ref _batterySummary, value); }
    public string BatteryVoltageText { get => _batteryVoltageText; set => SetProperty(ref _batteryVoltageText, value); }
    public string CalibrationState { get => _calibrationState; set => SetProperty(ref _calibrationState, value); }
    public string LastSeenTransport { get => _lastSeenTransport; set => SetProperty(ref _lastSeenTransport, value); }
    public string LastSeenText { get => _lastSeenText; set => SetProperty(ref _lastSeenText, value); }
    public DateTime LastSeenUtc { get => _lastSeenUtc; set => SetProperty(ref _lastSeenUtc, value); }
    public bool TrackingEnabled { get => _trackingEnabled; set => SetProperty(ref _trackingEnabled, value); }
    public string ConnectionSummary => string.Join(" · ", new[] { SerialPort, StaIp, Mac }.Where(item => !string.IsNullOrWhiteSpace(item)));
}

public sealed class FingerRuntimeVm : ObservableObject
{
    private string _name = string.Empty;
    private string _displayName = string.Empty;
    private bool _active;
    private bool _packetActive;
    private double _bend;
    private int _raw;
    private int _centerRaw = 2048;
    private int _minRaw = 4095;
    private int _maxRaw;
    private int _calibratedOpenRaw = -1;
    private int _calibratedClosedRaw = -1;
    private string _direction = "auto";

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public bool Active { get => _active; set => SetProperty(ref _active, value); }
    public bool PacketActive { get => _packetActive; set => SetProperty(ref _packetActive, value); }
    public double Bend { get => _bend; set => SetProperty(ref _bend, value); }
    public int Raw { get => _raw; set => SetProperty(ref _raw, value); }
    public int CenterRaw { get => _centerRaw; set => SetProperty(ref _centerRaw, value); }
    public int MinRaw { get => _minRaw; set => SetProperty(ref _minRaw, value); }
    public int MaxRaw { get => _maxRaw; set => SetProperty(ref _maxRaw, value); }
    public int CalibratedOpenRaw { get => _calibratedOpenRaw; set => SetProperty(ref _calibratedOpenRaw, value); }
    public int CalibratedClosedRaw { get => _calibratedClosedRaw; set => SetProperty(ref _calibratedClosedRaw, value); }
    public string Direction { get => _direction; set => SetProperty(ref _direction, value); }
}

public sealed class JoystickRuntimeVm : ObservableObject
{
    private bool _available;
    private double _axisX;
    private double _axisY;
    private int _rawX = -1;
    private int _rawY = -1;
    private bool? _switchPressed;

    public bool Available { get => _available; set => SetProperty(ref _available, value); }
    public double AxisX { get => _axisX; set => SetProperty(ref _axisX, value); }
    public double AxisY { get => _axisY; set => SetProperty(ref _axisY, value); }
    public int RawX { get => _rawX; set => SetProperty(ref _rawX, value); }
    public int RawY { get => _rawY; set => SetProperty(ref _rawY, value); }
    public bool? SwitchPressed { get => _switchPressed; set => SetProperty(ref _switchPressed, value); }
}

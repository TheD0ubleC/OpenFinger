using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFinger.Control;

public sealed class OpenFingerAppConfig
{
    public int AdcMax { get; set; } = 4095;
    public RuntimeConfig Runtime { get; set; } = new();
    public ServiceConfig Service { get; set; } = new();
    public HandsConfig Hands { get; set; } = new();
    public JoystickSettingsConfig Joystick { get; set; } = new();
    public GestureSettingsConfig Gestures { get; set; } = new();
    public ControllerPoseOffsetsConfig PoseOffsets { get; set; } = new();
    public FirmwareConfigState Firmware { get; set; } = new();
    public AlgorithmTuningConfig AlgorithmTuning { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public List<KnownDevice> Devices { get; set; } = new();
}

public sealed class RuntimeConfig
{
    public string HostIp { get; set; } = "auto";
    public int DeviceUdpPort { get; set; } = 39001;
    public int LocalRuntimeUdpPort { get; set; } = 39003;
    public int PublishHz { get; set; } = 90;
}

public sealed class ServiceConfig
{
    public int RawInputUdpPort { get; set; } = 39004;
}

public sealed class HandsConfig
{
    public HandConfig Left { get; set; } = HandConfig.CreateDefault();
    public HandConfig Right { get; set; } = HandConfig.CreateDefault();
}

public sealed class HandConfig
{
    public Dictionary<string, FingerConfig> Fingers { get; set; } = CreateFingerMap();

    public static HandConfig CreateDefault() => new() { Fingers = CreateFingerMap() };

    private static Dictionary<string, FingerConfig> CreateFingerMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["thumb"] = new(),
        ["index"] = new(),
        ["middle"] = new(),
        ["ring"] = new(),
        ["pinky"] = new()
    };
}

public sealed class FingerConfig
{
    public bool Enabled { get; set; } = true;
    public int CenterRaw { get; set; } = 2048;
    public int CalibratedOpenRaw { get; set; } = -1;
    public int CalibratedClosedRaw { get; set; } = -1;
    public string Direction { get; set; } = "auto";
    public double SmoothingAlpha { get; set; } = 0.35;
    public double Deadzone { get; set; } = 0.03;
}

public sealed class JoystickSettingsConfig
{
    public JoystickHandSettings Left { get; set; } = new();
    public JoystickHandSettings Right { get; set; } = new();
}

public sealed class JoystickHandSettings
{
    public string SteamVrAxisMode { get; set; } = JoystickSteamVrCatalog.AxisJoystick;
    public string SteamVrClickAction { get; set; } = JoystickSteamVrCatalog.ClickJoystick;
    public string Orientation { get; set; } = JoystickOrientationCatalog.Normal;
    public double DeadzonePercent { get; set; } = 12;
    public int CenterRawX { get; set; } = -1;
    public int CenterRawY { get; set; } = -1;
}

public sealed class GestureSettingsConfig
{
    public GestureHandSettings Left { get; set; } = GestureHandSettings.CreateDefault();
    public GestureHandSettings Right { get; set; } = GestureHandSettings.CreateDefault();
}

public sealed class GestureHandSettings
{
    public bool Enabled { get; set; }
    public Dictionary<string, GestureBindingConfig> Mappings { get; set; } = CreateDefaultMappings();

    public static GestureHandSettings CreateDefault() => new() { Mappings = CreateDefaultMappings() };

    private static Dictionary<string, GestureBindingConfig> CreateDefaultMappings()
    {
        return GestureComboCatalog.Definitions.ToDictionary(
            item => item.Key,
            _ => new GestureBindingConfig(),
            StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class GestureBindingConfig
{
    public bool Enabled { get; set; }
    public string MappedButton { get; set; } = GestureButtonCatalog.Disabled;
    public GestureCalibrationConfig Calibration { get; set; } = new();
}

public sealed class GestureCalibrationConfig
{
    public bool Calibrated { get; set; }
    public double OpenScore { get; set; }
    public double PinchScore { get; set; } = 0.75;
    public double TriggerThreshold { get; set; } = 0.62;
    public double ReleaseThreshold { get; set; } = 0.42;
    public double ConfidenceThreshold { get; set; } = 0.54;
    public int CalibrationRepeats { get; set; } = 5;
    public double ThumbOpenMean { get; set; }
    public double ThumbPinchMean { get; set; } = 0.75;
    public double TargetOpenMean { get; set; }
    public double TargetPinchMean { get; set; } = 0.75;
    public double PrimaryOpenScore { get; set; }
    public double PrimaryPinchScore { get; set; } = 0.75;
    public double[] SupportOpenMeans { get; set; } = new double[3];
    public double[] SupportPinchMeans { get; set; } = new double[3];
    public double[] SupportPinchStdDevs { get; set; } = new double[3];
    public double[] SupportInfluences { get; set; } = new[] { 0.2, 0.2, 0.2 };
}

public sealed class ControllerStyleConfigState
{
    public string StyleId { get; set; } = ControllerStyleCatalog.Knuckles;
    public string DisplayName { get; set; } = string.Empty;
    public string ControllerTypeOverride { get; set; } = string.Empty;
    public string RenderModelOverride { get; set; } = string.Empty;
}


public sealed class ControllerPoseOffsetsConfig
{
    public ControllerPoseOffsetConfig Left { get; set; } = new();
    public ControllerPoseOffsetConfig Right { get; set; } = new();
}

public sealed class ControllerPoseOffsetConfig
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double PositionZ { get; set; }
    public double RotationPitch { get; set; }
    public double RotationYaw { get; set; }
    public double RotationRoll { get; set; }
}

public class FirmwareConfigState
{
    public string Target { get; set; } = FirmwareTargetCatalog.Esp32C3;
    public string PreferredSource { get; set; } = "bundled";
    public string OnlineCatalogUrl { get; set; } = string.Empty;
    public string ExternalPackagePath { get; set; } = string.Empty;
    public string LastPackageId { get; set; } = string.Empty;
    public string VersionTag { get; set; } = OpenFingerVersion.Version;
    public int ReportRateHz { get; set; } = 30;
    public int ThumbPin { get; set; } = 0;
    public int IndexPin { get; set; } = 1;
    public int MiddlePin { get; set; } = 2;
    public int RingPin { get; set; } = 3;
    public int PinkyPin { get; set; } = 4;
    public int TrackingSwitchPin { get; set; } = -1;
    public string TrackingSwitchMode { get; set; } = "disabled";
    public int JoystickVrxPin { get; set; } = -1;
    public int JoystickVryPin { get; set; } = -1;
    public int JoystickSwPin { get; set; } = -1;
    public int BatteryAdcPin { get; set; } = -1;
    public int BatteryChargePin { get; set; } = -1;
}

public sealed class FirmwareConfig : FirmwareConfigState
{
}

public sealed class AlgorithmTuningConfig
{
    public double SensitivityLevel { get; set; } = 2;
    public double AntiShakeLevel { get; set; } = 2;
    public double SmoothingAlpha { get; set; } = 0.35;
    public double DeadzonePercent { get; set; } = 3;
    public double KalmanQ { get; set; } = 0.01;
}

public sealed class UiConfig
{
    public bool ShowAdvanced { get; set; }
    public string ThemeMode { get; set; } = UiThemeCatalog.Light;
    public TrayConfig Tray { get; set; } = new();
    public WindowConfig Window { get; set; } = new();
    public NavigationConfig Navigation { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public UpdateConfig Updates { get; set; } = new();
}

public sealed class TrayConfig
{
    public string CloseButtonAction { get; set; } = UiCloseActionCatalog.Ask;
    public string Visibility { get; set; } = UiTrayVisibilityCatalog.BackgroundOnly;
    public bool ReduceLoadWhenHidden { get; set; } = true;
    public bool EnableWindowsStartup { get; set; }
    public bool StartHiddenOnWindowsStartup { get; set; }
}

public sealed class WindowConfig
{
    public bool RememberBounds { get; set; } = true;
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 1360;
    public double Height { get; set; } = 900;
    public bool WasMaximized { get; set; }
}

public sealed class NavigationConfig
{
    public bool RememberLastPage { get; set; }
    public string LaunchPage { get; set; } = UiPageCatalog.Home;
    public string LastPage { get; set; } = UiPageCatalog.Home;
}

public sealed class NotificationConfig
{
    public bool EnableTrayNotifications { get; set; } = true;
    public bool DeviceEvents { get; set; } = true;
    public bool FlashResults { get; set; } = true;
    public bool DriverResults { get; set; } = true;
    public bool UpdateResults { get; set; } = true;
}

public sealed class UpdateConfig
{
    public bool CheckOnStartup { get; set; } = true;
    public bool PromptWhenAvailable { get; set; } = true;
    public string IgnoredVersion { get; set; } = string.Empty;
    public string LastCheckedUtc { get; set; } = string.Empty;
    public string LastKnownVersion { get; set; } = string.Empty;
}

public sealed class KnownDevice
{
    public string Name { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string BleAddress { get; set; } = string.Empty;
    public string SerialPort { get; set; } = string.Empty;
    public string StaIp { get; set; } = string.Empty;
    public string PreferredRole { get; set; } = "unknown";
    public string SavedRole { get; set; } = "unknown";
    public int UdpPort { get; set; } = 39001;
    public int AdcMask { get; set; } = 31;
    public string BoardTarget { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public int ReportHz { get; set; }
    public int ThumbPin { get; set; } = -1;
    public int IndexPin { get; set; } = -1;
    public int MiddlePin { get; set; } = -1;
    public int RingPin { get; set; } = -1;
    public int PinkyPin { get; set; } = -1;
    public int JoystickVrxPin { get; set; } = -1;
    public int JoystickVryPin { get; set; } = -1;
    public int JoystickSwPin { get; set; } = -1;
    public string CalibrationState { get; set; } = "unknown";
    public string LastSeenTransport { get; set; } = string.Empty;
}

public sealed class OpenFingerConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string PathValue { get; }

    public OpenFingerConfigStore()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        PathValue = Path.Combine(root, "OpenFinger", "openfinger_control.json");
    }

    public OpenFingerAppConfig Load()
    {
        try
        {
            if (File.Exists(PathValue))
            {
                var config = JsonSerializer.Deserialize<OpenFingerAppConfig>(File.ReadAllText(PathValue), JsonOptions);
                if (config is not null)
                {
                    Normalize(config);
                    return config;
                }
            }
        }
        catch
        {
        }

        var fallback = new OpenFingerAppConfig();
        Normalize(fallback);
        Save(fallback);
        return fallback;
    }

    public void Save(OpenFingerAppConfig config)
    {
        Normalize(config);
        Directory.CreateDirectory(Path.GetDirectoryName(PathValue)!);
        File.WriteAllText(PathValue, JsonSerializer.Serialize(config, JsonOptions));
    }


    private static void NormalizePoseOffset(ControllerPoseOffsetConfig offset)
    {
        offset.PositionX = ClampFinite(offset.PositionX, -1.0, 1.0);
        offset.PositionY = ClampFinite(offset.PositionY, -1.0, 1.0);
        offset.PositionZ = ClampFinite(offset.PositionZ, -1.0, 1.0);
        offset.RotationPitch = ClampFinite(offset.RotationPitch, -180.0, 180.0);
        offset.RotationYaw = ClampFinite(offset.RotationYaw, -180.0, 180.0);
        offset.RotationRoll = ClampFinite(offset.RotationRoll, -180.0, 180.0);
    }

    private static double ClampFinite(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.0;
        }

        return Math.Clamp(value, min, max);
    }

    private static void Normalize(OpenFingerAppConfig config)
    {
        config.Runtime ??= new RuntimeConfig();
        config.Service ??= new ServiceConfig();
        config.Hands ??= new HandsConfig();
        config.Hands.Left ??= HandConfig.CreateDefault();
        config.Hands.Right ??= HandConfig.CreateDefault();
        config.Hands.Left.Fingers ??= HandConfig.CreateDefault().Fingers;
        config.Hands.Right.Fingers ??= HandConfig.CreateDefault().Fingers;
        config.Joystick ??= new JoystickSettingsConfig();
        config.Joystick.Left ??= new JoystickHandSettings();
        config.Joystick.Right ??= new JoystickHandSettings();
        config.Gestures ??= new GestureSettingsConfig();
        config.Gestures.Left ??= GestureHandSettings.CreateDefault();
        config.Gestures.Right ??= GestureHandSettings.CreateDefault();
        config.Gestures.Left.Mappings ??= GestureHandSettings.CreateDefault().Mappings;
        config.Gestures.Right.Mappings ??= GestureHandSettings.CreateDefault().Mappings;
        NormalizeGestureHand(config.Gestures.Left);
        NormalizeGestureHand(config.Gestures.Right);
        config.PoseOffsets ??= new ControllerPoseOffsetsConfig();
        config.PoseOffsets.Left ??= new ControllerPoseOffsetConfig();
        config.PoseOffsets.Right ??= new ControllerPoseOffsetConfig();
        NormalizePoseOffset(config.PoseOffsets.Left);
        NormalizePoseOffset(config.PoseOffsets.Right);
        config.Firmware ??= new FirmwareConfigState();
        config.AlgorithmTuning ??= new AlgorithmTuningConfig();
        config.Ui ??= new UiConfig();
        config.Ui.Tray ??= new TrayConfig();
        config.Ui.Window ??= new WindowConfig();
        config.Ui.Navigation ??= new NavigationConfig();
        config.Ui.Notifications ??= new NotificationConfig();
        config.Ui.Updates ??= new UpdateConfig();
        config.Devices ??= new List<KnownDevice>();
        config.Firmware.Target = FirmwareTargetCatalog.NormalizeTarget(config.Firmware.Target);
        config.Firmware.VersionTag = string.IsNullOrWhiteSpace(config.Firmware.VersionTag) ? OpenFingerVersion.Version : config.Firmware.VersionTag;
        config.Ui.ThemeMode = UiThemeCatalog.Normalize(config.Ui.ThemeMode);
        config.Ui.Tray.CloseButtonAction = UiCloseActionCatalog.Normalize(config.Ui.Tray.CloseButtonAction);
        config.Ui.Tray.Visibility = UiTrayVisibilityCatalog.Normalize(config.Ui.Tray.Visibility);
        config.Ui.Navigation.LaunchPage = UiPageCatalog.NormalizePage(config.Ui.Navigation.LaunchPage);
        config.Ui.Navigation.LastPage = UiPageCatalog.NormalizePage(config.Ui.Navigation.LastPage);

        SanitizeFiniteNumbers(config);
        ClampConfig(config);
    }

    private static void ClampConfig(OpenFingerAppConfig config)
    {
        config.AdcMax = Math.Clamp(config.AdcMax, 1, 65535);
        config.Runtime.DeviceUdpPort = ClampPort(config.Runtime.DeviceUdpPort, 39001);
        config.Runtime.LocalRuntimeUdpPort = ClampPort(config.Runtime.LocalRuntimeUdpPort, 39003);
        config.Runtime.PublishHz = Math.Clamp(config.Runtime.PublishHz, 1, 240);
        config.Service.RawInputUdpPort = ClampPort(config.Service.RawInputUdpPort, 39004);

        config.Ui.Window.Width = ClampFinite(config.Ui.Window.Width, 1360, 640, 7680);
        config.Ui.Window.Height = ClampFinite(config.Ui.Window.Height, 900, 480, 4320);
        config.Ui.Window.Left = ClampFinite(config.Ui.Window.Left, 100, -32000, 32000);
        config.Ui.Window.Top = ClampFinite(config.Ui.Window.Top, 100, -32000, 32000);

        config.AlgorithmTuning.SensitivityLevel = ClampFinite(config.AlgorithmTuning.SensitivityLevel, 2, 0, 10);
        config.AlgorithmTuning.AntiShakeLevel = ClampFinite(config.AlgorithmTuning.AntiShakeLevel, 2, 0, 10);
        config.AlgorithmTuning.SmoothingAlpha = ClampFinite(config.AlgorithmTuning.SmoothingAlpha, 0.35, 0, 1);
        config.AlgorithmTuning.DeadzonePercent = ClampFinite(config.AlgorithmTuning.DeadzonePercent, 3, 0, 100);
        config.AlgorithmTuning.KalmanQ = ClampFinite(config.AlgorithmTuning.KalmanQ, 0.01, 0, 10);

        ClampHand(config.Hands.Left);
        ClampHand(config.Hands.Right);
        ClampJoystick(config.Joystick.Left);
        ClampJoystick(config.Joystick.Right);
        ClampGestureHand(config.Gestures.Left);
        ClampGestureHand(config.Gestures.Right);
    }

    private static void ClampHand(HandConfig hand)
    {
        foreach (var finger in hand.Fingers.Values)
        {
            finger.SmoothingAlpha = ClampFinite(finger.SmoothingAlpha, 0.35, 0, 1);
            finger.Deadzone = ClampFinite(finger.Deadzone, 0.03, 0, 1);
        }
    }

    private static void ClampJoystick(JoystickHandSettings settings)
    {
        settings.DeadzonePercent = ClampFinite(settings.DeadzonePercent, 12, 0, 100);
    }

    private static void NormalizeGestureHand(GestureHandSettings hand)
    {
        var defaults = GestureHandSettings.CreateDefault().Mappings;
        foreach (var entry in defaults)
        {
            if (!hand.Mappings.TryGetValue(entry.Key, out var binding) || binding is null)
            {
                hand.Mappings[entry.Key] = new GestureBindingConfig();
                continue;
            }

            binding.Calibration ??= new GestureCalibrationConfig();
            NormalizeGestureCalibration(binding.Calibration);
        }

        foreach (var staleKey in hand.Mappings.Keys.Where(key => !defaults.ContainsKey(key)).ToArray())
        {
            hand.Mappings.Remove(staleKey);
        }
    }

    private static void ClampGestureHand(GestureHandSettings hand)
    {
        foreach (var combo in GestureComboCatalog.Definitions)
        {
            if (!hand.Mappings.TryGetValue(combo.Key, out var binding) || binding is null)
            {
                hand.Mappings[combo.Key] = new GestureBindingConfig();
                continue;
            }

            binding.MappedButton = GestureButtonCatalog.Normalize(binding.MappedButton);
            binding.Calibration ??= new GestureCalibrationConfig();
            NormalizeGestureCalibration(binding.Calibration);
            binding.Calibration.OpenScore = ClampFinite(binding.Calibration.OpenScore, 0, 0, 1);
            binding.Calibration.PinchScore = ClampFinite(binding.Calibration.PinchScore, 0.75, 0, 1);
            binding.Calibration.TriggerThreshold = ClampFinite(binding.Calibration.TriggerThreshold, 0.62, 0, 1);
            binding.Calibration.ReleaseThreshold = ClampFinite(binding.Calibration.ReleaseThreshold, 0.42, 0, 1);
            binding.Calibration.ConfidenceThreshold = ClampFinite(binding.Calibration.ConfidenceThreshold, 0.54, 0, 1);
            binding.Calibration.CalibrationRepeats = Math.Clamp(binding.Calibration.CalibrationRepeats, 1, 10);
            binding.Calibration.ThumbOpenMean = ClampFinite(binding.Calibration.ThumbOpenMean, 0, 0, 1);
            binding.Calibration.ThumbPinchMean = ClampFinite(binding.Calibration.ThumbPinchMean, 0.75, 0, 1);
            binding.Calibration.TargetOpenMean = ClampFinite(binding.Calibration.TargetOpenMean, 0, 0, 1);
            binding.Calibration.TargetPinchMean = ClampFinite(binding.Calibration.TargetPinchMean, 0.75, 0, 1);
            binding.Calibration.PrimaryOpenScore = ClampFinite(binding.Calibration.PrimaryOpenScore, 0, 0, 1);
            binding.Calibration.PrimaryPinchScore = ClampFinite(binding.Calibration.PrimaryPinchScore, 0.75, 0, 1);
            for (var index = 0; index < 3; index++)
            {
                binding.Calibration.SupportOpenMeans[index] = ClampFinite(binding.Calibration.SupportOpenMeans[index], 0, 0, 1);
                binding.Calibration.SupportPinchMeans[index] = ClampFinite(binding.Calibration.SupportPinchMeans[index], 0, 0, 1);
                binding.Calibration.SupportPinchStdDevs[index] = ClampFinite(binding.Calibration.SupportPinchStdDevs[index], 0.08, 0, 1);
                binding.Calibration.SupportInfluences[index] = ClampFinite(binding.Calibration.SupportInfluences[index], 0.2, 0, 1);
            }
        }
    }

    private static void NormalizeGestureCalibration(GestureCalibrationConfig calibration)
    {
        calibration.SupportOpenMeans = EnsureGestureVector(calibration.SupportOpenMeans, 3, 0.0);
        calibration.SupportPinchMeans = EnsureGestureVector(calibration.SupportPinchMeans, 3, 0.0);
        calibration.SupportPinchStdDevs = EnsureGestureVector(calibration.SupportPinchStdDevs, 3, 0.08);
        calibration.SupportInfluences = EnsureGestureVector(calibration.SupportInfluences, 3, 0.2);
    }

    private static double[] EnsureGestureVector(double[]? values, int size, double fallback)
    {
        var normalized = new double[size];
        if (values is not null)
        {
            for (var index = 0; index < Math.Min(values.Length, size); index++)
            {
                normalized[index] = double.IsFinite(values[index]) ? values[index] : fallback;
            }
        }

        for (var index = 0; index < size; index++)
        {
            if (!double.IsFinite(normalized[index]) || normalized[index] == 0 && fallback != 0 && (values is null || index >= values.Length))
            {
                normalized[index] = fallback;
            }
        }

        return normalized;
    }

    private static int ClampPort(int value, int fallback)
    {
        return value is >= 1 and <= 65535 ? value : fallback;
    }

    private static double ClampFinite(double value, double fallback, double min, double max)
    {
        return double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static void SanitizeFiniteNumbers(OpenFingerAppConfig config)
    {
        // Keep this explicit rather than relying on JsonNumberHandling.AllowNamedFloatingPointLiterals:
        // JSON config should stay valid and portable, never containing NaN or Infinity tokens.
        config.Ui.Window.Left = double.IsFinite(config.Ui.Window.Left) ? config.Ui.Window.Left : 100;
        config.Ui.Window.Top = double.IsFinite(config.Ui.Window.Top) ? config.Ui.Window.Top : 100;
        config.Ui.Window.Width = double.IsFinite(config.Ui.Window.Width) ? config.Ui.Window.Width : 1180;
        config.Ui.Window.Height = double.IsFinite(config.Ui.Window.Height) ? config.Ui.Window.Height : 760;
        config.AlgorithmTuning.SensitivityLevel = double.IsFinite(config.AlgorithmTuning.SensitivityLevel) ? config.AlgorithmTuning.SensitivityLevel : 2;
        config.AlgorithmTuning.AntiShakeLevel = double.IsFinite(config.AlgorithmTuning.AntiShakeLevel) ? config.AlgorithmTuning.AntiShakeLevel : 2;
        config.AlgorithmTuning.SmoothingAlpha = double.IsFinite(config.AlgorithmTuning.SmoothingAlpha) ? config.AlgorithmTuning.SmoothingAlpha : 0.35;
        config.AlgorithmTuning.DeadzonePercent = double.IsFinite(config.AlgorithmTuning.DeadzonePercent) ? config.AlgorithmTuning.DeadzonePercent : 3;
        config.AlgorithmTuning.KalmanQ = double.IsFinite(config.AlgorithmTuning.KalmanQ) ? config.AlgorithmTuning.KalmanQ : 0.01;
    }
}

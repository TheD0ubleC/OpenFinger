namespace OpenFinger.Control;


public static class UiThemeCatalog
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    public static IReadOnlyList<FirmwareModeOption> Options { get; } = new[]
    {
        new FirmwareModeOption { Value = Light, Label = "浅色" },
        new FirmwareModeOption { Value = Dark, Label = "深色" },
        new FirmwareModeOption { Value = System, Label = "跟随系统" }
    };

    public static string Normalize(string? value) => Options.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
        ? Options.First(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)).Value
        : Light;

    public static string GetLabel(string? value) => Options.First(item => item.Value == Normalize(value)).Label;
}

public static class UiCloseActionCatalog
{
    public const string Ask = "ask";
    public const string Tray = "tray";
    public const string Close = "close";

    public static IReadOnlyList<FirmwareModeOption> Options { get; } = new[]
    {
        new FirmwareModeOption { Value = Ask, Label = "每次询问" },
        new FirmwareModeOption { Value = Tray, Label = "最小化到托盘" },
        new FirmwareModeOption { Value = Close, Label = "直接退出" }
    };

    public static string Normalize(string? value) => Options.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
        ? Options.First(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)).Value
        : Ask;
}

public static class UiTrayVisibilityCatalog
{
    public const string Always = "always";
    public const string BackgroundOnly = "background_only";
    public const string Never = "never";

    public static IReadOnlyList<FirmwareModeOption> Options { get; } = new[]
    {
        new FirmwareModeOption { Value = Always, Label = "始终显示" },
        new FirmwareModeOption { Value = BackgroundOnly, Label = "后台运行时显示" },
        new FirmwareModeOption { Value = Never, Label = "不显示" }
    };

    public static string Normalize(string? value) => Options.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
        ? Options.First(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)).Value
        : BackgroundOnly;
}

public static class UiPageCatalog
{
    public const string Home = "home";
    public const string Status = "status";
    public const string Devices = "devices";
    public const string Firmware = "firmware";
    public const string Calibration = "calibration";
    public const string SteamVr = "steamvr";
    public const string Settings = "settings";
    public const string About = "about";

    public static IReadOnlyList<FirmwareModeOption> Options { get; } = new[]
    {
        new FirmwareModeOption { Value = Home, Label = "开始" },
        new FirmwareModeOption { Value = Status, Label = "状态" },
        new FirmwareModeOption { Value = Devices, Label = "设备" },
        new FirmwareModeOption { Value = Firmware, Label = "固件" },
        new FirmwareModeOption { Value = Calibration, Label = "校准" },
        new FirmwareModeOption { Value = SteamVr, Label = "SteamVR" },
        new FirmwareModeOption { Value = Settings, Label = "设置" },
        new FirmwareModeOption { Value = About, Label = "关于" }
    };

    public static string NormalizePage(string? value) => Options.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
        ? Options.First(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)).Value
        : Home;

    public static string GetLabel(string? value) => Options.First(item => item.Value == NormalizePage(value)).Label;
}

public static class JoystickSteamVrCatalog
{
    public const string AxisJoystick = "joystick";
    public const string AxisTrackpad = "trackpad";
    public const string AxisDisabled = "disabled";
    public const string ClickJoystick = "joystick_click";
    public const string ClickTrackpad = "trackpad_click";
    public const string ClickDisabled = "disabled";

    public static IReadOnlyList<FirmwareModeOption> AxisModeOptions { get; } = new[]
    {
        new FirmwareModeOption { Value = AxisJoystick, Label = "映射为摇杆" },
        new FirmwareModeOption { Value = AxisTrackpad, Label = "映射为触控板" },
        new FirmwareModeOption { Value = AxisDisabled, Label = "不转发轴" }
    };

    public static IReadOnlyList<FirmwareModeOption> ClickActionOptions { get; } = new[]
    {
        new FirmwareModeOption { Value = ClickJoystick, Label = "映射为摇杆按下" },
        new FirmwareModeOption { Value = ClickTrackpad, Label = "映射为触控板按下" },
        new FirmwareModeOption { Value = ClickDisabled, Label = "不转发按键" }
    };

    public static bool IsValidAxisMode(string? value) => AxisModeOptions.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));
    public static bool IsValidClickAction(string? value) => ClickActionOptions.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));
}

public static class JoystickOrientationCatalog
{
    public const string Normal = "normal";
    public const string Rot90 = "rot90";
    public const string Rot180 = "rot180";
    public const string Rot270 = "rot270";
    public const string FlipX = "flip_x";
    public const string FlipY = "flip_y";

    public static IReadOnlyList<FirmwareModeOption> Options { get; } = new[]
    {
        new FirmwareModeOption { Value = Normal, Label = "正常" },
        new FirmwareModeOption { Value = Rot90, Label = "顺时针 90°" },
        new FirmwareModeOption { Value = Rot180, Label = "旋转 180°" },
        new FirmwareModeOption { Value = Rot270, Label = "逆时针 90°" },
        new FirmwareModeOption { Value = FlipX, Label = "水平翻转" },
        new FirmwareModeOption { Value = FlipY, Label = "垂直翻转" }
    };

    public static bool IsValid(string? value) => Options.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));

    public static (double X, double Y) Apply(string? orientation, double x, double y) => Normalize(orientation) switch
    {
        Rot90 => (y, -x),
        Rot180 => (-x, -y),
        Rot270 => (-y, x),
        FlipX => (-x, y),
        FlipY => (x, -y),
        _ => (x, y)
    };

    private static string Normalize(string? value) => IsValid(value)
        ? Options.First(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)).Value
        : Normal;
}

public sealed class FirmwareTargetDefinition
{
    public string Value { get; init; } = FirmwareTargetCatalog.Esp32C3;
    public string Label { get; init; } = string.Empty;
    public IReadOnlyList<int> AdcPins { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> TrackingSwitchPins { get; init; } = Array.Empty<int>();
    public int DefaultReportRateHz { get; init; } = 30;
    public string BootHint { get; init; } = string.Empty;
}

public static class FirmwareTargetCatalog
{
    public const string Esp32C3 = "esp32c3";
    public const string Esp32S3 = "esp32s3";

    private static readonly FirmwareTargetDefinition C3 = new()
    {
        Value = Esp32C3,
        Label = "ESP32-C3 SuperMini",
        AdcPins = new[] { 0, 1, 2, 3, 4 },
        TrackingSwitchPins = new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
        DefaultReportRateHz = 30,
        BootHint = "如果设备没有自动进入下载模式，请按住 BOOT，轻按一次 RESET，再松开 BOOT。"
    };

    private static readonly FirmwareTargetDefinition S3 = new()
    {
        Value = Esp32S3,
        Label = "ESP32-S3 SuperMini",
        AdcPins = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
        TrackingSwitchPins = new[] { -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 48 },
        DefaultReportRateHz = 30,
        BootHint = "ESP32-S3 第一次刷写时可能需要手动进入下载模式：按住 BOOT，轻按 RESET，再松开 BOOT。"
    };

    public static IReadOnlyList<FirmwareTargetDefinition> Definitions { get; } = new[] { C3, S3 };

    public static IReadOnlyList<FirmwareModeOption> TargetOptions { get; } = Definitions
        .Select(item => new FirmwareModeOption { Value = item.Value, Label = item.Label })
        .ToArray();

    public static IReadOnlyList<FirmwareModeOption> SourceOptions { get; } = new[]
    {
        new FirmwareModeOption { Value = "bundled", Label = "内置固件包" },
        new FirmwareModeOption { Value = "external", Label = "本地固件包" },
        new FirmwareModeOption { Value = "online", Label = "在线固件目录" }
    };

    public static IReadOnlyList<FirmwareModeOption> ReportRateOptions { get; } = new[] { 30, 60, 90, 120 }
        .Select(rate => new FirmwareModeOption { Value = rate.ToString(), Label = $"{rate} Hz" })
        .ToArray();

    public static IReadOnlyList<FirmwareModeOption> TrackingModeOptions { get; } = new[]
    {
        new FirmwareModeOption { Value = "disabled", Label = "不使用追踪开关" },
        new FirmwareModeOption { Value = "active_high_pulldown", Label = "高电平为启用 / 下拉" },
        new FirmwareModeOption { Value = "active_low_pullup", Label = "低电平为启用 / 上拉" }
    };

    public static FirmwareTargetDefinition Get(string? target)
    {
        var normalized = NormalizeTarget(target);
        return Definitions.First(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Esp32C3;
        }

        foreach (var definition in Definitions)
        {
            if (string.Equals(target, definition.Value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, definition.Label, StringComparison.OrdinalIgnoreCase))
            {
                return definition.Value;
            }
        }

        if (string.Equals(target, "esp32-s3", StringComparison.OrdinalIgnoreCase))
        {
            return Esp32S3;
        }
        if (string.Equals(target, "esp32-c3", StringComparison.OrdinalIgnoreCase))
        {
            return Esp32C3;
        }
        return Esp32C3;
    }

    public static FirmwareConfig CreateDefaultConfig(string? target)
    {
        var definition = Get(target);
        var pins = definition.AdcPins.ToArray();
        return new FirmwareConfig
        {
            Target = definition.Value,
            VersionTag = OpenFingerVersion.Version,
            ReportRateHz = definition.DefaultReportRateHz,
            ThumbPin = pins.ElementAtOrDefault(0),
            IndexPin = pins.ElementAtOrDefault(1),
            MiddlePin = pins.ElementAtOrDefault(2),
            RingPin = pins.ElementAtOrDefault(3),
            PinkyPin = pins.ElementAtOrDefault(4),
            TrackingSwitchPin = -1,
            TrackingSwitchMode = "disabled",
            JoystickVrxPin = -1,
            JoystickVryPin = -1,
            JoystickSwPin = -1,
            BatteryAdcPin = -1,
            BatteryChargePin = -1
        };
    }

    public static IReadOnlyList<FirmwarePinOption> CreateAdcPinOptions(string? target) => Get(target).AdcPins
        .Select(pin => new FirmwarePinOption { Value = pin, Label = $"GPIO{pin}" })
        .ToArray();

    public static IReadOnlyList<FirmwarePinOption> CreateSwitchPinOptions(string? target) => Get(target).TrackingSwitchPins
        .Select(pin => new FirmwarePinOption { Value = pin, Label = pin < 0 ? "不使用" : $"GPIO{pin}" })
        .ToArray();

    public static bool IsValidAdcPin(string? target, int pin) => Get(target).AdcPins.Contains(pin);
    public static bool IsValidOptionalAdcPin(string? target, int pin) => pin < 0 || IsValidAdcPin(target, pin);
    public static bool IsValidTrackingSwitchPin(string? target, int pin) => Get(target).TrackingSwitchPins.Contains(pin);
    public static bool IsValidOptionalSwitchPin(string? target, int pin) => pin < 0 || Get(target).TrackingSwitchPins.Contains(pin);
}

public static class Esp32C3PinCatalog
{
    public static IReadOnlyList<FirmwareModeOption> TrackingSwitchModes => FirmwareTargetCatalog.TrackingModeOptions;

    public static bool IsValidTrackingSwitchMode(string? value)
    {
        return TrackingSwitchModes.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));
    }
}

public static class RuntimeJoystickActionCatalog
{
    public static int AxisModeToId(string? value) => value switch
    {
        JoystickSteamVrCatalog.AxisTrackpad => 2,
        JoystickSteamVrCatalog.AxisDisabled => 0,
        _ => 1
    };

    public static int ClickActionToId(string? value) => value switch
    {
        JoystickSteamVrCatalog.ClickTrackpad => 2,
        JoystickSteamVrCatalog.ClickDisabled => 0,
        _ => 1
    };
}

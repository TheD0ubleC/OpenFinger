using System.Windows.Media;

namespace OpenFinger.Control;

public enum UiTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger
}

public sealed record StatusBadge(string Text, UiTone Tone);

public sealed class DeviceReadinessState
{
    public string Title { get; init; } = string.Empty;
    public StatusBadge Connection { get; init; } = new("未连接", UiTone.Neutral);
    public StatusBadge Firmware { get; init; } = new("未识别", UiTone.Neutral);
    public StatusBadge Calibration { get; init; } = new("待校准", UiTone.Warning);
    public StatusBadge Usage { get; init; } = new("未就绪", UiTone.Neutral);
    public string Detail { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
}

public sealed class HomeDashboardState
{
    public StatusBadge Overall { get; init; } = new("等待设备接入", UiTone.Neutral);
    public string NextActionTitle { get; init; } = string.Empty;
    public string NextActionDescription { get; init; } = string.Empty;
    public string PrimaryActionKey { get; init; } = string.Empty;
    public string PrimaryActionLabel { get; init; } = string.Empty;
    public string SecondaryActionKey { get; init; } = string.Empty;
    public string SecondaryActionLabel { get; init; } = string.Empty;
    public DeviceReadinessState Left { get; init; } = new() { Title = "左手" };
    public DeviceReadinessState Right { get; init; } = new() { Title = "右手" };
}

public sealed class DiagnosticsDashboardState
{
    public StatusBadge OpenFingerKit { get; init; } = new("等待检测", UiTone.Neutral);
    public StatusBadge SteamVr { get; init; } = new("等待检测", UiTone.Neutral);
    public StatusBadge DeviceComm { get; init; } = new("等待检测", UiTone.Neutral);
    public StatusBadge Driver { get; init; } = new("等待检测", UiTone.Neutral);
    public string DriverActionLabel { get; init; } = "安装驱动";
    public bool DriverInstalled { get; init; }
    public string OpenFingerKitDetail { get; init; } = string.Empty;
    public string SteamVrDetail { get; init; } = string.Empty;
    public string DeviceCommDetail { get; init; } = string.Empty;
    public string DriverDetail { get; init; } = string.Empty;
    public string FriendlyLog { get; init; } = string.Empty;
    public string RawLog { get; init; } = string.Empty;
    public bool ShowAdvanced { get; init; }
}

public sealed class FirmwareDashboardState
{
    public string SelectedDeviceTitle { get; init; } = "未选择设备";
    public string SelectedDeviceDetail { get; init; } = "选择串口后即可开始刷写。";
    public string SourceStatus { get; init; } = "等待检查固件包";
    public string CurrentFirmwareText { get; init; } = "未识别";
    public string TargetFirmwareText { get; init; } = "未选择";
    public string RecommendationText { get; init; } = "等待匹配";
    public string BootHint { get; init; } = string.Empty;
    public string ProgressText { get; init; } = "等待开始";
    public bool Busy { get; init; }
    public bool ShowAdvanced { get; init; }
}

public sealed class SettingsDashboardState
{
    public bool ShowAdvanced { get; init; }
    public string ThemeMode { get; init; } = UiThemeCatalog.Light;
    public string CloseButtonAction { get; init; } = UiCloseActionCatalog.Ask;
    public string TrayVisibility { get; init; } = UiTrayVisibilityCatalog.BackgroundOnly;
    public bool ReduceLoadWhenHidden { get; init; } = true;
    public bool RememberWindowBounds { get; init; } = true;
    public bool RememberLastPage { get; init; }
    public string LaunchPage { get; init; } = UiPageCatalog.Home;
    public bool EnableWindowsStartup { get; init; }
    public bool StartHiddenOnWindowsStartup { get; init; }
    public bool EnableTrayNotifications { get; init; } = true;
    public bool EnableDeviceNotifications { get; init; } = true;
    public bool EnableFlashNotifications { get; init; } = true;
    public bool EnableDriverNotifications { get; init; } = true;
    public string ConfigPath { get; init; } = string.Empty;
}

public static class UiTonePalette
{
    public static Brush Accent(UiTone tone) => tone switch
    {
        UiTone.Success => ThemeManager.GetBrush("BrushSuccessAccent", "#10B981"),
        UiTone.Info => ThemeManager.GetBrush("BrushInfoAccent", "#3B82F6"),
        UiTone.Warning => ThemeManager.GetBrush("BrushWarningText", "#F59E0B"),
        UiTone.Danger => ThemeManager.GetBrush("BrushDangerText", "#EF4444"),
        _ => ThemeManager.GetBrush("BrushNeutralAccent", "#94A3B8")
    };

    public static Brush Edge(UiTone tone) => tone switch
    {
        UiTone.Success => ThemeManager.GetBrush("BrushSuccessBorder", "#A7F3D0"),
        UiTone.Info => ThemeManager.GetBrush("BrushInfoBorder", "#DBEAFE"),
        UiTone.Warning => ThemeManager.GetBrush("BrushWarningBorder", "#FDE68A"),
        UiTone.Danger => ThemeManager.GetBrush("BrushDangerBorder", "#FECACA"),
        _ => ThemeManager.GetBrush("BrushBorder", "#DDE4EE")
    };

    public static Brush Card(UiTone tone) => tone switch
    {
        UiTone.Success => ThemeManager.GetBrush("BrushSuccessBg", "#ECFDF5"),
        UiTone.Info => ThemeManager.GetBrush("BrushInfoBg", "#EFF6FF"),
        UiTone.Warning => ThemeManager.GetBrush("BrushWarningBg", "#FFFBEB"),
        UiTone.Danger => ThemeManager.GetBrush("BrushDangerBg", "#FEF2F2"),
        _ => ThemeManager.GetBrush("BrushPanelAlt", "#F8FAFC")
    };

    public static Brush Background(UiTone tone) => tone switch
    {
        UiTone.Success => ThemeManager.GetBrush("BrushSuccessBg", "#ECFDF5"),
        UiTone.Info => ThemeManager.GetBrush("BrushInfoBg", "#EFF6FF"),
        UiTone.Warning => ThemeManager.GetBrush("BrushWarningBg", "#FFFBEB"),
        UiTone.Danger => ThemeManager.GetBrush("BrushDangerBg", "#FEF2F2"),
        _ => ThemeManager.GetBrush("BrushMutedPanel", "#F1F5F9")
    };

    public static Brush Text(UiTone tone) => tone switch
    {
        UiTone.Success => ThemeManager.GetBrush("BrushSuccessText", "#047857"),
        UiTone.Info => ThemeManager.GetBrush("BrushInfoText", "#2563EB"),
        UiTone.Warning => ThemeManager.GetBrush("BrushWarningText", "#D97706"),
        UiTone.Danger => ThemeManager.GetBrush("BrushDangerText", "#DC2626"),
        _ => ThemeManager.GetBrush("BrushNeutralText", "#64748B")
    };
}

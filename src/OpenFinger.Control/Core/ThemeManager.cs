using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace OpenFinger.Control;

public enum OpenFingerResolvedTheme
{
    Light,
    Dark
}

public static class ThemeManager
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const int DwmwaUseImmersiveDarkMode = 20;

    private sealed record ThemePalette(
        string Bg,
        string Panel,
        string PanelAlt,
        string Border,
        string Text,
        string SubText,
        string Primary,
        string PrimaryHover,
        string PrimaryPressed,
        string Hover,
        string ControlPressed,
        string PrimarySoft,
        string ActiveBorder,
        string Input,
        string InputReadonly,
        string Track,
        string MutedPanel,
        string IconTile,
        string CodeBg,
        string CodeText,
        string WarningBg,
        string WarningBorder,
        string WarningText,
        string DangerBg,
        string DangerBorder,
        string DangerText,
        string InfoBg,
        string InfoBorder,
        string InfoText,
        string InfoAccent,
        string SuccessBg,
        string SuccessBorder,
        string SuccessText,
        string SuccessAccent,
        string NeutralAccent,
        string NeutralText,
        string SliderThumbHover,
        string SliderThumbPressed,
        string Shadow);

    private static readonly ThemePalette LightPalette = new(
        Bg: "#F5F7FA",
        Panel: "#FFFFFF",
        PanelAlt: "#F7F9FC",
        Border: "#DDE4EE",
        Text: "#0F172A",
        SubText: "#5F6B7C",
        Primary: "#2C63D6",
        PrimaryHover: "#255BC8",
        PrimaryPressed: "#214EAD",
        Hover: "#EEF3F8",
        ControlPressed: "#E8EDF5",
        PrimarySoft: "#F2F6FF",
        ActiveBorder: "#D7E3FB",
        Input: "#FFFFFF",
        InputReadonly: "#F8FAFC",
        Track: "#E2E8F0",
        MutedPanel: "#F1F5F9",
        IconTile: "#FFFFFF",
        CodeBg: "#0F172A",
        CodeText: "#E2E8F0",
        WarningBg: "#FFFBEB",
        WarningBorder: "#FDE68A",
        WarningText: "#B45309",
        DangerBg: "#FEF2F2",
        DangerBorder: "#FECACA",
        DangerText: "#B91C1C",
        InfoBg: "#EFF6FF",
        InfoBorder: "#DBEAFE",
        InfoText: "#1D4ED8",
        InfoAccent: "#3B82F6",
        SuccessBg: "#ECFDF5",
        SuccessBorder: "#A7F3D0",
        SuccessText: "#047857",
        SuccessAccent: "#10B981",
        NeutralAccent: "#94A3B8",
        NeutralText: "#64748B",
        SliderThumbHover: "#F2F6FF",
        SliderThumbPressed: "#E7EEFC",
        Shadow: "#0F172A");

    private static readonly ThemePalette DarkPalette = new(
        Bg: "#121212",
        Panel: "#1A1A1A",
        PanelAlt: "#202020",
        Border: "#343434",
        Text: "#F4F4F5",
        SubText: "#B7B7BC",
        Primary: "#7EA6FF",
        PrimaryHover: "#94B8FF",
        PrimaryPressed: "#6F95EA",
        Hover: "#242424",
        ControlPressed: "#2B2B2B",
        PrimarySoft: "#252525",
        ActiveBorder: "#4F74BA",
        Input: "#161616",
        InputReadonly: "#1E1E1E",
        Track: "#303030",
        MutedPanel: "#222222",
        IconTile: "#252525",
        CodeBg: "#161616",
        CodeText: "#EEEEEE",
        WarningBg: "#201B10",
        WarningBorder: "#5A461D",
        WarningText: "#F5C15C",
        DangerBg: "#241414",
        DangerBorder: "#5A2A2A",
        DangerText: "#FF9A9A",
        InfoBg: "#1B1D22",
        InfoBorder: "#3A4150",
        InfoText: "#A6C1FF",
        InfoAccent: "#7EA6FF",
        SuccessBg: "#132018",
        SuccessBorder: "#2F5A3E",
        SuccessText: "#7EE2A8",
        SuccessAccent: "#34D399",
        NeutralAccent: "#9CA3AF",
        NeutralText: "#C4C4C7",
        SliderThumbHover: "#2B2B2B",
        SliderThumbPressed: "#333333",
        Shadow: "#000000");

    public static OpenFingerResolvedTheme ApplyThemeMode(string? mode)
    {
        var resolved = Resolve(mode);
        CurrentResolvedTheme = resolved;
        ApplyPalette(resolved == OpenFingerResolvedTheme.Dark ? DarkPalette : LightPalette);
        return resolved;
    }

    public static OpenFingerResolvedTheme Resolve(string? mode)
    {
        var normalized = UiThemeCatalog.Normalize(mode);
        if (normalized == UiThemeCatalog.System)
        {
            return IsSystemUsingDarkApps() ? OpenFingerResolvedTheme.Dark : OpenFingerResolvedTheme.Light;
        }

        return normalized == UiThemeCatalog.Dark ? OpenFingerResolvedTheme.Dark : OpenFingerResolvedTheme.Light;
    }


    public static OpenFingerResolvedTheme CurrentResolvedTheme { get; private set; } = OpenFingerResolvedTheme.Light;

    public static bool IsDark => CurrentResolvedTheme == OpenFingerResolvedTheme.Dark;

    public static Brush GetBrush(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        var fallbackBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(fallback)!;
        fallbackBrush.Freeze();
        return fallbackBrush;
    }

    public static string DescribeResolved(string? mode)
    {
        var normalized = UiThemeCatalog.Normalize(mode);
        if (normalized != UiThemeCatalog.System)
        {
            return UiThemeCatalog.GetLabel(normalized);
        }

        return Resolve(normalized) == OpenFingerResolvedTheme.Dark
            ? "跟随系统（当前深色）"
            : "跟随系统（当前浅色）";
    }

    public static void ApplyWindowChrome(Window window, string? mode)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var useDark = Resolve(mode) == OpenFingerResolvedTheme.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        }
        catch
        {
            // Cosmetic only. Ignore older Windows / unsupported desktop composition states.
        }
    }

    private static void ApplyPalette(ThemePalette palette)
    {
        SetColor("ColorBg", palette.Bg);
        SetColor("ColorPanel", palette.Panel);
        SetColor("ColorPanelAlt", palette.PanelAlt);
        SetColor("ColorBorder", palette.Border);
        SetColor("ColorText", palette.Text);
        SetColor("ColorSubText", palette.SubText);
        SetColor("ColorPrimary", palette.Primary);
        SetColor("ColorPrimaryHover", palette.PrimaryHover);
        SetColor("ColorPrimaryPressed", palette.PrimaryPressed);
        SetColor("ColorHover", palette.Hover);
        SetColor("ColorControlPressed", palette.ControlPressed);
        SetColor("ColorPrimarySoft", palette.PrimarySoft);
        SetColor("ColorActiveBorder", palette.ActiveBorder);
        SetColor("ColorInput", palette.Input);
        SetColor("ColorInputReadonly", palette.InputReadonly);
        SetColor("ColorTrack", palette.Track);
        SetColor("ColorMutedPanel", palette.MutedPanel);
        SetColor("ColorIconTile", palette.IconTile);
        SetColor("ColorCodeBg", palette.CodeBg);
        SetColor("ColorCodeText", palette.CodeText);
        SetColor("ColorWarningBg", palette.WarningBg);
        SetColor("ColorWarningBorder", palette.WarningBorder);
        SetColor("ColorWarningText", palette.WarningText);
        SetColor("ColorDangerBg", palette.DangerBg);
        SetColor("ColorDangerBorder", palette.DangerBorder);
        SetColor("ColorDangerText", palette.DangerText);
        SetColor("ColorInfoBg", palette.InfoBg);
        SetColor("ColorInfoBorder", palette.InfoBorder);
        SetColor("ColorInfoText", palette.InfoText);
        SetColor("ColorInfoAccent", palette.InfoAccent);
        SetColor("ColorSuccessBg", palette.SuccessBg);
        SetColor("ColorSuccessBorder", palette.SuccessBorder);
        SetColor("ColorSuccessText", palette.SuccessText);
        SetColor("ColorSuccessAccent", palette.SuccessAccent);
        SetColor("ColorNeutralAccent", palette.NeutralAccent);
        SetColor("ColorNeutralText", palette.NeutralText);
        SetColor("ColorSliderThumbHover", palette.SliderThumbHover);
        SetColor("ColorSliderThumbPressed", palette.SliderThumbPressed);
        SetColor("ColorShadow", palette.Shadow);

        SetBrush("BrushBg", palette.Bg);
        SetBrush("BrushPanel", palette.Panel);
        SetBrush("BrushPanelAlt", palette.PanelAlt);
        SetBrush("BrushBorder", palette.Border);
        SetBrush("BrushText", palette.Text);
        SetBrush("BrushSubText", palette.SubText);
        SetBrush("BrushPrimary", palette.Primary);
        SetBrush("BrushPrimaryHover", palette.PrimaryHover);
        SetBrush("BrushPrimaryPressed", palette.PrimaryPressed);
        SetBrush("BrushHover", palette.Hover);
        SetBrush("BrushControlPressed", palette.ControlPressed);
        SetBrush("BrushPrimarySoft", palette.PrimarySoft);
        SetBrush("BrushActiveBorder", palette.ActiveBorder);
        SetBrush("BrushInput", palette.Input);
        SetBrush("BrushInputReadonly", palette.InputReadonly);
        SetBrush("BrushTrack", palette.Track);
        SetBrush("BrushMutedPanel", palette.MutedPanel);
        SetBrush("BrushIconTile", palette.IconTile);
        SetBrush("BrushCodeBg", palette.CodeBg);
        SetBrush("BrushCodeText", palette.CodeText);
        SetBrush("BrushWarningBg", palette.WarningBg);
        SetBrush("BrushWarningBorder", palette.WarningBorder);
        SetBrush("BrushWarningText", palette.WarningText);
        SetBrush("BrushDangerBg", palette.DangerBg);
        SetBrush("BrushDangerBorder", palette.DangerBorder);
        SetBrush("BrushDangerText", palette.DangerText);
        SetBrush("BrushInfoBg", palette.InfoBg);
        SetBrush("BrushInfoBorder", palette.InfoBorder);
        SetBrush("BrushInfoText", palette.InfoText);
        SetBrush("BrushInfoAccent", palette.InfoAccent);
        SetBrush("BrushSuccessBg", palette.SuccessBg);
        SetBrush("BrushSuccessBorder", palette.SuccessBorder);
        SetBrush("BrushSuccessText", palette.SuccessText);
        SetBrush("BrushSuccessAccent", palette.SuccessAccent);
        SetBrush("BrushNeutralAccent", palette.NeutralAccent);
        SetBrush("BrushNeutralText", palette.NeutralText);
        SetBrush("BrushSliderThumbHover", palette.SliderThumbHover);
        SetBrush("BrushSliderThumbPressed", palette.SliderThumbPressed);
    }

    private static bool IsSystemUsingDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var raw = key?.GetValue(AppsUseLightThemeValue);
            return raw is int intValue ? intValue == 0 : raw is long longValue && longValue == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetColor(string key, string value)
    {
        if (Application.Current?.Resources is null)
        {
            return;
        }

        var color = (MediaColor)MediaColorConverter.ConvertFromString(value)!;
        Application.Current.Resources[key] = color;
    }

    private static void SetBrush(string key, string value)
    {
        if (Application.Current?.Resources is null)
        {
            return;
        }

        var color = (MediaColor)MediaColorConverter.ConvertFromString(value)!;

        // Do not mutate an existing SolidColorBrush here. Brushes created from XAML
        // resources may be frozen/read-only by WPF, and changing brush.Color can
        // throw InvalidOperationException during startup or theme switching.
        // Replacing the resource keeps DynamicResource bindings refreshable and
        // avoids touching frozen brush instances.
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}

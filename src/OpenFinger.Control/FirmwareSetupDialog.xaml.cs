using System.Collections;
using System.Linq;

namespace OpenFinger.Control;

public partial class FirmwareSetupDialog : Window
{
    private readonly FirmwareConfig _defaultConfig;
    private readonly string _target;
    private readonly Dictionary<string, FingerField> _fingerFields;

    private sealed class FingerField
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required ComboBox PinComboBox { get; init; }
        public required ComboBox ShareComboBox { get; init; }
    }

    private sealed class ShareOption
    {
        public string Value { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
    }

    public FirmwareSetupDialog(MainVm vm, string packageLabel, string bootHint)
    {
        InitializeComponent();

        _target = FirmwareTargetCatalog.NormalizeTarget(vm.FirmwareTarget);
        _defaultConfig = FirmwareTargetCatalog.CreateDefaultConfig(_target);
        _fingerFields = CreateFingerFieldMap();

        TargetTextBlock.Text = FirmwareTargetCatalog.Get(_target).Label;
        PackageTextBlock.Text = string.IsNullOrWhiteSpace(packageLabel) ? "未选择固件" : packageLabel;
        PortTextBlock.Text = string.IsNullOrWhiteSpace(vm.FirmwarePort) ? "未选择串口" : vm.FirmwarePort;
        BootHintTextBlock.Text = string.IsNullOrWhiteSpace(bootHint)
            ? "如果设备没有自动进入下载模式，请按住 BOOT，轻按一次 RESET，再松开 BOOT。"
            : bootHint;
        BoardAdcHintTextBlock.Text = BuildBoardAdcHint(_target);

        var adcOptions = vm.FirmwareAdcPinOptions.ToList();
        var optionalAdcOptions = vm.FirmwareOptionalAdcPinOptions.ToList();
        var switchOptions = vm.FirmwareSwitchPinOptions.ToList();
        var optionalSwitchOptions = vm.FirmwareOptionalSwitchPinOptions.ToList();
        var trackingModes = vm.FirmwareTrackingSwitchModes.ToList();

        ConfigureFingerCombos(vm, adcOptions);
        ConfigurePinCombo(TrackingSwitchComboBox, switchOptions, vm.FirmwareTrackingSwitchPin);
        ConfigurePinCombo(JoystickVrxComboBox, optionalAdcOptions, vm.FirmwareJoystickVrxPin);
        ConfigurePinCombo(JoystickVryComboBox, optionalAdcOptions, vm.FirmwareJoystickVryPin);
        ConfigurePinCombo(JoystickSwComboBox, optionalSwitchOptions, vm.FirmwareJoystickSwPin);
        ConfigurePinCombo(BatteryAdcComboBox, optionalAdcOptions, vm.FirmwareBatteryAdcPin);
        ConfigurePinCombo(BatteryChargeComboBox, optionalSwitchOptions, vm.FirmwareBatteryChargePin);

        TrackingModeComboBox.ItemsSource = trackingModes;
        TrackingModeComboBox.DisplayMemberPath = nameof(FirmwareModeOption.Label);
        TrackingModeComboBox.SelectedValuePath = nameof(FirmwareModeOption.Value);
        TrackingModeComboBox.SelectedValue = string.IsNullOrWhiteSpace(vm.FirmwareTrackingSwitchMode)
            ? "disabled"
            : vm.FirmwareTrackingSwitchMode;

        AttachValidationHandlers();
        RefreshShareStates();
        RefreshValidationState();
    }

    public int ThumbPin => GetResolvedFingerPin("thumb");
    public int IndexPin => GetResolvedFingerPin("index");
    public int MiddlePin => GetResolvedFingerPin("middle");
    public int RingPin => GetResolvedFingerPin("ring");
    public int PinkyPin => GetResolvedFingerPin("pinky");
    public int TrackingSwitchPin => GetSelectedPin(TrackingSwitchComboBox);
    public string TrackingSwitchMode => TrackingModeComboBox.SelectedValue as string ?? "disabled";
    public int JoystickVrxPin => GetSelectedPin(JoystickVrxComboBox);
    public int JoystickVryPin => GetSelectedPin(JoystickVryComboBox);
    public int JoystickSwPin => GetSelectedPin(JoystickSwComboBox);
    public int BatteryAdcPin => GetSelectedPin(BatteryAdcComboBox);
    public int BatteryChargePin => GetSelectedPin(BatteryChargeComboBox);

    private Dictionary<string, FingerField> CreateFingerFieldMap()
    {
        return new Dictionary<string, FingerField>(StringComparer.OrdinalIgnoreCase)
        {
            ["thumb"] = new FingerField { Key = "thumb", Label = "拇指", PinComboBox = ThumbComboBox, ShareComboBox = ThumbShareComboBox },
            ["index"] = new FingerField { Key = "index", Label = "食指", PinComboBox = IndexComboBox, ShareComboBox = IndexShareComboBox },
            ["middle"] = new FingerField { Key = "middle", Label = "中指", PinComboBox = MiddleComboBox, ShareComboBox = MiddleShareComboBox },
            ["ring"] = new FingerField { Key = "ring", Label = "无名指", PinComboBox = RingComboBox, ShareComboBox = RingShareComboBox },
            ["pinky"] = new FingerField { Key = "pinky", Label = "小指", PinComboBox = PinkyComboBox, ShareComboBox = PinkyShareComboBox }
        };
    }

    private void ConfigureFingerCombos(MainVm vm, IReadOnlyList<FirmwarePinOption> adcOptions)
    {
        var currentPins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["thumb"] = vm.FirmwareThumbPin,
            ["index"] = vm.FirmwareIndexPin,
            ["middle"] = vm.FirmwareMiddlePin,
            ["ring"] = vm.FirmwareRingPin,
            ["pinky"] = vm.FirmwarePinkyPin
        };

        foreach (var field in _fingerFields.Values)
        {
            ConfigurePinCombo(field.PinComboBox, adcOptions, currentPins[field.Key]);
            ConfigureShareCombo(field.ShareComboBox, field.Key);
        }

        var firstFingerByPin = new Dictionary<int, string>();
        foreach (var key in FingerOrder())
        {
            var pin = currentPins[key];
            var field = _fingerFields[key];
            if (pin >= 0 && firstFingerByPin.TryGetValue(pin, out var sourceKey))
            {
                field.ShareComboBox.SelectedValue = sourceKey;
            }
            else
            {
                field.ShareComboBox.SelectedValue = string.Empty;
                if (pin >= 0)
                {
                    firstFingerByPin[pin] = key;
                }
            }
        }
    }

    private void ConfigureShareCombo(ComboBox comboBox, string selfKey)
    {
        var options = new List<ShareOption>
        {
            new() { Value = string.Empty, Label = "独立输入" }
        };

        foreach (var field in _fingerFields.Values.Where(item => !string.Equals(item.Key, selfKey, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new ShareOption
            {
                Value = field.Key,
                Label = $"共用{field.Label}输入"
            });
        }

        comboBox.ItemsSource = options;
        comboBox.DisplayMemberPath = nameof(ShareOption.Label);
        comboBox.SelectedValuePath = nameof(ShareOption.Value);
    }

    private static void ConfigurePinCombo(ComboBox comboBox, IEnumerable itemsSource, int selectedValue)
    {
        comboBox.ItemsSource = itemsSource;
        comboBox.DisplayMemberPath = nameof(FirmwarePinOption.Label);
        comboBox.SelectedValuePath = nameof(FirmwarePinOption.Value);
        comboBox.SelectedValue = selectedValue;
    }

    private static int GetSelectedPin(ComboBox comboBox)
    {
        return comboBox.SelectedValue is int value ? value : -1;
    }

    private static IReadOnlyList<string> FingerOrder()
    {
        return ["thumb", "index", "middle", "ring", "pinky"];
    }

    private void AttachValidationHandlers()
    {
        foreach (var comboBox in _fingerFields.Values.SelectMany(field => new[] { field.PinComboBox, field.ShareComboBox })
                     .Concat(new[]
                     {
                         TrackingSwitchComboBox,
                         TrackingModeComboBox,
                         JoystickVrxComboBox,
                         JoystickVryComboBox,
                         JoystickSwComboBox,
                         BatteryAdcComboBox,
                         BatteryChargeComboBox
                     }))
        {
            comboBox.SelectionChanged += OnSelectionChanged;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshShareStates();
        RefreshValidationState();
    }

    private void RefreshShareStates()
    {
        foreach (var field in _fingerFields.Values)
        {
            var shareKey = field.ShareComboBox.SelectedValue as string ?? string.Empty;
            field.PinComboBox.IsEnabled = string.IsNullOrWhiteSpace(shareKey);
            field.PinComboBox.Opacity = field.PinComboBox.IsEnabled ? 1.0 : 0.62;
        }
    }

    private void RefreshValidationState()
    {
        var trackingPin = TrackingSwitchPin;
        if (trackingPin < 0)
        {
            TrackingModeComboBox.SelectedValue = "disabled";
        }

        TrackingModeComboBox.IsEnabled = trackingPin >= 0;

        var message = BuildValidationMessage();
        var hasError = !string.IsNullOrWhiteSpace(message);
        ValidationBorder.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        ValidationTextBlock.Text = message;
        ConfirmButton.IsEnabled = !hasError;
    }

    private string BuildValidationMessage()
    {
        var effectivePins = ResolveEffectivePins(out var resolveError);
        if (!string.IsNullOrWhiteSpace(resolveError))
        {
            return resolveError;
        }

        var usedAdcPins = effectivePins.Values.Where(pin => pin >= 0).Distinct().ToHashSet();
        if (usedAdcPins.Count == 0)
        {
            return "至少需要有一个有效的手指 ADC 输入。";
        }

        if (TrackingSwitchPin >= 0 && usedAdcPins.Contains(TrackingSwitchPin))
        {
            return "追踪开关 GPIO 不能和任意手指最终使用的 ADC GPIO 重复。";
        }

        if (TrackingSwitchPin >= 0 && (TrackingSwitchPin == JoystickVrxPin || TrackingSwitchPin == JoystickVryPin))
        {
            return "追踪开关 GPIO 不能和摇杆 VRX / VRY 重复。";
        }

        if (JoystickVrxPin >= 0 && usedAdcPins.Contains(JoystickVrxPin))
        {
            return "摇杆 VRX 不能和手指最终使用的 ADC GPIO 重复。";
        }

        if (JoystickVryPin >= 0 && usedAdcPins.Contains(JoystickVryPin))
        {
            return "摇杆 VRY 不能和手指最终使用的 ADC GPIO 重复。";
        }

        if (JoystickVrxPin >= 0 && JoystickVrxPin == JoystickVryPin)
        {
            return "摇杆 VRX 和 VRY 不能使用同一个 ADC GPIO。";
        }

        if (JoystickSwPin >= 0
            && (usedAdcPins.Contains(JoystickSwPin)
                || JoystickSwPin == TrackingSwitchPin
                || JoystickSwPin == JoystickVrxPin
                || JoystickSwPin == JoystickVryPin))
        {
            return "摇杆 SW GPIO 不能和手指、追踪开关或摇杆轴 GPIO 重复。";
        }

        if (BatteryAdcPin >= 0
            && (usedAdcPins.Contains(BatteryAdcPin)
                || BatteryAdcPin == JoystickVrxPin
                || BatteryAdcPin == JoystickVryPin))
        {
            return "电池 ADC 不能和手指或摇杆轴 ADC GPIO 重复。";
        }

        if (BatteryChargePin >= 0
            && (usedAdcPins.Contains(BatteryChargePin)
                || BatteryChargePin == TrackingSwitchPin
                || BatteryChargePin == JoystickVrxPin
                || BatteryChargePin == JoystickVryPin
                || BatteryChargePin == JoystickSwPin
                || BatteryChargePin == BatteryAdcPin))
        {
            return "充电检测 GPIO 不能和已有手指、摇杆、电池或追踪开关 GPIO 重复。";
        }

        if (TrackingSwitchPin >= 0 && string.Equals(TrackingSwitchMode, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "已经选择了追踪开关 GPIO，请同时设置开关模式；如果不需要追踪开关，请把 GPIO 设为“不使用追踪开关”。";
        }

        return string.Empty;
    }

    private int GetResolvedFingerPin(string key)
    {
        var pins = ResolveEffectivePins(out _);
        return pins.TryGetValue(key, out var pin) ? pin : -1;
    }

    private Dictionary<string, int> ResolveEffectivePins(out string error)
    {
        var resolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localError = string.Empty;

        int Resolve(string key)
        {
            if (resolved.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!_fingerFields.TryGetValue(key, out var field))
            {
                localError = $"未知的手指来源：{key}";
                return -1;
            }

            if (!visiting.Add(key))
            {
                localError = "共享输入存在循环引用，请检查互相共用的手指设置。";
                return -1;
            }

            var shareKey = field.ShareComboBox.SelectedValue as string ?? string.Empty;
            int pin;
            if (string.IsNullOrWhiteSpace(shareKey))
            {
                pin = GetSelectedPin(field.PinComboBox);
                if (pin < 0)
                {
                    localError = $"{field.Label}还没有选择 ADC GPIO。";
                }
            }
            else
            {
                if (string.Equals(shareKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    localError = $"{field.Label}不能引用自己。";
                    pin = -1;
                }
                else if (!_fingerFields.ContainsKey(shareKey))
                {
                    localError = $"{field.Label}引用了未知的共享来源。";
                    pin = -1;
                }
                else
                {
                    pin = Resolve(shareKey);
                    if (pin < 0 && string.IsNullOrWhiteSpace(localError))
                    {
                        localError = $"{field.Label}共用了{_fingerFields[shareKey].Label}输入，但来源没有有效 ADC GPIO。";
                    }
                }
            }

            visiting.Remove(key);
            resolved[key] = pin;
            return pin;
        }

        foreach (var key in FingerOrder())
        {
            Resolve(key);
            if (!string.IsNullOrWhiteSpace(localError))
            {
                break;
            }
        }

        error = localError;
        return resolved;
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        ThumbComboBox.SelectedValue = _defaultConfig.ThumbPin;
        IndexComboBox.SelectedValue = _defaultConfig.IndexPin;
        MiddleComboBox.SelectedValue = _defaultConfig.MiddlePin;
        RingComboBox.SelectedValue = _defaultConfig.RingPin;
        PinkyComboBox.SelectedValue = _defaultConfig.PinkyPin;

        ThumbShareComboBox.SelectedValue = string.Empty;
        IndexShareComboBox.SelectedValue = string.Empty;
        MiddleShareComboBox.SelectedValue = string.Empty;
        RingShareComboBox.SelectedValue = string.Empty;
        PinkyShareComboBox.SelectedValue = string.Empty;

        TrackingSwitchComboBox.SelectedValue = _defaultConfig.TrackingSwitchPin;
        TrackingModeComboBox.SelectedValue = _defaultConfig.TrackingSwitchMode;
        JoystickVrxComboBox.SelectedValue = _defaultConfig.JoystickVrxPin;
        JoystickVryComboBox.SelectedValue = _defaultConfig.JoystickVryPin;
        JoystickSwComboBox.SelectedValue = _defaultConfig.JoystickSwPin;
        BatteryAdcComboBox.SelectedValue = _defaultConfig.BatteryAdcPin;
        BatteryChargeComboBox.SelectedValue = _defaultConfig.BatteryChargePin;
        RefreshShareStates();
        RefreshValidationState();
    }

    private static string BuildBoardAdcHint(string target)
    {
        return string.Equals(target, FirmwareTargetCatalog.Esp32S3, StringComparison.OrdinalIgnoreCase)
            ? "ESP32-S3 SuperMini：手指、摇杆轴和电池检测 ADC 可用 GPIO1~10；共享输入时会直接复用来源手指的 GPIO。"
            : "ESP32-C3 SuperMini：手指、摇杆轴和电池检测 ADC 只支持 GPIO0~5；GPIO6 以上只能做开关类数字输入。共享输入时会直接复用来源手指的 GPIO。";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        RefreshValidationState();
        if (!ConfirmButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
    }
}

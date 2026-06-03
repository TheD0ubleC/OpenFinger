namespace OpenFinger.Control;

public sealed class GestureDashboardState
{
    public bool ShowAdvanced { get; init; }
    public GestureHandDashboardState Left { get; init; } = new() { Side = "left", Title = "左手" };
    public GestureHandDashboardState Right { get; init; } = new() { Side = "right", Title = "右手" };
}

public sealed class GestureHandDashboardState
{
    public string Side { get; init; } = "left";
    public string Title { get; init; } = "左手";
    public bool Enabled { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string LiveSummary { get; init; } = string.Empty;
    public IReadOnlyList<GestureRowState> Rows { get; init; } = Array.Empty<GestureRowState>();
}

public sealed class GestureRowState : ObservableObject
{
    private string _side = "left";
    private string _comboKey = string.Empty;
    private string _comboLabel = string.Empty;
    private bool _enabled;
    private string _buttonValue = GestureButtonCatalog.Disabled;
    private string _buttonLabel = "不映射";
    private IReadOnlyList<FirmwareModeOption> _buttonOptions = Array.Empty<FirmwareModeOption>();
    private bool _calibrated;
    private string _calibrationActionLabel = "开始校准";
    private bool _active;
    private double _score;
    private double _triggerThreshold;
    private double _releaseThreshold;
    private double _confidenceThreshold;
    private string _statusText = string.Empty;
    private string _stateLabel = "未启用";
    private string _stateKind = "disabled";
    private string _advancedText = string.Empty;

    public string Side { get => _side; set => SetProperty(ref _side, value); }
    public string ComboKey { get => _comboKey; set => SetProperty(ref _comboKey, value); }
    public string ComboLabel { get => _comboLabel; set => SetProperty(ref _comboLabel, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string ButtonValue { get => _buttonValue; set => SetProperty(ref _buttonValue, value); }
    public string ButtonLabel { get => _buttonLabel; set => SetProperty(ref _buttonLabel, value); }
    public IReadOnlyList<FirmwareModeOption> ButtonOptions { get => _buttonOptions; set => SetProperty(ref _buttonOptions, value); }
    public bool Calibrated { get => _calibrated; set => SetProperty(ref _calibrated, value); }
    public string CalibrationActionLabel { get => _calibrationActionLabel; set => SetProperty(ref _calibrationActionLabel, value); }
    public bool Active { get => _active; set => SetProperty(ref _active, value); }
    public double Score { get => _score; set => SetProperty(ref _score, value); }
    public double TriggerThreshold { get => _triggerThreshold; set => SetProperty(ref _triggerThreshold, value); }
    public double ReleaseThreshold { get => _releaseThreshold; set => SetProperty(ref _releaseThreshold, value); }
    public double ConfidenceThreshold { get => _confidenceThreshold; set => SetProperty(ref _confidenceThreshold, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string StateLabel { get => _stateLabel; set => SetProperty(ref _stateLabel, value); }
    public string StateKind { get => _stateKind; set => SetProperty(ref _stateKind, value); }
    public string AdvancedText { get => _advancedText; set => SetProperty(ref _advancedText, value); }
}

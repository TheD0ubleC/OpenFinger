using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shapes;

namespace OpenFinger.Control.Pages;

public partial class CalibrationPage : UserControl
{
    private bool _suppressTuningEvents;
    private bool _suppressProModeToggle;
    private bool _suppressJoystickEvents;
    private bool _suppressPoseOffsetEvents;
    private bool _realtimeExpanded = true;

    public CalibrationPage()
    {
        InitializeComponent();
        InitializeJoystickSelectors();
        ApplyAdvancedMode(false);
        SetRealtimeExpanded(true);
        UpdateTuningLabels();
    }

    public void SetAdvancedMode(bool enabled)
    {
        _suppressProModeToggle = true;
        try
        {
            if (ProModeToggle is not null)
            {
                ProModeToggle.IsChecked = enabled;
            }
        }
        finally
        {
            _suppressProModeToggle = false;
        }

        ApplyAdvancedMode(enabled);
    }

    public void ApplyJoystickSettings(JoystickSettingsConfig settings)
    {
        if (settings is null)
        {
            return;
        }

        _suppressJoystickEvents = true;
        try
        {
            LeftJoystickAxisModeComboBox.SelectedValue = settings.Left.SteamVrAxisMode;
            LeftJoystickClickActionComboBox.SelectedValue = settings.Left.SteamVrClickAction;
            LeftJoystickOrientationComboBox.SelectedValue = settings.Left.Orientation;
            LeftJoystickDeadzoneSlider.Value = settings.Left.DeadzonePercent;

            RightJoystickAxisModeComboBox.SelectedValue = settings.Right.SteamVrAxisMode;
            RightJoystickClickActionComboBox.SelectedValue = settings.Right.SteamVrClickAction;
            RightJoystickOrientationComboBox.SelectedValue = settings.Right.Orientation;
            RightJoystickDeadzoneSlider.Value = settings.Right.DeadzonePercent;

            LeftJoystickCenterText.Text = BuildCenterText(settings.Left);
            RightJoystickCenterText.Text = BuildCenterText(settings.Right);
            LeftJoystickDeadzoneValueText.Text = $"{settings.Left.DeadzonePercent:0.#}%";
            RightJoystickDeadzoneValueText.Text = $"{settings.Right.DeadzonePercent:0.#}%";
        }
        finally
        {
            _suppressJoystickEvents = false;
        }
    }

    public void ApplyPoseOffsets(ControllerPoseOffsetsConfig offsets)
    {
        if (offsets is null)
        {
            return;
        }

        _suppressPoseOffsetEvents = true;
        try
        {
            SetPoseOffsetText("left", offsets.Left ?? new ControllerPoseOffsetConfig());
            SetPoseOffsetText("right", offsets.Right ?? new ControllerPoseOffsetConfig());
        }
        finally
        {
            _suppressPoseOffsetEvents = false;
        }
    }

    private void InitializeJoystickSelectors()
    {
        foreach (var comboBox in new[]
                 {
                     LeftJoystickAxisModeComboBox,
                     RightJoystickAxisModeComboBox
                 })
        {
            comboBox.ItemsSource = JoystickSteamVrCatalog.AxisModeOptions;
            comboBox.DisplayMemberPath = nameof(FirmwareModeOption.Label);
            comboBox.SelectedValuePath = nameof(FirmwareModeOption.Value);
        }

        foreach (var comboBox in new[]
                 {
                     LeftJoystickOrientationComboBox,
                     RightJoystickOrientationComboBox
                 })
        {
            comboBox.ItemsSource = JoystickOrientationCatalog.Options;
            comboBox.DisplayMemberPath = nameof(FirmwareModeOption.Label);
            comboBox.SelectedValuePath = nameof(FirmwareModeOption.Value);
        }

        foreach (var comboBox in new[]
                 {
                     LeftJoystickClickActionComboBox,
                     RightJoystickClickActionComboBox
                 })
        {
            comboBox.ItemsSource = JoystickSteamVrCatalog.ClickActionOptions;
            comboBox.DisplayMemberPath = nameof(FirmwareModeOption.Label);
            comboBox.SelectedValuePath = nameof(FirmwareModeOption.Value);
        }
    }

    public void RefreshFingerCards(
        IReadOnlyList<FingerRuntimeVm> leftFingers,
        IReadOnlyList<FingerRuntimeVm> rightFingers,
        JoystickRuntimeVm leftJoystick,
        JoystickRuntimeVm rightJoystick)
    {
        UpdateFingerCard(FingerLThumb, leftFingers, 0);
        UpdateFingerCard(FingerLIndex, leftFingers, 1);
        UpdateFingerCard(FingerLMiddle, leftFingers, 2);
        UpdateFingerCard(FingerLRing, leftFingers, 3);
        UpdateFingerCard(FingerLPinky, leftFingers, 4);
        UpdateFingerCard(CompactFingerLThumb, leftFingers, 0);
        UpdateFingerCard(CompactFingerLIndex, leftFingers, 1);
        UpdateFingerCard(CompactFingerLMiddle, leftFingers, 2);
        UpdateFingerCard(CompactFingerLRing, leftFingers, 3);
        UpdateFingerCard(CompactFingerLPinky, leftFingers, 4);

        UpdateFingerCard(FingerRThumb, rightFingers, 0);
        UpdateFingerCard(FingerRIndex, rightFingers, 1);
        UpdateFingerCard(FingerRMiddle, rightFingers, 2);
        UpdateFingerCard(FingerRRing, rightFingers, 3);
        UpdateFingerCard(FingerRPinky, rightFingers, 4);
        UpdateFingerCard(CompactFingerRThumb, rightFingers, 0);
        UpdateFingerCard(CompactFingerRIndex, rightFingers, 1);
        UpdateFingerCard(CompactFingerRMiddle, rightFingers, 2);
        UpdateFingerCard(CompactFingerRRing, rightFingers, 3);
        UpdateFingerCard(CompactFingerRPinky, rightFingers, 4);

        UpdateJoystickCard(
            leftJoystick,
            LeftJoystickStatusText,
            LeftJoystickAxisText,
            LeftJoystickRawText,
            LeftJoystickSwitchText,
            LeftJoystickKnobTransform,
            LeftJoystickActiveRing,
            28.0);
        UpdateJoystickCard(
            rightJoystick,
            RightJoystickStatusText,
            RightJoystickAxisText,
            RightJoystickRawText,
            RightJoystickSwitchText,
            RightJoystickKnobTransform,
            RightJoystickActiveRing,
            28.0);
        UpdateJoystickCard(
            leftJoystick,
            null,
            null,
            null,
            null,
            CompactLeftJoystickKnobTransform,
            CompactLeftJoystickActiveRing,
            11.0);
        UpdateJoystickCard(
            rightJoystick,
            null,
            null,
            null,
            null,
            CompactRightJoystickKnobTransform,
            CompactRightJoystickActiveRing,
            11.0);
    }

    public void ApplyAlgorithmTuning(AlgorithmTuningConfig tuning)
    {
        if (SensitivitySlider == null
            || AntiShakeSlider == null
            || SmoothingSlider == null
            || DeadzoneSlider == null
            || QFactorSlider == null)
        {
            return;
        }

        _suppressTuningEvents = true;
        try
        {
            SensitivitySlider.Value = tuning.SensitivityLevel;
            AntiShakeSlider.Value = tuning.AntiShakeLevel;
            SmoothingSlider.Value = tuning.SmoothingAlpha;
            DeadzoneSlider.Value = tuning.DeadzonePercent;
            QFactorSlider.Value = tuning.KalmanQ;
            UpdateTuningLabels();
        }
        finally
        {
            _suppressTuningEvents = false;
        }
    }

    private static void UpdateFingerCard(Controls.FingerCard card, IReadOnlyList<FingerRuntimeVm> fingers, int index)
    {
        if (index >= fingers.Count)
        {
            return;
        }

        var finger = fingers[index];
        card.FingerName = finger.DisplayName;
        card.Value = finger.Bend;
        card.DisplayValue = $"{finger.Bend:P0}";
        card.RawValue = finger.Raw;
        card.MinValue = finger.CalibratedOpenRaw >= 0 ? finger.CalibratedOpenRaw : finger.MinRaw;
        card.MaxValue = finger.CalibratedClosedRaw >= 0 ? finger.CalibratedClosedRaw : finger.MaxRaw;
    }

    private static void UpdateFingerCard(Controls.CompactFingerCard card, IReadOnlyList<FingerRuntimeVm> fingers, int index)
    {
        if (index >= fingers.Count)
        {
            return;
        }

        var finger = fingers[index];
        card.FingerName = finger.DisplayName;
        card.Value = finger.Bend;
        card.DisplayValue = $"{finger.Bend:P0}";
        card.RawValue = finger.Raw;
        card.MinValue = finger.CalibratedOpenRaw >= 0 ? finger.CalibratedOpenRaw : finger.MinRaw;
        card.MaxValue = finger.CalibratedClosedRaw >= 0 ? finger.CalibratedClosedRaw : finger.MaxRaw;
    }

    private static void UpdateJoystickCard(
        JoystickRuntimeVm joystick,
        TextBlock? statusText,
        TextBlock? axisText,
        TextBlock? rawText,
        TextBlock? switchText,
        TranslateTransform knobTransform,
        Shape activeRing,
        double knobTravelRadius)
    {
        if (!joystick.Available)
        {
            if (statusText is not null)
            {
                statusText.Text = "未启用摇杆";
            }
            if (axisText is not null)
            {
                axisText.Text = "轴向：-";
            }
            if (rawText is not null)
            {
                rawText.Text = "原始：-";
            }
            if (switchText is not null)
            {
                switchText.Text = "按键：未配置";
            }
            knobTransform.X = 0;
            knobTransform.Y = 0;
            activeRing.Opacity = 0.28;
            return;
        }

        if (statusText is not null)
        {
            statusText.Text = joystick.SwitchPressed == true ? "按下" : "在线";
        }
        var axisXText = joystick.RawX >= 0 ? joystick.AxisX.ToString("+0%;-0%;0%") : "-";
        var axisYText = joystick.RawY >= 0 ? joystick.AxisY.ToString("+0%;-0%;0%") : "-";
        if (axisText is not null)
        {
            axisText.Text = $"轴向：X {axisXText} · Y {axisYText}";
        }
        if (rawText is not null)
        {
            rawText.Text = $"原始：{(joystick.RawX >= 0 ? joystick.RawX.ToString() : "-")} / {(joystick.RawY >= 0 ? joystick.RawY.ToString() : "-")}";
        }
        if (switchText is not null)
        {
            switchText.Text = joystick.SwitchPressed.HasValue
                ? $"按键：{(joystick.SwitchPressed.Value ? "按下" : "松开")}"
                : "按键：未配置";
        }

        var axisX = Math.Clamp(joystick.RawX >= 0 ? joystick.AxisX : 0.0, -1.0, 1.0);
        var axisY = Math.Clamp(joystick.RawY >= 0 ? joystick.AxisY : 0.0, -1.0, 1.0);
        knobTransform.X = axisX * knobTravelRadius;
        knobTransform.Y = axisY * knobTravelRadius;
        activeRing.Opacity = joystick.SwitchPressed == true ? 0.92 : 0.7;
    }

    private void ApplyAdvancedMode(bool enabled)
    {
        if (NormalTuningPanel == null || ProTuningPanel == null)
        {
            return;
        }

        NormalTuningPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProTuningPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (AdvancedModeBanner != null)
        {
            AdvancedModeBanner.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var card in new[]
                 {
                     FingerLThumb, FingerLIndex, FingerLMiddle, FingerLRing, FingerLPinky,
                     FingerRThumb, FingerRIndex, FingerRMiddle, FingerRRing, FingerRPinky
                 })
        {
            if (card is not null)
            {
                card.IsProMode = enabled;
            }
        }

        foreach (var card in new[]
                 {
                     CompactFingerLThumb, CompactFingerLIndex, CompactFingerLMiddle, CompactFingerLRing, CompactFingerLPinky,
                     CompactFingerRThumb, CompactFingerRIndex, CompactFingerRMiddle, CompactFingerRRing, CompactFingerRPinky
                 })
        {
            if (card is not null)
            {
                card.IsProMode = enabled;
            }
        }
    }

    private void SetRealtimeExpanded(bool expanded)
    {
        _realtimeExpanded = expanded;
        if (DetailedRealtimePanel is not null)
        {
            DetailedRealtimePanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (CompactRealtimePanel is not null)
        {
            CompactRealtimePanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        }

        if (RealtimeToggleButton is not null)
        {
            RealtimeToggleButton.Content = expanded ? "切换为概览" : "展开详细";
        }
    }

    private static string BuildCenterText(JoystickHandSettings settings)
    {
        var centerX = settings.CenterRawX >= 0 ? settings.CenterRawX.ToString() : "2048";
        var centerY = settings.CenterRawY >= 0 ? settings.CenterRawY.ToString() : "2048";
        var mode = settings.CenterRawX >= 0 || settings.CenterRawY >= 0 ? "已记录" : "自动";
        return $"中心：{mode}（{centerX} / {centerY}）";
    }

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private void OnRealtimeToggleClick(object sender, RoutedEventArgs e)
    {
        SetRealtimeExpanded(!_realtimeExpanded);
    }

    private void OnProModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressProModeToggle)
        {
            return;
        }

        ApplyAdvancedMode(ProModeToggle.IsChecked == true);
    }

    private async void OnCalibrateLeftClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.StartHandCalibrationAsync("left");
        }
    }

    private async void OnCalibrateRightClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.StartHandCalibrationAsync("right");
        }
    }

    private void OnResetCalibrationClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ResetCalibration();
    }

    private void OnTuningSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressTuningEvents)
        {
            return;
        }

        UpdateTuningLabels();
        GetOwnerWindow()?.UpdateAlgorithmTuning(new AlgorithmTuningConfig
        {
            SensitivityLevel = Math.Round(SensitivitySlider.Value),
            AntiShakeLevel = Math.Round(AntiShakeSlider.Value),
            SmoothingAlpha = Math.Round(SmoothingSlider.Value, 3),
            DeadzonePercent = Math.Round(DeadzoneSlider.Value, 1),
            KalmanQ = Math.Round(QFactorSlider.Value, 4)
        });
    }

    private void OnJoystickSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressJoystickEvents)
        {
            return;
        }

        PushJoystickSettings(IsLeftJoystickControl(sender) ? "left" : "right");
    }

    private void OnJoystickDeadzoneChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressJoystickEvents)
        {
            return;
        }

        // During InitializeComponent the slider value change can fire before
        // the related TextBlocks are assigned. Guard against nulls.
        if (LeftJoystickDeadzoneValueText is null || RightJoystickDeadzoneValueText is null
            || LeftJoystickDeadzoneSlider is null || RightJoystickDeadzoneSlider is null)
        {
            return;
        }

        LeftJoystickDeadzoneValueText.Text = $"{LeftJoystickDeadzoneSlider.Value:0.#}%";
        RightJoystickDeadzoneValueText.Text = $"{RightJoystickDeadzoneSlider.Value:0.#}%";
        PushJoystickSettings(IsLeftJoystickControl(sender) ? "left" : "right");
    }

    private void PushJoystickSettings(string side)
    {
        var owner = GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        if (string.Equals(side, "left", StringComparison.OrdinalIgnoreCase))
        {
            owner.UpdateJoystickSettings(
                "left",
                LeftJoystickAxisModeComboBox.SelectedValue as string ?? JoystickSteamVrCatalog.AxisJoystick,
                LeftJoystickClickActionComboBox.SelectedValue as string ?? JoystickSteamVrCatalog.ClickJoystick,
                LeftJoystickOrientationComboBox.SelectedValue as string ?? JoystickOrientationCatalog.Normal,
                LeftJoystickDeadzoneSlider.Value);
        }
        else
        {
            owner.UpdateJoystickSettings(
                "right",
                RightJoystickAxisModeComboBox.SelectedValue as string ?? JoystickSteamVrCatalog.AxisJoystick,
                RightJoystickClickActionComboBox.SelectedValue as string ?? JoystickSteamVrCatalog.ClickJoystick,
                RightJoystickOrientationComboBox.SelectedValue as string ?? JoystickOrientationCatalog.Normal,
                RightJoystickDeadzoneSlider.Value);
        }
    }

    private bool IsLeftJoystickControl(object sender)
    {
        return ReferenceEquals(sender, LeftJoystickAxisModeComboBox)
            || ReferenceEquals(sender, LeftJoystickClickActionComboBox)
            || ReferenceEquals(sender, LeftJoystickOrientationComboBox)
            || ReferenceEquals(sender, LeftJoystickDeadzoneSlider);
    }

    private void OnCaptureLeftJoystickCenterClick(object sender, RoutedEventArgs e)
    {
        if (GetOwnerWindow()?.CaptureJoystickCenter("left") == true)
        {
            LeftJoystickCenterText.Text = BuildCenterText(GetOwnerWindow()!.CloneJoystickSettings("left"));
        }
    }

    private async void OnAutoCalibrateLeftJoystickDirectionClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null && await owner.AutoCalibrateJoystickDirectionAsync("left"))
        {
            ApplySingleJoystickSettings("left", owner.CloneJoystickSettings("left"));
        }
    }

    private void OnCaptureRightJoystickCenterClick(object sender, RoutedEventArgs e)
    {
        if (GetOwnerWindow()?.CaptureJoystickCenter("right") == true)
        {
            RightJoystickCenterText.Text = BuildCenterText(GetOwnerWindow()!.CloneJoystickSettings("right"));
        }
    }

    private async void OnAutoCalibrateRightJoystickDirectionClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null && await owner.AutoCalibrateJoystickDirectionAsync("right"))
        {
            ApplySingleJoystickSettings("right", owner.CloneJoystickSettings("right"));
        }
    }

    private void OnResetLeftJoystickCalibrationClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        owner?.ResetJoystickCalibration("left");
        if (owner is not null)
        {
            ApplySingleJoystickSettings("left", owner.CloneJoystickSettings("left"));
        }
    }

    private void OnResetRightJoystickCalibrationClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        owner?.ResetJoystickCalibration("right");
        if (owner is not null)
        {
            ApplySingleJoystickSettings("right", owner.CloneJoystickSettings("right"));
        }
    }

    private void ApplySingleJoystickSettings(string side, JoystickHandSettings settings)
    {
        _suppressJoystickEvents = true;
        try
        {
            if (string.Equals(side, "left", StringComparison.OrdinalIgnoreCase))
            {
                LeftJoystickAxisModeComboBox.SelectedValue = settings.SteamVrAxisMode;
                LeftJoystickClickActionComboBox.SelectedValue = settings.SteamVrClickAction;
                LeftJoystickOrientationComboBox.SelectedValue = settings.Orientation;
                LeftJoystickDeadzoneSlider.Value = settings.DeadzonePercent;
                LeftJoystickCenterText.Text = BuildCenterText(settings);
                LeftJoystickDeadzoneValueText.Text = $"{settings.DeadzonePercent:0.#}%";
            }
            else
            {
                RightJoystickAxisModeComboBox.SelectedValue = settings.SteamVrAxisMode;
                RightJoystickClickActionComboBox.SelectedValue = settings.SteamVrClickAction;
                RightJoystickOrientationComboBox.SelectedValue = settings.Orientation;
                RightJoystickDeadzoneSlider.Value = settings.DeadzonePercent;
                RightJoystickCenterText.Text = BuildCenterText(settings);
                RightJoystickDeadzoneValueText.Text = $"{settings.DeadzonePercent:0.#}%";
            }
        }
        finally
        {
            _suppressJoystickEvents = false;
        }
    }

    private void OnApplyLeftPoseOffsetClick(object sender, RoutedEventArgs e)
    {
        ApplyPoseOffsetFromInputs("left");
    }

    private void OnApplyRightPoseOffsetClick(object sender, RoutedEventArgs e)
    {
        ApplyPoseOffsetFromInputs("right");
    }

    private void OnResetLeftPoseOffsetClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ResetPoseOffset("left");
    }

    private void OnResetRightPoseOffsetClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ResetPoseOffset("right");
    }

    private void OnPoseOffsetTextBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || _suppressPoseOffsetEvents)
        {
            return;
        }

        e.Handled = true;
        ApplyPoseOffsetFromInputs(IsLeftPoseOffsetControl(sender) ? "left" : "right");
    }

    private void ApplyPoseOffsetFromInputs(string side)
    {
        var owner = GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        if (!TryReadPoseOffset(side, out var offset))
        {
            return;
        }

        owner.UpdatePoseOffset(side, offset);
    }

    private bool TryReadPoseOffset(string side, out ControllerPoseOffsetConfig offset)
    {
        var isLeft = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
        var positionX = isLeft ? LeftPosePositionXTextBox : RightPosePositionXTextBox;
        var positionY = isLeft ? LeftPosePositionYTextBox : RightPosePositionYTextBox;
        var positionZ = isLeft ? LeftPosePositionZTextBox : RightPosePositionZTextBox;
        var rotationPitch = isLeft ? LeftPoseRotationPitchTextBox : RightPoseRotationPitchTextBox;
        var rotationYaw = isLeft ? LeftPoseRotationYawTextBox : RightPoseRotationYawTextBox;
        var rotationRoll = isLeft ? LeftPoseRotationRollTextBox : RightPoseRotationRollTextBox;

        var ok = TryReadNumber(positionX, -1.0, 1.0, out var x)
            & TryReadNumber(positionY, -1.0, 1.0, out var y)
            & TryReadNumber(positionZ, -1.0, 1.0, out var z)
            & TryReadNumber(rotationPitch, -180.0, 180.0, out var pitch)
            & TryReadNumber(rotationYaw, -180.0, 180.0, out var yaw)
            & TryReadNumber(rotationRoll, -180.0, 180.0, out var roll);

        offset = new ControllerPoseOffsetConfig
        {
            PositionX = x,
            PositionY = y,
            PositionZ = z,
            RotationPitch = pitch,
            RotationYaw = yaw,
            RotationRoll = roll
        };

        return ok;
    }

    private static bool TryReadNumber(System.Windows.Controls.TextBox textBox, double min, double max, out double value)
    {
        var text = textBox.Text.Trim().Replace('，', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            textBox.BorderBrush = Brushes.IndianRed;
            value = 0.0;
            return false;
        }

        value = Math.Clamp(value, min, max);
        textBox.Text = FormatPoseNumber(value);
        textBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        return true;
    }

    private void SetPoseOffsetText(string side, ControllerPoseOffsetConfig offset)
    {
        var isLeft = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
        (isLeft ? LeftPosePositionXTextBox : RightPosePositionXTextBox).Text = FormatPoseNumber(offset.PositionX);
        (isLeft ? LeftPosePositionYTextBox : RightPosePositionYTextBox).Text = FormatPoseNumber(offset.PositionY);
        (isLeft ? LeftPosePositionZTextBox : RightPosePositionZTextBox).Text = FormatPoseNumber(offset.PositionZ);
        (isLeft ? LeftPoseRotationPitchTextBox : RightPoseRotationPitchTextBox).Text = FormatPoseNumber(offset.RotationPitch);
        (isLeft ? LeftPoseRotationYawTextBox : RightPoseRotationYawTextBox).Text = FormatPoseNumber(offset.RotationYaw);
        (isLeft ? LeftPoseRotationRollTextBox : RightPoseRotationRollTextBox).Text = FormatPoseNumber(offset.RotationRoll);
    }

    private static string FormatPoseNumber(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private bool IsLeftPoseOffsetControl(object sender)
    {
        return ReferenceEquals(sender, LeftPosePositionXTextBox)
            || ReferenceEquals(sender, LeftPosePositionYTextBox)
            || ReferenceEquals(sender, LeftPosePositionZTextBox)
            || ReferenceEquals(sender, LeftPoseRotationPitchTextBox)
            || ReferenceEquals(sender, LeftPoseRotationYawTextBox)
            || ReferenceEquals(sender, LeftPoseRotationRollTextBox);
    }

    private void UpdateTuningLabels()
    {
        if (SensitivityValueText == null
            || AntiShakeValueText == null
            || SmoothingValueText == null
            || DeadzoneValueText == null
            || QFactorValueText == null
            || SensitivitySlider == null
            || AntiShakeSlider == null
            || SmoothingSlider == null
            || DeadzoneSlider == null
            || QFactorSlider == null)
        {
            return;
        }

        SensitivityValueText.Text = GetSensitivityLabel(Math.Round(SensitivitySlider.Value));
        AntiShakeValueText.Text = GetAntiShakeLabel(Math.Round(AntiShakeSlider.Value));
        SmoothingValueText.Text = $"{SmoothingSlider.Value:0.000}";
        DeadzoneValueText.Text = $"{DeadzoneSlider.Value:0.#}%";
        QFactorValueText.Text = $"{QFactorSlider.Value:0.0000}";
    }

    private static string GetSensitivityLabel(double value)
    {
        return value switch
        {
            <= 1 => "稳重",
            >= 3 => "灵敏",
            _ => "中等"
        };
    }

    private static string GetAntiShakeLabel(double value)
    {
        return value switch
        {
            <= 1 => "低防抖",
            >= 3 => "高防抖",
            _ => "中防抖"
        };
    }
}

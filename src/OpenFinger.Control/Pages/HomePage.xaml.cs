namespace OpenFinger.Control.Pages;

public partial class HomePage : UserControl
{
    private string _primaryActionKey = "devices";
    private string _secondaryActionKey = "diagnostics";

    public HomePage()
    {
        InitializeComponent();
    }

    public void UpdateDashboard(HomeDashboardState state)
    {
        _primaryActionKey = string.IsNullOrWhiteSpace(state.PrimaryActionKey) ? "devices" : state.PrimaryActionKey;
        _secondaryActionKey = string.IsNullOrWhiteSpace(state.SecondaryActionKey) ? "diagnostics" : state.SecondaryActionKey;

        NextActionDot.Fill = UiTonePalette.Accent(state.Overall.Tone);
        NextActionTitleText.Text = state.NextActionTitle;
        NextActionDescriptionText.Text = state.NextActionDescription;
        NextActionPrimaryButton.Content = state.PrimaryActionLabel;
        NextActionSecondaryButton.Content = state.SecondaryActionLabel;

        ApplyDeviceState(
            LeftDetailText,
            LeftConnectionText,
            LeftFirmwareText,
            LeftCalibrationText,
            LeftUsageText,
            LeftMetaBorder,
            LeftMetaText,
            state.Left);

        ApplyDeviceState(
            RightDetailText,
            RightConnectionText,
            RightFirmwareText,
            RightCalibrationText,
            RightUsageText,
            RightMetaBorder,
            RightMetaText,
            state.Right);
    }

    private static void ApplyDeviceState(
        TextBlock detailText,
        TextBlock connectionText,
        TextBlock firmwareText,
        TextBlock calibrationText,
        TextBlock usageText,
        Border metaBorder,
        TextBlock metaText,
        DeviceReadinessState state)
    {
        detailText.Text = state.Detail;
        connectionText.Text = state.Connection.Text;
        connectionText.Foreground = UiTonePalette.Text(state.Connection.Tone);
        firmwareText.Text = state.Firmware.Text;
        firmwareText.Foreground = UiTonePalette.Text(state.Firmware.Tone);
        calibrationText.Text = state.Calibration.Text;
        calibrationText.Foreground = UiTonePalette.Text(state.Calibration.Tone);
        usageText.Text = state.Usage.Text;
        usageText.Foreground = UiTonePalette.Text(state.Usage.Tone);
        metaBorder.Background = UiTonePalette.Background(state.Usage.Tone);
        metaText.Text = state.Meta;
        metaText.Foreground = UiTonePalette.Text(state.Usage.Tone);
    }

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private void OnOpenDevicesClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.NavigateToDevices();
    }

    private void OnOpenFirmwareClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.NavigateToFirmware();
    }

    private void OnOpenCalibrationClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.NavigateToCalibration();
    }

    private void OnOpenDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.NavigateToStatus();
    }

    private void OnOpenSteamVrClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.NavigateToDiagnostics();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.NavigateToSettings();
    }

    private async void OnStartSteamVrClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.StartSteamVrAsync();
        }
    }

    private void OnNextPrimaryClick(object sender, RoutedEventArgs e)
    {
        DispatchAction(_primaryActionKey);
    }

    private void OnNextSecondaryClick(object sender, RoutedEventArgs e)
    {
        DispatchAction(_secondaryActionKey);
    }

    private void DispatchAction(string key)
    {
        var owner = GetOwnerWindow();
        if (owner is null)
        {
            return;
        }

        switch (key)
        {
            case "firmware":
                owner.NavigateToFirmware();
                break;
            case "calibration":
                owner.NavigateToCalibration();
                break;
            case "steamvr":
                _ = owner.StartSteamVrAsync();
                break;
            case "driver":
                owner.NavigateToDiagnostics();
                break;
            case "diagnostics":
                owner.NavigateToStatus();
                break;
            default:
                owner.NavigateToDevices();
                break;
        }
    }
}

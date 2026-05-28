namespace OpenFinger.Control.Pages;

public partial class StatusPage : UserControl
{
    private bool _suppressAdvancedToggle;

    public StatusPage()
    {
        InitializeComponent();
    }

    public void UpdateStatus(DiagnosticsDashboardState state)
    {
        ApplyCard(KitStatusBorder, KitStatusText, KitDetailText, state.OpenFingerKit, state.OpenFingerKitDetail);
        ApplyCard(SteamVrStatusBorder, SteamVrStatusText, SteamVrDetailText, state.SteamVr, state.SteamVrDetail);
        ApplyCard(DeviceCommStatusBorder, DeviceCommStatusText, DeviceCommDetailText, state.DeviceComm, state.DeviceCommDetail);
    }

    public void SetAdvancedMode(bool enabled)
    {
        _suppressAdvancedToggle = true;
        try
        {
            AdvancedModeToggle.IsChecked = enabled;
        }
        finally
        {
            _suppressAdvancedToggle = false;
        }
    }

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private void OnAdvancedModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressAdvancedToggle)
        {
            return;
        }

        GetOwnerWindow()?.SetAdvancedMode(AdvancedModeToggle.IsChecked == true);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.RefreshAllAsync(forceSerialProbe: true);
        }
    }

    private async void OnStartSteamVrClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.StartSteamVrAsync();
        }
    }

    private async void OnRestartSteamVrClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.RestartSteamVrAsync();
        }
    }

    private void OnRepairConfigClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.RepairConfig();
    }

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.OpenConfig();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ClearAllLogs();
    }

    private static void ApplyCard(Border border, TextBlock title, TextBlock detail, StatusBadge badge, string detailText)
    {
        border.Background = UiTonePalette.Card(badge.Tone);
        border.BorderBrush = UiTonePalette.Edge(badge.Tone);
        border.BorderThickness = badge.Tone == UiTone.Neutral ? new Thickness(1) : new Thickness(1.25);
        title.Text = badge.Text;
        title.Foreground = UiTonePalette.Text(badge.Tone);
        detail.Text = detailText;
    }
}

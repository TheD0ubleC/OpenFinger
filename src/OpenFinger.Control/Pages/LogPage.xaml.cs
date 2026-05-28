namespace OpenFinger.Control.Pages;

public partial class LogPage : UserControl
{
    public LogPage()
    {
        InitializeComponent();
    }

    public void UpdateDiagnostics(DiagnosticsDashboardState state)
    {
        ApplyCard(SteamVrStatusBorder, SteamVrStatusText, SteamVrDetailText, state.SteamVr, state.SteamVrDetail);
        ApplyCard(DriverStatusBorder, DriverStatusText, DriverDetailText, state.Driver, state.DriverDetail);
        UpdateDriverButton.Content = state.DriverActionLabel;
        RemoveDriverButton.IsEnabled = state.DriverInstalled;
        FriendlyLogTextBox.Text = state.FriendlyLog;
        RawLogTextBox.Text = state.RawLog;
        SetAdvancedMode(state.ShowAdvanced);
    }

    public void SetAdvancedMode(bool enabled)
    {
        AdvancedPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
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

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
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

    private void OnRepairConfigClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.RepairConfig();
    }

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.OpenConfig();
    }

    private async void OnUpdateDriverClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.UpdateSteamVrDriverAsync();
        }
    }

    private async void OnRemoveDriverClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.RemoveSteamVrDriverAsync();
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

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ClearAllLogs();
    }
}

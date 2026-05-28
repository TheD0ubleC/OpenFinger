namespace OpenFinger.Control.Pages;

public partial class FirmwarePage : UserControl
{
    public FirmwarePage()
    {
        InitializeComponent();
        RefreshSourcePanels();
    }

    public void UpdateDashboard(FirmwareDashboardState state)
    {
        FirmwareSourceStatusText.Text = state.SourceStatus;
        CurrentFirmwareField.Value = state.CurrentFirmwareText;
        TargetFirmwareField.Value = state.TargetFirmwareText;
        RecommendationField.Value = state.RecommendationText;
        BootHintTextBlock.Text = string.IsNullOrWhiteSpace(state.BootHint)
            ? "刷写前请确认设备可进入下载模式。"
            : state.BootHint;
        SetAdvancedMode(state.ShowAdvanced);
        SetProgressState(state.Busy, state.ProgressText);
    }

    public void SetProgressState(bool busy, string? text)
    {
        FirmwareProgressBar.IsIndeterminate = busy;
        FirmwareProgressBar.Value = busy ? 0 : 1;
        FirmwareProgressTextBlock.Text = string.IsNullOrWhiteSpace(text) ? "等待开始" : text;
        StartFlashButton.IsEnabled = !busy;
        RefreshCatalogButton.IsEnabled = !busy;
        RefreshPortsButton.IsEnabled = !busy;
        ReverifyButton.IsEnabled = !busy;
    }

    public void SetProgressText(string text)
    {
        FirmwareProgressTextBlock.Text = text;
    }

    public void SetAdvancedMode(bool enabled)
    {
        AdvancedOptionsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RawLogPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshSourcePanels()
    {
        var source = FirmwareSourceComboBox?.SelectedValue as string ?? "bundled";
        ExternalSourcePanel.Visibility = string.Equals(source, "external", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnlineSourcePanel.Visibility = string.Equals(source, "online", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private async void OnStartFlashClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.StartFirmwareFlowAsync();
        }
    }

    private async void OnRefreshCatalogClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.RefreshFirmwareCatalogAsync(forceReload: true);
        }
    }

    private async void OnRefreshPortsClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.RefreshFirmwarePortsOnlyAsync();
        }
    }

    private async void OnReverifyClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.ReverifySelectedFirmwareAsync();
        }
    }

    private void OnBrowsePackageClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.BrowseFirmwarePackageManifest();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ClearAllLogs();
    }

    private async void OnFirmwareSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSourcePanels();
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.RefreshFirmwareCatalogAsync(forceReload: true);
        }
    }

    private void OnFirmwarePackageChanged(object sender, SelectionChangedEventArgs e)
    {
        GetOwnerWindow()?.NotifyFirmwarePackageSelectionChanged();
    }
}

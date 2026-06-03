
namespace OpenFinger.Control.Pages;

public partial class SettingsPage : UserControl
{
    private bool _suppressEvents;

    public SettingsPage()
    {
        InitializeComponent();
        CloseActionComboBox.ItemsSource = UiCloseActionCatalog.Options;
        TrayVisibilityComboBox.ItemsSource = UiTrayVisibilityCatalog.Options;
        LaunchPageComboBox.ItemsSource = UiPageCatalog.Options;
        ThemeModeComboBox.ItemsSource = UiThemeCatalog.Options;
    }

    public void UpdateSettings(SettingsDashboardState state)
    {
        _suppressEvents = true;
        try
        {
            CloseActionComboBox.SelectedValue = state.CloseButtonAction;
            TrayVisibilityComboBox.SelectedValue = state.TrayVisibility;
            ReduceLoadWhenHiddenCheckBox.IsChecked = state.ReduceLoadWhenHidden;
            RememberBoundsCheckBox.IsChecked = state.RememberWindowBounds;
            EnableWindowsStartupCheckBox.IsChecked = state.EnableWindowsStartup;
            StartHiddenOnWindowsStartupCheckBox.IsChecked = state.StartHiddenOnWindowsStartup;
            LaunchPageComboBox.SelectedValue = state.LaunchPage;
            RememberLastPageCheckBox.IsChecked = state.RememberLastPage;
            EnableTrayNotificationsCheckBox.IsChecked = state.EnableTrayNotifications;
            DeviceNotificationsCheckBox.IsChecked = state.EnableDeviceNotifications;
            FlashNotificationsCheckBox.IsChecked = state.EnableFlashNotifications;
            DriverNotificationsCheckBox.IsChecked = state.EnableDriverNotifications;
            UpdateNotificationsCheckBox.IsChecked = state.EnableUpdateNotifications;
            CheckUpdatesOnStartupCheckBox.IsChecked = state.CheckUpdatesOnStartup;
            PromptUpdateWhenAvailableCheckBox.IsChecked = state.PromptUpdateWhenAvailable;
            ThemeModeComboBox.SelectedValue = state.ThemeMode;
            ShowAdvancedCheckBox.IsChecked = state.ShowAdvanced;
            CurrentVersionField.Value = state.CurrentVersion;
            LatestVersionField.Value = state.LatestVersion;
            LatestPublishedField.Value = state.LatestPublishedText;
            LastCheckedField.Value = state.LastCheckedText;
            IgnoredVersionField.Value = state.IgnoredVersion;
            ReleaseAssetField.Value = state.ReleaseAssetName;
            UpdateStatusTextBlock.Text = state.UpdateStatus;
            ConfigPathField.Value = state.ConfigPath;
            UpdateStartupDependentState();
            UpdateNotificationDependentState();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private void UpdateStartupDependentState()
    {
        StartHiddenOnWindowsStartupCheckBox.IsEnabled = EnableWindowsStartupCheckBox.IsChecked == true;
    }

    private void UpdateNotificationDependentState()
    {
        var enabled = EnableTrayNotificationsCheckBox.IsChecked == true;
        DeviceNotificationsCheckBox.IsEnabled = enabled;
        FlashNotificationsCheckBox.IsEnabled = enabled;
        DriverNotificationsCheckBox.IsEnabled = enabled;
        UpdateNotificationsCheckBox.IsEnabled = enabled;
    }

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (CloseActionComboBox.SelectedValue is string value)
        {
            GetOwnerWindow()?.SetCloseButtonAction(value);
        }
    }

    private void OnTrayVisibilityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (TrayVisibilityComboBox.SelectedValue is string value)
        {
            GetOwnerWindow()?.SetTrayVisibilityMode(value);
        }
    }

    private void OnReduceLoadChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetReduceLoadWhenHidden(ReduceLoadWhenHiddenCheckBox.IsChecked == true);
    }

    private void OnRememberBoundsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetRememberWindowBounds(RememberBoundsCheckBox.IsChecked == true);
    }

    private void OnResetClosePromptClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ResetClosePromptBehavior();
    }

    private void OnEnableWindowsStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        UpdateStartupDependentState();
        GetOwnerWindow()?.SetEnableWindowsStartup(EnableWindowsStartupCheckBox.IsChecked == true);
    }

    private void OnStartHiddenOnWindowsStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetStartHiddenOnWindowsStartup(StartHiddenOnWindowsStartupCheckBox.IsChecked == true);
    }

    private void OnLaunchPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (LaunchPageComboBox.SelectedValue is string value)
        {
            GetOwnerWindow()?.SetLaunchPage(value);
        }
    }

    private void OnRememberLastPageChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetRememberLastPage(RememberLastPageCheckBox.IsChecked == true);
    }

    private void OnEnableTrayNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        UpdateNotificationDependentState();
        GetOwnerWindow()?.SetEnableTrayNotifications(EnableTrayNotificationsCheckBox.IsChecked == true);
    }

    private void OnDeviceNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetDeviceNotificationsEnabled(DeviceNotificationsCheckBox.IsChecked == true);
    }

    private void OnFlashNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetFlashNotificationsEnabled(FlashNotificationsCheckBox.IsChecked == true);
    }

    private void OnDriverNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetDriverNotificationsEnabled(DriverNotificationsCheckBox.IsChecked == true);
    }

    private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (ThemeModeComboBox.SelectedValue is string value)
        {
            GetOwnerWindow()?.SetThemeMode(value);
        }
    }

    private void OnShowAdvancedChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetAdvancedMode(ShowAdvancedCheckBox.IsChecked == true);
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.OpenConfigDirectory();
    }

    private void OnUpdateNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetUpdateNotificationsEnabled(UpdateNotificationsCheckBox.IsChecked == true);
    }

    private void OnCheckUpdatesOnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetCheckUpdatesOnStartup(CheckUpdatesOnStartupCheckBox.IsChecked == true);
    }

    private void OnPromptUpdateWhenAvailableChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        GetOwnerWindow()?.SetPromptUpdateWhenAvailable(PromptUpdateWhenAvailableCheckBox.IsChecked == true);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (GetOwnerWindow() is { } owner)
        {
            await owner.CheckForUpdatesAsync(userInitiated: true);
        }
    }

    private async void OnPreviewUpdateDialogClick(object sender, RoutedEventArgs e)
    {
        if (GetOwnerWindow() is { } owner)
        {
            await owner.PreviewUpdateDialogAsync();
        }
    }

    private void OnOpenReleasePageClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.OpenReleasePage();
    }

    private void OnOpenUpdatesFolderClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.OpenUpdatesDirectory();
    }

    private void OnClearIgnoredVersionClick(object sender, RoutedEventArgs e)
    {
        GetOwnerWindow()?.ClearIgnoredUpdateVersion();
    }
}

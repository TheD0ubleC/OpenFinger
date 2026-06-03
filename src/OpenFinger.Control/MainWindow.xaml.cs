using System.Windows.Controls;

namespace OpenFinger.Control;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InitializeBackend();
        InitializeShell();
    }

    private void OnHomeTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToHome();
    }

    private void OnDeviceTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToDevices();
    }

    private void OnFirmwareTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToFirmware();
    }

    private void OnCalibrationTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToCalibration();
    }

    private void OnStatusTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToStatus();
    }

    private void OnGestureTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToGestures();
    }

    private void OnDiagnosticsTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToDiagnostics();
    }

    private void OnSettingsTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToSettings();
    }

    private void OnAboutTabClick(object sender, RoutedEventArgs e)
    {
        NavigateToAbout();
    }

    public void NavigateToHome()
    {
        ShowPage(HomePageView, HomeTabButton);
    }

    public void NavigateToDevices()
    {
        ShowPage(DevicesPageView, DeviceTabButton);
    }

    public void NavigateToFirmware(DeviceVm? device = null)
    {
        if (device is not null)
        {
            SelectDevice(device);
        }

        ShowPage(FirmwarePageView, FirmwareTabButton);
    }

    public void NavigateToCalibration()
    {
        ShowPage(CalibrationPageView, CalibrationTabButton);
    }

    public void NavigateToGestures()
    {
        ShowPage(GesturePageView, GestureTabButton);
    }

    public void NavigateToStatus()
    {
        ShowPage(StatusPageView, StatusTabButton);
    }

    public void NavigateToDiagnostics()
    {
        ShowPage(DiagnosticsPageView, DiagnosticsTabButton);
    }

    public void NavigateToSettings()
    {
        ShowPage(SettingsPageView, SettingsTabButton);
    }

    public void NavigateToAbout()
    {
        ShowPage(AboutPageView, AboutTabButton);
    }

    private void ShowPage(UIElement activePage, Button activeButton)
    {
        HomePageView.Visibility = Visibility.Collapsed;
        DevicesPageView.Visibility = Visibility.Collapsed;
        FirmwarePageView.Visibility = Visibility.Collapsed;
        CalibrationPageView.Visibility = Visibility.Collapsed;
        GesturePageView.Visibility = Visibility.Collapsed;
        StatusPageView.Visibility = Visibility.Collapsed;
        DiagnosticsPageView.Visibility = Visibility.Collapsed;
        SettingsPageView.Visibility = Visibility.Collapsed;
        AboutPageView.Visibility = Visibility.Collapsed;

        activePage.Visibility = Visibility.Visible;
        ActivateButton(activeButton);
        RememberActivePage(activePage);
        RefreshUiFromState();
    }

    private void ActivateButton(Button activeButton)
    {
        HomeTabButton.Style = (Style)FindResource("NavButtonStyle");
        DeviceTabButton.Style = (Style)FindResource("NavButtonStyle");
        FirmwareTabButton.Style = (Style)FindResource("NavButtonStyle");
        CalibrationTabButton.Style = (Style)FindResource("NavButtonStyle");
        GestureTabButton.Style = (Style)FindResource("NavButtonStyle");
        StatusTabButton.Style = (Style)FindResource("NavButtonStyle");
        DiagnosticsTabButton.Style = (Style)FindResource("NavButtonStyle");
        SettingsTabButton.Style = (Style)FindResource("NavButtonStyle");
        AboutTabButton.Style = (Style)FindResource("NavButtonStyle");
        activeButton.Style = (Style)FindResource("NavButtonActiveStyle");
    }
}

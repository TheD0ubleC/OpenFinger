namespace OpenFinger.Control.Pages;


public partial class DevicesPage : UserControl
{
    public DevicesPage()
    {
        InitializeComponent();
    }

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private static DeviceVm? ResolveDevice(object? source)
    {
        return source switch
        {
            FrameworkElement { Tag: DeviceVm device } => device,
            _ => null
        };
    }

    private async void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.RefreshAllAsync(forceSerialProbe: true);
        }
    }

    private async void OnProvisionClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            await owner.ProvisionSelectedDeviceAsync(WifiPasswordTextBox.Text);
        }
    }

    private async void OnProvisionDeviceClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        var device = ResolveDevice(sender);
        if (owner is null || device is null)
        {
            return;
        }

        owner.SelectDevice(device);
        await owner.ProvisionSelectedDeviceAsync(WifiPasswordTextBox.Text);
    }

    private async void OnIdentifyDeviceClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        var device = ResolveDevice(sender);
        if (owner is null || device is null)
        {
            return;
        }

        owner.SelectDevice(device);
        await owner.IdentifySelectedDeviceAsync();
    }

    private async void OnForgetDeviceClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        var device = ResolveDevice(sender);
        if (owner is null || device is null)
        {
            return;
        }

        owner.SelectDevice(device);
        await owner.ForgetSelectedDeviceAsync();
    }

    private void OnOpenFirmwareClick(object sender, RoutedEventArgs e)
    {
        var owner = GetOwnerWindow();
        var device = ResolveDevice(sender);
        if (owner is null || device is null)
        {
            return;
        }

        owner.SelectDevice(device);
        owner.NavigateToFirmware(device);
    }

    private void OnDeviceRoleChanged(object sender, SelectionChangedEventArgs e)
    {
        var owner = GetOwnerWindow();
        if (owner is null || sender is not ComboBox comboBox)
        {
            return;
        }

        if (!comboBox.IsKeyboardFocusWithin && !comboBox.IsDropDownOpen)
        {
            return;
        }

        var device = ResolveDevice(sender);
        if (device is not null)
        {
            owner.NotifyRoleEdited(device);
        }
    }
}

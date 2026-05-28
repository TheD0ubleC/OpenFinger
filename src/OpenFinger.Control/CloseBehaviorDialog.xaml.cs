namespace OpenFinger.Control;

public enum CloseBehaviorChoice
{
    Cancel,
    Close,
    Tray
}

public partial class CloseBehaviorDialog : Window
{
    public CloseBehaviorDialog()
    {
        InitializeComponent();
    }

    public CloseBehaviorChoice Choice { get; private set; } = CloseBehaviorChoice.Cancel;
    public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Choice = CloseBehaviorChoice.Close;
        DialogResult = true;
    }

    private void OnTrayClick(object sender, RoutedEventArgs e)
    {
        Choice = CloseBehaviorChoice.Tray;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Choice = CloseBehaviorChoice.Cancel;
        DialogResult = false;
    }
}

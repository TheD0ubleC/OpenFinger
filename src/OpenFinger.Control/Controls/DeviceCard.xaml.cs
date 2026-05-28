using System.Windows.Media;

namespace OpenFinger.Control.Controls;

public partial class DeviceCard : UserControl
{
    public static readonly DependencyProperty DeviceNameProperty = DependencyProperty.Register(
        nameof(DeviceName), typeof(string), typeof(DeviceCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(DeviceCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusBrushProperty = DependencyProperty.Register(
        nameof(StatusBrush), typeof(Brush), typeof(DeviceCard), new PropertyMetadata(Brushes.Gray));

    public DeviceCard()
    {
        InitializeComponent();
    }

    public string DeviceName
    {
        get => (string)GetValue(DeviceNameProperty);
        set => SetValue(DeviceNameProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public Brush StatusBrush
    {
        get => (Brush)GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }
}

namespace OpenFinger.Control.Controls;
using System.Windows.Media;

public partial class StatusCard : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(StatusCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.Register(
        nameof(IndicatorBrush), typeof(Brush), typeof(StatusCard), new PropertyMetadata(null));

    public StatusCard()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush IndicatorBrush
    {
        get => (Brush)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }
}

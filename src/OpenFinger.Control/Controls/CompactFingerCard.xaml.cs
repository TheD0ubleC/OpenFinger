using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OpenFinger.Control.Controls;

public partial class CompactFingerCard : UserControl
{
    public static readonly DependencyProperty FingerNameProperty = DependencyProperty.Register(
        nameof(FingerName), typeof(string), typeof(CompactFingerCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CompactFingerCard), new PropertyMetadata(0d, OnValueChanged));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(string), typeof(CompactFingerCard), new PropertyMetadata("0%", OnValueChanged));

    public static readonly DependencyProperty RawValueProperty = DependencyProperty.Register(
        nameof(RawValue), typeof(int), typeof(CompactFingerCard), new PropertyMetadata(-1));

    public static readonly DependencyProperty MinValueProperty = DependencyProperty.Register(
        nameof(MinValue), typeof(int), typeof(CompactFingerCard), new PropertyMetadata(-1));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(int), typeof(CompactFingerCard), new PropertyMetadata(-1));

    public static readonly DependencyProperty IsProModeProperty = DependencyProperty.Register(
        nameof(IsProMode), typeof(bool), typeof(CompactFingerCard), new PropertyMetadata(false));

    public CompactFingerCard()
    {
        InitializeComponent();
        UpdateProgressArc();
    }

    public string FingerName
    {
        get => (string)GetValue(FingerNameProperty);
        set => SetValue(FingerNameProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string DisplayValue
    {
        get => (string)GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    public int RawValue
    {
        get => (int)GetValue(RawValueProperty);
        set => SetValue(RawValueProperty, value);
    }

    public int MinValue
    {
        get => (int)GetValue(MinValueProperty);
        set => SetValue(MinValueProperty, value);
    }

    public int MaxValue
    {
        get => (int)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public bool IsProMode
    {
        get => (bool)GetValue(IsProModeProperty);
        set => SetValue(IsProModeProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CompactFingerCard card)
        {
            card.UpdateProgressArc();
        }
    }

    private void UpdateProgressArc()
    {
        if (ProgressArc is null)
        {
            return;
        }

        var progress = Math.Clamp(Value, 0.0, 1.0);
        if (progress <= 0.0001)
        {
            ProgressArc.Data = Geometry.Empty;
            return;
        }

        const double radius = 10.4;
        const double center = 12.0;
        const double startAngle = -90.0;
        var endAngle = startAngle + (progress * 359.99);
        var startPoint = PointOnCircle(radius, startAngle);
        var endPoint = PointOnCircle(radius, endAngle);

        var figure = new PathFigure
        {
            StartPoint = new Point(center + startPoint.X, center + startPoint.Y),
            IsClosed = false,
            IsFilled = false
        };

        if (progress >= 0.999)
        {
            var midPoint = PointOnCircle(radius, 89.99);
            figure.Segments.Add(new ArcSegment(
                new Point(center + midPoint.X, center + midPoint.Y),
                new Size(radius, radius),
                0,
                true,
                SweepDirection.Clockwise,
                true));
            figure.Segments.Add(new ArcSegment(
                new Point(center + endPoint.X, center + endPoint.Y),
                new Size(radius, radius),
                0,
                true,
                SweepDirection.Clockwise,
                true));
        }
        else
        {
            figure.Segments.Add(new ArcSegment(
                new Point(center + endPoint.X, center + endPoint.Y),
                new Size(radius, radius),
                0,
                progress > 0.5,
                SweepDirection.Clockwise,
                true));
        }

        ProgressArc.Data = new PathGeometry([figure]);
    }

    private static Vector PointOnCircle(double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Vector(Math.Cos(radians) * radius, Math.Sin(radians) * radius);
    }
}

using System.Windows;
using System.Windows.Controls;

namespace OpenFinger.Control.Controls;

public partial class FingerCard : UserControl
{
    private const double AdcMax = 4095d;

    public static readonly DependencyProperty FingerNameProperty = DependencyProperty.Register(
        nameof(FingerName), typeof(string), typeof(FingerCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(FingerCard), new PropertyMetadata(0d, OnValueOrProChange));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(string), typeof(FingerCard), new PropertyMetadata("0.00", OnValueOrProChange));

    public static readonly DependencyProperty RawValueProperty = DependencyProperty.Register(
        nameof(RawValue), typeof(int), typeof(FingerCard), new PropertyMetadata(-1, OnValueOrProChange));

    public static readonly DependencyProperty MinValueProperty = DependencyProperty.Register(
        nameof(MinValue), typeof(int), typeof(FingerCard), new PropertyMetadata(-1, OnValueOrProChange));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(int), typeof(FingerCard), new PropertyMetadata(-1, OnValueOrProChange));

    public static readonly DependencyProperty IsProModeProperty = DependencyProperty.Register(
        nameof(IsProMode), typeof(bool), typeof(FingerCard), new PropertyMetadata(false, OnValueOrProChange));

    public FingerCard()
    {
        InitializeComponent();
        UpdateUI();
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

    private static void OnValueOrProChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FingerCard card)
        {
            card.UpdateUI();
        }
    }

    private void UpdateUI()
    {
        if (ProAdcGrid == null || ProAdcRangeBar == null || SimpleTextVal == null)
        {
            return;
        }

        if (IsProMode)
        {
            ProAdcGrid.Visibility = Visibility.Visible;
            ProAdcRangeBar.Visibility = Visibility.Visible;
            if (RawTextLabel != null)
            {
                RawTextLabel.Visibility = Visibility.Visible;
            }

            RawValueText.Text = RawValue >= 0 ? RawValue.ToString() : "--";
            var rangeLow = ResolveRangeLow();
            var rangeHigh = ResolveRangeHigh();
            MinAdcText.Text = rangeLow >= 0 ? rangeLow.ToString() : "--";
            MaxAdcText.Text = rangeHigh >= 0 ? rangeHigh.ToString() : "--";

            SimpleTextVal.Text = $"{DisplayValue} (物理强度)";
            UpdateRangeVisuals();
        }
        else
        {
            ProAdcGrid.Visibility = Visibility.Collapsed;
            ProAdcRangeBar.Visibility = Visibility.Collapsed;
            if (RawTextLabel != null)
            {
                RawTextLabel.Visibility = Visibility.Collapsed;
            }

            if (ProAdcCalibratedRange != null)
            {
                ProAdcCalibratedRange.Visibility = Visibility.Collapsed;
            }

            if (ProAdcPointer != null)
            {
                ProAdcPointer.Visibility = Visibility.Collapsed;
            }

            SimpleTextVal.Text = DisplayValue;
        }
    }

    private void OnProAdcRangeBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRangeVisuals();
    }

    private void UpdateRangeVisuals()
    {
        if (!IsProMode
            || ProAdcRangeBar == null
            || ProAdcRangeCanvas == null
            || ProAdcCalibratedRange == null
            || ProAdcPointer == null)
        {
            return;
        }

        var width = ProAdcRangeBar.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        ProAdcRangeCanvas.Width = width;
        ProAdcRangeCanvas.Height = 10;

        var rangeLow = ResolveRangeLow();
        var rangeHigh = ResolveRangeHigh();
        if (rangeLow >= 0 && rangeHigh >= 0 && rangeLow != rangeHigh)
        {
            var start = Normalize(rangeLow);
            var end = Normalize(rangeHigh);
            var left = start * width;
            var rangeWidth = Math.Max(2, (end - start) * width);
            ProAdcCalibratedRange.Width = rangeWidth;
            Canvas.SetLeft(ProAdcCalibratedRange, left);
            ProAdcCalibratedRange.Visibility = Visibility.Visible;
        }
        else
        {
            ProAdcCalibratedRange.Visibility = Visibility.Collapsed;
        }

        if (RawValue >= 0)
        {
            var pointerLeft = Math.Clamp((Normalize(RawValue) * width) - (ProAdcPointer.Width / 2), 0, Math.Max(0, width - ProAdcPointer.Width));
            Canvas.SetLeft(ProAdcPointer, pointerLeft);
            ProAdcPointer.Visibility = Visibility.Visible;
        }
        else
        {
            ProAdcPointer.Visibility = Visibility.Collapsed;
        }
    }

    private int ResolveRangeLow()
    {
        if (MinValue < 0 || MaxValue < 0)
        {
            return -1;
        }

        return Math.Min(MinValue, MaxValue);
    }

    private int ResolveRangeHigh()
    {
        if (MinValue < 0 || MaxValue < 0)
        {
            return -1;
        }

        return Math.Max(MinValue, MaxValue);
    }

    private static double Normalize(int value)
    {
        return Math.Clamp(value / AdcMax, 0.0, 1.0);
    }
}

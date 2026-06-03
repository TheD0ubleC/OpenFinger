using System.Collections.ObjectModel;

namespace OpenFinger.Control.Pages;

public partial class GesturePage : UserControl
{
    private bool _suppressEvents;

    public ObservableCollection<GestureRowState> LeftRows { get; } = new();
    public ObservableCollection<GestureRowState> RightRows { get; } = new();

    public GesturePage()
    {
        InitializeComponent();
        LeftItemsControl.ItemsSource = LeftRows;
        RightItemsControl.ItemsSource = RightRows;
    }

    public void UpdateDashboard(GestureDashboardState state)
    {
        _suppressEvents = true;
        try
        {
            LeftEnabledCheckBox.IsChecked = state.Left.Enabled;
            RightEnabledCheckBox.IsChecked = state.Right.Enabled;
            LeftSummaryText.Text = state.Left.Summary;
            RightSummaryText.Text = state.Right.Summary;
            LeftLiveSummaryText.Text = state.Left.LiveSummary;
            RightLiveSummaryText.Text = state.Right.LiveSummary;
            MergeRows(LeftRows, state.Left.Rows);
            MergeRows(RightRows, state.Right.Rows);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private static void MergeRows(ObservableCollection<GestureRowState> target, IReadOnlyList<GestureRowState> rows)
    {
        var existingByKey = target.ToDictionary(item => item.ComboKey, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rows.Count; index++)
        {
            var source = rows[index];
            if (!existingByKey.TryGetValue(source.ComboKey, out var row))
            {
                row = new GestureRowState();
                if (index >= target.Count)
                {
                    target.Add(row);
                }
                else
                {
                    target.Insert(index, row);
                }
            }
            else
            {
                var currentIndex = target.IndexOf(row);
                if (currentIndex >= 0 && currentIndex != index)
                {
                    target.Move(currentIndex, index);
                }
            }

            ApplyRow(row, source);
            existingByKey.Remove(source.ComboKey);
        }

        foreach (var stale in existingByKey.Values.ToArray())
        {
            target.Remove(stale);
        }
    }

    private static void ApplyRow(GestureRowState target, GestureRowState source)
    {
        target.Side = source.Side;
        target.ComboKey = source.ComboKey;
        target.ComboLabel = source.ComboLabel;
        target.Enabled = source.Enabled;
        target.ButtonValue = source.ButtonValue;
        target.ButtonLabel = source.ButtonLabel;
        target.ButtonOptions = source.ButtonOptions;
        target.Calibrated = source.Calibrated;
        target.CalibrationActionLabel = source.CalibrationActionLabel;
        target.Active = source.Active;
        target.Score = source.Score;
        target.TriggerThreshold = source.TriggerThreshold;
        target.ReleaseThreshold = source.ReleaseThreshold;
        target.ConfidenceThreshold = source.ConfidenceThreshold;
        target.StatusText = source.StatusText;
        target.StateLabel = source.StateLabel;
        target.StateKind = source.StateKind;
        target.AdvancedText = source.AdvancedText;
    }

    private MainWindow? GetOwnerWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private void OnHandEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is FrameworkElement element && element.Tag is string side && sender is System.Windows.Controls.CheckBox checkBox)
        {
            GetOwnerWindow()?.SetGestureHandEnabled(side, checkBox.IsChecked == true);
        }
    }

    private void OnGestureEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: GestureRowState row } && sender is System.Windows.Controls.CheckBox checkBox)
        {
            GetOwnerWindow()?.SetGestureMappingEnabled(row.Side, row.ComboKey, checkBox.IsChecked == true);
        }
    }

    private void OnMappedButtonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is ComboBox { DataContext: GestureRowState row, SelectedValue: string mappedButton })
        {
            GetOwnerWindow()?.SetGestureMappedButton(row.Side, row.ComboKey, mappedButton);
        }
    }

    private async void OnCalibrateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GestureRowState row })
        {
            var owner = GetOwnerWindow();
            if (owner is not null)
            {
                await owner.CalibrateGestureAsync(row.Side, row.ComboKey);
            }
        }
    }

    private async void OnCalibrateHandClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string side })
        {
            var owner = GetOwnerWindow();
            if (owner is not null)
            {
                await owner.CalibrateGestureHandAsync(side);
            }
        }
    }
}

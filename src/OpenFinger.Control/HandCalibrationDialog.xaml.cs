using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OpenFinger.Control;

public partial class HandCalibrationDialog : Window
{
    private const int PrepareSeconds = 3;

    public sealed class CalibrationResult
    {
        public string FingerName { get; init; } = string.Empty;
        public int OpenRaw { get; init; } = -1;
        public int ClosedRaw { get; init; } = -1;
    }

    private sealed class CalibrationEntry : ObservableObject
    {
        private string _displayName = string.Empty;
        private string _name = string.Empty;
        private int _openRaw = -1;
        private int _closedRaw = -1;
        private string _summary = "等待开始";
        private string _detail = "还没有开始记录";

        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public int OpenRaw
        {
            get => _openRaw;
            set
            {
                if (SetProperty(ref _openRaw, value))
                {
                    Raise(nameof(OpenRawText));
                }
            }
        }

        public int ClosedRaw
        {
            get => _closedRaw;
            set
            {
                if (SetProperty(ref _closedRaw, value))
                {
                    Raise(nameof(ClosedRawText));
                }
            }
        }

        public string Summary { get => _summary; set => SetProperty(ref _summary, value); }
        public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
        public string OpenRawText => OpenRaw >= 0 ? $"伸直 {OpenRaw}" : "伸直 --";
        public string ClosedRawText => ClosedRaw >= 0 ? $"弯曲 {ClosedRaw}" : "弯曲 --";
    }

    private readonly ObservableCollection<FingerRuntimeVm> _fingers;
    private readonly ObservableCollection<CalibrationEntry> _entries = new();
    private readonly List<CalibrationResult> _results = new();
    private CancellationTokenSource? _runCts;

    public IReadOnlyList<CalibrationResult> Results => _results;

    public HandCalibrationDialog(string side, ObservableCollection<FingerRuntimeVm> fingers, HandConfig handConfig)
    {
        InitializeComponent();
        _fingers = fingers;

        foreach (var pair in handConfig.Fingers.Where(item =>
                     item.Value.Enabled
                     && fingers.Any(finger => string.Equals(finger.Name, item.Key, StringComparison.OrdinalIgnoreCase) && finger.Active)))
        {
            _entries.Add(new CalibrationEntry
            {
                Name = pair.Key,
                DisplayName = GetFingerDisplayName(pair.Key)
            });
        }

        ResultItemsControl.ItemsSource = _entries;
        TitleText.Text = side == "left" ? "左手校准" : "右手校准";
        SubtitleText.Text = $"按提示完成{(side == "left" ? "左手" : "右手")}每根启用手指的伸直和弯曲记录。";
        ApplyAdvancedMode(false);

        if (_entries.Count == 0)
        {
            StepText.Text = "没有可校准的手指";
            InstructionText.Text = "当前这只手还没有检测到可用于校准的手指输入。";
            FriendlyHintText.Text = "请先确认设备已经连接，并且手指实时输入能正常变化。";
            StartButton.IsEnabled = false;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        _runCts = new CancellationTokenSource();
        _ = RunCalibrationAsync(_runCts.Token);
    }

    private async Task RunCalibrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var index = 0; index < _entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = _entries[index];
                var fingerVm = _fingers.FirstOrDefault(item => string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                if (fingerVm is null)
                {
                    entry.Summary = "未检测到输入";
                    entry.Detail = "没有找到这根手指的实时数据。";
                    continue;
                }

                await RunSingleStepAsync(entry, fingerVm, isOpenStep: true, cancellationToken);
                await RunSingleStepAsync(entry, fingerVm, isOpenStep: false, cancellationToken);
                entry.Summary = "已完成";
                entry.Detail = "伸直值和弯曲值都已记录。";
            }

            StepText.Text = "校准完成";
            InstructionText.Text = "这只手的所有启用手指都已经记录完成。";
            FriendlyHintText.Text = "结果会自动保存并立刻生效。";
            FooterText.Text = "校准完成，正在保存。";
            BuildCalibrationResults();
            await Task.Delay(500, cancellationToken);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            FooterText.Text = "校准已取消。";
        }
    }

    private async Task RunSingleStepAsync(
        CalibrationEntry entry,
        FingerRuntimeVm fingerVm,
        bool isOpenStep,
        CancellationToken cancellationToken)
    {
        var actionText = isOpenStep ? "自然伸直并保持不动" : "慢慢弯到最用力的位置并保持不动";
        StepText.Text = $"{entry.DisplayName} · {(isOpenStep ? "伸直记录" : "弯曲记录")}";
        InstructionText.Text = $"请让{entry.DisplayName}{actionText}，倒计时结束后系统会自动记录。";
        FriendlyHintText.Text = isOpenStep
            ? "不用刻意绷紧，保持自然稳定就可以。"
            : "保持这个姿势一小会儿，系统会自动取较稳的结果。";

        for (var remain = PrepareSeconds; remain >= 1; remain--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FooterText.Text = $"{entry.DisplayName}：{remain} 秒后开始记录。";
            await Task.Delay(1000, cancellationToken);
        }

        FooterText.Text = $"正在记录 {entry.DisplayName}…";
        var (median, range) = await SampleFingerRawAsync(fingerVm, cancellationToken);
        if (isOpenStep)
        {
            entry.OpenRaw = median;
            entry.Summary = "已记录伸直，接下来请弯曲";
            entry.Detail = median >= 0 ? $"伸直值 {median}，采样波动 {range}" : "伸直记录失败，没有采到有效数据。";
            FooterText.Text = median >= 0
                ? $"{entry.DisplayName} 的伸直姿势已记录。"
                : $"{entry.DisplayName} 的伸直姿势没有记录成功。";
        }
        else
        {
            entry.ClosedRaw = median;
            entry.Summary = median >= 0 ? "已记录弯曲" : "弯曲记录失败";
            entry.Detail = median >= 0 ? $"弯曲值 {median}，采样波动 {range}" : "弯曲记录失败，没有采到有效数据。";
            FooterText.Text = median >= 0
                ? $"{entry.DisplayName} 的弯曲姿势已记录。"
                : $"{entry.DisplayName} 的弯曲姿势没有记录成功。";
        }

        await Task.Delay(500, cancellationToken);
    }

    private void OnAdvancedModeChanged(object sender, RoutedEventArgs e)
    {
        ApplyAdvancedMode(AdvancedModeToggle.IsChecked == true);
    }

    private void ApplyAdvancedMode(bool enabled)
    {
        // Advanced mode now only reveals detailed values inside the result list.
    }

    private void BuildCalibrationResults()
    {
        _results.Clear();
        foreach (var entry in _entries)
        {
            if (entry.OpenRaw < 0 || entry.ClosedRaw < 0 || entry.OpenRaw == entry.ClosedRaw)
            {
                continue;
            }

            _results.Add(new CalibrationResult
            {
                FingerName = entry.Name,
                OpenRaw = entry.OpenRaw,
                ClosedRaw = entry.ClosedRaw
            });
        }
    }

    private static async Task<(int Median, int Range)> SampleFingerRawAsync(FingerRuntimeVm finger, CancellationToken cancellationToken)
    {
        var samples = new List<int>();
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(40, cancellationToken);
            if (finger.Raw >= 0)
            {
                samples.Add(finger.Raw);
            }
        }

        if (samples.Count == 0)
        {
            return (-1, 0);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var range = samples[^1] - samples[0];
        return (median, range);
    }

    private static string GetFingerDisplayName(string finger)
    {
        return finger switch
        {
            "thumb" => "拇指",
            "index" => "食指",
            "middle" => "中指",
            "ring" => "无名指",
            "pinky" => "小指",
            _ => finger
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        base.OnClosed(e);
    }
}

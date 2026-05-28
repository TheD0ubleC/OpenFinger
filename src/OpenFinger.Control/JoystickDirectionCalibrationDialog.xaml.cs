using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OpenFinger.Control;

public partial class JoystickDirectionCalibrationDialog : Window
{
    private const int PrepareSeconds = 2;
    private const int SampleCount = 14;
    private const int SampleIntervalMs = 45;
    private const double MinMovementLength = 220.0;

    public readonly record struct RawJoystickSnapshot(int RawX, int RawY, bool Available);

    private sealed class StepResultEntry : ObservableObject
    {
        private string _displayName = string.Empty;
        private string _sampleText = "--";
        private string _status = "待记录";

        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
        public string SampleText { get => _sampleText; set => SetProperty(ref _sampleText, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }
    }

    private sealed record CalibrationStep(string Key, string DisplayName, string Instruction, double ExpectedX, double ExpectedY);

    private readonly Func<RawJoystickSnapshot> _snapshotProvider;
    private readonly ObservableCollection<StepResultEntry> _entries = new();
    private readonly List<(CalibrationStep Step, int RawX, int RawY)> _samples = new();
    private readonly CalibrationStep[] _steps =
    [
        new("center", "中心", "请松开摇杆，保持自然回中。", 0, 0),
        new("up", "向上", "请把摇杆推到上方并保持。", 0, -1),
        new("right", "向右", "请把摇杆推到右侧并保持。", 1, 0),
        new("down", "向下", "请把摇杆推到下方并保持。", 0, 1),
        new("left", "向左", "请把摇杆推到左侧并保持。", -1, 0)
    ];
    private CancellationTokenSource? _runCts;

    public string ResultOrientation { get; private set; } = JoystickOrientationCatalog.Normal;
    public int ResultCenterRawX { get; private set; } = -1;
    public int ResultCenterRawY { get; private set; } = -1;

    public JoystickDirectionCalibrationDialog(string side, Func<RawJoystickSnapshot> snapshotProvider)
    {
        InitializeComponent();
        _snapshotProvider = snapshotProvider;

        TitleText.Text = side == "left" ? "左手摇杆方向校准" : "右手摇杆方向校准";
        SubtitleText.Text = $"将为{(side == "left" ? "左手" : "右手")}摇杆自动识别上下左右朝向，并同步记录中心点。";

        foreach (var step in _steps)
        {
            _entries.Add(new StepResultEntry
            {
                DisplayName = step.DisplayName
            });
        }

        ResultGrid.ItemsSource = _entries;
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
            _samples.Clear();
            for (var index = 0; index < _steps.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = _steps[index];
                var entry = _entries[index];
                await RunStepAsync(step, entry, cancellationToken);
            }

            var center = _samples.First(sample => sample.Step.Key == "center");
            ResultCenterRawX = center.RawX;
            ResultCenterRawY = center.RawY;

            var inferredOrientation = InferOrientation(_samples, center.RawX, center.RawY);
            if (string.IsNullOrWhiteSpace(inferredOrientation))
            {
                FooterText.Text = "这次采样方向不够稳定，没法可靠判断朝向。";
                StepText.Text = "校准未完成";
                InstructionText.Text = "请重新开始，并尽量把摇杆推到明显的上、右、下、左四个方向。";
                HintText.Text = "如果某个方向本身就不稳定，可以先确认摇杆接线和供电。";
                StartButton.IsEnabled = true;
                return;
            }

            ResultOrientation = inferredOrientation;
            var label = JoystickOrientationCatalog.Options.FirstOrDefault(option =>
                string.Equals(option.Value, inferredOrientation, StringComparison.OrdinalIgnoreCase))?.Label ?? inferredOrientation;

            StepText.Text = "校准完成";
            InstructionText.Text = $"已自动选择：{label}";
            HintText.Text = "结果会同时写入摇杆方向和中心点。";
            FooterText.Text = $"校准完成，已识别为“{label}”。";
            await Task.Delay(450, cancellationToken);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            FooterText.Text = "校准已取消。";
        }
    }

    private async Task RunStepAsync(CalibrationStep step, StepResultEntry entry, CancellationToken cancellationToken)
    {
        StepText.Text = $"{step.DisplayName}校准";
        InstructionText.Text = step.Instruction;
        HintText.Text = step.Key == "center"
            ? "保持摇杆自然回中，不要触碰。"
            : "等倒计时结束后，保持当前方向不要松开。";

        for (var remain = PrepareSeconds; remain >= 1; remain--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FooterText.Text = $"{step.DisplayName}将在 {remain} 秒后开始采样。";
            await Task.Delay(1000, cancellationToken);
        }

        FooterText.Text = $"正在采样“{step.DisplayName}”…";
        var sample = await SampleJoystickAsync(cancellationToken);
        if (sample is null)
        {
            entry.Status = "未收到摇杆数据";
            throw new OperationCanceledException("未收到摇杆数据。");
        }

        entry.SampleText = $"{sample.Value.RawX} / {sample.Value.RawY}";
        entry.Status = $"波动 {sample.Value.RangeX} / {sample.Value.RangeY}";
        _samples.Add((step, sample.Value.RawX, sample.Value.RawY));
        FooterText.Text = $"{step.DisplayName}已记录：{sample.Value.RawX} / {sample.Value.RawY}";
        await Task.Delay(300, cancellationToken);
    }

    private async Task<(int RawX, int RawY, int RangeX, int RangeY)?> SampleJoystickAsync(CancellationToken cancellationToken)
    {
        var xs = new List<int>();
        var ys = new List<int>();

        for (var i = 0; i < SampleCount; i++)
        {
            await Task.Delay(SampleIntervalMs, cancellationToken);
            var snapshot = _snapshotProvider();
            if (!snapshot.Available || snapshot.RawX < 0 || snapshot.RawY < 0)
            {
                continue;
            }

            xs.Add(snapshot.RawX);
            ys.Add(snapshot.RawY);
        }

        if (xs.Count < 6 || ys.Count < 6)
        {
            return null;
        }

        xs.Sort();
        ys.Sort();
        return (xs[xs.Count / 2], ys[ys.Count / 2], xs[^1] - xs[0], ys[^1] - ys[0]);
    }

    private static string? InferOrientation(IReadOnlyList<(CalibrationStep Step, int RawX, int RawY)> samples, int centerX, int centerY)
    {
        var directionalSamples = samples
            .Where(item => item.Step.Key != "center")
            .Select(item =>
            {
                var dx = item.RawX - centerX;
                var dy = item.RawY - centerY;
                var length = Math.Sqrt(dx * dx + dy * dy);
                return new
                {
                    item.Step,
                    Dx = dx,
                    Dy = dy,
                    Length = length
                };
            })
            .Where(item => item.Length >= MinMovementLength)
            .ToList();

        if (directionalSamples.Count < 3)
        {
            return null;
        }

        string? bestOrientation = null;
        var bestScore = double.NegativeInfinity;

        foreach (var candidate in JoystickOrientationCatalog.Options.Select(option => option.Value))
        {
            var score = 0.0;
            foreach (var sample in directionalSamples)
            {
                var normalizedX = sample.Dx / sample.Length;
                var normalizedY = sample.Dy / sample.Length;
                var oriented = JoystickOrientationCatalog.Apply(candidate, normalizedX, normalizedY);
                score += oriented.X * sample.Step.ExpectedX + oriented.Y * sample.Step.ExpectedY;
            }

            score /= directionalSamples.Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestOrientation = candidate;
            }
        }

        return bestScore >= 0.72 ? bestOrientation : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        base.OnClosed(e);
    }
}

namespace OpenFinger.Control;

public partial class GestureCalibrationDialog : Window
{
    private readonly TaskCompletionSource<bool> _startSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GestureCalibrationDialog(string handLabel, string comboLabel)
    {
        InitializeComponent();
        TitleTextBlock.Text = $"{handLabel} {comboLabel}";
        SubtitleTextBlock.Text = "先记录自然张开，再连续完成 5 次明确捏合。系统会自动生成更稳的触发和释放阈值。";
        ShowReady();
    }

    public bool IsCancellationRequested { get; private set; }

    public Task<bool> WaitForStartAsync() => _startSource.Task;

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        _startSource.TrySetResult(true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        IsCancellationRequested = true;
        if (!_startSource.Task.IsCompleted)
        {
            _startSource.TrySetResult(false);
        }

        Close();
    }

    public void ShowReady()
    {
        StepTextBlock.Text = "准备开始";
        HintTextBlock.Text = "点开始后，先保持自然张开，再按提示连续完成 5 次明显捏合。";
        LiveTextBlock.Text = "校准前请先确认当前手已经在实时输入。";
        SamplingProgressBar.Value = 0;
    }

    public void ShowSampling(string step, int durationMs)
    {
        StepTextBlock.Text = step;
        HintTextBlock.Text = "保持动作稳定，系统会自动采样。";
        LiveTextBlock.Text = "正在等待实时数据...";
        SamplingProgressBar.Maximum = Math.Max(1, durationMs);
        SamplingProgressBar.Value = 0;
        StartButton.IsEnabled = false;
        CancelButton.Content = "终止";
    }

    public void UpdateSamplingProgress(int remainingMs, double score, bool available)
    {
        SamplingProgressBar.Value = SamplingProgressBar.Maximum - remainingMs;
        LiveTextBlock.Text = available
            ? $"当前识别强度：{score:0.00}"
            : "当前没有拿到稳定输入，请保持设备在线。";
    }

    public void ShowFailure(string message)
    {
        StepTextBlock.Text = "校准失败";
        HintTextBlock.Text = message;
        LiveTextBlock.Text = "可以直接关闭窗口后重试。";
        StartButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "关闭";
    }

    public void ShowSuccess(string message)
    {
        StepTextBlock.Text = "校准完成";
        HintTextBlock.Text = message;
        LiveTextBlock.Text = "关闭窗口后，这个组合就会按新阈值参与识别。";
        StartButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "完成";
    }
}

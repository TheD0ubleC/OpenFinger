using System.Diagnostics;

namespace OpenFinger.Control;

public partial class MainWindow
{
    private const int GestureCalibrationRepeatCount = 5;
    private const int GestureCalibrationMinimumValidRepeats = 3;
    private const int GestureCalibrationSampleDurationMs = 1150;
    private const int GestureCalibrationOpenDurationMs = 1500;
    private const double GestureCompetitionMargin = 0.035;
    private const double GestureRiseAlpha = 0.42;
    private const double GestureFallAlpha = 0.24;
    private static readonly TimeSpan GestureDebounceWindow = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan GestureMinHoldWindow = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan GestureCooldownWindow = TimeSpan.FromMilliseconds(120);

    private sealed class GestureRuntimeLatchState
    {
        public double Score { get; set; }
        public double RawScore { get; set; }
        public double PrimaryScore { get; set; }
        public double SupportScore { get; set; }
        public double StabilityScore { get; set; }
        public bool Active { get; set; }
        public DateTime AboveTriggerSinceUtc { get; set; } = DateTime.MinValue;
        public DateTime ActivatedUtc { get; set; } = DateTime.MinValue;
        public DateTime ReleasedUtc { get; set; } = DateTime.MinValue;
    }

    private sealed class GestureButtonPublishState
    {
        public bool TriggerClick { get; set; }
        public bool GripClick { get; set; }
        public bool PrimaryClick { get; set; }
        public bool SecondaryClick { get; set; }
        public bool SystemClick { get; set; }
    }

    private sealed class GestureCalibrationProbe
    {
        public bool Available { get; init; }
        public double Score { get; init; }
    }

    private sealed class GestureBendObservation
    {
        public required double[] Bends { get; init; }
    }

    private sealed class GestureRepeatAggregate
    {
        public required double ThumbMedian { get; init; }
        public required double TargetMedian { get; init; }
        public required double[] SupportMedians { get; init; }
        public double PrimaryScore { get; init; }
    }

    private sealed class GestureEvaluationMetrics
    {
        public double PrimaryScore { get; init; }
        public double SupportScore { get; init; }
        public double StabilityScore { get; init; }
        public double ConfidenceScore { get; init; }
    }

    private readonly Dictionary<string, Dictionary<string, GestureRuntimeLatchState>> _gestureRuntimeStateBySide = CreateGestureRuntimeState();

    private static Dictionary<string, Dictionary<string, GestureRuntimeLatchState>> CreateGestureRuntimeState()
    {
        static Dictionary<string, GestureRuntimeLatchState> CreateComboMap()
        {
            return GestureComboCatalog.Definitions.ToDictionary(
                item => item.Key,
                _ => new GestureRuntimeLatchState(),
                StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, Dictionary<string, GestureRuntimeLatchState>>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = CreateComboMap(),
            ["right"] = CreateComboMap()
        };
    }

    public void SetGestureHandEnabled(string side, bool enabled)
    {
        var hand = GetGestureHandSettings(side);
        hand.Enabled = enabled;
        _configStore.Save(_config);
        PublishRuntimeFrame();
        RefreshUiFromState();
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}手势已{(enabled ? "启用" : "关闭")}。", 4);
    }

    public void SetGestureMappingEnabled(string side, string comboKey, bool enabled)
    {
        var binding = GetGestureBinding(side, comboKey);
        binding.Enabled = enabled;
        _configStore.Save(_config);
        PublishRuntimeFrame();
        RefreshUiFromState();
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}{GestureComboCatalog.Get(comboKey).Label}已{(enabled ? "启用" : "关闭")}。", 4);
    }

    public void SetGestureMappedButton(string side, string comboKey, string mappedButton)
    {
        var binding = GetGestureBinding(side, comboKey);
        binding.MappedButton = GestureButtonCatalog.Normalize(mappedButton);
        binding.Enabled = !string.Equals(binding.MappedButton, GestureButtonCatalog.Disabled, StringComparison.OrdinalIgnoreCase);
        _configStore.Save(_config);
        PublishRuntimeFrame();
        RefreshUiFromState();
        SetPinnedStatusLine(
            $"{(IsLeftSide(side) ? "左手" : "右手")}{GestureComboCatalog.Get(comboKey).Label}已映射到 {GestureButtonCatalog.GetLabel(side, binding.MappedButton)}。",
            4);
    }

    public async Task CalibrateGestureHandAsync(string side)
    {
        var handLabel = IsLeftSide(side) ? "左手" : "右手";
        var dialog = new GestureCalibrationDialog(handLabel, "整手手势")
        {
            Owner = this
        };
        dialog.Show();
        Activate();
        if (!await dialog.WaitForStartAsync())
        {
            SetPinnedStatusLine("已取消手势校准。", 3);
            return;
        }

        var openSamples = await CollectGestureBendSamplesAsync(
            dialog,
            side,
            null,
            "请自然张开整只手，保持稳定。接下来每个组合都会连续记录 5 次明显捏合。",
            GestureCalibrationOpenDurationMs);
        if (dialog.IsCancellationRequested)
        {
            SetPinnedStatusLine("已取消手势校准。", 3);
            return;
        }

        if (openSamples.Count < 8)
        {
            dialog.ShowFailure("没有收集到足够的张开数据。先确认设备在线，再重试一次。");
            SetPinnedStatusLine("手势校准失败：张开阶段没有拿到稳定数据。", 5);
            return;
        }

        var calibrated = new List<string>();
        var failed = new List<string>();
        foreach (var combo in GestureComboCatalog.Definitions)
        {
            if (dialog.IsCancellationRequested)
            {
                SetPinnedStatusLine("已取消手势校准。", 3);
                return;
            }

            var pinchRepeats = await CollectGestureRepeatAggregatesAsync(dialog, side, combo);
            if (dialog.IsCancellationRequested)
            {
                SetPinnedStatusLine("已取消手势校准。", 3);
                return;
            }

            if (!TryCreateGestureCalibration(combo, openSamples, pinchRepeats, out var calibration, out _))
            {
                failed.Add(combo.Label);
                continue;
            }

            var binding = GetGestureBinding(side, combo.Key);
            binding.Calibration = calibration;
            calibrated.Add(combo.Label);
        }

        _configStore.Save(_config);
        PublishRuntimeFrame();
        RefreshUiFromState();

        if (calibrated.Count == GestureComboCatalog.Definitions.Count)
        {
            dialog.ShowSuccess("整只手的捏合手势已经全部校准完成。");
            SetPinnedStatusLine($"{handLabel}手势已完成整手校准。", 5);
            return;
        }

        if (calibrated.Count > 0)
        {
            dialog.ShowFailure($"已完成 {calibrated.Count} 个组合，未完成：{string.Join("、", failed)}。可以直接重试整手校准。");
            SetPinnedStatusLine($"{handLabel}手势已部分校准，未完成：{string.Join("、", failed)}。", 6);
            return;
        }

        dialog.ShowFailure("这次没有得到足够稳定的捏合差异，请把目标两指捏得更明显一些再重试。");
        SetPinnedStatusLine($"{handLabel}手势校准失败：没有得到稳定结果。", 6);
    }

    public async Task CalibrateGestureAsync(string side, string comboKey)
    {
        var combo = GestureComboCatalog.Get(comboKey);
        var dialog = new GestureCalibrationDialog(IsLeftSide(side) ? "左手" : "右手", combo.Label)
        {
            Owner = this
        };
        dialog.Show();
        Activate();
        if (!await dialog.WaitForStartAsync())
        {
            SetPinnedStatusLine("已取消手势校准。", 3);
            return;
        }

        var openSamples = await CollectGestureBendSamplesAsync(
            dialog,
            side,
            null,
            "请自然张开整只手，保持稳定。接下来会连续记录 5 次明显捏合。",
            GestureCalibrationOpenDurationMs);
        if (dialog.IsCancellationRequested)
        {
            SetPinnedStatusLine("已取消手势校准。", 3);
            return;
        }

        if (openSamples.Count < 8)
        {
            dialog.ShowFailure("没有收集到足够的张开数据。先确认设备在线，再重试一次。");
            SetPinnedStatusLine("手势校准失败：张开阶段没有拿到稳定数据。", 5);
            return;
        }

        var pinchRepeats = await CollectGestureRepeatAggregatesAsync(dialog, side, combo);
        if (dialog.IsCancellationRequested)
        {
            SetPinnedStatusLine("已取消手势校准。", 3);
            return;
        }

        if (!TryCreateGestureCalibration(combo, openSamples, pinchRepeats, out var calibration, out var failureReason))
        {
            dialog.ShowFailure(failureReason);
            SetPinnedStatusLine($"手势校准失败：{failureReason}", 5);
            return;
        }

        var binding = GetGestureBinding(side, comboKey);
        binding.Calibration = calibration;
        _configStore.Save(_config);
        PublishRuntimeFrame();
        RefreshUiFromState();
        dialog.ShowSuccess($"已完成 5 次校准：主触发 {binding.Calibration.TriggerThreshold:0.00} / 释放 {binding.Calibration.ReleaseThreshold:0.00}");
        SetPinnedStatusLine($"{(IsLeftSide(side) ? "左手" : "右手")}{combo.Label}手势已校准。", 5);
    }

    private async Task<List<GestureBendObservation>> CollectGestureBendSamplesAsync(
        GestureCalibrationDialog dialog,
        string side,
        string? comboKey,
        string message,
        int durationMs)
    {
        dialog.ShowSampling(message, durationMs);
        var samples = new List<GestureBendObservation>();
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < durationMs)
        {
            if (dialog.IsCancellationRequested)
            {
                break;
            }

            var bends = TryBuildGestureCalibrationBends(side);
            var previewScore = comboKey is null || bends is null ? 0.0 : ComputeGesturePreviewScore(comboKey, bends);
            dialog.UpdateSamplingProgress((int)Math.Clamp(durationMs - deadline.ElapsedMilliseconds, 0, durationMs), previewScore, bends is not null);
            if (bends is not null)
            {
                samples.Add(new GestureBendObservation { Bends = bends.ToArray() });
            }

            await Task.Delay(45);
        }

        return samples;
    }

    private async Task<List<GestureRepeatAggregate>> CollectGestureRepeatAggregatesAsync(
        GestureCalibrationDialog dialog,
        string side,
        GestureComboDefinition combo)
    {
        var repeats = new List<GestureRepeatAggregate>();
        for (var repeatIndex = 0; repeatIndex < GestureCalibrationRepeatCount; repeatIndex++)
        {
            var repeatSamples = await CollectGestureBendSamplesAsync(
                dialog,
                side,
                combo.Key,
                $"第 {repeatIndex + 1} / {GestureCalibrationRepeatCount} 次：请保持 {combo.Label} 明显捏合。",
                GestureCalibrationSampleDurationMs);
            if (dialog.IsCancellationRequested)
            {
                break;
            }

            if (repeatSamples.Count < 8)
            {
                continue;
            }

            repeats.Add(BuildGestureRepeatAggregate(combo, repeatSamples));
            await Task.Delay(120);
        }

        return repeats;
    }

    private static GestureRepeatAggregate BuildGestureRepeatAggregate(
        GestureComboDefinition combo,
        IReadOnlyList<GestureBendObservation> samples)
    {
        var supportIndices = GetSupportFingerIndices(combo.TargetFingerIndex);
        var thumbMedian = Median(samples.Select(item => GetBend(item.Bends, 0)).ToArray());
        var targetMedian = Median(samples.Select(item => GetBend(item.Bends, combo.TargetFingerIndex)).ToArray());
        var supportMedians = supportIndices
            .Select(index => Median(samples.Select(item => GetBend(item.Bends, index)).ToArray()))
            .ToArray();

        return new GestureRepeatAggregate
        {
            ThumbMedian = thumbMedian,
            TargetMedian = targetMedian,
            SupportMedians = supportMedians,
            PrimaryScore = ComputePrimaryPreviewScore(thumbMedian, targetMedian)
        };
    }

    private static bool TryCreateGestureCalibration(
        GestureComboDefinition combo,
        IReadOnlyList<GestureBendObservation> openSamples,
        IReadOnlyList<GestureRepeatAggregate> pinchRepeats,
        out GestureCalibrationConfig calibration,
        out string failureReason)
    {
        calibration = new GestureCalibrationConfig();
        failureReason = "没有得到足够稳定的捏合数据。";

        if (pinchRepeats.Count < GestureCalibrationMinimumValidRepeats)
        {
            failureReason = "连续 5 次捏合里稳定样本太少，请把动作做得更明显一些。";
            return false;
        }

        var filteredRepeats = RejectGestureRepeatOutliers(pinchRepeats);
        if (filteredRepeats.Count < GestureCalibrationMinimumValidRepeats)
        {
            failureReason = "5 次捏合之间差异太大，系统无法形成稳定模板。";
            return false;
        }

        var supportIndices = GetSupportFingerIndices(combo.TargetFingerIndex);
        var openThumb = Median(openSamples.Select(item => GetBend(item.Bends, 0)).ToArray());
        var openTarget = Median(openSamples.Select(item => GetBend(item.Bends, combo.TargetFingerIndex)).ToArray());
        var openSupports = supportIndices
            .Select(index => Median(openSamples.Select(item => GetBend(item.Bends, index)).ToArray()))
            .ToArray();

        var pinchThumb = Median(filteredRepeats.Select(item => item.ThumbMedian).ToArray());
        var pinchTarget = Median(filteredRepeats.Select(item => item.TargetMedian).ToArray());
        var pinchSupports = Enumerable.Range(0, supportIndices.Length)
            .Select(index => Median(filteredRepeats.Select(item => item.SupportMedians[index]).ToArray()))
            .ToArray();
        var pinchSupportStdDevs = Enumerable.Range(0, supportIndices.Length)
            .Select(index => StandardDeviation(filteredRepeats.Select(item => item.SupportMedians[index]).ToArray()))
            .ToArray();

        var openPrimary = ComputePrimaryComponent(openThumb, openTarget, openThumb, pinchThumb, openTarget, pinchTarget);
        var pinchPrimarySamples = filteredRepeats
            .Select(item => ComputePrimaryComponent(item.ThumbMedian, item.TargetMedian, openThumb, pinchThumb, openTarget, pinchTarget))
            .ToArray();
        var pinchPrimary = Median(pinchPrimarySamples);
        var primaryDelta = pinchPrimary - openPrimary;
        if (primaryDelta < 0.10)
        {
            failureReason = "张开和捏合的主动作差异太小，请把目标两指捏得更明显一些。";
            return false;
        }

        var supportInfluences = Enumerable.Range(0, supportIndices.Length)
            .Select(index =>
            {
                var motionDelta = Math.Abs(pinchSupports[index] - openSupports[index]);
                var consistency = 1.0 - Clamp01(pinchSupportStdDevs[index] / 0.16);
                return Clamp01(0.12 + (Clamp01(motionDelta / 0.22) * 0.48) + (consistency * 0.20));
            })
            .ToArray();

        calibration = new GestureCalibrationConfig
        {
            Calibrated = true,
            CalibrationRepeats = GestureCalibrationRepeatCount,
            OpenScore = Clamp01(openPrimary),
            PinchScore = Clamp01(pinchPrimary),
            TriggerThreshold = Clamp01(openPrimary + (primaryDelta * 0.64)),
            ReleaseThreshold = Clamp01(openPrimary + (primaryDelta * 0.40)),
            ConfidenceThreshold = Clamp01(openPrimary + (primaryDelta * 0.54)),
            ThumbOpenMean = Clamp01(openThumb),
            ThumbPinchMean = Clamp01(pinchThumb),
            TargetOpenMean = Clamp01(openTarget),
            TargetPinchMean = Clamp01(pinchTarget),
            PrimaryOpenScore = Clamp01(openPrimary),
            PrimaryPinchScore = Clamp01(pinchPrimary),
            SupportOpenMeans = openSupports.Select(Clamp01).ToArray(),
            SupportPinchMeans = pinchSupports.Select(Clamp01).ToArray(),
            SupportPinchStdDevs = pinchSupportStdDevs.Select(item => ClampFiniteLocal(item, 0.08, 0.0, 1.0)).ToArray(),
            SupportInfluences = supportInfluences
        };

        return true;
    }

    private static List<GestureRepeatAggregate> RejectGestureRepeatOutliers(IReadOnlyList<GestureRepeatAggregate> repeats)
    {
        if (repeats.Count <= GestureCalibrationMinimumValidRepeats)
        {
            return repeats.ToList();
        }

        var scores = repeats.Select(item => item.PrimaryScore).ToArray();
        var median = Median(scores);
        var deviations = scores.Select(item => Math.Abs(item - median)).ToArray();
        var mad = Median(deviations);
        var tolerance = Math.Max(0.05, mad * 2.8);
        return repeats
            .Where(item => Math.Abs(item.PrimaryScore - median) <= tolerance)
            .ToList();
    }

    private GestureCalibrationProbe? TryBuildGestureCalibrationProbe(string side, string comboKey)
    {
        var bends = TryBuildGestureCalibrationBends(side);
        if (bends is null)
        {
            return null;
        }

        return new GestureCalibrationProbe
        {
            Available = true,
            Score = ComputeGesturePreviewScore(comboKey, bends)
        };
    }

    private IReadOnlyList<double>? TryBuildGestureCalibrationBends(string side)
    {
        var cache = SnapshotRuntimeCache(side);
        if (!ResolveRuntimeHandPresent(side, cache) || ResolveRuntimeHandStale(cache))
        {
            return null;
        }

        return BuildHandBends(side, cache);
    }

    private GestureDashboardState BuildGestureDashboardState()
    {
        return new GestureDashboardState
        {
            ShowAdvanced = _vm.ShowAdvanced,
            Left = BuildGestureHandDashboardState("left", "左手"),
            Right = BuildGestureHandDashboardState("right", "右手")
        };
    }

    private GestureHandDashboardState BuildGestureHandDashboardState(string side, string title)
    {
        var hand = GetGestureHandSettings(side);
        var activeCount = hand.Mappings.Values.Count(item =>
            item.Enabled
            && !string.Equals(item.MappedButton, GestureButtonCatalog.Disabled, StringComparison.OrdinalIgnoreCase));
        var calibratedCount = hand.Mappings.Values.Count(item => item.Calibration.Calibrated);
        var activeCombo = GestureComboCatalog.Definitions
            .Select(combo => new
            {
                Combo = combo,
                Binding = GetGestureBinding(side, combo.Key),
                Runtime = _gestureRuntimeStateBySide[side][combo.Key]
            })
            .FirstOrDefault(item => item.Runtime.Active);

        return new GestureHandDashboardState
        {
            Side = side,
            Title = title,
            Enabled = hand.Enabled,
            Summary = hand.Enabled
                ? $"已启用 {activeCount} / 4 个组合，已完成 {calibratedCount} / 4 个组合校准。"
                : "整只手的手势功能当前处于关闭状态。",
            LiveSummary = BuildGestureLiveSummary(side, hand.Enabled, activeCombo?.Combo.Label, activeCombo?.Binding?.MappedButton),
            Rows = GestureComboCatalog.Definitions.Select(combo => BuildGestureRowState(side, combo, hand)).ToArray()
        };
    }

    private GestureRowState BuildGestureRowState(string side, GestureComboDefinition combo, GestureHandSettings hand)
    {
        var binding = GetGestureBinding(side, combo.Key);
        var runtime = _gestureRuntimeStateBySide[side][combo.Key];
        var stateKind = ResolveGestureStateKind(hand.Enabled, binding, runtime);
        var status = BuildGestureStatusText(side, combo.Key, hand.Enabled, binding, runtime, _vm.ShowAdvanced);
        return new GestureRowState
        {
            Side = side,
            ComboKey = combo.Key,
            ComboLabel = combo.Label,
            Enabled = binding.Enabled,
            ButtonValue = GestureButtonCatalog.Normalize(binding.MappedButton),
            ButtonLabel = GestureButtonCatalog.GetLabel(side, binding.MappedButton),
            ButtonOptions = GestureButtonCatalog.CreateOptions(side),
            Calibrated = binding.Calibration.Calibrated,
            CalibrationActionLabel = binding.Calibration.Calibrated ? "重新校准" : "开始校准",
            Active = runtime.Active,
            Score = runtime.Score,
            TriggerThreshold = binding.Calibration.TriggerThreshold,
            ReleaseThreshold = binding.Calibration.ReleaseThreshold,
            ConfidenceThreshold = binding.Calibration.ConfidenceThreshold,
            StatusText = status,
            StateKind = stateKind,
            StateLabel = BuildGestureStateLabel(side, binding, runtime, stateKind),
            AdvancedText = _vm.ShowAdvanced ? BuildGestureAdvancedText(runtime, binding) : string.Empty
        };
    }

    private string BuildGestureStatusText(
        string side,
        string comboKey,
        bool handEnabled,
        GestureBindingConfig binding,
        GestureRuntimeLatchState runtime,
        bool showAdvanced)
    {
        if (!handEnabled)
        {
            return "先打开这只手的手势功能，当前组合才会参与识别。";
        }

        if (!binding.Enabled)
        {
            return "当前组合还没有启用，选择一个按键映射后就会开始生效。";
        }

        if (string.Equals(binding.MappedButton, GestureButtonCatalog.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return "当前组合还没有映射到按键。";
        }

        if (!binding.Calibration.Calibrated)
        {
            return "还没有完成校准，先用“校准整只手”记录这只手的捏合动作。";
        }

        if (!showAdvanced)
        {
            return runtime.Active
                ? $"当前正在触发 {GestureButtonCatalog.GetLabel(side, binding.MappedButton)}。"
                : $"已经校准完成，捏合时会触发 {GestureButtonCatalog.GetLabel(side, binding.MappedButton)}。";
        }

        return runtime.Active
            ? $"综合 {runtime.Score:0.00}，主判据 {runtime.PrimaryScore:0.00}，当前已经触发。"
            : $"综合 {runtime.Score:0.00}，主判据 {runtime.PrimaryScore:0.00}，还在等待更明确的两指捏合。";
    }

    private static string ResolveGestureStateKind(bool handEnabled, GestureBindingConfig binding, GestureRuntimeLatchState runtime)
    {
        if (!handEnabled
            || !binding.Enabled
            || string.Equals(binding.MappedButton, GestureButtonCatalog.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return "disabled";
        }

        if (!binding.Calibration.Calibrated)
        {
            return "needs_calibration";
        }

        return runtime.Active ? "active" : "ready";
    }

    private static string BuildGestureStateLabel(string side, GestureBindingConfig binding, GestureRuntimeLatchState runtime, string stateKind)
    {
        return stateKind switch
        {
            "disabled" => string.Equals(binding.MappedButton, GestureButtonCatalog.Disabled, StringComparison.OrdinalIgnoreCase) ? "未映射" : "已关闭",
            "needs_calibration" => "待校准",
            "active" => $"触发 {GestureButtonCatalog.GetLabel(side, binding.MappedButton)}",
            _ => "待命"
        };
    }

    private static string BuildGestureAdvancedText(GestureRuntimeLatchState runtime, GestureBindingConfig binding)
    {
        if (!binding.Calibration.Calibrated)
        {
            return string.Empty;
        }

        return $"综合 {runtime.Score:0.00} · 主判据 {runtime.PrimaryScore:0.00} · 辅助 {runtime.SupportScore:0.00} · 触发 {binding.Calibration.TriggerThreshold:0.00} · 释放 {binding.Calibration.ReleaseThreshold:0.00}";
    }

    private static string BuildGestureLiveSummary(string side, bool handEnabled, string? activeComboLabel, string? mappedButton)
    {
        if (!handEnabled)
        {
            return "当前触发：整只手手势已关闭";
        }

        if (!string.IsNullOrWhiteSpace(activeComboLabel) && !string.IsNullOrWhiteSpace(mappedButton))
        {
            return $"当前触发：{activeComboLabel} -> {GestureButtonCatalog.GetLabel(side, mappedButton)}";
        }

        return "当前触发：无";
    }

    private GestureButtonPublishState EvaluateGestureButtons(string side, bool present, bool stale, IReadOnlyList<double> bends)
    {
        var nowUtc = DateTime.UtcNow;
        var hand = GetGestureHandSettings(side);
        var result = new GestureButtonPublishState();
        var metricsByCombo = new Dictionary<string, GestureEvaluationMetrics>(StringComparer.OrdinalIgnoreCase);

        foreach (var combo in GestureComboCatalog.Definitions)
        {
            var binding = GetGestureBinding(side, combo.Key);
            var runtime = _gestureRuntimeStateBySide[side][combo.Key];
            var metrics = present && !stale
                ? ComputeGestureMetrics(combo, bends, binding.Calibration, runtime)
                : new GestureEvaluationMetrics();
            metricsByCombo[combo.Key] = metrics;
            runtime.RawScore = metrics.ConfidenceScore;
            runtime.PrimaryScore = metrics.PrimaryScore;
            runtime.SupportScore = metrics.SupportScore;
            runtime.StabilityScore = metrics.StabilityScore;
            runtime.Score = SmoothGestureScore(runtime.Score, metrics.ConfidenceScore);
        }

        var dominantCombo = metricsByCombo
            .OrderByDescending(item => item.Value.PrimaryScore)
            .ThenByDescending(item => item.Value.ConfidenceScore)
            .FirstOrDefault();

        foreach (var combo in GestureComboCatalog.Definitions)
        {
            var binding = GetGestureBinding(side, combo.Key);
            var runtime = _gestureRuntimeStateBySide[side][combo.Key];
            var metrics = metricsByCombo[combo.Key];

            var canRun = hand.Enabled
                && binding.Enabled
                && binding.Calibration.Calibrated
                && !string.Equals(binding.MappedButton, GestureButtonCatalog.Disabled, StringComparison.OrdinalIgnoreCase)
                && present
                && !stale;

            if (!canRun)
            {
                runtime.Active = false;
                runtime.AboveTriggerSinceUtc = DateTime.MinValue;
                runtime.Score = 0;
                runtime.RawScore = 0;
                runtime.PrimaryScore = 0;
                runtime.SupportScore = 0;
                runtime.StabilityScore = 0;
                continue;
            }

            var suppressedByDominant =
                !runtime.Active
                && !string.IsNullOrWhiteSpace(dominantCombo.Key)
                && !string.Equals(dominantCombo.Key, combo.Key, StringComparison.OrdinalIgnoreCase)
                && dominantCombo.Value.PrimaryScore >= metrics.PrimaryScore + GestureCompetitionMargin;

            if (runtime.Active)
            {
                if ((metrics.PrimaryScore <= binding.Calibration.ReleaseThreshold || runtime.Score <= binding.Calibration.ReleaseThreshold)
                    && nowUtc - runtime.ActivatedUtc >= GestureMinHoldWindow)
                {
                    runtime.Active = false;
                    runtime.ReleasedUtc = nowUtc;
                    runtime.AboveTriggerSinceUtc = DateTime.MinValue;
                }
            }
            else
            {
                if (!suppressedByDominant
                    && metrics.PrimaryScore >= binding.Calibration.TriggerThreshold
                    && runtime.Score >= binding.Calibration.ConfidenceThreshold)
                {
                    if (runtime.AboveTriggerSinceUtc == DateTime.MinValue)
                    {
                        runtime.AboveTriggerSinceUtc = nowUtc;
                    }

                    if (nowUtc - runtime.AboveTriggerSinceUtc >= GestureDebounceWindow
                        && (runtime.ReleasedUtc == DateTime.MinValue || nowUtc - runtime.ReleasedUtc >= GestureCooldownWindow))
                    {
                        runtime.Active = true;
                        runtime.ActivatedUtc = nowUtc;
                    }
                }
                else
                {
                    runtime.AboveTriggerSinceUtc = DateTime.MinValue;
                }
            }

            if (!runtime.Active)
            {
                continue;
            }

            switch (GestureButtonCatalog.Normalize(binding.MappedButton))
            {
                case GestureButtonCatalog.Trigger:
                    result.TriggerClick = true;
                    break;
                case GestureButtonCatalog.Grip:
                    result.GripClick = true;
                    break;
                case GestureButtonCatalog.Primary:
                    result.PrimaryClick = true;
                    break;
                case GestureButtonCatalog.Secondary:
                    result.SecondaryClick = true;
                    break;
                case GestureButtonCatalog.System:
                    result.SystemClick = true;
                    break;
            }
        }

        return result;
    }

    private GestureEvaluationMetrics ComputeGestureMetrics(
        GestureComboDefinition combo,
        IReadOnlyList<double> bends,
        GestureCalibrationConfig calibration,
        GestureRuntimeLatchState runtime)
    {
        var thumb = Clamp01(GetBend(bends, 0));
        var target = Clamp01(GetBend(bends, combo.TargetFingerIndex));
        var primary = ComputePrimaryComponent(
            thumb,
            target,
            calibration.ThumbOpenMean,
            calibration.ThumbPinchMean,
            calibration.TargetOpenMean,
            calibration.TargetPinchMean);
        var primaryProgress = NormalizeBetween(primary, calibration.PrimaryOpenScore, calibration.PrimaryPinchScore);
        var supportIndices = GetSupportFingerIndices(combo.TargetFingerIndex);
        var supportValues = supportIndices.Select(index => Clamp01(GetBend(bends, index))).ToArray();
        var supportScore = ComputeSupportConfidence(primaryProgress, supportValues, calibration);
        var stabilityScore = Clamp01(1.0 - (Math.Abs(primary - runtime.PrimaryScore) * 0.85));
        var confidence = Clamp01(primary * (0.90 + (supportScore * 0.10)) * (0.95 + (stabilityScore * 0.05)));

        return new GestureEvaluationMetrics
        {
            PrimaryScore = primary,
            SupportScore = supportScore,
            StabilityScore = stabilityScore,
            ConfidenceScore = confidence
        };
    }

    private static double ComputeGesturePreviewScore(string comboKey, IReadOnlyList<double> bends)
    {
        var combo = GestureComboCatalog.Get(comboKey);
        return ComputePrimaryPreviewScore(
            Clamp01(GetBend(bends, 0)),
            Clamp01(GetBend(bends, combo.TargetFingerIndex)));
    }

    private static double ComputePrimaryPreviewScore(double thumb, double target)
    {
        var pairClosure = Math.Sqrt(Math.Max(0, thumb * target));
        var sync = Clamp01(1.0 - (Math.Abs(thumb - target) * 1.18));
        return Clamp01(pairClosure * (0.72 + (sync * 0.28)));
    }

    private static double ComputePrimaryComponent(
        double thumb,
        double target,
        double thumbOpen,
        double thumbPinch,
        double targetOpen,
        double targetPinch)
    {
        var thumbNorm = NormalizeBetween(thumb, thumbOpen, thumbPinch);
        var targetNorm = NormalizeBetween(target, targetOpen, targetPinch);
        var closure = Math.Sqrt(Math.Max(0, thumbNorm * targetNorm));
        var sync = Clamp01(1.0 - (Math.Abs(thumbNorm - targetNorm) * 1.15));
        var openGap = thumbOpen - targetOpen;
        var pinchGap = thumbPinch - targetPinch;
        var gapTolerance = Math.Max(0.10, (Math.Abs(pinchGap - openGap) * 0.55) + 0.08);
        var gapAlignment = Clamp01(1.0 - (Math.Abs((thumb - target) - pinchGap) / gapTolerance));
        return Clamp01(closure * (0.62 + (sync * 0.20) + (gapAlignment * 0.18)));
    }

    private static double ComputeSupportConfidence(
        double primaryProgress,
        IReadOnlyList<double> supportValues,
        GestureCalibrationConfig calibration)
    {
        var weightedScore = 0.0;
        var weightTotal = 0.0;
        for (var index = 0; index < Math.Min(supportValues.Count, calibration.SupportPinchMeans.Length); index++)
        {
            var influence = index < calibration.SupportInfluences.Length
                ? Clamp01(calibration.SupportInfluences[index])
                : 0.2;
            if (influence <= 0.001)
            {
                continue;
            }

            var openMean = index < calibration.SupportOpenMeans.Length ? calibration.SupportOpenMeans[index] : 0.0;
            var pinchMean = index < calibration.SupportPinchMeans.Length ? calibration.SupportPinchMeans[index] : openMean;
            var stdDev = index < calibration.SupportPinchStdDevs.Length ? calibration.SupportPinchStdDevs[index] : 0.08;
            var expected = Lerp(openMean, pinchMean, primaryProgress);
            var tolerance = Math.Max(0.10, (stdDev * 2.8) + (Math.Abs(pinchMean - openMean) * 0.55) + 0.04);
            var closeness = Clamp01(1.0 - (Math.Abs(supportValues[index] - expected) / tolerance));
            weightedScore += closeness * influence;
            weightTotal += influence;
        }

        return weightTotal > 0.0001 ? Clamp01(weightedScore / weightTotal) : 1.0;
    }

    private static double SmoothGestureScore(double previous, double next)
    {
        if (previous <= 0.0001)
        {
            return next;
        }

        var alpha = next >= previous ? GestureRiseAlpha : GestureFallAlpha;
        return Clamp01(previous + ((next - previous) * alpha));
    }

    private static int[] GetSupportFingerIndices(int targetFingerIndex)
    {
        return Enumerable.Range(1, 4)
            .Where(index => index != targetFingerIndex)
            .ToArray();
    }

    private static double NormalizeBetween(double value, double open, double pinch)
    {
        var low = Math.Min(open, pinch);
        var high = Math.Max(open, pinch);
        var span = Math.Max(0.06, high - low);
        return Clamp01((value - low) / span);
    }

    private static double GetBend(IReadOnlyList<double> bends, int index)
    {
        if (index < 0 || index >= bends.Count)
        {
            return 0.0;
        }

        var value = bends[index];
        return double.IsFinite(value) ? value : 0.0;
    }

    private static double Clamp01(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        var ordered = values.OrderBy(item => item).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2.0
            : ordered[middle];
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count <= 1)
        {
            return 0.0;
        }

        var mean = values.Average();
        var variance = values.Sum(item => Math.Pow(item - mean, 2)) / values.Count;
        return double.IsFinite(variance) && variance > 0 ? Math.Sqrt(variance) : 0.0;
    }

    private static double ClampFiniteLocal(double value, double fallback, double min, double max)
    {
        return double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    private List<double> BuildHandBends(string side, RuntimeSideCache cache)
    {
        var bends = new List<double>(5);
        var hand = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? _config.Hands.Left : _config.Hands.Right;
        for (var i = 0; i < FingerNames.Length; i++)
        {
            var fingerName = FingerNames[i];
            var enabled = !hand.Fingers.TryGetValue(fingerName, out var fingerConfig) || fingerConfig.Enabled;
            bends.Add(enabled ? cache.FilteredBends[i] : 0.0);
        }

        return bends;
    }

    private GestureHandSettings GetGestureHandSettings(string side)
    {
        return IsLeftSide(side) ? _config.Gestures.Left : _config.Gestures.Right;
    }

    private GestureBindingConfig GetGestureBinding(string side, string comboKey)
    {
        var hand = GetGestureHandSettings(side);
        var normalizedKey = GestureComboCatalog.Normalize(comboKey);
        if (!hand.Mappings.TryGetValue(normalizedKey, out var binding) || binding is null)
        {
            binding = new GestureBindingConfig();
            hand.Mappings[normalizedKey] = binding;
        }

        binding.Calibration ??= new GestureCalibrationConfig();
        binding.MappedButton = GestureButtonCatalog.Normalize(binding.MappedButton);
        return binding;
    }
}

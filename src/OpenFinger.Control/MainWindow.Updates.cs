using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace OpenFinger.Control;

public partial class MainWindow
{
    private AppUpdateCheckResult? _lastUpdateCheckResult;
    private bool _updateCheckBusy;
    private bool _updateInstallBusy;
    private bool _updatePromptShownThisLaunch;

    public async Task CheckForUpdatesAsync(bool userInitiated, bool previewMode = false)
    {
        if (_updateCheckBusy || _updateInstallBusy)
        {
            return;
        }

        _updateCheckBusy = true;
        RefreshUiFromState();
        if (userInitiated)
        {
            SetPinnedStatusLine("正在检查更新...", 8);
        }

        try
        {
            var result = await AppUpdateService.CheckLatestReleaseAsync();
            _lastUpdateCheckResult = result;
            _config.Ui.Updates.LastCheckedUtc = DateTimeOffset.UtcNow.ToString("O");
            if (result.Success)
            {
                _config.Ui.Updates.LastKnownVersion = result.LatestVersion;
            }

            _configStore.Save(_config);
            RefreshUiFromState();

            if (!result.Success)
            {
                if (userInitiated)
                {
                    SetPinnedStatusLine(result.Message, 8);
                }

                MaybeShowTrayNotification("OpenFinger 更新检查失败", result.Message, ClientNotificationKind.Update, Forms.ToolTipIcon.Error);
                return;
            }

            if (result.HasUpdate)
            {
                var ignored = string.Equals(_config.Ui.Updates.IgnoredVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase);
                var statusText = $"发现新版本 v{result.LatestVersion}。";
                SetPinnedStatusLine(statusText, 8);
                MaybeShowTrayNotification("发现 OpenFinger 新版本", statusText, ClientNotificationKind.Update, Forms.ToolTipIcon.Info);

                if (previewMode)
                {
                    await ShowUpdateDialogAsync(result, previewMode: true);
                    return;
                }

                var shouldPrompt =
                    _config.Ui.Updates.PromptWhenAvailable
                    && !ignored
                    && !_updatePromptShownThisLaunch
                    && !_startHiddenOnLaunch
                    && !_isHiddenToTray;
                if (userInitiated || shouldPrompt)
                {
                    _updatePromptShownThisLaunch = true;
                    await ShowUpdateDialogAsync(result, previewMode: false);
                }

                if (userInitiated && ignored)
                {
                    SetPinnedStatusLine($"已检测到 v{result.LatestVersion}，但这个版本当前处于忽略状态。", 8);
                }

                return;
            }

            if (previewMode)
            {
                await ShowUpdateDialogAsync(result, previewMode: true);
                return;
            }

            if (userInitiated)
            {
                SetPinnedStatusLine($"当前已是最新版本（v{result.CurrentVersion}）。", 6);
            }
        }
        finally
        {
            _updateCheckBusy = false;
            RefreshUiFromState();
        }
    }

    public void SetCheckUpdatesOnStartup(bool enabled)
    {
        _config.Ui.Updates.CheckOnStartup = enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "已开启启动时自动检查更新。" : "已关闭启动时自动检查更新。", 4);
    }

    public void SetPromptUpdateWhenAvailable(bool enabled)
    {
        _config.Ui.Updates.PromptWhenAvailable = enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "检测到新版本时会自动弹出提示。" : "检测到新版本后不会自动弹出提示。", 4);
    }

    public void SetUpdateNotificationsEnabled(bool enabled)
    {
        _config.Ui.Notifications.UpdateResults = enabled;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine(enabled ? "已开启更新结果提示。" : "已关闭更新结果提示。", 4);
    }

    public void ClearIgnoredUpdateVersion()
    {
        _config.Ui.Updates.IgnoredVersion = string.Empty;
        _configStore.Save(_config);
        RefreshUiFromState();
        SetPinnedStatusLine("已重新启用版本提醒。", 4);
    }

    public void OpenUpdatesDirectory()
    {
        var directory = AppUpdateService.GetUpdatesRootDirectory();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    public void OpenReleasePage()
    {
        var url = _lastUpdateCheckResult?.ReleasePageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = AppUpdateService.ReleasePageUrl;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public async Task PreviewUpdateDialogAsync()
    {
        if (_lastUpdateCheckResult is null)
        {
            await CheckForUpdatesAsync(userInitiated: false, previewMode: true);
            return;
        }

        await ShowUpdateDialogAsync(_lastUpdateCheckResult, previewMode: true);
    }

    private async Task ShowUpdateDialogAsync(AppUpdateCheckResult result, bool previewMode)
    {
        var dialog = new UpdateAvailableDialog(result, previewMode)
        {
            Owner = this
        };

        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case UpdateDialogChoice.Ignore:
                _config.Ui.Updates.IgnoredVersion = result.LatestVersion;
                _configStore.Save(_config);
                RefreshUiFromState();
                SetPinnedStatusLine($"已忽略 v{result.LatestVersion} 的更新提醒。", 6);
                break;
            case UpdateDialogChoice.ViewRelease:
                OpenUrl(result.ReleasePageUrl);
                break;
            case UpdateDialogChoice.Download:
                if (string.IsNullOrWhiteSpace(result.AssetDownloadUrl))
                {
                    OpenUrl(result.ReleasePageUrl);
                    return;
                }

                await DownloadAndApplyUpdateAsync(result);
                break;
            default:
                break;
        }
    }

    private async Task DownloadAndApplyUpdateAsync(AppUpdateCheckResult result)
    {
        if (_updateInstallBusy)
        {
            return;
        }

        _updateInstallBusy = true;
        RefreshUiFromState();
        try
        {
            SetPinnedStatusLine("正在下载更新包...", 20);
            var prepared = await AppUpdateService.PrepareUpdatePackageAsync(result);
            SetPinnedStatusLine("更新包已下载，准备替换当前版本...", 20);

            var scriptPath = WriteUpdateApplyScript(prepared);
            LaunchUpdateScript(scriptPath);

            SetPinnedStatusLine("已启动更新流程，窗口即将关闭并完成替换。", 12);
            MaybeShowTrayNotification("OpenFinger 正在更新", $"将切换到 v{result.LatestVersion}。", ClientNotificationKind.Update, Forms.ToolTipIcon.Info);

            _allowClose = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
        }
        catch (Exception ex)
        {
            _updateInstallBusy = false;
            RefreshUiFromState();
            SetPinnedStatusLine($"准备更新失败：{ex.Message}", 10);
            MaybeShowTrayNotification("OpenFinger 更新失败", ex.Message, ClientNotificationKind.Update, Forms.ToolTipIcon.Error);
        }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private string WriteUpdateApplyScript(AppPreparedUpdatePackage prepared)
    {
        var updateRoot = Path.Combine(AppUpdateService.GetUpdatesRootDirectory(), prepared.Version);
        Directory.CreateDirectory(updateRoot);

        var scriptPath = Path.Combine(updateRoot, "apply_update.cmd");
        var destinationDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var executablePath = Path.Combine(destinationDirectory, "OpenFinger.Control.exe");
        var currentProcessId = Environment.ProcessId;
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("setlocal enableextensions");
        builder.AppendLine($"set \"SRC={prepared.SourceDirectory}\"");
        builder.AppendLine($"set \"DST={destinationDirectory}\"");
        builder.AppendLine($"set \"PID={currentProcessId}\"");
        builder.AppendLine(":wait_for_exit");
        builder.AppendLine("tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul");
        builder.AppendLine("if not errorlevel 1 (");
        builder.AppendLine("  timeout /t 1 /nobreak >nul");
        builder.AppendLine("  goto wait_for_exit");
        builder.AppendLine(")");
        builder.AppendLine("robocopy \"%SRC%\" \"%DST%\" /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NC /NS /NP >nul");
        builder.AppendLine($"start \"\" \"{executablePath}\"");
        builder.AppendLine("endlocal");
        File.WriteAllText(scriptPath, builder.ToString(), Encoding.Default);
        return scriptPath;
    }

    private static void LaunchUpdateScript(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" /min \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private string BuildLastCheckedText()
    {
        if (!DateTimeOffset.TryParse(_config.Ui.Updates.LastCheckedUtc, out var value))
        {
            return "尚未检查";
        }

        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    private string BuildIgnoredVersionText()
    {
        return string.IsNullOrWhiteSpace(_config.Ui.Updates.IgnoredVersion)
            ? "未忽略"
            : $"v{AppUpdateService.NormalizeVersionText(_config.Ui.Updates.IgnoredVersion)}";
    }

    private string BuildLatestPublishedText()
    {
        return _lastUpdateCheckResult is null
            ? "尚未检查"
            : AppUpdateService.FormatPublishedText(_lastUpdateCheckResult.PublishedAtUtc);
    }

    private string BuildUpdateStatusText()
    {
        if (_updateInstallBusy)
        {
            return "正在准备更新包并应用新版本。";
        }

        if (_updateCheckBusy)
        {
            return "正在检查更新...";
        }

        if (_lastUpdateCheckResult is null)
        {
            return "还没有检查过更新。";
        }

        if (!_lastUpdateCheckResult.Success)
        {
            return _lastUpdateCheckResult.Message;
        }

        if (_lastUpdateCheckResult.HasUpdate)
        {
            return $"发现新版本 v{_lastUpdateCheckResult.LatestVersion}。";
        }

        return $"当前已是最新版本（v{_lastUpdateCheckResult.CurrentVersion}）。";
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Roger.Models;
using Roger.Services;

namespace Roger;

public partial class MainWindow
{
    private readonly UpdateInstallService _updateInstaller = new();
    private UpdateResult? _availableUpdate;
    private Task? _downloadUpdateTask;
    private CancellationTokenSource? _updateDownloadCancellation;
    private bool _updatesViewReady, _updateHandoffStarted;
    private bool _updateEnvironmentReady;
    private UpdateInstallKind _updateInstallKind;
    private string? _lastUpdateLogPath;
    private bool UpdateFlowBusy => _downloadUpdateTask is { IsCompleted: false } || _updateHandoffStarted;

    private void InitializeUpdateViews()
    {
        _updatesViewReady = true;
        try
        {
            _updateInstallKind = _updateInstaller.GetCurrentKind();
            _updateEnvironmentReady = true;
            UpdateDeployment.Text = _updateInstallKind == UpdateInstallKind.Installer
                ? "安装版 · 更新时自动运行安装器，完成后重新打开 Roger。"
                : "便携版 · 更新时备份程序文件并替换，失败时尝试恢复原文件。";
            var result = _updateInstaller.ReadLastResult();
            if (result is not null)
            {
                LastUpdateResult.Text = result.Message + (result.TargetVersion is null ? "" : "\n目标版本：" + result.TargetVersion);
                _lastUpdateLogPath = result.LogPath;
                OpenUpdateLogButton.IsEnabled = !string.IsNullOrWhiteSpace(result.LogPath) && File.Exists(result.LogPath);
            }
        }
        catch (Exception ex) { LastUpdateResult.Text = "上次更新结果暂不可读取：" + ex.Message; }
        RefreshUpdateControls();
    }

    private void RepositoryInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_updatesViewReady || UpdateFlowBusy) return;
        _availableUpdate = null; _releaseUrl = null;
        UpdateStatus.Text = "发布仓库已更改，请重新检查更新。";
        RefreshUpdateControls();
    }
    private void SaveRepository_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateFlowBusy) return;
        try
        {
            _settings.UpdateRepository = string.IsNullOrWhiteSpace(RepositoryInput.Text) ? "" : UpdateService.ValidateRepository(RepositoryInput.Text);
            _settingsService.Save(_settings); RepositoryInput.Text = _settings.UpdateRepository;
            _availableUpdate = null; _releaseUrl = null;
            UpdateStatus.Text = "发布仓库已保存，请检查更新。"; RefreshUpdateControls();
        }
        catch (Exception ex) { UpdateStatus.Text = "仓库未保存：" + ex.Message; }
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();
    private Task CheckUpdatesAsync()
    {
        if (_shuttingDown || UpdateFlowBusy) return Task.CompletedTask;
        if (_updateTask is { IsCompleted: false }) return _updateTask;
        _updateTask = RunUpdateCheckAsync(); return _updateTask;
    }
    private async Task RunUpdateCheckAsync()
    {
        string repository = RepositoryInput.Text.Trim();
        _availableUpdate = null; _releaseUrl = null; ReleaseNotes.Text = "";
        CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = OpenReleaseButton.IsEnabled = false;
        UpdateStatus.Text = "正在查询 GitHub 正式版本…";
        UpdateDownloadProgress.Value = 0; UpdateDownloadProgress.IsIndeterminate = false;
        UpdateDownloadDetail.Text = "";
        try
        {
            var result = await _updater.CheckAsync(repository, _lifetime.Token);
            if (_shuttingDown) return;
            if (!string.Equals(UpdateService.ValidateRepository(repository), UpdateService.ValidateRepository(RepositoryInput.Text), StringComparison.OrdinalIgnoreCase))
            { UpdateStatus.Text = "仓库已更改，请重新检查更新。"; return; }
            _availableUpdate = result; _releaseUrl = result.ReleaseUrl; ReleaseNotes.Text = result.Notes;
            UpdateStatus.Text = result.IsNewer ? "发现新版本 " + result.Tag : "当前已是最新版本（远端 " + result.Tag + "）";
            if (result.IsNewer)
            {
                var kind = CurrentDownloadKind();
                var asset = kind == UpdatePackageKind.Installer ? result.Installer : result.Portable;
                UpdateDownloadDetail.Text = result.CanPrepare(kind) && asset is not null
                    ? "更新包：" + asset.Name + " · " + FormatBytes(asset.SizeBytes)
                    : result.UnavailableReason ?? "此版本未提供适用于当前部署方式的更新包。";
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Roger.Updater.exe")))
                    UpdateDownloadDetail.Text += "\n此构建缺少独立更新组件，请使用完整安装包升级一次。";
            }
        }
        catch (OperationCanceledException) { if (!_shuttingDown) UpdateStatus.Text = "检查已取消或超时，可重新检查。"; }
        catch (Exception ex) { if (!_shuttingDown) UpdateStatus.Text = "无法检查更新：" + ex.Message; }
        finally { if (!_shuttingDown) RefreshUpdateControls(checkFinished: true); }
    }

    private UpdatePackageKind CurrentDownloadKind() => _updateInstallKind == UpdateInstallKind.Installer ? UpdatePackageKind.Installer : UpdatePackageKind.Portable;
    private void RefreshUpdateControls(bool checkFinished = false)
    {
        if (!_updatesViewReady || _shuttingDown) return;
        bool checking = !checkFinished && _updateTask is { IsCompleted: false };
        bool busy = UpdateFlowBusy;
        CheckUpdateButton.IsEnabled = SaveRepositoryButton.IsEnabled = !busy && !checking;
        RepositoryInput.IsReadOnly = busy;
        OpenReleaseButton.IsEnabled = !busy && _releaseUrl is not null;
        InstallUpdateButton.IsEnabled = _updateEnvironmentReady && !busy && !checking && _availableUpdate?.CanPrepare(CurrentDownloadKind()) == true
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "Roger.Updater.exe"));
        if (!busy) { CancelUpdateButton.IsEnabled = false; InstallUpdateButton.Content = "下载并更新"; }
    }
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_shuttingDown || !_updateEnvironmentReady || UpdateFlowBusy || _availableUpdate is not { IsNewer: true } update) return;
        if (_driverTask is { IsCompleted: false }) { UpdateStatus.Text = "温度驱动正在安装，请等待完成后再更新 Roger。"; return; }
        _downloadUpdateTask = RunInAppUpdateAsync(update);
        await _downloadUpdateTask;
    }
    private async Task RunInAppUpdateAsync(UpdateResult update)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _updateDownloadCancellation = cancellation;
        bool acceptingProgress = true;
        UpdateInstallLaunch? handoff = null;
        bool committed = false;
        CheckUpdateButton.IsEnabled = SaveRepositoryButton.IsEnabled = InstallUpdateButton.IsEnabled = OpenReleaseButton.IsEnabled = false;
        RepositoryInput.IsReadOnly = true; CancelUpdateButton.IsEnabled = true;
        UpdateDownloadProgress.Value = 0; UpdateDownloadProgress.IsIndeterminate = true;
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            if (!acceptingProgress || _shuttingDown) return;
            UpdateStatus.Text = value.Stage;
            UpdateDownloadProgress.IsIndeterminate = !value.Percent.HasValue;
            UpdateDownloadProgress.Value = value.Percent ?? 0;
            UpdateDownloadDetail.Text = value.TotalBytes is long total ? $"{FormatBytes(value.BytesReceived)} / {FormatBytes(total)}" : value.Stage;
        });
        try
        {
            if (!string.Equals(update.Repository, UpdateService.ValidateRepository(RepositoryInput.Text), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("发布仓库已更改，请重新检查。 ");
            var kind = CurrentDownloadKind();
            var package = await _updater.PrepareAsync(update, kind, progress, cancellation.Token);
            acceptingProgress = false;
            cancellation.Token.ThrowIfCancellationRequested();
            CancelUpdateButton.IsEnabled = false; UpdateDownloadProgress.IsIndeterminate = true;
            UpdateStatus.Text = "校验完成，正在准备独立更新组件…";
            UpdateDownloadDetail.Text = "Roger 将保存历史并退出，安装完成后自动重新打开。";
            handoff = await _updateInstaller.StartAsync(package.FilePath, package.Sha256, package.SizeBytes, package.Version.ToString(3),
                kind == UpdatePackageKind.Installer ? UpdateInstallKind.Installer : UpdateInstallKind.Portable, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_shuttingDown) return;
            _updateInstaller.Commit(handoff);
            committed = true;
            _updateHandoffStarted = true;
            _exitRequested = true;
            Close(); // Do not await shutdown here: Window_Closing waits for this task to finish.
        }
        catch (OperationCanceledException) { if (!_shuttingDown) UpdateStatus.Text = "更新下载已取消，当前版本继续运行。"; }
        catch (Exception ex) { if (!_shuttingDown) UpdateStatus.Text = "更新未完成，可重试：" + ex.Message; }
        finally
        {
            if (handoff is not null && !committed)
            {
                try { _updateInstaller.Cancel(handoff); }
                catch (Exception) { /* The helper also expires an uncommitted request without installing. */ }
            }
            acceptingProgress = false; _updateDownloadCancellation = null;
            if (!_shuttingDown)
            {
                UpdateDownloadProgress.IsIndeterminate = false;
                CheckUpdateButton.IsEnabled = SaveRepositoryButton.IsEnabled = true;
                RepositoryInput.IsReadOnly = false; CancelUpdateButton.IsEnabled = false;
                InstallUpdateButton.IsEnabled = _updateEnvironmentReady && _availableUpdate?.CanPrepare(CurrentDownloadKind()) == true && File.Exists(Path.Combine(AppContext.BaseDirectory, "Roger.Updater.exe"));
                OpenReleaseButton.IsEnabled = _releaseUrl is not null;
            }
        }
    }
    private void CancelUpdate_Click(object sender, RoutedEventArgs e)
    { _updateDownloadCancellation?.Cancel(); CancelUpdateButton.IsEnabled = false; UpdateStatus.Text = "正在取消下载…"; }
    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    { if (_releaseUrl is not null) TryOpen(() => Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true })); }
    private void OpenUpdateLog_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdateLogPath is null || !File.Exists(_lastUpdateLogPath)) return;
        TryOpen(() =>
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = true };
            start.ArgumentList.Add(_lastUpdateLogPath);
            return Process.Start(start);
        });
    }
}

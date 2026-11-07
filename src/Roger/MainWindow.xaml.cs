using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Roger.Models;
using Roger.Services;
using Roger.Views;
using Forms = System.Windows.Forms;

namespace Roger;
public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly HardwareMonitorService _monitor = new();
    private readonly DiskAnalyzer _analyzer = new();
    private readonly NetworkSpeedTestService _speedTest = new();
    private readonly UpdateService _updater = new();
    private readonly FileSystemCheckService _fileSystemCheck = new();
    private readonly ObservableCollection<SensorRow> _sensorRows = new();
    private readonly CancellationTokenSource _lifetime = new();
    private AppSettings _settings;
    private DockWindow? _dock;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Task? _monitorTask, _scanTask, _speedTask, _updateTask, _checkTask, _driverTask;
    private CancellationTokenSource? _scanCancellation, _speedCancellation;
    private CancellationTokenSource? _checkCancellation;
    private bool _loadingSettings;
    private bool _exitRequested, _shutdownComplete, _shuttingDown;
    private string? _releaseUrl;
    private string _driveSignature = "";

    public MainWindow()
    {
        _settings = _settingsService.Load();
        ThemeService.Apply(_settings.Theme);
        InitializeComponent();
        WindowPlacementService.Attach(this);
        ThemeService.Changed += OnThemeChanged;
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        SensorGrid.ItemsSource = _sensorRows;
        StateChanged += (_, _) => MaximizeGlyph.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");
        SidebarVersion.Text = "ROGER / v" + UpdateService.CurrentVersion;
        VersionLabel.Text = "v" + UpdateService.CurrentVersion + " · Windows x64 · MIT License";
        MachineName.Text = Environment.MachineName;
        MachineOs.Text = "Windows · " + Environment.OSVersion.Version;
        ScanPath.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LoadSettingsControls();
        RepositoryInput.Text = _settings.UpdateRepository;
        UpdateThemeGlyph();
        RefreshHardwareAccess();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowPlacementService.ApplyTheme(this, ThemeService.IsDark);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 Roger", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add("显示 / 隐藏监控条", null, (_, _) => Dispatcher.Invoke(() => { _settings.ShowDock = !_settings.ShowDock; ApplyDock(); SaveQuietly(); LoadSettingsControls(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 Roger", null, (_, _) => Dispatcher.Invoke(() => { _exitRequested = true; Close(); }));
        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Roger · 硬件监控", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
        _dock = new DockWindow(_settings);
        _dock.OpenRequested += (_, _) => ShowMain();
        _dock.SettingsChanged += (_, _) => { SaveQuietly(); LoadSettingsControls(); };
        ApplyDock();
        SidebarInterval.Text = $"每 {_settings.RefreshIntervalSeconds} 秒采样一次";
        if (_settingsService.LastLoadWarning is string warning) SensorNotice.Text = warning;
        _monitorTask = Task.Run(MonitorLoopAsync);
        if (Environment.GetCommandLineArgs().Contains("--tray")) Hide();
        if (_settings.CheckUpdatesOnStartup && !string.IsNullOrWhiteSpace(_settings.UpdateRepository)) await CheckUpdatesAsync();
    }

    private async Task MonitorLoopAsync()
    {
        try
        {
            _monitor.Initialize();
            _lifetime.Token.ThrowIfCancellationRequested();
            // Start publishing readings before the slower WMI inventory pass.
            var first = _monitor.Sample();
            await Dispatcher.InvokeAsync(() => DisplaySnapshot(first));
            _lifetime.Token.ThrowIfCancellationRequested();
            var info = _monitor.GetHardwareInfo();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_shuttingDown) return;
                HardwareGrid.ItemsSource = info;
                CpuName.Text = info.FirstOrDefault(i => i.Category == "CPU")?.Name ?? "未识别";
                GpuName.Text = string.Join(" / ", info.Where(i => i.Category == "GPU").Select(i => i.Name));
                if (GpuName.Text.Length == 0) GpuName.Text = "未识别";
                MachineOs.Text = info.FirstOrDefault(i => i.Category is "系统" or "操作系统")?.Name ?? MachineOs.Text;
            });
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds), _lifetime.Token);
                try { var snapshot = _monitor.Sample(); await Dispatcher.InvokeAsync(() => DisplaySnapshot(snapshot)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { await Dispatcher.InvokeAsync(() => FooterStatus.Text = "采样暂时失败：" + ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => { SensorNotice.Text = "硬件采集失败：" + ex.Message; FooterStatus.Text = "请检查驱动与硬件支持情况"; }); }
        finally { _monitor.Dispose(); }
    }

    private void DisplaySnapshot(MonitorSnapshot s)
    {
        if (_shuttingDown) return;
        CpuValue.Text = Percent(s.CpuLoad); GpuValue.Text = Percent(s.GpuLoad);
        MemoryValue.Text = s.MemoryTotalGb > 0 ? Percent(s.MemoryPercent) : "—";
        CpuProgress.Value = s.CpuLoad ?? 0; GpuProgress.Value = s.GpuLoad ?? 0; MemoryProgress.Value = s.MemoryPercent;
        CpuDetail.Text = "温度 " + Temperature(s.CpuTemperature);
        GpuDetail.Text = "温度 " + Temperature(s.GpuTemperature);
        GpuDetail.ToolTip = s.GpuMemoryUsedMb.HasValue ? $"显存 {s.GpuMemoryUsedMb:0} / {s.GpuMemoryTotalMb:0} MiB" : "显存数据不可用";
        MemoryDetail.Text = $"{s.MemoryUsedGb:0.0} / {s.MemoryTotalGb:0.0} GiB";
        DownloadValue.Text = "↓ " + Rate(s.DownloadBytesPerSecond);
        UploadValue.Text = "↑ " + Rate(s.UploadBytesPerSecond);
        NetworkDetail.Text = s.NetworkName; NetworkDetail.ToolTip = s.NetworkName;
        SensorNotice.Text = s.StatusMessage ?? "传感器正常 · 数据仅保存在本机";
        UpdateSensorRows(s.Sensors);
        PerformanceChart.Add(s.CpuLoad, s.GpuLoad, s.MemoryPercent);
        ChartSpan.Text = $"最近 60 次采样 · {_settings.RefreshIntervalSeconds} 秒间隔";
        FooterStatus.Text = $"● 实时监控   /   {s.Sensors.Count} 个传感器   /   {s.Drives.Count} 个卷";
        FooterTime.Text = "最后采样 " + s.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        if (_tray is not null) _tray.Text = $"Roger · CPU {Percent(s.CpuLoad)} · RAM {Percent(s.MemoryPercent)}";
        _dock?.UpdateSnapshot(s);
        string signature = string.Join(";", s.Drives.Select(d => $"{d.Name}:{d.FreeBytes}:{d.TotalBytes}"));
        if (signature != _driveSignature)
        {
            _driveSignature = signature; DriveCards.Children.Clear();
            foreach (var d in s.Drives)
            {
                var grid = new Grid { Margin = new Thickness(0, 12, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
                var driveName = new TextBlock { Text = d.Name + " " + d.Label, ToolTip = d.Format, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                driveName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); grid.Children.Add(driveName);
                var bar = new ProgressBar { Value = d.UsedPercent, Margin = new Thickness(12, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = d.UsedPercent > 90 ? Brushes.Salmon : (Brush)FindResource("Accent") }; Grid.SetColumn(bar, 1); grid.Children.Add(bar);
                var text = new TextBlock { Text = $"{FormatBytes(d.TotalBytes - d.FreeBytes)} / {FormatBytes(d.TotalBytes)}", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 }; text.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(text, 2); grid.Children.Add(text); DriveCards.Children.Add(grid);
            }
        }
    }

    private void Navigate_Checked(object sender, RoutedEventArgs e)
    {
        if (OverviewPage is null) return;
        string? tag = (sender as RadioButton)?.Tag as string;
        OverviewPage.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        HardwarePage.Visibility = tag == "hardware" ? Visibility.Visible : Visibility.Collapsed;
        DiskPage.Visibility = tag == "disk" ? Visibility.Visible : Visibility.Collapsed;
        NetworkPage.Visibility = tag == "network" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPage.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        FileSystemPage.Visibility = tag == "filesystem" ? Visibility.Visible : Visibility.Collapsed;
        (string title, string subtitle) = tag switch
        {
            "hardware" => ("硬件与传感器", "设备配置、硬件访问状态与实时传感器读数。"),
            "disk" => ("磁盘空间", "分析目录分布与大文件，了解存储空间的去向。"),
            "filesystem" => ("文件系统检查", "在 Roger 内查看 Windows 磁盘只读检查报告。"),
            "network" => ("网络测速", "测量当前连接的下载、上传和 HTTP 延迟。"),
            "settings" => ("偏好设置", "调整外观、桌面监控条与应用行为。"),
            "updates" => ("版本与更新", "查看 Roger 版本与开源发布动态。"),
            _ => ("运行概览", "一览处理器、显卡、内存与网络的实时表现。")
        };
        PageTitle.Text = title; PageSubtitle.Text = subtitle;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要分析的文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) ScanPath.Text = dialog.FolderName;
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _scanTask = RunScanAsync(); await _scanTask;
    }
    private async Task RunScanAsync()
    {
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ScanButton.IsEnabled = false; CancelScanButton.IsEnabled = true; ScanPath.IsEnabled = false;
        ScanProgress.IsIndeterminate = true; DiskBars.Children.Clear(); FilesGrid.ItemsSource = null; ScanErrors.Text = "";
        try
        {
            var progress = new Progress<DiskScanProgress>(p => { ScanStatus.Text = $"已发现 {p.FileCount:N0} 个文件 · {FormatBytes(p.TotalBytes)} · 跳过 {p.SkippedEntries:N0} 项 · {p.Elapsed.TotalSeconds:0.0}s"; ScanCurrent.Text = p.CurrentPath; });
            var result = await _analyzer.ScanAsync(ScanPath.Text.Trim(), progress, _scanCancellation.Token);
            ScanStatus.Text = $"扫描完成 · {FormatBytes(result.TotalBytes)} · {result.FileCount:N0} 个文件 · {result.DirectoryCount:N0} 个目录 · {result.Elapsed.TotalSeconds:0.0}s";
            ScanCurrent.Text = result.RootPath;
            foreach (var entry in result.Children.OrderByDescending(c => c.Bytes).Take(30))
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                var entryName = new TextBlock { Text = entry.Name, ToolTip = entry.FullPath, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 20 }; entryName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); row.Children.Add(entryName);
                var bar = new ProgressBar { Value = result.TotalBytes > 0 ? entry.Bytes * 100d / result.TotalBytes : 0, Margin = new Thickness(10, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center, Height = 8 }; Grid.SetColumn(bar, 1); row.Children.Add(bar);
                var size = new TextBlock { Text = $"{FormatBytes(entry.Bytes)}  {(result.TotalBytes > 0 ? entry.Bytes * 100d / result.TotalBytes : 0):0.0}%", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right }; size.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(size, 2); row.Children.Add(size); DiskBars.Children.Add(row);
            }
            FilesGrid.ItemsSource = result.LargestFiles.Select(f => new FileView(f.Name, f.FullPath, FormatBytes(f.Bytes))).ToArray();
            ScanErrors.Text = $"逻辑文件大小；硬链接可能重复计数，压缩/稀疏文件与磁盘实际占用可能不同。分布展示前 30 项。跳过 {result.SkippedEntries:N0} 项（包括链接与无权限项）。" + (result.Errors.Count > 0 ? "\n" + string.Join("\n", result.Errors.Take(5)) : "");
        }
        catch (OperationCanceledException) { ScanStatus.Text = "扫描已取消 · 未生成完整分析结果"; }
        catch (Exception ex) { ScanStatus.Text = "扫描失败：" + ex.Message; }
        finally { ScanProgress.IsIndeterminate = false; ScanButton.IsEnabled = true; CancelScanButton.IsEnabled = false; ScanPath.IsEnabled = true; _scanCancellation.Dispose(); _scanCancellation = null; }
    }
    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
    private void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileView file)
            TryOpen(() => Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file.FullPath + "\"") { UseShellExecute = true }));
    }
    private async void CheckDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_checkTask is { IsCompleted: false }) return;
        _checkTask = RunFileSystemCheckAsync(); await _checkTask;
    }
    private async Task RunFileSystemCheckAsync()
    {
        _checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var thisRun = _checkCancellation;
        bool acceptingProgress = true;
        CheckDiskButton.IsEnabled = false; CancelCheckDiskButton.IsEnabled = true; CheckVolume.IsEnabled = false;
        CheckDiskProgress.IsIndeterminate = true; CheckDiskReport.Clear(); CheckDiskStatus.Text = "正在启动 Windows 文件系统只读检查…";
        try
        {
            var progress = new Progress<FileSystemCheckProgress>(p =>
            {
                if (!acceptingProgress || !ReferenceEquals(_checkCancellation, thisRun) || _shuttingDown) return;
                CheckDiskReport.AppendText(p.OutputChunk); CheckDiskReport.ScrollToEnd();
                CheckDiskStatus.Text = $"正在检查 · {p.Elapsed.TotalSeconds:0} 秒 · 可以随时取消";
            });
            var result = await _fileSystemCheck.CheckAsync(CheckVolume.Text.Trim(), progress, _checkCancellation.Token);
            acceptingProgress = false;
            CheckDiskReport.Text = result.Report;
            CheckDiskStatus.Text = $"{result.Summary} · 耗时 {result.Elapsed.TotalSeconds:0.0} 秒 · 退出码 {result.ExitCode}";
        }
        catch (OperationCanceledException) { acceptingProgress = false; CheckDiskStatus.Text = "检查已取消，当前报告为未完成结果。"; }
        catch (Exception ex) { acceptingProgress = false; CheckDiskStatus.Text = ex.Message; }
        finally { acceptingProgress = false; CheckDiskProgress.IsIndeterminate = false; CheckDiskButton.IsEnabled = true; CancelCheckDiskButton.IsEnabled = false; CheckVolume.IsEnabled = true; thisRun.Dispose(); _checkCancellation = null; }
    }
    private void CancelCheckDisk_Click(object sender, RoutedEventArgs e) => _checkCancellation?.Cancel();

    private async void Speed_Click(object sender, RoutedEventArgs e) { _speedTask = RunSpeedAsync(); await _speedTask; }
    private async Task RunSpeedAsync()
    {
        _speedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SpeedButton.IsEnabled = false; CancelSpeedButton.IsEnabled = true; SpeedProgress.Value = 0;
        SpeedDownload.Text = SpeedUpload.Text = SpeedLatency.Text = "—";
        try
        {
            var result = await _speedTest.RunAsync(new Progress<SpeedTestProgress>(p =>
            {
                SpeedProgress.Value = p.Percent; SpeedStatus.Text = $"{p.Message} · 已传输 {FormatBytes(p.BytesTransferred)}";
                if (p.Phase == "download") SpeedDownload.Text = p.MegabitsPerSecond.ToString("0.0");
                if (p.Phase == "upload") SpeedUpload.Text = p.MegabitsPerSecond.ToString("0.0");
            }), _speedCancellation.Token);
            SpeedDownload.Text = result.DownloadMbps.ToString("0.0"); SpeedUpload.Text = result.UploadMbps.ToString("0.0"); SpeedLatency.Text = result.HttpLatencyMs.ToString("0");
            SpeedStatus.Text = $"完成于 {result.CompletedAt.LocalDateTime:HH:mm:ss} · HTTP 抖动 {result.JitterMs:0} ms · 传输 {FormatBytes(result.DownloadBytes + result.UploadBytes)}";
        }
        catch (OperationCanceledException) { SpeedStatus.Text = "测速已取消 · 当前显示的是未完成的过程值"; }
        catch (Exception ex) { SpeedStatus.Text = "测速未完成：" + ex.Message; }
        finally { SpeedButton.IsEnabled = true; CancelSpeedButton.IsEnabled = false; _speedCancellation.Dispose(); _speedCancellation = null; }
    }
    private void CancelSpeed_Click(object sender, RoutedEventArgs e) => _speedCancellation?.Cancel();

    private void LoadSettingsControls()
    {
        _loadingSettings = true;
        SettingTheme.SelectedIndex = (int)_settings.Theme;
        SettingDock.IsChecked = _settings.ShowDock; SettingCpu.IsChecked = _settings.ShowCpu; SettingGpu.IsChecked = _settings.ShowGpu; SettingMemory.IsChecked = _settings.ShowMemory; SettingNetwork.IsChecked = _settings.ShowNetwork;
        SettingTemperature.IsChecked = _settings.ShowTemperature; SettingCompact.IsChecked = _settings.CompactDock; SettingPosition.SelectedIndex = (int)_settings.DockPosition; SettingOpacity.Value = _settings.DockOpacity;
        SettingTray.IsChecked = _settings.MinimizeToTray; SettingStartup.IsChecked = _settings.LaunchAtLogin; SettingAutoUpdate.IsChecked = _settings.CheckUpdatesOnStartup;
        SettingInterval.SelectedIndex = Array.IndexOf(new[] { 1, 2, 3, 5, 10 }, _settings.RefreshIntervalSeconds);
        _loadingSettings = false;
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool startup = SettingStartup.IsChecked == true;
            if (startup != _settings.LaunchAtLogin) _settingsService.SetLaunchAtLogin(startup);
            _settings.ShowDock = SettingDock.IsChecked == true; _settings.ShowCpu = SettingCpu.IsChecked == true; _settings.ShowGpu = SettingGpu.IsChecked == true; _settings.ShowMemory = SettingMemory.IsChecked == true; _settings.ShowNetwork = SettingNetwork.IsChecked == true;
            _settings.ShowTemperature = SettingTemperature.IsChecked == true; _settings.CompactDock = SettingCompact.IsChecked == true; _settings.DockPosition = (DockPosition)Math.Max(0, SettingPosition.SelectedIndex); _settings.DockOpacity = SettingOpacity.Value;
            _settings.MinimizeToTray = SettingTray.IsChecked == true; _settings.LaunchAtLogin = startup; _settings.CheckUpdatesOnStartup = SettingAutoUpdate.IsChecked == true;
            _settings.RefreshIntervalSeconds = SettingInterval.SelectedIndex >= 0 ? new[] { 1, 2, 3, 5, 10 }[SettingInterval.SelectedIndex] : 2;
            _settings.Theme = (AppTheme)Math.Max(0, SettingTheme.SelectedIndex);
            _settingsService.Save(_settings); ApplyDock(); SidebarInterval.Text = $"每 {_settings.RefreshIntervalSeconds} 秒采样一次"; SettingsStatus.Text = "已保存并应用";
        }
        catch (Exception ex) { SettingsStatus.Text = "保存失败：" + ex.Message; }
    }
    private void SaveRepository_Click(object sender, RoutedEventArgs e)
    {
        try { _settings.UpdateRepository = string.IsNullOrWhiteSpace(RepositoryInput.Text) ? "" : UpdateService.ValidateRepository(RepositoryInput.Text); _settingsService.Save(_settings); RepositoryInput.Text = _settings.UpdateRepository; _releaseUrl = null; OpenReleaseButton.IsEnabled = false; UpdateStatus.Text = "发布仓库已保存。"; }
        catch (Exception ex) { UpdateStatus.Text = ex.Message; }
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();
    private Task CheckUpdatesAsync()
    {
        if (_shuttingDown) return Task.CompletedTask;
        if (_updateTask is { IsCompleted: false }) return _updateTask;
        _updateTask = RunUpdateCheckAsync();
        return _updateTask;
    }
    private async Task RunUpdateCheckAsync()
    {
        string repository = RepositoryInput.Text.Trim();
        _releaseUrl = null;
        ReleaseNotes.Text = "";
        CheckUpdateButton.IsEnabled = false; OpenReleaseButton.IsEnabled = false; UpdateStatus.Text = "正在查询 GitHub Releases…";
        try
        {
            var result = await _updater.CheckAsync(repository, _lifetime.Token);
            if (_shuttingDown) return;
            if (!string.Equals(repository, RepositoryInput.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatus.Text = "仓库已更改，请重新检查更新。";
                return;
            }
            _releaseUrl = result.ReleaseUrl; OpenReleaseButton.IsEnabled = true;
            UpdateStatus.Text = result.IsNewer ? "发现新版本 " + result.Tag : "当前已是最新版本（远端 " + result.Tag + "）"; ReleaseNotes.Text = result.Notes;
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "检查已取消或超时。"; }
        catch (Exception ex) { UpdateStatus.Text = "无法检查更新（请确认仓库有正式 Release）：" + ex.Message; }
        finally { CheckUpdateButton.IsEnabled = !_shuttingDown; }
    }
    private void OpenRelease_Click(object sender, RoutedEventArgs e) { if (_releaseUrl is not null) TryOpen(() => Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true })); }

    private void ApplyDock() { if (_dock is null) return; _dock.ApplySettings(_settings); if (_settings.ShowDock) _dock.Show(); else _dock.Hide(); }
    private void SaveQuietly() { try { _settingsService.Save(_settings); } catch (Exception ex) { FooterStatus.Text = "设置未保存：" + ex.Message; } }
    private void ShowMain() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize_Click(sender, e); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (!_exitRequested && _settings.MinimizeToTray) { Hide(); return; }
        if (_shuttingDown) return;
        _shuttingDown = true; _exitRequested = true; IsEnabled = false; FooterStatus.Text = "正在停止采集…"; _lifetime.Cancel();
        try { await Task.WhenAll(new[] { _monitorTask, _scanTask, _speedTask, _updateTask, _checkTask, _driverTask }.Where(t => t is not null).Select(t => t!)); } catch (OperationCanceledException) { }
        finally { ThemeService.Changed -= OnThemeChanged; SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged; _dock?.Close(); _tray?.Dispose(); _trayIcon?.Dispose(); _speedTest.Dispose(); _updater.Dispose(); _lifetime.Dispose(); _shutdownComplete = true; Application.Current.Shutdown(); }
    }
    private static void TryOpen(Func<Process?> action) { try { action(); } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } catch (Exception ex) { MessageBox.Show(ex.Message, "Roger"); } }
    public static string FormatBytes(long bytes) { string[] units = { "B", "KiB", "MiB", "GiB", "TiB" }; double value = bytes; int i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; } return $"{value:0.0} {units[i]}"; }
    private static string Rate(double bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000:0.0} MB/s" : $"{bytes / 1_000:0.0} KB/s";
    private static string Percent(double? v) => v.HasValue ? $"{v:0}%" : "—";
    private static string Temperature(double? v) => v.HasValue ? $"{v:0} °C" : "不可用";
    private sealed record FileView(string Name, string FullPath, string Size);
    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeService.IsDark ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Apply(_settings.Theme); SaveQuietly(); LoadSettingsControls();
    }
    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || SettingTheme is null || SettingTheme.SelectedIndex < 0 || ThemeGlyph is null) return;
        _settings.Theme = (AppTheme)SettingTheme.SelectedIndex; ThemeService.Apply(_settings.Theme); SaveQuietly();
    }
    private void OnThemeChanged(object? sender, EventArgs e) { UpdateThemeGlyph(); _driveSignature = ""; PerformanceChart.InvalidateVisual(); }
    private void UpdateThemeGlyph() => ThemeGlyph.Data = (Geometry)FindResource(ThemeService.IsDark ? "IconSun" : "IconMoon");
    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme == AppTheme.System && !_shuttingDown) Dispatcher.BeginInvoke(() => ThemeService.Apply(AppTheme.System));
    }
    private void RefreshHardwareAccess()
    {
        var status = HardwareAccessService.GetStatus();
        AdminBadge.Text = status.IsAdministrator ? "管理员" : "普通权限";
        AdminButton.SetResourceReference(Button.ForegroundProperty, status.IsAdministrator ? "Accent" : "Muted");
        AdminButton.ToolTip = status.IsAdministrator ? "管理员模式：可读取底层温度并检查文件系统" : "以管理员身份重启，启用完整硬件访问";
        HardwareAccessText.Text = status.Summary;
        InstallDriverButton.IsEnabled = !status.DriverInstalled;
        InstallDriverButton.Content = status.DriverInstalled ? "温度驱动已安装" : "安装官方温度驱动";
    }
    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (ElevationService.IsAdministrator) { RefreshHardwareAccess(); FooterStatus.Text = "当前已在管理员模式运行。"; return; }
        TryOpen(() =>
        {
            var process = ElevationService.StartElevatedInstance();
            if (process is not null) { _exitRequested = true; Close(); }
            return process;
        });
    }
    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        if (_driverTask is { IsCompleted: false }) return;
        _driverTask = RunDriverInstallAsync(); await _driverTask;
    }
    private async Task RunDriverInstallAsync()
    {
        InstallDriverButton.IsEnabled = false;
        try
        {
            HardwareAccessText.Text = "正在下载并校验官方签名温度驱动…";
            string installer = await HardwareAccessService.DownloadVerifiedDriverAsync(_lifetime.Token);
            HardwareAccessText.Text = "正在安装官方驱动，请确认 Windows UAC 提示…";
            await HardwareAccessService.InstallDriverAsync(installer, _lifetime.Token);
            RefreshHardwareAccess();
            HardwareAccessText.Text += " 安装完成，正在以管理员身份重新启动采样。";
            using var restarted = ElevationService.StartElevatedInstance();
            if (restarted is not null) { _exitRequested = true; Close(); }
        }
        catch (OperationCanceledException) { HardwareAccessText.Text = "驱动操作已取消。"; }
        catch (Exception ex) { HardwareAccessText.Text = "驱动操作未完成：" + ex.Message; }
        finally { if (!_shuttingDown) InstallDriverButton.IsEnabled = !HardwareAccessService.GetStatus().DriverInstalled; }
    }
    private void UpdateSensorRows(IReadOnlyList<SensorReading> readings)
    {
        bool same = readings.Count == _sensorRows.Count;
        if (same) for (int i = 0; i < readings.Count; i++) if (!_sensorRows[i].Matches(readings[i])) { same = false; break; }
        if (!same) { _sensorRows.Clear(); foreach (var sensor in readings) _sensorRows.Add(new SensorRow(sensor)); }
        else for (int i = 0; i < readings.Count; i++) _sensorRows[i].Value = readings[i].Value;
    }
    private sealed class SensorRow : INotifyPropertyChanged
    {
        public string Hardware { get; } public string Name { get; } public string Kind { get; } public string Unit { get; }
        private double _value;
        public double Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public SensorRow(SensorReading s) { Hardware = s.Hardware; Name = s.Name; Kind = s.Kind; Unit = s.Unit; _value = s.Value; }
        public bool Matches(SensorReading s) => Hardware == s.Hardware && Name == s.Name && Kind == s.Kind && Unit == s.Unit;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

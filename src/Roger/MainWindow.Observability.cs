using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Roger.Models;
using Roger.Services;
using Roger.Views;
using Forms = System.Windows.Forms;

namespace Roger;

public partial class MainWindow
{
    private readonly AlertService _alerts = new();
    private readonly HistoryMonitorService _history = new();
    private readonly ProcessMonitorService _processMonitor = new();
    private Task? _processTask, _historyReadTask, _historyExportTask;
    private bool _loadingObservability = true;
    private DateTimeOffset _lastHistoryRefresh = DateTimeOffset.MinValue;

    private void InitializeObservabilityViews()
    {
        InitializeProcessViews();
        InitializeOptimizationJournalViews();
        var options = _alerts.Options;
        AlertsEnabled.IsChecked = options.Enabled; AlertsQuiet.IsChecked = options.Quiet;
        AlertCpuEnabled.IsChecked = options.CpuTemperatureEnabled; AlertGpuEnabled.IsChecked = options.GpuTemperatureEnabled;
        AlertMemoryEnabled.IsChecked = options.MemoryEnabled; AlertDiskEnabled.IsChecked = options.DiskSpaceEnabled;
        AlertCpuThreshold.Text = options.CpuTemperatureThresholdCelsius.ToString(CultureInfo.CurrentCulture);
        AlertGpuThreshold.Text = options.GpuTemperatureThresholdCelsius.ToString(CultureInfo.CurrentCulture);
        AlertMemoryThreshold.Text = options.MemoryPercentThreshold.ToString(CultureInfo.CurrentCulture);
        AlertDiskThreshold.Text = options.DiskFreePercentThreshold.ToString(CultureInfo.CurrentCulture);
        AlertDuration.Text = options.RequiredDurationSeconds.ToString(CultureInfo.CurrentCulture);
        AlertCooldown.Text = options.CooldownSeconds.ToString(CultureInfo.CurrentCulture);
        HistoryRange.SelectedIndex = 0; HistoryMetric.SelectedIndex = 0;
        _loadingObservability = false;
        RefreshAlertRecords();
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var sample = _processMonitor.Sample();
                await Dispatcher.InvokeAsync(() => { if (!_shuttingDown) DisplayProcesses(sample); });
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => ProcessNotice.Text = "进程采样未完成：" + ex.Message); }
        finally { _processMonitor.Dispose(); }
    }

    // Runs on the serialized monitor worker. UI notification work is dispatched separately.
    private void RecordObservability(MonitorSnapshot sample, StorageSnapshot? storage = null)
    {
        try { _history.Record(sample, storage); }
        catch (Exception ex) { Dispatcher.BeginInvoke(new Action(() => { if (!_shuttingDown) HistoryStatus.Text = "历史记录暂未保存：" + ex.Message; })); }
        var events = _alerts.Evaluate(sample);
        if (events.Count > 0)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_shuttingDown) return;
                RefreshAlertRecords();
                if (!_alerts.Options.Quiet && _alerts.Options.Enabled && _tray is not null)
                {
                    string title = events.Count == 1 ? "Roger · " + events[0].Title : $"Roger · {events.Count} 项异常提醒";
                    string message = string.Join("\n", events.Take(3).Select(item => item.Message));
                    try { _tray.ShowBalloonTip(8000, title[..Math.Min(title.Length, 63)], message[..Math.Min(message.Length, 950)], Forms.ToolTipIcon.Warning); }
                    catch (System.ComponentModel.Win32Exception ex) { AlertStatus.Text = "异常已记录，Windows 通知未显示：" + ex.Message; }
                }
            }));
        if (DateTimeOffset.UtcNow - _lastHistoryRefresh >= TimeSpan.FromMinutes(1))
        {
            _lastHistoryRefresh = DateTimeOffset.UtcNow;
            Dispatcher.BeginInvoke(new Action(() => { if (!_shuttingDown && HistoryPage.Visibility == Visibility.Visible) _ = RefreshHistoryAsync(); }));
        }
    }

    private void SaveAlerts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = _alerts.Options with
            {
                Enabled = AlertsEnabled.IsChecked == true, Quiet = AlertsQuiet.IsChecked == true,
                CpuTemperatureEnabled = AlertCpuEnabled.IsChecked == true, GpuTemperatureEnabled = AlertGpuEnabled.IsChecked == true,
                MemoryEnabled = AlertMemoryEnabled.IsChecked == true, DiskSpaceEnabled = AlertDiskEnabled.IsChecked == true,
                CpuTemperatureThresholdCelsius = ParseNumber(AlertCpuThreshold.Text), GpuTemperatureThresholdCelsius = ParseNumber(AlertGpuThreshold.Text),
                MemoryPercentThreshold = ParseNumber(AlertMemoryThreshold.Text), DiskFreePercentThreshold = ParseNumber(AlertDiskThreshold.Text),
                RequiredDurationSeconds = int.Parse(AlertDuration.Text.Trim(), CultureInfo.CurrentCulture),
                CooldownSeconds = int.Parse(AlertCooldown.Text.Trim(), CultureInfo.CurrentCulture)
            };
            _alerts.SaveOptions(options); RefreshAlertRecords(); AlertStatus.Text = "提醒设置已保存。" + (_alerts.LastPersistenceWarning ?? "");
        }
        catch (Exception ex) { AlertStatus.Text = "提醒设置未保存：" + ex.Message; }
    }
    private static double ParseNumber(string value) => double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture);
    private void ClearAlerts_Click(object sender, RoutedEventArgs e)
    {
        try { _alerts.ClearRecords(); RefreshAlertRecords(); }
        catch (Exception ex) { AlertStatus.Text = "记录未清空：" + ex.Message; }
    }
    private void RefreshAlertRecords()
    {
        AlertRecordsGrid.ItemsSource = _alerts.Records.Select(item => new
        { Time = item.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss"), item.Title, item.Message }).ToArray();
        AlertStatus.Text = (_alerts.Options.Enabled ? "提醒已启用" : "提醒已关闭") + (_alerts.Options.Quiet ? " · 静音：仅记录，不通知" : " · 通过 Windows 托盘通知")
            + " · 最近 " + _alerts.Records.Count + " 条记录" + (_alerts.LastPersistenceWarning is string warning ? "\n" + warning : "");
    }

    private void HistorySelection_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_loadingObservability && HistoryPage is not null) _ = RefreshHistoryAsync(); }
    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();
    private Task RefreshHistoryAsync()
    {
        if (_shuttingDown) return Task.CompletedTask;
        // Read/query is small and serialized; coalesce UI changes into a follow-up refresh.
        if (_historyReadTask is { IsCompleted: false }) return _historyReadTask;
        _historyReadTask = ReadHistoryAsync(); return _historyReadTask;
    }
    private async Task ReadHistoryAsync()
    {
        try
        {
            var all = await Task.Run(() => _history.Query(TimeSpan.FromDays(7)));
            if (_shuttingDown) return;
            string? savedGpu = (HistoryGpu.SelectedItem as HistoricalGpuChoice)?.Id ?? _latestSnapshot.SelectedGpuId;
            var choices = all.Where(b => b.GpuId is not null).GroupBy(b => b.GpuId)
                .Select(g => new HistoricalGpuChoice(g.Key!, g.Last().GpuName)).ToArray();
            _loadingObservability = true;
            HistoryGpu.ItemsSource = choices; HistoryGpu.SelectedValue = choices.Any(g => g.Id == savedGpu) ? savedGpu : choices.LastOrDefault()?.Id;
            _loadingObservability = false;
            TimeSpan range = HistoryRange.SelectedIndex switch { 1 => TimeSpan.FromHours(24), 2 => TimeSpan.FromDays(7), _ => TimeSpan.FromHours(1) };
            var buckets = all.Where(b => b.Timestamp >= DateTimeOffset.UtcNow - range).ToArray();
            string? gpuId = (HistoryGpu.SelectedItem as HistoricalGpuChoice)?.Id;
            int metric = HistoryMetric.SelectedIndex;
            string[] keys = metric switch
            {
                1 => new[] { HistoryMetrics.CpuTemperature, HistoryMetrics.GpuTemperature },
                2 => new[] { HistoryMetrics.Download, HistoryMetrics.Upload },
                3 => new[] { HistoryMetrics.DiskRead, HistoryMetrics.DiskWrite },
                _ => new[] { HistoryMetrics.CpuLoad, HistoryMetrics.GpuLoad, HistoryMetrics.Memory }
            };
            string[] names = metric switch { 1 => new[] { "CPU 温度", "GPU 温度" }, 2 => new[] { "下载", "上传" }, 3 => new[] { "物理盘合计读取", "物理盘合计写入" }, _ => new[] { "CPU", "GPU", "内存" } };
            string unit = metric switch { 1 => "°C", 2 or 3 => "MB/s", _ => "%" };
            double divisor = metric is 2 or 3 ? 1_000_000 : 1;
            double? Value(HistoryBucket bucket, int index) => index < keys.Length && (!keys[index].StartsWith("gpu.", StringComparison.Ordinal) || bucket.GpuId == gpuId)
                && bucket.Metrics.TryGetValue(keys[index], out var value) ? value.Average / divisor : null;
            HistoricalChart.SetData(buckets.Select((b, index) => new HistoryTrendPoint(b.Timestamp, Value(b, 0), Value(b, 1), Value(b, 2),
                index > 0 && b.SessionId != buckets[index - 1].SessionId)).ToArray(), unit);
            HistoryLegendOne.Text = "● " + names[0]; HistoryLegendTwo.Text = "● " + names[1]; HistoryLegendThree.Text = names.Length > 2 ? "● " + names[2] : "";
            HistorySummaryOneTitle.Text = names[0]; HistorySummaryTwoTitle.Text = names[1]; HistorySummaryThreeTitle.Text = names.Length > 2 ? names[2] : "记录范围";
            string Summary(int index)
            {
                var stat = HistoryMonitorService.Summarize(buckets, keys[index], gpuId);
                return stat is null ? "暂无有效读数" : $"平均 {stat.Average / divisor:0.#} {unit}\n峰值 {stat.Maximum / divisor:0.#} {unit}";
            }
            HistorySummaryOne.Text = Summary(0); HistorySummaryTwo.Text = Summary(1); HistorySummaryThree.Text = names.Length > 2 ? Summary(2) : $"{buckets.Length} 个汇总区间\n{buckets.Sum(b => b.SampleCount)} 次采样";
            HistoryStatus.Text = $"本地保留最近 7 天 · 图表为分钟采样平均 · 峰值来自原始采样 · 当前 {buckets.Length} 个区间"
                + (buckets.Length == 0 ? "\nRoger 开始运行后会逐步积累历史数据。" : "")
                + (_history.LastPersistenceWarning is string warning ? "\n" + warning : "");
        }
        catch (Exception ex) { if (!_shuttingDown) HistoryStatus.Text = "历史数据读取未完成：" + ex.Message; }
        finally { _loadingObservability = false; }
    }
    private async void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_shuttingDown || _historyExportTask is { IsCompleted: false }) return;
        var dialog = new SaveFileDialog { Title = "导出最近七天的监控历史", Filter = "CSV 文件|*.csv", FileName = "Roger-history-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv" };
        if (dialog.ShowDialog(this) != true) return;
        ExportHistoryButton.IsEnabled = false;
        _historyExportTask = ExportHistoryAsync(dialog.FileName);
        await _historyExportTask;
    }
    private async Task ExportHistoryAsync(string path)
    {
        try
        {
            await Task.Run(() => HistoryMonitorService.ExportCsv(path, _history.Query(TimeSpan.FromDays(7))));
            if (!_shuttingDown) HistoryStatus.Text = "已导出七天内全部设备的分钟汇总：" + path;
        }
        catch (Exception ex) { if (!_shuttingDown) HistoryStatus.Text = "导出未完成：" + ex.Message; }
        finally { if (!_shuttingDown) ExportHistoryButton.IsEnabled = true; }
    }
    private sealed record HistoricalGpuChoice(string Id, string Name);
}

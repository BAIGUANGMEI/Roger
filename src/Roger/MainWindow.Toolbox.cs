using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Roger.Models;
using Roger.Services;

namespace Roger;
public partial class MainWindow
{
    private readonly OptimizationService _optimization = new();
    private OptimizationJournalService? _optimizationJournal;
    private OptimizationJournalService Journal => _optimizationJournal ??= new OptimizationJournalService(_optimization);
    private Task? _toolboxStateTask, _dnsTask;
    private CancellationTokenSource? _dnsCancellation;
    private bool _optimizationBusy;
    private bool OptimizationStatePending => _toolboxStateTask is { IsCompleted: false };

    private void InitializeOptimizationJournalViews() => DisplayOptimizationJournal();

    private void DisplayOptimizationJournal()
    {
        Guid? selected = (OptimizationHistoryGrid.SelectedItem as OptimizationJournalEntry)?.Id;
        var entries = Journal.Entries;
        OptimizationHistoryGrid.ItemsSource = entries;
        if (selected.HasValue) OptimizationHistoryGrid.SelectedItem = entries.FirstOrDefault(e => e.Id == selected);
        OptimizationJournalStatus.Text = Journal.LastLoadWarning ?? $"已保存 {entries.Count} 条操作记录（最多 200 条）。恢复前核对当前值；同项设置按变更倒序恢复。";
        OptimizationJournalStatus.ToolTip = Journal.StoragePath;
    }

    private Task RefreshOptimizationStateAsync()
    {
        if (_shuttingDown) return Task.CompletedTask;
        if (_toolboxStateTask is { IsCompleted: false }) return _toolboxStateTask;
        _toolboxStateTask = ReadOptimizationStateAsync();
        return _toolboxStateTask;
    }

    private async Task ReadOptimizationStateAsync()
    {
        try
        {
            // Publishing a stale read after a write would misrepresent the current Windows state.
            PowerPlanPicker.IsEnabled = ApplyPowerPlanButton.IsEnabled = UndoPowerPlanButton.IsEnabled = false;
            ToolAnimationsEnabled.IsEnabled = ApplyAnimationsButton.IsEnabled = UndoAnimationsButton.IsEnabled = false;
            ToolboxStatus.Text = "正在读取 Windows 当前状态…";
            var state = await Task.Run(_optimization.GetState);
            if (_shuttingDown) return;
            PowerPlanPicker.ItemsSource = state.PowerPlans;
            PowerPlanPicker.SelectedValue = state.ActivePowerPlanId;
            PowerPlanPicker.IsEnabled = ApplyPowerPlanButton.IsEnabled = state.PowerPlans.Count > 0 && state.ActivePowerPlanId.HasValue;
            UndoPowerPlanButton.IsEnabled = PowerPlanPicker.IsEnabled && Journal.LatestRestorable(OptimizationChangeKind.PowerPlan) is not null;
            var current = state.PowerPlans.FirstOrDefault(p => p.Id == state.ActivePowerPlanId);
            PowerPlanStatus.Text = current is null ? "当前电源计划不可用。" : "当前：" + current.Name;
            ToolAnimationsEnabled.IsEnabled = ApplyAnimationsButton.IsEnabled = state.ClientAreaAnimationsEnabled.HasValue;
            UndoAnimationsButton.IsEnabled = ToolAnimationsEnabled.IsEnabled && Journal.LatestRestorable(OptimizationChangeKind.ClientAreaAnimations) is not null;
            ToolAnimationsEnabled.IsChecked = state.ClientAreaAnimationsEnabled;
            AnimationsStatus.Text = state.ClientAreaAnimationsEnabled is bool enabled ? "当前客户端动画：" + (enabled ? "开启" : "关闭") : "Windows 未能返回客户端动画状态。";
            DisplayOptimizationJournal();
            ToolboxStatus.Text = state.StatusMessage ?? "状态已更新。只有点击操作按钮才会更改设置。";
        }
        catch (Exception ex) { if (!_shuttingDown) ToolboxStatus.Text = "读取未完成：" + ex.Message; }
    }

    private async void RefreshToolbox_Click(object sender, RoutedEventArgs e) => await RefreshOptimizationStateAsync();
    private async void ApplyPowerPlan_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlanPicker.SelectedItem is not PowerPlanInfo plan) return;
        await RunOptimizationChangeAsync(() => Journal.ApplyPowerPlan(plan.Id, plan.Name));
    }
    private async void UndoPowerPlan_Click(object sender, RoutedEventArgs e)
    {
        if (Journal.LatestRestorable(OptimizationChangeKind.PowerPlan) is not { } entry) return;
        await RunOptimizationChangeAsync(() => Journal.Restore(entry.Id));
    }
    private async void ApplyAnimations_Click(object sender, RoutedEventArgs e)
    {
        if (ToolAnimationsEnabled.IsChecked is not bool enabled) return;
        await RunOptimizationChangeAsync(() => Journal.ApplyAnimations(enabled));
    }
    private async void UndoAnimations_Click(object sender, RoutedEventArgs e)
    {
        if (Journal.LatestRestorable(OptimizationChangeKind.ClientAreaAnimations) is not { } entry) return;
        await RunOptimizationChangeAsync(() => Journal.Restore(entry.Id));
    }
    private async void RestoreOptimizationRecord_Click(object sender, RoutedEventArgs e)
    {
        if (OptimizationHistoryGrid.SelectedItem is not OptimizationJournalEntry entry)
        { ToolboxStatus.Text = "请先从优化记录中选择一项。"; return; }
        await RunOptimizationChangeAsync(() => Journal.Restore(entry.Id));
    }
    private void RefreshOptimizationHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_optimizationBusy || OptimizationStatePending || _shuttingDown) return;
        Journal.Reload();
        DisplayOptimizationJournal();
        _ = RefreshOptimizationStateAsync();
    }
    private async Task RunOptimizationChangeAsync(Func<OptimizationJournalEntry> change)
    {
        if (_shuttingDown || _optimizationBusy || OptimizationStatePending) return;
        _optimizationBusy = true;
        string message;
        try
        {
            OptimizationJournalEntry entry = change();
            message = entry.StatusText + "：" + entry.Message;
        }
        catch (Exception ex) { message = "操作未完成：" + ex.Message; }
        try
        {
            DisplayOptimizationJournal();
            await RefreshOptimizationStateAsync();
            if (!_shuttingDown) ToolboxStatus.Text = message;
        }
        finally { _optimizationBusy = false; }
    }
    private void OpenOptimizationTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OptimizationToolId id }) return;
        try { _optimization.OpenTool(id); ToolboxStatus.Text = "已打开 " + OptimizationService.Tools.First(t => t.Id == id).Title + "。"; }
        catch (Exception ex) { ToolboxStatus.Text = "无法打开工具：" + ex.Message; }
    }
    private async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsTask is { IsCompleted: false }) return;
        _dnsTask = FlushDnsAsync(); await _dnsTask;
    }
    private async Task FlushDnsAsync()
    {
        _dnsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        FlushDnsButton.IsEnabled = false; CancelDnsButton.IsEnabled = true; DnsReport.Text = "正在刷新 Windows DNS 缓存…";
        OptimizationEntryStatus status;
        string message;
        try
        {
            var result = await _optimization.FlushDnsAsync(_dnsCancellation.Token);
            status = result.Succeeded ? OptimizationEntryStatus.Completed : OptimizationEntryStatus.Failed;
            message = result.Summary + "\n" + result.Report;
        }
        catch (OperationCanceledException) { status = OptimizationEntryStatus.Cancelled; message = "DNS 刷新已取消。"; }
        catch (Exception ex) { status = OptimizationEntryStatus.Failed; message = "DNS 刷新未完成：" + ex.Message; }
        finally
        {
            FlushDnsButton.IsEnabled = true; CancelDnsButton.IsEnabled = false;
            _dnsCancellation.Dispose(); _dnsCancellation = null;
        }
        DnsReport.Text = message;
        try { Journal.RecordDnsResult(status, message); DisplayOptimizationJournal(); }
        catch (Exception ex) { DnsReport.Text += "\n本次 DNS 操作已经结束，但结果记录保存失败：" + ex.Message; }
    }
    private void CancelDns_Click(object sender, RoutedEventArgs e) => _dnsCancellation?.Cancel();
}
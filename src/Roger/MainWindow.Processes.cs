using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using Roger.Models;
using Roger.Services;

namespace Roger;

public partial class MainWindow
{
    private readonly ObservableCollection<ProcessResourceRow> _processRows = new();
    private ProcessSnapshot _processSnapshot = ProcessSnapshot.Empty;
    private bool _processViewsReady;

    private void InitializeProcessViews()
    {
        ProcessesGrid.ItemsSource = _processRows;
        ProcessSort.ItemsSource = new[]
        {
            new ProcessSortChoice(ProcessSortOrder.Cpu, "CPU 使用率"),
            new ProcessSortChoice(ProcessSortOrder.Memory, "内存占用"),
            new ProcessSortChoice(ProcessSortOrder.Read, "I/O 读取速率"),
            new ProcessSortChoice(ProcessSortOrder.Write, "I/O 写入速率")
        };
        ProcessSort.SelectedIndex = 0;
        _processViewsReady = true;
    }

    private void DisplayProcesses(ProcessSnapshot snapshot)
    {
        if (_shuttingDown) return;
        _processSnapshot = snapshot;
        RefreshProcessRanking();
    }

    private void ProcessFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (_processViewsReady) RefreshProcessRanking();
    }
    private void ProcessSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_processViewsReady) RefreshProcessRanking();
    }

    private void RefreshProcessRanking()
    {
        var order = ProcessSort.SelectedItem is ProcessSortChoice choice ? choice.Order : ProcessSortOrder.Cpu;
        var ranked = ProcessMonitorService.Rank(_processSnapshot.Processes, order, ProcessFilter.Text);
        var keys = ranked.Select(p => p.Identity).ToHashSet(StringComparer.Ordinal);
        for (int i = _processRows.Count - 1; i >= 0; i--)
            if (!keys.Contains(_processRows[i].Data.Identity)) _processRows.RemoveAt(i);
        var known = _processRows.ToDictionary(row => row.Data.Identity, StringComparer.Ordinal);
        for (int i = 0; i < ranked.Count; i++)
        {
            ProcessReading reading = ranked[i];
            if (known.TryGetValue(reading.Identity, out ProcessResourceRow? row))
            {
                row.Update(reading, i + 1);
                int current = _processRows.IndexOf(row);
                if (current != i) _processRows.Move(current, i);
            }
            else _processRows.Insert(i, new ProcessResourceRow(reading, i + 1));
        }
        string filter = ProcessFilter.Text.Trim();
        ProcessSummary.Text = $"已读取 {_processSnapshot.Processes.Count} 个进程 · 显示 {ranked.Count} 项（最多 100）"
            + (filter.Length == 0 ? "" : " · 已应用搜索");
        ProcessUpdated.Text = _processSnapshot.Processes.Count == 0 ? "等待进程采样" : "最后采样 " + _processSnapshot.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        ProcessNotice.Text = $"CPU 按全机 {_processSnapshot.LogicalProcessorCount} 个逻辑处理器归一；内存为工作集，包含可能共享的物理页。I/O 是进程的全部读写，可能包含文件、网络与设备操作，不等同于物理磁盘吞吐。"
            + (_processSnapshot.StatusMessage is string status ? "\n" + status : "");
    }

    private sealed record ProcessSortChoice(ProcessSortOrder Order, string Label);
    private sealed class ProcessResourceRow(ProcessReading data, int rank) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public ProcessReading Data { get; private set; } = data;
        public int Rank { get; private set; } = rank;
        public string Name => Data.Name;
        public int Pid => Data.Pid;
        public string CpuText => Data.CpuPercent is double value ? $"{value:0.0}%" : "不可用";
        public string MemoryText => Data.WorkingSetBytes is long value ? FormatBytes(value) : "不可用";
        public string ReadText => Data.ReadBytesPerSecond is double value ? Rate(value) : "不可用";
        public string WriteText => Data.WriteBytesPerSecond is double value ? Rate(value) : "不可用";
        public string Details => (Data.ExecutablePath ?? "程序路径不可读取")
            + (Data.StatusMessage is string status ? "\n" + status : "");
        public void Update(ProcessReading next, int rank)
        {
            Data = next; Rank = rank;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}

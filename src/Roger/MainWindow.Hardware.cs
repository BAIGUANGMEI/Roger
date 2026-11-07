using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Roger.Models;
using Roger.Services;

namespace Roger;
public partial class MainWindow
{
    private readonly StorageMonitorService _storageMonitor = new();
    private readonly ObservableCollection<CpuRow> _cpuRows = new();
    private readonly ObservableCollection<GpuRow> _gpuRows = new();
    private readonly ObservableCollection<DiskRow> _physicalDiskRows = new();
    private StorageSnapshot _storageSnapshot = StorageSnapshot.Empty;
    private MonitorSnapshot _latestSnapshot = MonitorSnapshot.Empty;
    private int? _selectedDiskNumber;
    private string? _chartedGpuId;
    private string? _chartedDiskIdentity;
    private bool _updatingDeviceSelectors;
    private string _gpuChoicesSignature = "", _memoryModulesSignature = "", _diskChoicesSignature = "";

    private void InitializeDeviceViews()
    {
        CpuDevicesGrid.ItemsSource = _cpuRows; GpuDevicesGrid.ItemsSource = _gpuRows; PhysicalDisksGrid.ItemsSource = _physicalDiskRows;
    }
    private void DisplayDeviceSnapshot(MonitorSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        UpdateRows(_cpuRows, snapshot.CpuDevices, s => s.Id, r => r.Data.Id, s => new CpuRow(s), (r, s) => r.Update(s));
        UpdateRows(_gpuRows, snapshot.GpuDevices, s => s.Id, r => r.Data.Id, s => new GpuRow(s), (r, s) => r.Update(s));
        string signature = string.Join("|", snapshot.GpuDevices.Select(g => g.Id + ":" + g.Name)) + "#" + _settings.SelectedGpuId;
        if (signature != _gpuChoicesSignature)
        {
            _gpuChoicesSignature = signature; _updatingDeviceSelectors = true;
            var choices = new List<GpuChoice> { new(null, "自动选择显卡") };
            choices.AddRange(snapshot.GpuDevices.Select(g => new GpuChoice(g.Id, g.Name)));
            if (_settings.SelectedGpuId is string saved && !choices.Any(c => c.Id == saved)) choices.Add(new(saved, "已选择的显卡暂不可用"));
            GpuSelector.ItemsSource = choices; GpuSelector.SelectedIndex = choices.FindIndex(c => c.Id == _settings.SelectedGpuId);
            _updatingDeviceSelectors = false;
        }
        var selected = snapshot.GpuDevices.FirstOrDefault(g => g.Id == snapshot.SelectedGpuId);
        GpuDeviceStatus.Text = selected is null ? "当前没有可读取的 GPU 遥测。设备清单与温度支持取决于硬件、驱动和权限。"
            : $"当前概览：{selected.Name} · 来源：{selected.Source}" + (selected.Status is null ? "" : " · " + selected.Status);
        GpuDetail.ToolTip = selected is null ? GpuDetail.ToolTip : $"{selected.Name}\n专用 GPU 内存 {MemoryPair(selected.MemoryUsedMb, selected.MemoryTotalMb)}\n共享 GPU 内存 {MemoryPair(selected.SharedMemoryUsedMb, selected.SharedMemoryTotalMb)}";
        string moduleSignature = string.Join("|", snapshot.MemoryModules.Select(m => $"{m.Id}:{m.CapacityBytes}:{m.ConfiguredSpeedMtPerSec}:{m.RatedSpeedMtPerSec}"));
        if (moduleSignature != _memoryModulesSignature)
        {
            _memoryModulesSignature = moduleSignature;
            MemoryModulesGrid.ItemsSource = snapshot.MemoryModules.Select(m => new
            {
                m.Slot, m.Manufacturer, m.PartNumber, CapacityText = m.CapacityBytes > 0 ? FormatBytes(m.CapacityBytes) : "未报告",
                SpeedText = $"{Speed(m.ConfiguredSpeedMtPerSec)} / {Speed(m.RatedSpeedMtPerSec)}"
            }).ToArray();
        }
        MemoryModuleStatus.Text = snapshot.MemoryModules.Count > 0
            ? $"识别 {snapshot.MemoryModules.Count} 条内存 · 速率单位 MT/s · 来源 Windows SMBIOS；未知厂商代码保留原值。"
            : "暂未取得内存条清单；物理内存总占用仍通过 Windows 通用接口读取。";
    }
    private void GpuSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDeviceSelectors || GpuSelector.SelectedItem is not GpuChoice choice || _settings.SelectedGpuId == choice.Id) return;
        _settings.SelectedGpuId = choice.Id; SaveQuietly(); PerformanceChart.Reset();
        var gpu = _latestSnapshot.GpuDevices.FirstOrDefault(g => g.Id == choice.Id);
        if (gpu is not null)
        {
            GpuValue.Text = Percent(gpu.Load); GpuProgress.Value = gpu.Load ?? 0; GpuDetail.Text = "温度 " + Temperature(gpu.Temperature);
        }
        GpuDeviceStatus.Text = "已选择，下一次实时采样会同步概览与监控条。";
    }
    private void DisplayStorageSnapshot(StorageSnapshot snapshot, bool appendHistory = true)
    {
        if (_shuttingDown) return;
        _storageSnapshot = snapshot;
        UpdateRows(_physicalDiskRows, snapshot.Disks, s => s.DiskNumber, r => r.Data.DiskNumber, s => new DiskRow(s), (r, s) => r.Update(s));
        string signature = string.Join("|", snapshot.Disks.Select(d => d.DiskNumber + ":" + d.Model));
        if (signature != _diskChoicesSignature)
        {
            _diskChoicesSignature = signature; _updatingDeviceSelectors = true;
            StorageSelector.ItemsSource = snapshot.Disks.Select(d => new DiskChoice(d.DiskNumber, $"磁盘 {d.DiskNumber} · {d.Model}")).ToArray();
            if (!snapshot.Disks.Any(d => d.DiskNumber == _selectedDiskNumber)) { _selectedDiskNumber = snapshot.Disks.FirstOrDefault()?.DiskNumber; StorageRateChart.Reset(); }
            StorageSelector.SelectedValue = _selectedDiskNumber; _updatingDeviceSelectors = false;
        }
        var disk = snapshot.Disks.FirstOrDefault(d => d.DiskNumber == _selectedDiskNumber);
        string? diskIdentity = disk is null ? null : $"{disk.DiskNumber}:{disk.DeviceId}:{disk.Model}:{disk.SizeBytes}";
        if (_chartedDiskIdentity != diskIdentity)
        {
            _chartedDiskIdentity = diskIdentity;
            StorageRateChart.Reset();
        }
        StorageStatus.Text = snapshot.StatusMessage ?? "物理硬盘实时监控 · 缺失值保留为空，不替代为零。";
        if (disk is null)
        {
            StorageModel.Text = "尚未发现可读取的物理硬盘"; StorageIdentity.Text = "请检查存储驱动与权限。";
            StorageActive.Text = StorageRead.Text = StorageWrite.Text = StorageTemperature.Text = "不可用"; StorageActiveBar.Value = 0;
            StorageHealth.Text = "不可用"; StorageReliability.Text = StorageResponse.Text = StorageSource.Text = ""; return;
        }
        StorageModel.Text = disk.Model;
        StorageIdentity.Text = $"磁盘 {disk.DiskNumber} · {disk.Manufacturer} · {disk.BusType} / {disk.MediaType} · {DiskSize(disk.SizeBytes)} · 卷 {disk.Volumes}";
        StorageActive.Text = disk.ActivePercent is double busy ? $"{busy:0.#}%" : "不可用";
        StorageActiveBar.Value = disk.ActivePercent ?? 0;
        StorageRead.Text = RateOrMissing(disk.ReadBytesPerSecond); StorageWrite.Text = RateOrMissing(disk.WriteBytesPerSecond);
        StorageTemperature.Text = Temperature(disk.TemperatureCelsius);
        StorageHealth.Text = disk.HealthStatus + " · " + disk.OperationalStatus;
        StorageReliability.Text = $"已消耗寿命：{(disk.WearPercent.HasValue ? disk.WearPercent + "%" : "不可用")}\n通电：{(disk.PowerOnHours.HasValue ? disk.PowerOnHours + " 小时" : "不可用")}\n未校正读 / 写错误：{ValueOrMissing(disk.ReadErrorsUncorrected)} / {ValueOrMissing(disk.WriteErrorsUncorrected)}";
        StorageResponse.Text = $"平均响应：{(disk.AverageResponseMs.HasValue ? disk.AverageResponseMs.Value.ToString("0.##") + " ms" : "不可用")}\n队列长度：{(disk.QueueLength.HasValue ? disk.QueueLength.Value.ToString("0.##") : "不可用")}";
        StorageSource.Text = disk.TelemetrySource;
        if (!string.IsNullOrWhiteSpace(disk.StatusMessage)) StorageStatus.Text += "\n" + disk.StatusMessage;
        if (appendHistory) StorageRateChart.Add(disk.ReadBytesPerSecond, disk.WriteBytesPerSecond);
    }
    private void StorageSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDeviceSelectors || StorageSelector.SelectedItem is not DiskChoice choice || _selectedDiskNumber == choice.Id) return;
        _selectedDiskNumber = choice.Id; StorageRateChart.Reset(); DisplayStorageSnapshot(_storageSnapshot, false);
    }
    private static void UpdateRows<TRow,TData,TKey>(ObservableCollection<TRow> rows, IReadOnlyList<TData> data,
        Func<TData,TKey> key, Func<TRow,TKey> rowKey, Func<TData,TRow> create, Action<TRow,TData> update) where TKey : notnull
    {
        var keys = data.Select(key).ToHashSet();
        for (int i = rows.Count - 1; i >= 0; i--) if (!keys.Contains(rowKey(rows[i]))) rows.RemoveAt(i);
        var known = rows.ToDictionary(rowKey);
        foreach (var value in data) { if (known.TryGetValue(key(value), out var row)) update(row, value); else rows.Add(create(value)); }
    }
    private static string Speed(uint? v) => v.HasValue ? v + " MT/s" : "未知";
    private static string DiskSize(long bytes) => bytes > 0 ? FormatBytes(bytes) : "容量未报告";
    private static string RateOrMissing(double? v) => v.HasValue ? Rate(v.Value) : "不可用";
    private static string ValueOrMissing(ulong? v) => v?.ToString() ?? "不可用";
    private static string MemoryPair(double? used, double? total) => (used?.ToString("0") ?? "—") + " / " + (total?.ToString("0") ?? "—") + " MiB";
    private sealed record GpuChoice(string? Id, string Label);
    private sealed record DiskChoice(int Id, string Label);
    private abstract class LiveRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    private sealed class CpuRow(CpuDeviceReading data) : LiveRow
    {
        public CpuDeviceReading Data { get; private set; } = data;
        public string Name => Data.Name; public string Vendor => Data.Vendor;
        public string LoadText => Percent(Data.Load); public string TemperatureText => Temperature(Data.Temperature);
        public string StatusText => Data.Source + (Data.Status is null ? "" : " · " + Data.Status);
        public void Update(CpuDeviceReading next) { Data = next; Refresh(); }
    }
    private sealed class GpuRow(GpuDeviceReading data) : LiveRow
    {
        public GpuDeviceReading Data { get; private set; } = data;
        public string Name => Data.Name; public string Vendor => Data.Vendor;
        public string LoadText => Percent(Data.Load); public string TemperatureText => Temperature(Data.Temperature);
        public string DedicatedText => MemoryPair(Data.MemoryUsedMb, Data.MemoryTotalMb);
        public string SharedText => MemoryPair(Data.SharedMemoryUsedMb, Data.SharedMemoryTotalMb);
        public void Update(GpuDeviceReading next) { Data = next; Refresh(); }
    }
    private sealed class DiskRow(PhysicalDiskReading data) : LiveRow
    {
        public PhysicalDiskReading Data { get; private set; } = data;
        public string NumberText => "磁盘 " + Data.DiskNumber; public string Model => Data.Model;
        public string InterfaceText => Data.BusType + " / " + Data.MediaType; public string SizeText => DiskSize(Data.SizeBytes);
        public string ActiveText => Percent(Data.ActivePercent); public string RateText => RateOrMissing(Data.ReadBytesPerSecond) + " / " + RateOrMissing(Data.WriteBytesPerSecond);
        public string Volumes => Data.Volumes;
        public void Update(PhysicalDiskReading next) { Data = next; Refresh(); }
    }
}

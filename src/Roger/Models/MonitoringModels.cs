using System;
using System.Collections.Generic;

namespace Roger.Models;

public sealed record HardwareInfo(string Category, string Name, string Details);
public sealed record SensorReading(string Hardware, string Name, string Kind, double Value, string Unit)
{
    public string? HardwareId { get; init; }
}
public sealed record DriveReading(string Name, string Label, long TotalBytes, long FreeBytes, string Format)
{
    public double UsedPercent => TotalBytes > 0 ? (TotalBytes - FreeBytes) * 100d / TotalBytes : 0;
}
public sealed record MonitorSnapshot(
    DateTimeOffset Timestamp, double? CpuLoad, double? CpuTemperature,
    double? GpuLoad, double? GpuTemperature, double? GpuMemoryUsedMb, double? GpuMemoryTotalMb,
    double MemoryUsedGb, double MemoryTotalGb, double DownloadBytesPerSecond, double UploadBytesPerSecond,
    string NetworkName, IReadOnlyList<DriveReading> Drives, IReadOnlyList<SensorReading> Sensors,
    string? StatusMessage)
{
    public IReadOnlyList<CpuDeviceReading> CpuDevices { get; init; } = Array.Empty<CpuDeviceReading>();
    public IReadOnlyList<GpuDeviceReading> GpuDevices { get; init; } = Array.Empty<GpuDeviceReading>();
    public IReadOnlyList<MemoryModuleInfo> MemoryModules { get; init; } = Array.Empty<MemoryModuleInfo>();
    public string? SelectedGpuId { get; init; }
    public double MemoryPercent => MemoryTotalGb > 0 ? MemoryUsedGb / MemoryTotalGb * 100 : 0;
    public static MonitorSnapshot Empty { get; } = new(DateTimeOffset.Now, null, null, null, null, null, null, 0, 0, 0, 0, "等待采样", Array.Empty<DriveReading>(), Array.Empty<SensorReading>(), null);
}

public enum DockPosition { TaskbarRight, TaskbarLeft, TopRight, BottomRight, Free }
public enum AppTheme { Dark, Light, System }
public sealed class AppSettings
{
    public string? SelectedGpuId { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool ShowDock { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowTemperature { get; set; } = true;
    public bool CompactDock { get; set; } = false;
    public DockPosition DockPosition { get; set; } = DockPosition.TaskbarRight;
    public double DockLeft { get; set; } = 50;
    public double DockTop { get; set; } = 50;
    public double DockOpacity { get; set; } = 0.96;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public bool MinimizeToTray { get; set; } = true;
    public bool LaunchAtLogin { get; set; } = false;
    public string UpdateRepository { get; set; } = "BAIGUANGMEI/Roger";
    public bool CheckUpdatesOnStartup { get; set; } = false;
}


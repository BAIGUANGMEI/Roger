using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Roger.Models;

public sealed record HistoryStatistic(int Count, double Sum, double Minimum, double Maximum)
{
    [JsonIgnore] public double Average => Count > 0 ? Sum / Count : double.NaN;
}

public sealed record HistoryBucket(Guid Id, DateTimeOffset Timestamp, string? GpuId, string GpuName,
    int SampleCount, Dictionary<string, HistoryStatistic> Metrics)
{
    public Guid SessionId { get; init; }
}

public static class HistoryMetrics
{
    public const string CpuLoad = "cpu.load", CpuTemperature = "cpu.temperature", GpuLoad = "gpu.load",
        GpuTemperature = "gpu.temperature", Memory = "memory.percent", Download = "network.download",
        Upload = "network.upload", DiskRead = "disk.read", DiskWrite = "disk.write";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { CpuLoad, CpuTemperature, GpuLoad, GpuTemperature, Memory, Download, Upload, DiskRead, DiskWrite });
}

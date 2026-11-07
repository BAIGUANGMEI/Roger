using System;
using System.Collections.Generic;

namespace Roger.Models;

public sealed record StorageSnapshot(DateTimeOffset Timestamp, IReadOnlyList<PhysicalDiskReading> Disks, string? StatusMessage)
{
    public static StorageSnapshot Empty { get; } = new(DateTimeOffset.Now, Array.Empty<PhysicalDiskReading>(), "等待硬盘采样");
}

/// <summary>One Windows physical-disk number, rather than one partition. Missing telemetry stays null.</summary>
public sealed record PhysicalDiskReading(
    int DiskNumber, string DeviceId, string Model, string Manufacturer, string BusType, string MediaType,
    long SizeBytes, string Volumes, double? ActivePercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond,
    double? QueueLength, double? AverageResponseMs, double? TemperatureCelsius, uint? WearPercent,
    ulong? PowerOnHours, ulong? ReadErrorsUncorrected, ulong? WriteErrorsUncorrected,
    string HealthStatus, string OperationalStatus, string TelemetrySource, string? StatusMessage);

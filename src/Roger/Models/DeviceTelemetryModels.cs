using System;

namespace Roger.Models;

public sealed record CpuDeviceReading(string Id, string Name, string Vendor, double? Load,
    double? Temperature, string Source, string? Status);

public sealed record GpuDeviceReading(string Id, string Name, string Vendor, bool? IsDiscrete,
    double? Load, double? Temperature, double? MemoryUsedMb, double? MemoryTotalMb,
    double? SharedMemoryUsedMb, double? SharedMemoryTotalMb, string Source, string? Status);

/// <summary>Memory module information reported by Windows/SMBIOS, not an SPD benchmark or live clock.</summary>
public sealed record MemoryModuleInfo(string Id, string Manufacturer, string PartNumber,
    long CapacityBytes, uint? ConfiguredSpeedMtPerSec, uint? RatedSpeedMtPerSec, string Slot, string Source)
{
    public string Name => string.Join(" ", new[] { Manufacturer, PartNumber }).Trim();
    public double CapacityGb => CapacityBytes / (1024d * 1024 * 1024);
}

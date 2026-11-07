using System;

namespace Roger.Models;

/// <summary>Quiet mode records incidents while suppressing the UI notification.</summary>
public sealed record AlertOptions
{
    public bool Enabled { get; init; } = true;
    public bool CpuTemperatureEnabled { get; init; } = true;
    public bool GpuTemperatureEnabled { get; init; } = true;
    public bool MemoryEnabled { get; init; } = true;
    public bool DiskSpaceEnabled { get; init; } = true;
    public double CpuTemperatureThresholdCelsius { get; init; } = 90;
    public double GpuTemperatureThresholdCelsius { get; init; } = 85;
    public double MemoryPercentThreshold { get; init; } = 90;
    public double DiskFreePercentThreshold { get; init; } = 10;
    public int RequiredDurationSeconds { get; init; } = 15;
    public int CooldownSeconds { get; init; } = 300;
    public bool Quiet { get; init; }
}

public sealed record AlertEvent(Guid Id, DateTimeOffset Timestamp, string MetricKey,
    string Title, string Message, double Value, double Threshold);

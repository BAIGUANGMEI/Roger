using System;
using System.Collections.Generic;

namespace Roger.Models;

public sealed record ProcessReading(
    int Pid, long? CreationTimeFileTime, string Name, double? CpuPercent, long? WorkingSetBytes,
    double? ReadBytesPerSecond, double? WriteBytesPerSecond, string? ExecutablePath, string? StatusMessage)
{
    public string Identity => Pid + ":" + (CreationTimeFileTime?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
}

public sealed record ProcessSnapshot(DateTimeOffset Timestamp, IReadOnlyList<ProcessReading> Processes,
    int LogicalProcessorCount, string? StatusMessage)
{
    public static ProcessSnapshot Empty { get; } = new(DateTimeOffset.Now, Array.Empty<ProcessReading>(), 0, "等待进程采样");
}

public enum ProcessSortOrder { Cpu, Memory, Read, Write }

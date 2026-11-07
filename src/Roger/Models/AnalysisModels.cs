using System;
using System.Collections.Generic;

namespace Roger.Models;

public sealed record DiskUsageEntry(string Name, string FullPath, long Bytes, long FileCount, bool IsDirectory);
public sealed record DiskFileEntry(string Name, string FullPath, long Bytes);
public sealed record DiskScanProgress(string CurrentPath, long TotalBytes, long FileCount,
    long DirectoryCount, long SkippedEntries, TimeSpan Elapsed);
public sealed record DiskScanResult(string RootPath, long TotalBytes, long FileCount,
    long DirectoryCount, long SkippedEntries, IReadOnlyList<DiskUsageEntry> Children,
    IReadOnlyList<DiskFileEntry> LargestFiles, IReadOnlyList<string> Errors, TimeSpan Elapsed);

public sealed record SpeedTestProgress(string Phase, double Percent, double MegabitsPerSecond,
    long BytesTransferred, string Message);
public sealed record SpeedTestResult(double DownloadMbps, double UploadMbps, double HttpLatencyMs,
    double JitterMs, long DownloadBytes, long UploadBytes, DateTimeOffset CompletedAt, string Provider);

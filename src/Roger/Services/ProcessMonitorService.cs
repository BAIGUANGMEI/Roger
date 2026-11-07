using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Roger.Models;

namespace Roger.Services;

/// <summary>
/// Read-only process accounting. CPU is normalized over all active logical processors;
/// process I/O includes all Windows I/O, not exclusively physical disk transfers.
/// Sample on one background worker. No process is suspended, terminated or reconfigured.
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<RawProcessSample>> _read;
    private readonly Dictionary<int, RawProcessSample> _previous = new();
    private readonly int _logicalProcessorCount;
    private readonly long _frequency;
    private bool _disposed;

    public ProcessMonitorService() : this(ReadProcesses, ActiveProcessorCount(), Stopwatch.Frequency) { }

    internal ProcessMonitorService(Func<IReadOnlyList<RawProcessSample>> read, int logicalProcessorCount, long frequency)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        if (logicalProcessorCount <= 0) throw new ArgumentOutOfRangeException(nameof(logicalProcessorCount));
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _logicalProcessorCount = logicalProcessorCount;
        _frequency = frequency;
    }

    public ProcessSnapshot Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IReadOnlyList<RawProcessSample> values;
            try { values = _read(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                _previous.Clear();
                return new ProcessSnapshot(DateTimeOffset.Now, Array.Empty<ProcessReading>(), _logicalProcessorCount,
                    "Windows 进程清单暂时不可用：" + ex.Message);
            }

            var readings = new List<ProcessReading>(values.Count);
            var seen = new HashSet<int>();
            foreach (RawProcessSample raw in values)
            {
                if (raw.Pid < 0 || !seen.Add(raw.Pid)) continue;
                _previous.TryGetValue(raw.Pid, out RawProcessSample? previous);
                readings.Add(Calculate(previous, raw, _logicalProcessorCount, _frequency));
                // Without the creation timestamp, a PID is not a safe interval identity.
                if (raw.CreationTimeFileTime.HasValue) _previous[raw.Pid] = raw;
                else _previous.Remove(raw.Pid);
            }
            foreach (int stale in _previous.Keys.Where(pid => !seen.Contains(pid)).ToArray()) _previous.Remove(stale);
            int unavailable = readings.Count(r => r.CpuPercent is null || r.WorkingSetBytes is null || r.ReadBytesPerSecond is null || r.WriteBytesPerSecond is null);
            return new ProcessSnapshot(DateTimeOffset.Now, readings.AsReadOnly(), _logicalProcessorCount,
                unavailable == 0 ? null : $"{unavailable} 个进程的部分指标正在建立基线或受 Windows 保护限制；缺失值显示“不可用”。");
        }
    }

    internal static ProcessReading Calculate(RawProcessSample? previous, RawProcessSample current, int processorCount, long frequency)
    {
        bool sameProcess = previous is not null && previous.Pid == current.Pid
            && previous.CreationTimeFileTime.HasValue && current.CreationTimeFileTime == previous.CreationTimeFileTime;
        double seconds = sameProcess && frequency > 0 && current.Timestamp > previous!.Timestamp
            ? (current.Timestamp - (double)previous.Timestamp) / frequency : 0;
        double? cpu = null, read = null, write = null;
        if (seconds > 0 && double.IsFinite(seconds))
        {
            if (processorCount > 0 && Delta(previous!.CpuTime100Ns, current.CpuTime100Ns) is double cpuTicks)
                cpu = Math.Clamp(cpuTicks / 10_000_000d / seconds / processorCount * 100d, 0, 100);
            read = Rate(previous!.ReadBytes, current.ReadBytes, seconds);
            write = Rate(previous.WriteBytes, current.WriteBytes, seconds);
        }
        string? status = current.StatusMessage;
        if (seconds <= 0 && current.CreationTimeFileTime.HasValue)
            status = string.IsNullOrWhiteSpace(status) ? "正在建立 CPU / I/O 采样基线" : status + "；正在建立 CPU / I/O 采样基线";
        return new ProcessReading(current.Pid, current.CreationTimeFileTime, current.Name, cpu,
            current.WorkingSetBytes is >= 0 ? current.WorkingSetBytes : null, read, write, current.ExecutablePath, status);
    }

    /// <summary>Filters first, then returns a deterministic ranking; missing metrics always sort last.</summary>
    internal static IReadOnlyList<ProcessReading> Rank(IReadOnlyList<ProcessReading> values, ProcessSortOrder order, string? filter, int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        string text = filter?.Trim() ?? "";
        var filtered = values.Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal));
        Func<ProcessReading, double?> metric = order switch
        {
            ProcessSortOrder.Memory => p => p.WorkingSetBytes,
            ProcessSortOrder.Read => p => p.ReadBytesPerSecond,
            ProcessSortOrder.Write => p => p.WriteBytesPerSecond,
            _ => p => p.CpuPercent
        };
        return filtered.OrderByDescending(p => metric(p).HasValue).ThenByDescending(metric)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).Take(limit).ToArray();
    }

    private static IReadOnlyList<RawProcessSample> ReadProcesses()
    {
        var readings = new List<RawProcessSample>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                int pid;
                string name;
                try { pid = process.Id; name = process.ProcessName; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { continue; }
                using SafeProcessHandle handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION only.
                long? creation = null, memory = null;
                ulong? cpu = null, read = null, write = null;
                string? path = null;
                var missing = new List<string>();
                if (!handle.IsInvalid)
                {
                    if (GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
                    {
                        if (created.Value is > 0 and <= long.MaxValue) creation = (long)created.Value;
                        if (kernel.Value <= ulong.MaxValue - user.Value) cpu = kernel.Value + user.Value;
                    }
                    else missing.Add("CPU 计时不可读");
                    if (GetProcessIoCounters(handle, out var io)) { read = io.ReadTransferCount; write = io.WriteTransferCount; }
                    else missing.Add("I/O 计数不可读");
                    uint size = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
                    var counters = new ProcessMemoryCounters { Size = size };
                    if (K32GetProcessMemoryInfo(handle, ref counters, size) && counters.WorkingSetSize.ToUInt64() <= long.MaxValue)
                        memory = (long)counters.WorkingSetSize.ToUInt64();
                    else missing.Add("内存占用不可读");
                    var buffer = new StringBuilder(2048);
                    uint characters = (uint)buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref characters)) path = buffer.ToString();
                }
                else missing.Add("受保护、权限不足或进程已退出");
                readings.Add(new RawProcessSample(pid, creation, name, cpu, memory, read, write,
                    Stopwatch.GetTimestamp(), path, missing.Count == 0 ? null : string.Join("；", missing)));
            }
        }
        return readings;
    }

    private static int ActiveProcessorCount()
    {
        uint count = GetActiveProcessorCount(0xffff); // ALL_PROCESSOR_GROUPS, including machines with more than 64 CPUs.
        return count is > 0 and <= int.MaxValue ? (int)count : Math.Max(1, Environment.ProcessorCount);
    }
    private static double? Delta(ulong? before, ulong? after) => before is ulong a && after is ulong b && b >= a ? b - a : null;
    private static double? Rate(ulong? before, ulong? after, double seconds) => Delta(before, after) is double bytes ? bytes / seconds : null;
    public void Dispose() { lock (_gate) { _disposed = true; _previous.Clear(); } }

    internal sealed record RawProcessSample(int Pid, long? CreationTimeFileTime, string Name,
        ulong? CpuTime100Ns, long? WorkingSetBytes, ulong? ReadBytes, ulong? WriteBytes,
        long Timestamp, string? ExecutablePath = null, string? StatusMessage = null);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime creation, out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCounters counters, uint size);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);
}

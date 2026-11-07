using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class ProcessSmoke
{
    public static Task RunAsync()
    {
        var first = new ProcessMonitorService.RawProcessSample(42, 123, "worker", 10_000_000, 4096, 1000, 2000, 1000);
        var second = first with { CpuTime100Ns = 30_000_000, ReadBytes = 1000 + 4_194_304, WriteBytes = 2000 + 1_048_576, Timestamp = 3000 };
        var baseline = ProcessMonitorService.Calculate(null, first, 4, 1000);
        Check(baseline.CpuPercent is null && baseline.ReadBytesPerSecond is null && baseline.WriteBytesPerSecond is null,
            "first sample does not invent CPU/I/O rates");
        Check(baseline.WorkingSetBytes == 4096, "instantaneous working set does not require a baseline");
        var rates = ProcessMonitorService.Calculate(first, second, 4, 1000);
        Near(rates.CpuPercent, 25, "CPU elapsed time normalized across all logical processors");
        Near(rates.ReadBytesPerSecond, 2_097_152, "read transfer rate uses elapsed monotonic time");
        Near(rates.WriteBytesPerSecond, 524_288, "write transfer rate uses elapsed monotonic time");
        var twoGroups = ProcessMonitorService.Calculate(first, second, 128, 1000);
        Near(twoGroups.CpuPercent, .78125, "processor counts greater than 64 normalize over all groups");
        var rebootedPid = ProcessMonitorService.Calculate(first, second with { CreationTimeFileTime = 124 }, 4, 1000);
        Check(rebootedPid.CpuPercent is null && rebootedPid.ReadBytesPerSecond is null, "PID reuse is a new process");
        var uncertain = ProcessMonitorService.Calculate(first with { CreationTimeFileTime = null }, second with { CreationTimeFileTime = null }, 4, 1000);
        Check(uncertain.CpuPercent is null && uncertain.ReadBytesPerSecond is null, "unknown creation time must never bridge PID intervals");
        var denied = ProcessMonitorService.Calculate(first, second with { CpuTime100Ns = null, WorkingSetBytes = null, ReadBytes = null }, 4, 1000);
        Check(denied.CpuPercent is null && denied.WorkingSetBytes is null && denied.ReadBytesPerSecond is null,
            "access-denied fields remain missing rather than zero");
        Near(denied.WriteBytesPerSecond, 524_288, "one unavailable metric does not discard another valid metric");
        var zero = ProcessMonitorService.Calculate(first, first with { Timestamp = 3000 }, 4, 1000);
        Check(zero.CpuPercent == 0 && zero.ReadBytesPerSecond == 0 && zero.WriteBytesPerSecond == 0,
            "confirmed unchanged counters are true zero");
        var reset = ProcessMonitorService.Calculate(first, second with { CpuTime100Ns = 1, ReadBytes = 1, WriteBytes = 1 }, 4, 1000);
        Check(reset.CpuPercent is null && reset.ReadBytesPerSecond is null && reset.WriteBytesPerSecond is null, "counter resets do not wrap into spikes");
        var stalled = ProcessMonitorService.Calculate(first, second with { Timestamp = first.Timestamp }, 4, 1000);
        Check(stalled.CpuPercent is null && stalled.ReadBytesPerSecond is null, "no elapsed interval is not a rate");
        var invalidMemory = ProcessMonitorService.Calculate(null, first with { WorkingSetBytes = -1 }, 4, 1000);
        Check(invalidMemory.WorkingSetBytes is null, "invalid working set is unavailable");

        var queue = new Queue<IReadOnlyList<ProcessMonitorService.RawProcessSample>>(new[]
        {
            new[] { first }, Array.Empty<ProcessMonitorService.RawProcessSample>(), new[] { second }, new[] { second with { Timestamp = 5000 } }
        });
        using (var monitor = new ProcessMonitorService(() => queue.Dequeue(), 4, 1000))
        {
            Check(monitor.Sample().Processes.Count == 1, "process appears");
            Check(monitor.Sample().Processes.Count == 0, "exited process removed");
            Check(monitor.Sample().Processes.Single().CpuPercent is null, "a disappeared process starts a fresh baseline even if PID matches");
            Check(monitor.Sample().Processes.Single().CpuPercent == 0, "continuous process resumes valid intervals");
        }
        using (var deniedMonitor = new ProcessMonitorService(() => throw new UnauthorizedAccessException("fixture"), 4, 1000))
        {
            var unavailable = deniedMonitor.Sample();
            Check(unavailable.Processes.Count == 0 && unavailable.StatusMessage is not null, "enumeration failure is explicit and does not invent processes");
        }

        var ranking = new[]
        {
            new ProcessReading(100, 1, "alpha", 5, 2048, 20, 10, null, null),
            new ProcessReading(102, 1, "Beta", null, null, null, null, null, "denied"),
            new ProcessReading(101, 1, "zeta", 9, 1024, 30, 40, null, null),
            new ProcessReading(103, 1, "alpha", 5, 4096, 10, 50, null, null)
        };
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Cpu, null).Select(r => r.Pid).SequenceEqual(new[] { 101, 100, 103, 102 }), "CPU rank stable ties and missing values last");
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Memory, "").First().Pid == 103, "working-set sorting");
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Read, null).First().Pid == 101, "I/O read sorting");
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Write, null).First().Pid == 103, "I/O write sorting");
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Cpu, "  ALPHA ").Count == 2, "name search is case insensitive and trims whitespace");
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Cpu, "102").Single().Name == "Beta", "PID search includes protected processes");
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Cpu, "not-there").Count == 0, "empty search results do not show unrelated processes");
        Check(ProcessMonitorService.Rank(ranking, ProcessSortOrder.Cpu, null, 2).Count == 2, "ranking output is bounded");
        var disposed = new ProcessMonitorService(() => new[] { first }, 4, 1000);
        disposed.Dispose();
        bool rejected = false;
        try { disposed.Sample(); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "disposed monitor rejects sampling before touching Windows");
        Console.WriteLine("PASS process CPU/I/O accounting, all-processor normalization, PID reuse, missing data, lifecycle, search and bounded ranking");
        return Task.CompletedTask;
    }

    public static async Task RunLiveAsync()
    {
        using var monitor = new ProcessMonitorService();
        await Task.Run(() => monitor.Sample());
        await Task.Delay(300);
        var snapshot = await Task.Run(() => monitor.Sample());
        Check(snapshot.Processes.Select(p => p.Pid).Distinct().Count() == snapshot.Processes.Count, "live PID identities unique");
        var own = snapshot.Processes.Single(p => p.Pid == Environment.ProcessId);
        Check(own.CreationTimeFileTime.HasValue && own.WorkingSetBytes > 0 && own.CpuPercent is >= 0 and <= 100,
            "own-process live identity, working set and CPU can be read");
        Check(own.ReadBytesPerSecond is >= 0 && own.WriteBytesPerSecond is >= 0, "own-process live I/O can be read");
        Console.WriteLine($"PASS process live sampling: {snapshot.Processes.Count} processes, {snapshot.LogicalProcessorCount} logical processors; self CPU {own.CpuPercent:0.00}%, working set {own.WorkingSetBytes:N0} bytes.");
    }
    private static void Near(double? actual, double expected, string message) => Check(actual.HasValue && Math.Abs(actual.Value - expected) <= .00001 * Math.Max(1, Math.Abs(expected)), message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Process check failed: " + message); }
}

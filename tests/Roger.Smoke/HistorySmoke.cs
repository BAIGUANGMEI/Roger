using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class HistorySmoke
{
    private static readonly DateTimeOffset Now = new(2030, 1, 8, 12, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Roger-History-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            VerifyAggregation(Path.Combine(directory, "aggregation"));
            VerifyGpuSeparation(Path.Combine(directory, "gpus"));
            VerifyDiskAndMissing(Path.Combine(directory, "disks"));
            VerifyPersistenceAndCorruptLines(Path.Combine(directory, "persistence"));
            VerifyRestartSessions(Path.Combine(directory, "sessions"));
            VerifyRetention(Path.Combine(directory, "retention"));
            VerifyCsv(Path.Combine(directory, "export.csv"));
            Console.WriteLine("PASS history: weighted minute aggregates, peaks/missing data, GPU isolation, bounded retention, reload/corrupt-line preservation and escaped CSV export");
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static void VerifyAggregation(string directory)
    {
        using var service = new HistoryMonitorService(directory, () => Now);
        service.Initialize();
        Check(!Directory.Exists(directory), "initializing an empty history creates no data files");
        DateTimeOffset start = Now.AddMinutes(-2);
        service.Record(Sample(start.AddSeconds(1), cpu: 10, temperature: 40));
        service.Record(Sample(start.AddSeconds(20), cpu: 20, temperature: 50));
        service.Record(Sample(start.AddSeconds(50), cpu: null, temperature: 0));
        var first = service.Query(TimeSpan.FromHours(1)).Single();
        Check(first.SampleCount == 3 && first.Timestamp == start, "samples share the UTC minute bucket");
        var statistic = first.Metrics[HistoryMetrics.CpuLoad];
        Check(statistic.Count == 2 && statistic.Average == 15 && statistic.Minimum == 10 && statistic.Maximum == 20,
            "missing reading contributes no zero or denominator");
        Check(first.Metrics[HistoryMetrics.CpuTemperature].Average == 45 && first.Metrics[HistoryMetrics.CpuTemperature].Maximum == 50,
            "uninitialized zero temperature is not a real reading");
        service.Record(Sample(start.AddMinutes(1), cpu: 90, temperature: 60));
        var buckets = service.Query(TimeSpan.FromHours(1));
        Check(buckets.Count == 2, "crossing a minute finishes the previous bucket");
        var combined = HistoryMonitorService.Summarize(buckets, HistoryMetrics.CpuLoad)!;
        Near(combined.Average, 40, "summary is weighted by actual samples, not the average of minute averages");
        Check(combined.Maximum == 90 && combined.Count == 3, "summary peak comes from original readings");
        first.Metrics.Clear();
        Check(service.Query(TimeSpan.FromHours(1))[0].Metrics.Count > 0, "returned bucket dictionaries are isolated copies");
        service.Record(Sample(start.AddMinutes(1), cpu: 100));
        service.Record(Sample(start, cpu: 100));
        Check(service.Query(TimeSpan.FromHours(1)).Sum(bucket => bucket.SampleCount) == 4, "duplicates and out-of-order snapshots are ignored");
        Expect<ArgumentOutOfRangeException>(() => service.Query(TimeSpan.Zero));
        Expect<ArgumentOutOfRangeException>(() => service.Query(TimeSpan.FromDays(8)));
        service.Dispose();
        Expect<ObjectDisposedException>(() => service.Query(TimeSpan.FromHours(1)));
        Expect<ObjectDisposedException>(service.Flush);
    }

    private static void VerifyGpuSeparation(string directory)
    {
        using var service = new HistoryMonitorService(directory, () => Now);
        DateTimeOffset start = Now.AddMinutes(-1);
        service.Record(Sample(start.AddSeconds(1), gpu: 10, gpuId: "/gpu/A"));
        service.Record(Sample(start.AddSeconds(2), gpu: 20, gpuId: "/gpu/A"));
        service.Record(Sample(start.AddSeconds(3), gpu: 95, gpuId: "/gpu/B"));
        service.Record(Sample(start.AddSeconds(4), gpu: 40, gpuId: "/gpu/A"));
        var buckets = service.Query(TimeSpan.FromHours(1));
        Check(buckets.Count == 3 && buckets.All(bucket => bucket.Timestamp == start), "GPU changes split even within one clock minute");
        var a = HistoryMonitorService.Summarize(buckets, HistoryMetrics.GpuLoad, "/gpu/A")!;
        var b = HistoryMonitorService.Summarize(buckets, HistoryMetrics.GpuLoad, "/gpu/B")!;
        Near(a.Average, 70d / 3, "same GPU segments can be summarized without borrowing another device's values");
        Check(a.Count == 3 && a.Maximum == 40 && b.Count == 1 && b.Maximum == 95, "same-model GPU readings stay attached to stable IDs");
        Check(HistoryMonitorService.Summarize(buckets, HistoryMetrics.GpuLoad, "/gpu/missing") is null, "missing selected GPU has no fabricated zero");
    }

    private static void VerifyDiskAndMissing(string directory)
    {
        using var service = new HistoryMonitorService(directory, () => Now);
        DateTimeOffset start = Now.AddMinutes(-2);
        var reading = Sample(start.AddSeconds(1), network: "网卡 · 等待采样", download: 10);
        service.Record(reading, new StorageSnapshot(reading.Timestamp, new[] { Disk(0, 100, 30), Disk(1, 200, 70) }, null));
        reading = Sample(start.AddSeconds(2), network: "Ethernet", download: 0);
        service.Record(reading, new StorageSnapshot(reading.Timestamp, new[] { Disk(0, 100, 30), Disk(1, null, 70) }, null));
        reading = Sample(start.AddSeconds(3), network: "无活动物理网卡", download: 999);
        service.Record(reading, new StorageSnapshot(reading.Timestamp.AddSeconds(-31), new[] { Disk(0, 999, 999) }, null));
        HistoryBucket bucket = service.Query(TimeSpan.FromHours(1)).Single();
        Check(bucket.Metrics[HistoryMetrics.DiskRead].Count == 1 && bucket.Metrics[HistoryMetrics.DiskRead].Average == 300,
            "physical read totals require all disks, stale storage does not join current samples");
        Check(bucket.Metrics[HistoryMetrics.DiskWrite].Count == 2 && bucket.Metrics[HistoryMetrics.DiskWrite].Average == 100,
            "valid write totals remain available when a read counter is missing");
        Check(bucket.Metrics[HistoryMetrics.Download].Count == 1 && bucket.Metrics[HistoryMetrics.Download].Average == 0,
            "network baseline/disconnected state is missing while a measured zero is genuine");
        service.Record(Sample(start.AddMinutes(1), cpu: double.NaN, gpu: double.PositiveInfinity, temperature: null) with
        { MemoryUsedGb = 0, MemoryTotalGb = 0, NetworkName = "等待采样" });
        var empty = service.Query(TimeSpan.FromHours(1)).Last();
        Check(!empty.Metrics.ContainsKey(HistoryMetrics.CpuLoad) && !empty.Metrics.ContainsKey(HistoryMetrics.GpuLoad)
            && !empty.Metrics.ContainsKey(HistoryMetrics.Memory), "nonfinite and unavailable metrics never turn into zeros");
    }

    private static void VerifyPersistenceAndCorruptLines(string directory)
    {
        Directory.CreateDirectory(directory);
        var prior = Bucket(Now.AddMinutes(-3), 20);
        string file = DayPath(directory, Now);
        string corrupt = JsonSerializer.Serialize(prior) + "\n{incomplete-tail";
        File.WriteAllText(file, corrupt);
        using (var service = new HistoryMonitorService(directory, () => Now))
        {
            service.Initialize();
            Check(service.Query(TimeSpan.FromHours(1)).Count == 1 && service.LastPersistenceWarning is not null, "bad append tail does not hide previous valid rows");
            Check(File.ReadAllText(file) == corrupt, "loading preserves incomplete original bytes");
            service.Record(Sample(Now.AddMinutes(-1), cpu: 70));
            service.Flush();
            Check(File.ReadAllText(file).Contains("{incomplete-tail", StringComparison.Ordinal), "subsequent append retains corrupt evidence");
        }
        using (var service = new HistoryMonitorService(directory, () => Now))
        {
            var buckets = service.Query(TimeSpan.FromHours(1));
            Check(buckets.Count == 2 && buckets.Last().Metrics[HistoryMetrics.CpuLoad].Average == 70,
                "leading newline isolates corrupt tail and valid appended aggregate reloads");
        }
        string text = File.ReadAllText(file);
        File.AppendAllText(file, "\n" + JsonSerializer.Serialize(prior));
        using var deduped = new HistoryMonitorService(directory, () => Now);
        Check(deduped.Query(TimeSpan.FromHours(1)).Count == 2, "retried append with same ID is not counted twice");
    }

    private static void VerifyRetention(string directory)
    {
        Directory.CreateDirectory(directory);
        string oldestFile = DayPath(directory, Now.AddDays(-8));
        File.WriteAllText(oldestFile, JsonSerializer.Serialize(Bucket(Now.AddDays(-8), 90)));
        string cutoffFile = DayPath(directory, Now.AddDays(-7));
        File.WriteAllText(cutoffFile, JsonSerializer.Serialize(Bucket(Now.AddDays(-7).AddMinutes(-1), 90)) + "\n"
            + JsonSerializer.Serialize(Bucket(Now.AddDays(-7).AddMinutes(1), 10)));
        using var service = new HistoryMonitorService(directory, () => Now);
        Check(service.Query(TimeSpan.FromDays(7)).Count == 1, "queries enforce seven-day cutoff within the oldest daily file");
        Check(!File.Exists(oldestFile) && File.Exists(cutoffFile), "expired daily files are removed while boundary day is retained");
        service.Record(Sample(Now.AddDays(-8), cpu: 90));
        service.Record(Sample(Now.AddMinutes(6), cpu: 90));
        Check(service.Query(TimeSpan.FromDays(7)).Count == 1, "expired and implausibly future samples are not recorded");
    }

    private static void VerifyRestartSessions(string directory)
    {
        DateTimeOffset minute = Now.AddMinutes(-1);
        Guid firstSession;
        using (var first = new HistoryMonitorService(directory, () => Now))
        {
            first.Record(Sample(minute.AddSeconds(1), cpu: 5));
            firstSession = first.Query(TimeSpan.FromHours(1)).Single().SessionId;
            Check(firstSession != Guid.Empty, "new history records carry the current run's session identity");
        }
        Guid secondSession;
        using (var second = new HistoryMonitorService(directory, () => Now))
        {
            second.Record(Sample(minute.AddSeconds(2), cpu: 95));
            var sameMinute = second.Query(TimeSpan.FromHours(1));
            Check(sameMinute.Count == 2 && sameMinute.All(bucket => bucket.Timestamp == minute),
                "restarting within the same minute does not merge runs into a single bucket");
            secondSession = sameMinute.Single(bucket => bucket.Metrics[HistoryMetrics.CpuLoad].Average == 95).SessionId;
            Check(secondSession != Guid.Empty && secondSession != firstSession, "each service lifetime gets a distinct nonempty session");
        }
        using (var reloaded = new HistoryMonitorService(directory, () => Now))
        {
            var sessions = reloaded.Query(TimeSpan.FromHours(1)).Select(bucket => bucket.SessionId).ToHashSet();
            Check(sessions.Count == 2 && sessions.Contains(firstSession) && sessions.Contains(secondSession),
                "disk reload preserves both session identities for chart discontinuities");
        }

        var legacy = Bucket(minute.AddMinutes(-1), 35);
        // Deliberately omit SessionId, as records from before this feature do.
        string legacyJson = JsonSerializer.Serialize(new
        { legacy.Id, legacy.Timestamp, legacy.GpuId, legacy.GpuName, legacy.SampleCount, legacy.Metrics });
        Check(!legacyJson.Contains("SessionId", StringComparison.Ordinal), "legacy fixture actually omits the new property");
        File.AppendAllText(DayPath(directory, Now), "\n" + legacyJson);
        using var withLegacy = new HistoryMonitorService(directory, () => Now);
        var all = withLegacy.Query(TimeSpan.FromHours(1));
        Check(all.Count == 3 && all.Single(bucket => bucket.Id == legacy.Id).SessionId == Guid.Empty,
            "old JSON without session metadata remains readable without inventing an identity");
    }

    private static void VerifyCsv(string path)
    {
        HistoryBucket item = Bucket(Now, 1.5) with { GpuId = "+SUM(1,2)", GpuName = " \t=HYPERLINK(\"x\"),\r\nsecond" };
        CultureInfo prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            HistoryMonitorService.ExportCsv(path, new[] { item });
        }
        finally { CultureInfo.CurrentCulture = prior; }
        string csv = File.ReadAllText(path);
        Check(csv.Contains("\"'+SUM(1,2)\"", StringComparison.Ordinal), "formula-like GPU IDs receive an apostrophe inside the CSV field");
        Check(csv.Contains("\"' \t=HYPERLINK(\"\"x\"\"),  second\"", StringComparison.Ordinal), "CSV escapes quotes and commas, removes embedded newlines, and neutralizes whitespace-prefixed formulas");
        Check(File.ReadAllLines(path).Length == 2 && csv.Contains(",1.5,1.5,", StringComparison.Ordinal), "numeric export is invariant-culture and one physical row per bucket");
        Check(csv.StartsWith("时间,GPU标识,GPU型号,采样数,", StringComparison.Ordinal), "CSV names exported metrics and identity columns");
    }

    private static string DayPath(string directory, DateTimeOffset date) => Path.Combine(directory, "history-" + date.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
    private static HistoryBucket Bucket(DateTimeOffset timestamp, double cpu) => new(Guid.NewGuid(), timestamp, "/gpu/A", "Same model GPU", 1,
        new Dictionary<string, HistoryStatistic> { [HistoryMetrics.CpuLoad] = new(1, cpu, cpu, cpu) });
    private static MonitorSnapshot Sample(DateTimeOffset timestamp, double? cpu = 10, double? gpu = 20, double? temperature = 40,
        string gpuId = "/gpu/A", string network = "Ethernet", double download = 100)
        => new(timestamp, cpu, temperature, gpu, temperature, null, null, 40, 100, download, 50, network,
            Array.Empty<DriveReading>(), Array.Empty<SensorReading>(), null) { SelectedGpuId = gpuId };
    private static PhysicalDiskReading Disk(int number, double? read, double? write) => new(number, "disk" + number, "SSD", "test", "NVMe", "SSD",
        1024, "C:", 0, read, write, 0, null, null, null, null, null, null, "正常", "在线", "fixture", null);
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < 0.00001, message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("History smoke failed: " + message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}

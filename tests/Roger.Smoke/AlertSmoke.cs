using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class AlertSmoke
{
    private static readonly DateTimeOffset Epoch = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AlertOptions Fast = new() { RequiredDurationSeconds = 3, CooldownSeconds = 10 };

    public static Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Roger-Alerts-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyThresholdAndDuration(Path.Combine(root, "duration"));
            VerifyMissingAndIdentity(Path.Combine(root, "identity"));
            VerifyEveryDevice(Path.Combine(root, "devices"));
            VerifyPersistence(Path.Combine(root, "persistence"));
            VerifyCorruptPreservation(Path.Combine(root, "corrupt"));
            VerifyBounds(Path.Combine(root, "bounds"));
            Console.WriteLine("PASS alerts: sustained thresholds for every CPU/GPU, recovery/cooldown, missing sensors, device identity, quiet mode, bounded atomic persistence and corrupt-file preservation");
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static void VerifyThresholdAndDuration(string path)
    {
        var service = new AlertService(path);
        Check(!Directory.Exists(path), "loading defaults has no filesystem side effects");
        service.SaveOptions(Fast);
        Check(service.Evaluate(Sample(0, cpu: 91)).Count == 0, "first high reading must establish duration baseline");
        Check(service.Evaluate(Sample(2, cpu: 92)).Count == 0, "brief spike must not alert");
        var first = service.Evaluate(Sample(3, cpu: 93));
        Check(first.Count == 1 && first[0].Title == "CPU 持续高温" && first[0].Value == 93, "sustained threshold emits actual value");
        Check(service.Evaluate(Sample(6, cpu: 91)).Count == 0, "ongoing high readings are subject to cooldown");
        Check(service.Evaluate(Sample(13, cpu: 91)).Count == 1, "ongoing incident can remind after cooldown");
        Check(service.Evaluate(Sample(14, cpu: 50)).Count == 0, "normal state ends a high episode");
        Check(service.Evaluate(Sample(23, cpu: 91)).Count == 0, "new episode must establish duration again");
        Check(service.Evaluate(Sample(25, cpu: 91)).Count == 0, "recovery does not let new episode borrow old duration");
        Check(service.Evaluate(Sample(26, cpu: 91)).Count == 1, "recovered metric can alert after a sustained new episode");

        var all = new AlertService(Path.Combine(path, "all"));
        all.SaveOptions(Fast);
        all.Evaluate(Sample(0, cpu: 91, gpu: 86, memoryUsed: 95, free: 5));
        var incidents = all.Evaluate(Sample(3, cpu: 91, gpu: 86, memoryUsed: 95, free: 5));
        Check(incidents.Count == 4 && incidents.Select(item => item.MetricKey).Distinct().Count() == 4, "four configured metric categories evaluated separately");
        Check(incidents.Single(item => item.MetricKey.StartsWith("disk-free:", StringComparison.Ordinal)).Value == 5,
            "disk threshold is free percentage, not used percentage");
        all.SaveOptions(Fast with { Enabled = false });
        Check(all.Evaluate(Sample(20, cpu: 99, gpu: 99, memoryUsed: 99, free: 1)).Count == 0, "disabled reminders perform no incident recording");
        int recorded = all.Records.Count;
        all.SaveOptions(Fast with { Quiet = true });
        all.Evaluate(Sample(21, cpu: 99));
        Check(all.Evaluate(Sample(24, cpu: 99)).Count == 1 && all.Records.Count == recorded + 1 && all.Options.Quiet,
            "quiet mode retains incidents so the caller may suppress notifications only");
    }

    private static void VerifyMissingAndIdentity(string path)
    {
        var service = new AlertService(path);
        service.SaveOptions(Fast);
        service.Evaluate(Sample(0, cpu: 91));
        service.Evaluate(Sample(2, cpu: null));
        Check(service.Evaluate(Sample(3, cpu: 91)).Count == 0, "missing sensor breaks accumulation");
        Check(service.Evaluate(Sample(6, cpu: 91)).Count == 1, "valid sensor needs a fresh sustained interval");
        service.Evaluate(Sample(7, cpu: 0));
        Check(service.Evaluate(Sample(8, cpu: double.NaN, gpu: double.PositiveInfinity)).Count == 0, "zero/uninitialized or nonfinite temperatures are ignored");

        var gpu = new AlertService(Path.Combine(path, "gpu"));
        gpu.SaveOptions(Fast);
        gpu.Evaluate(Sample(0, gpu: 90, gpuId: "/gpu/A"));
        Check(gpu.Evaluate(Sample(3, gpu: 90, gpuId: "/gpu/B")).Count == 0, "new GPU cannot inherit previous GPU duration");
        Check(gpu.Evaluate(Sample(6, gpu: 90, gpuId: "/gpu/B")).Count == 1, "second GPU has its own threshold interval");
        Check(gpu.Evaluate(Sample(7, gpu: 90, gpuId: "/gpu/A")).Count == 0, "switching back needs fresh observations");
        Check(gpu.Evaluate(Sample(10, gpu: 90, gpuId: "/gpu/A")).Count == 1, "GPU A does not inherit B's cooldown");

        var gaps = new AlertService(Path.Combine(path, "gaps"));
        gaps.SaveOptions(Fast);
        gaps.Evaluate(Sample(0, cpu: 91));
        Check(gaps.Evaluate(Sample(40, cpu: 91)).Count == 0, "long telemetry gaps cannot imply continuous overload");
        Check(gaps.Evaluate(Sample(43, cpu: 91)).Count == 1, "fresh interval after a gap can alert");
        Check(gaps.Evaluate(Sample(5, cpu: 91)).Count == 0, "wall-clock reversal restarts the duration");
        Check(gaps.Evaluate(Sample(8, cpu: 91)).Count == 1, "future previous timestamp cannot suppress reminders indefinitely");

        var invalidMemory = Sample(0, memoryUsed: 101, free: -1);
        var invalid = new AlertService(Path.Combine(path, "invalid")); invalid.SaveOptions(Fast);
        invalid.Evaluate(invalidMemory);
        Check(invalid.Evaluate(invalidMemory with { Timestamp = Epoch.AddSeconds(3) }).Count == 0, "invalid memory and drive capacities do not create incidents");
    }

    private static void VerifyPersistence(string path)
    {
        var service = new AlertService(path);
        service.SaveOptions(Fast with { Quiet = true, CpuTemperatureThresholdCelsius = 95 });
        service.SaveOptions(Fast with { Quiet = true });
        Check(File.Exists(service.OptionsPath + ".bak"), "options save atomically retains previous version");
        service.Evaluate(Sample(0, cpu: 91));
        AlertEvent item = service.Evaluate(Sample(3, cpu: 91)).Single();
        var reloaded = new AlertService(path);
        Check(reloaded.Options == service.Options && reloaded.Records.Single() == item, "options and exact alert record survive restart");
        reloaded.Evaluate(Sample(4, cpu: 91));
        Check(reloaded.Evaluate(Sample(7, cpu: 91)).Count == 0, "persisted incident retains its own cooldown after restart");
        Check(reloaded.Evaluate(Sample(13, cpu: 91)).Count == 1, "persisted cooldown eventually expires");
        reloaded.ClearRecords();
        Check(reloaded.Records.Count == 0 && new AlertService(path).Records.Count == 0, "explicit clear persists an empty log");
        Check(reloaded.Evaluate(Sample(14, cpu: 91)).Count == 0, "clearing the log must not reset live cooldowns");
        Check(!Directory.EnumerateFiles(path, "*.tmp").Any(), "completed atomic saves leave no temporary files");

        string before = File.ReadAllText(service.OptionsPath);
        foreach (AlertOptions options in new[]
        {
            Fast with { CpuTemperatureThresholdCelsius = double.NaN }, Fast with { GpuTemperatureThresholdCelsius = 0 },
            Fast with { MemoryPercentThreshold = 101 }, Fast with { DiskFreePercentThreshold = 0 },
            Fast with { RequiredDurationSeconds = 0 }, Fast with { CooldownSeconds = 86401 }
        })
        {
            Expect<ArgumentException>(() => service.SaveOptions(options));
            Check(File.ReadAllText(service.OptionsPath) == before, "invalid options never replace persisted preferences");
        }
    }

    private static void VerifyEveryDevice(string path)
    {
        var service = new AlertService(path); service.SaveOptions(Fast);
        var a = new GpuDeviceReading("/gpu/A", "Cool integrated GPU", "Intel", false, 0, 40, null, null, null, null, "fixture", null);
        var b = new GpuDeviceReading("/gpu/B", "Hot discrete GPU", "AMD", true, 80, 90, null, null, null, null, "fixture", null);
        MonitorSnapshot Devices(int seconds, string selected, bool includeB = true) => Sample(seconds, gpu: 40, gpuId: selected) with
            { GpuDevices = includeB ? new[] { a, b } : new[] { a } };
        Check(service.Evaluate(Devices(0, a.Id)).Count == 0, "all GPU devices establish their own interval even when a cool GPU is selected");
        Check(service.Evaluate(Devices(1, b.Id)).Count == 0, "changing overview selection does not create an early incident");
        AlertEvent incident = service.Evaluate(Devices(3, a.Id)).Single();
        Check(incident.MetricKey.EndsWith(b.Id, StringComparison.Ordinal) && incident.Value == 90
            && incident.Title.Contains(b.Name, StringComparison.Ordinal) && incident.Message.Contains(b.Name, StringComparison.Ordinal),
            "non-selected hot GPU still alerts with the correct device identity and name");
        Check(service.Evaluate(Devices(6, b.Id)).Count == 0 && service.Evaluate(Devices(12, a.Id)).Count == 0,
            "overview selection neither resets nor bypasses the hot GPU cooldown");
        Check(service.Evaluate(Devices(13, a.Id)).Count == 1, "all-device incident can repeat once its own cooldown expires");
        service.Evaluate(Devices(14, a.Id, includeB: false));
        Check(service.Evaluate(Devices(23, a.Id)).Count == 0, "missing hot GPU cannot accumulate its previous duration");
        Check(service.Evaluate(Devices(25, b.Id)).Count == 0, "returning hot GPU requires the full duration independent of current selection");
        Check(service.Evaluate(Devices(26, a.Id)).Count == 1, "returning GPU alerts after a new sustained interval");

        var cpu = new AlertService(Path.Combine(path, "cpu")); cpu.SaveOptions(Fast);
        var cpuA = new CpuDeviceReading("/cpu/0", "Cool CPU socket", "Intel", 0, 40, "fixture", null);
        var cpuB = new CpuDeviceReading("/cpu/1", "Hot CPU socket", "AMD", 80, 95, "fixture", null);
        var first = Sample(0, cpu: 40) with { CpuDevices = new[] { cpuA, cpuB } };
        cpu.Evaluate(first);
        AlertEvent cpuIncident = cpu.Evaluate(first with { Timestamp = Epoch.AddSeconds(3) }).Single();
        Check(cpuIncident.MetricKey.EndsWith(cpuB.Id, StringComparison.Ordinal) && cpuIncident.Title.Contains(cpuB.Name, StringComparison.Ordinal)
            && cpuIncident.Message.Contains(cpuB.Name, StringComparison.Ordinal) && cpuIncident.Value == 95,
            "each CPU socket is checked even when the summary temperature is cooler");
        cpu.Evaluate(Sample(4, cpu: 95));
        Check(cpu.Evaluate(first with { Timestamp = Epoch.AddSeconds(5) }).Count == 0,
            "summary fallback does not lend its duration to a reappearing physical CPU");
    }

    private static void VerifyCorruptPreservation(string path)
    {
        Directory.CreateDirectory(path);
        string options = Path.Combine(path, "alerts-options.json"), records = Path.Combine(path, "alerts-records.json");
        File.WriteAllText(options, "{broken-options"); File.WriteAllText(records, "{broken-records");
        var service = new AlertService(path);
        Check(service.Options == new AlertOptions() && service.Records.Count == 0 && service.LastPersistenceWarning is not null,
            "corrupt inputs return defaults with explicit warning");
        Check(File.ReadAllText(options) == "{broken-options" && File.ReadAllText(records) == "{broken-records", "loading never overwrites corrupt inputs");
        service.SaveOptions(Fast);
        service.Evaluate(Sample(0, cpu: 91)); service.Evaluate(Sample(3, cpu: 91));
        Check(Directory.EnumerateFiles(path, "alerts-options.json.corrupt-*").Single() is string savedOptions && File.ReadAllText(savedOptions) == "{broken-options",
            "explicit configuration recovery preserves corrupt bytes");
        Check(Directory.EnumerateFiles(path, "alerts-records.json.corrupt-*").Single() is string savedRecords && File.ReadAllText(savedRecords) == "{broken-records",
            "background recording preserves corrupt log bytes before recovery");
        Check(new AlertService(path).Records.Count == 1, "subsequent log remains usable after preserving corruption");
    }

    private static void VerifyBounds(string path)
    {
        var service = new AlertService(path); service.SaveOptions(Fast with { RequiredDurationSeconds = 1, CooldownSeconds = 1 });
        service.Evaluate(Sample(0, cpu: 91));
        for (int i = 1; i <= 505; i++) service.Evaluate(Sample(i, cpu: 91));
        Check(service.Records.Count == AlertService.RecordCapacity && service.Records[0].Timestamp == Epoch.AddSeconds(505), "in-memory records keep newest bounded capacity");
        var loaded = new AlertService(path);
        Check(loaded.Records.Count == AlertService.RecordCapacity && loaded.Records[0] == service.Records[0], "persistent log retains bounded newest records");
    }

    private static MonitorSnapshot Sample(int seconds, double? cpu = null, double? gpu = null, string gpuId = "/gpu/A", double memoryUsed = 20, long free = 80)
        => new(Epoch.AddSeconds(seconds), 10, cpu, 20, gpu, null, null, memoryUsed, 100, 0, 0, "test",
            new[] { new DriveReading("C:\\", "test", 100, free, "NTFS") }, Array.Empty<SensorReading>(), null) { SelectedGpuId = gpuId };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Alert smoke failed: " + message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}

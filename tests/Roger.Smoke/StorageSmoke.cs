using System;
using System.Linq;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class StorageSmoke
{
    public static Task RunAsync()
    {
        var first = new StorageMonitorService.RawSample(10_000_000, 10_000_000, 100_000_000, 30_000_000,
            1_000, 2_000, 0, 100_000, 10);
        var second = new StorageMonitorService.RawSample(30_000_000, 10_000_000, 120_000_000, 45_000_000,
            1_000 + 4_194_304, 2_000 + 1_048_576, 2, 140_000, 30);
        var baseline = StorageMonitorService.Calculate(null, first);
        Check(baseline.ReadBytesPerSecond is null && baseline.ActivePercent is null && baseline.AverageResponseMs is null, "first sample must not invent a rate");
        var sample = StorageMonitorService.Calculate(first, second);
        Near(sample.ActivePercent, 25, "active is100%-idle, not concurrent disk-time sum");
        Near(sample.ReadBytesPerSecond, 2_097_152, "read byte delta divided by elapsed seconds");
        Near(sample.WriteBytesPerSecond, 524_288, "write byte delta divided by elapsed seconds");
        Near(sample.AverageResponseMs, 0.2, "PERF_AVERAGE_TIMER divided by frequency and completed requests");
        Near(sample.QueueLength, 2, "queue is an instantaneous gauge");

        var reset = StorageMonitorService.Calculate(first, second with { ReadBytes = 0, IdleTime = 1 });
        Check(reset.ReadBytesPerSecond is null && reset.ActivePercent is null, "reset counters must not yield underflow or false huge rates");
        Near(reset.WriteBytesPerSecond, 524_288, "an unrelated valid counter survives a partial reset");
        var idle = StorageMonitorService.Calculate(first, second with { IdleTime = 50_000_000, ResponseBase = 10 });
        Near(idle.ActivePercent, 0, "fully idle interval is a real zero");
        Check(idle.AverageResponseMs is null, "no completed requests means no measurable latency, not zero latency");
        Check(StorageMonitorService.Calculate(first, second with { Timestamp = first.Timestamp }).ReadBytesPerSecond is null, "zero elapsed interval");
        Check(StorageMonitorService.Calculate(first, second with { Frequency = 0 }).ReadBytesPerSecond is null, "invalid timer frequency");
        Check(StorageMonitorService.Calculate(first, second with { Frequency = 1_000 }).ReadBytesPerSecond is null, "changed clock frequency requires a baseline");
        Check(StorageMonitorService.Calculate(first, second with { SystemTime = 1 }).ActivePercent is null, "system timer reset cannot become100%busy");
        Check(StorageMonitorService.Calculate(first, second with { QueueLength = double.NaN }).QueueLength is null, "invalid queue gauge");

        Check(StorageMonitorService.ParseCounterDiskNumber("0 C: D:") == 0, "one physical disk can back several volumes");
        Check(StorageMonitorService.ParseCounterDiskNumber("12") == 12, "multi-digit physical disk index");
        Check(StorageMonitorService.ParseCounterDiskNumber("_Total") is null, "exclude aggregate to prevent double counting");
        Check(StorageMonitorService.ParseCounterDiskNumber("C:") is null, "logical volume is not a physical disk index");
        Check(StorageMonitorService.ReferenceKey("Win32_DiskPartition.DeviceID=\"Disk #0, Partition #2\"") == "Disk #0, Partition #2", "partition association key");
        Check(StorageMonitorService.ReferenceKey("Win32_LogicalDisk.DeviceID=\"D:\"") == "D:", "logical disk association key");

        var identities = new[]
        {
            new StorageMonitorService.PhysicalIdentity("unique-a", "serial-a", "SSD"),
            new StorageMonitorService.PhysicalIdentity("unique-b", "serial-b", "HDD")
        };
        Check(StorageMonitorService.MatchPhysical(identities, "unique-b", null)?.MediaType == "HDD", "stable identity, not model name or provider DeviceId");
        Check(StorageMonitorService.MatchPhysical(identities, null, "SERIAL-A")?.MediaType == "SSD", "unique serial fallback");
        Check(StorageMonitorService.MatchPhysical(identities, null, null) is null, "missing identity must not guess a disk");
        Check(StorageMonitorService.MatchPhysical(identities.Concat(identities.Take(1)).ToArray(), "unique-a", "serial-a") is null, "ambiguous device identities must not cross-assign telemetry");
        Check(StorageMonitorService.Bus(17) == "NVMe" && StorageMonitorService.Bus(1) == "SCSI", "NVMe bus does not get mislabeled SATA/SCSI");
        Check(StorageMonitorService.Media(null) == "未知" && StorageMonitorService.Health(null) == "未提供", "unsupported fields must not imply SSD or healthy SMART");
        var operationalFixtures = new (ushort Code, string Text)[]
        {
            (0, "未知"), (1, "其他（厂商定义）"), (14, "已中止"), (15, "未活动"), (17, "已完成"),
            (0xD010, "在线"), (0xD011, "未就绪"), (0xD012, "无介质"), (0xD013, "离线"), (0xD014, "故障")
        };
        foreach (var fixture in operationalFixtures)
        {
            Check(StorageMonitorService.Operational(fixture.Code) == fixture.Text, "documented MSFT_Disk scalar operational status " + fixture.Code);
            Check(StorageMonitorService.Operational(new[] { fixture.Code }) == fixture.Text, "WMI UInt16 array operational status " + fixture.Code);
        }
        Check(StorageMonitorService.Operational(new ushort[] { 2, 17 }) == "OK / 已完成", "completed status preserves accompanying operation result");
        Check(StorageMonitorService.Operational((ushort)65535) == "状态代码 65535", "unknown vendor operational code remains visible without a guessed meaning");
        Check(StorageMonitorService.Operational(null) == "未提供" && StorageMonitorService.Operational(Array.Empty<ushort>()) == "未提供", "absent operational status is distinct from reported unknown");
        Check(!StorageMonitorService.NeedsMetadataRefresh(false, true, TimeSpan.FromSeconds(4)), "failed inventory does not create a busy retry loop");
        Check(StorageMonitorService.NeedsMetadataRefresh(false, true, TimeSpan.FromSeconds(5)), "cold WMI inventory failure retries after five seconds");
        Check(StorageMonitorService.NeedsMetadataRefresh(false, false, TimeSpan.FromSeconds(5)), "empty inventory rechecks for devices without waiting a minute");
        Check(StorageMonitorService.NeedsMetadataRefresh(true, true, TimeSpan.FromSeconds(5)), "cached inventory with metadata error retries promptly");
        Check(!StorageMonitorService.NeedsMetadataRefresh(true, false, TimeSpan.FromSeconds(59)) && StorageMonitorService.NeedsMetadataRefresh(true, false, TimeSpan.FromSeconds(60)), "successful metadata remains cached for a minute");
        var temperatures = new[]
        {
            new SensorReading("Identical SSD", "Composite Temperature", "Temperature", 40, "°C") { HardwareId = "/nvme/0" },
            new SensorReading("Identical SSD", "Composite Temperature", "Temperature", 62, "°C") { HardwareId = "/nvme/1" },
            new SensorReading("Identical SSD", "Critical Temperature", "Temperature", 95, "°C") { HardwareId = "/nvme/0" }
        };
        Near(StorageMonitorService.TemperatureFromSensors(0, temperatures), 40, "same-model disks retain distinct physical-index temperatures; critical threshold is not live temperature");
        Near(StorageMonitorService.TemperatureFromSensors(1, temperatures), 62, "second physical disk temperature");
        Check(StorageMonitorService.TemperatureFromSensors(2, temperatures) is null, "absent physical index is not assigned by model name");
        Check(StorageMonitorService.TemperatureFromSensors(0, new[] { temperatures[0] with { HardwareId = null } }) is null, "sensor without hardware identity is not guessed");
        Check(StorageMonitorService.TemperatureFromSensors(0, new[] { temperatures[0] with { Value = 0 } }) is null, "uninitialized storage temperature is unavailable");
        Check(StorageMonitorService.TemperatureFromSensors(0, new[] { temperatures[0], temperatures[0] }) is null, "duplicate physical sensor identities fail closed");
        using var disposed = new StorageMonitorService();
        disposed.Dispose();
        bool rejected = false;
        try { disposed.Sample(); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "reject sampling after shutdown before any WMI query");
        Console.WriteLine("PASS physical storage counter formulas, missing/reset data, disk-volume identity, ambiguous-device rejection and shutdown");
        return Task.CompletedTask;
    }

    public static async Task RunLiveAsync()
    {
        using var monitor = new StorageMonitorService();
        await Task.Run(monitor.Initialize);
        var first = await Task.Run(() => monitor.Sample());
        await Task.Delay(600);
        var second = await Task.Run(() => monitor.Sample());
        Check(second.Timestamp >= first.Timestamp, "live timestamp must advance");
        Check(second.Disks.Select(x => x.DiskNumber).Distinct().Count() == second.Disks.Count, "live physical indices unique");
        foreach (PhysicalDiskReading disk in second.Disks)
        {
            Check(disk.DiskNumber >= 0 && !string.IsNullOrWhiteSpace(disk.Model), "live disk identity");
            Check(disk.ActivePercent is null or >= 0 and <= 100, "live active range");
            foreach (double? value in new[] { disk.ReadBytesPerSecond, disk.WriteBytesPerSecond, disk.QueueLength, disk.AverageResponseMs, disk.TemperatureCelsius })
                Check(value is null || double.IsFinite(value.Value) && value >= 0, "finite live optional metric");
            Console.WriteLine($"LIVE disk{disk.DiskNumber}: {disk.Model}, {disk.BusType}/{disk.MediaType}, {disk.Volumes}, active={disk.ActivePercent?.ToString("0.0") ?? "unavailable"}%, read={disk.ReadBytesPerSecond?.ToString("0") ?? "unavailable"}B/s, write={disk.WriteBytesPerSecond?.ToString("0") ?? "unavailable"}B/s, temp={disk.TemperatureCelsius?.ToString("0") ?? "unavailable"}C, {disk.HealthStatus}");
        }
        Console.WriteLine("INFO " + (second.StatusMessage ?? "storage providers available"));
    }

    private static void Near(double? value, double expected, string label) => Check(value.HasValue && Math.Abs(value.Value - expected) < 0.00001, label);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Storage smoke: " + label);
    }
}

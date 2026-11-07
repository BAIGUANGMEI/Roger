using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Roger.Models;

namespace Roger.Services;

/// <summary>
/// Read-only, vendor-independent Windows disk telemetry. Call on a serialized worker thread.
/// Metadata/reliability refresh once a minute; each live sample uses one raw performance query.
/// Sources: Microsoft Win32_PerfRawData_PerfDisk_PhysicalDisk, MSFT_Disk,
/// MSFT_DiskToStorageReliabilityCounter, MSFT_StorageReliabilityCounter.
/// </summary>
public sealed class StorageMonitorService : IDisposable
{
    private const string Cim = @"root\CIMV2";
    private const string Storage = @"root\Microsoft\Windows\Storage";
    private readonly object _gate = new();
    private readonly Dictionary<int, RawSample> _previous = new();
    private List<DiskMetadata> _disks = new();
    private long _lastMetadataRead;
    private string? _metadataWarning;
    private bool _initialized, _disposed;

    public void Initialize()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            _initialized = true;
            RefreshMetadata();
        }
    }

    public StorageSnapshot Sample(IReadOnlyList<SensorReading>? sensors = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Initialize();
            if (NeedsMetadataRefresh(_disks.Count > 0, _metadataWarning is not null, Stopwatch.GetElapsedTime(_lastMetadataRead))) RefreshMetadata();
            // Match LHM storage hardware by its Windows physical-drive number, never by a
            // model name (two identical drives may have very different temperatures).
            var current = new Dictionary<int, RawSample>();
            string? performanceWarning = null;
            try
            {
                Query(Cim, "SELECT Name, PercentIdleTime, Timestamp_Sys100NS, Timestamp_PerfTime, Frequency_PerfTime, DiskReadBytesPersec, DiskWriteBytesPersec, CurrentDiskQueueLength, AvgDisksecPerTransfer, AvgDisksecPerTransfer_Base FROM Win32_PerfRawData_PerfDisk_PhysicalDisk", item =>
                {
                    int? number = ParseCounterDiskNumber(Text(item, "Name"));
                    if (number is null) return;
                    current[number.Value] = new RawSample(
                        U64(item, "Timestamp_PerfTime"), U64(item, "Frequency_PerfTime"), U64(item, "Timestamp_Sys100NS"),
                        U64(item, "PercentIdleTime"), U64(item, "DiskReadBytesPersec"), U64(item, "DiskWriteBytesPersec"),
                        Number(item, "CurrentDiskQueueLength"), U64(item, "AvgDisksecPerTransfer"), U64(item, "AvgDisksecPerTransfer_Base"));
                });
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                performanceWarning = "磁盘实时计数器不可用（权限、设备或 Windows 提供程序受限）";
                Trace.TraceWarning("Disk performance query failed ({0}, HRESULT=0x{1:X8}, WMI={2}): {3}",
                    ex.GetType().Name, ex.HResult,
                    ex is ManagementException management ? management.ErrorCode.ToString() : "n/a", ex.Message);
            }

            var result = new List<PhysicalDiskReading>(_disks.Count);
            foreach (DiskMetadata disk in _disks)
            {
                PerfReading perf = PerfReading.Empty;
                string? sampleWarning = null;
                if (current.TryGetValue(disk.Number, out RawSample? raw))
                {
                    _previous.TryGetValue(disk.Number, out RawSample? previous);
                    perf = Calculate(previous, raw);
                    if (previous is null) sampleWarning = "正在建立读写采样基线";
                    _previous[disk.Number] = raw;
                }
                else
                {
                    _previous.Remove(disk.Number);
                    sampleWarning = performanceWarning ?? "此磁盘没有提供实时性能计数器";
                }
                double? sensorTemperature = TemperatureFromSensors(disk.Number, sensors);
                double? temperature = sensorTemperature ?? disk.Temperature;
                string? reliabilityWarning = disk.Warning;
                if (sensorTemperature.HasValue && reliabilityWarning is not null)
                    reliabilityWarning = "温度来自 LibreHardwareMonitor；Windows 寿命和可靠性计数器未提供";
                result.Add(new PhysicalDiskReading(disk.Number, disk.DeviceId, disk.Model, disk.Manufacturer,
                    disk.BusType, disk.MediaType, disk.SizeBytes, disk.Volumes,
                    perf.ActivePercent, perf.ReadBytesPerSecond, perf.WriteBytesPerSecond, perf.QueueLength, perf.AverageResponseMs,
                    temperature, disk.Wear, disk.PowerOnHours, disk.ReadErrors, disk.WriteErrors,
                    disk.Health, disk.Operational, "Windows 物理磁盘性能计数器 / Windows Storage / WMI 设备与卷映射"
                        + (sensorTemperature.HasValue ? " / LibreHardwareMonitor 温度（物理编号匹配）" : ""),
                    Join(reliabilityWarning, sampleWarning)));
            }
            foreach (int stale in _previous.Keys.Where(number => !current.ContainsKey(number)).ToArray()) _previous.Remove(stale);
            return new StorageSnapshot(DateTimeOffset.Now, result.AsReadOnly(), Join(_metadataWarning, performanceWarning));
        }
    }

    private void RefreshMetadata()
    {
        var disks = new Dictionary<int, DiskMetadata>();
        var warnings = new List<string>();
        try
        {
            Query(Cim, "SELECT Index, DeviceID, Model, Manufacturer, Size, InterfaceType, Status FROM Win32_DiskDrive", item =>
            {
                ulong? index = U64(item, "Index");
                if (index is null || index > int.MaxValue) return;
                int number = (int)index.Value;
                disks[number] = new DiskMetadata(number, Text(item, "DeviceID"), Text(item, "Model"), Text(item, "Manufacturer"),
                    Text(item, "InterfaceType"), Size(item, "Size"), "WMI 设备状态：" + Text(item, "Status"));
            });
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            _metadataWarning = "无法更新物理磁盘清单，保留之前的设备信息";
            _lastMetadataRead = Stopwatch.GetTimestamp();
            Trace.TraceWarning("Disk inventory: {0}", ex.Message);
            return;
        }

        try { AddVolumes(disks); }
        catch (Exception ex) when (Recoverable(ex)) { warnings.Add("部分卷映射不可用"); Trace.TraceWarning("Disk volume mapping: {0}", ex.Message); }

        var physical = new List<PhysicalIdentity>();
        try
        {
            Query(Storage, "SELECT UniqueId, SerialNumber, MediaType FROM MSFT_PhysicalDisk", item =>
                physical.Add(new PhysicalIdentity(TextOrNull(item, "UniqueId"), TextOrNull(item, "SerialNumber"), Media(U64(item, "MediaType")))));
        }
        catch (Exception ex) when (Recoverable(ex)) { warnings.Add("磁盘介质元数据不可用"); Trace.TraceWarning("Physical storage metadata: {0}", ex.Message); }

        try
        {
            Query(Storage, "SELECT Number, ObjectId, UniqueId, SerialNumber, FriendlyName, Manufacturer, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_Disk", item =>
            {
                ulong? number = U64(item, "Number");
                if (number is null || number > int.MaxValue || !disks.TryGetValue((int)number.Value, out var disk)) return;
                disk.BusType = Bus(U64(item, "BusType"));
                disk.Health = Health(U64(item, "HealthStatus"));
                disk.Operational = Operational(item["OperationalStatus"]);
                string? manufacturer = TextOrNull(item, "Manufacturer");
                if (manufacturer is not null) disk.Manufacturer = manufacturer;
                var candidates = MatchPhysical(physical, TextOrNull(item, "UniqueId"), TextOrNull(item, "SerialNumber"));
                if (candidates is not null) disk.MediaType = candidates.MediaType;
                // The association explicitly binds this MSFT_Disk Number to its reliability
                // counter. PhysicalDisk.DeviceId is NOT assumed equal to an OS disk number.
                if (item is ManagementObject managed) disk.StoragePath = managed.Path.RelativePath;
            });
        }
        catch (Exception ex) when (Recoverable(ex)) { warnings.Add("Windows Storage 元数据不可用"); Trace.TraceWarning("Storage metadata: {0}", ex.Message); }

        foreach (DiskMetadata disk in disks.Values)
        {
            if (disk.StoragePath is null) { disk.Warning = "温度、寿命与可靠性计数器不可用"; continue; }
            bool found = false;
            try
            {
                Query(Storage, "ASSOCIATORS OF {" + disk.StoragePath + "} WHERE AssocClass=MSFT_DiskToStorageReliabilityCounter", item =>
                {
                    if (found) return;
                    found = true;
                    double? temperature = Number(item, "Temperature");
                    disk.Temperature = temperature is > 0 and <= 150 ? temperature : null;
                    ulong? wear = U64(item, "Wear");
                    disk.Wear = wear is <= 100 ? (uint?)wear.Value : null;
                    disk.PowerOnHours = U64(item, "PowerOnHours");
                    disk.ReadErrors = U64(item, "ReadErrorsUncorrected");
                    disk.WriteErrors = U64(item, "WriteErrorsUncorrected");
                });
            }
            catch (Exception ex) when (Recoverable(ex)) { Trace.TraceWarning("Disk reliability {0}: {1}", disk.Number, ex.Message); }
            if (!found) disk.Warning = "温度、寿命与可靠性计数器不可用（可能需要管理员权限或设备未提供）";
        }
        _disks = disks.Values.OrderBy(disk => disk.Number).ToList();
        _metadataWarning = warnings.Count == 0 ? null : string.Join("；", warnings);
        // Count from query completion so a slow/missing provider cannot create a tight retry loop.
        _lastMetadataRead = Stopwatch.GetTimestamp();
    }

    internal static bool NeedsMetadataRefresh(bool hasDisks, bool hasWarning, TimeSpan elapsed) =>
        elapsed >= TimeSpan.FromSeconds(!hasDisks || hasWarning ? 5 : 60);

    private static void AddVolumes(Dictionary<int, DiskMetadata> disks)
    {
        var partitions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Query(Cim, "SELECT DiskIndex, DeviceID FROM Win32_DiskPartition", item =>
        {
            ulong? index = U64(item, "DiskIndex");
            if (index is <= int.MaxValue && TextOrNull(item, "DeviceID") is string id) partitions[id] = (int)index.Value;
        });
        var volumes = new Dictionary<int, SortedSet<string>>();
        Query(Cim, "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition", item =>
        {
            string? partition = ReferenceKey(TextOrNull(item, "Antecedent"));
            string? volume = ReferenceKey(TextOrNull(item, "Dependent"));
            if (partition is null || volume is null || !partitions.TryGetValue(partition, out int number)) return;
            if (!volumes.TryGetValue(number, out var list)) volumes[number] = list = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            list.Add(volume);
        });
        foreach (var disk in disks.Values) disk.Volumes = volumes.TryGetValue(disk.Number, out var list) ? string.Join("  ", list) : "无盘符 / 未提供映射";
    }

    internal static PerfReading Calculate(RawSample? previous, RawSample current)
    {
        double? queue = current.QueueLength is >= 0 && double.IsFinite(current.QueueLength.Value) ? current.QueueLength : null;
        if (previous is null || previous.Frequency is not > 0 || current.Frequency != previous.Frequency
            || Delta(previous.Timestamp, current.Timestamp) is not double ticks || ticks <= 0)
            return PerfReading.Empty with { QueueLength = queue };
        double seconds = ticks / current.Frequency.Value;
        double? active = null, response = null;
        // PercentIdleTime is PERF_100NSEC_TIMER: delta idle / delta Timestamp_Sys100NS.
        // Use 100 - idle, not PercentDiskTime (concurrent I/O can make that exceed 100%).
        if (Delta(previous.SystemTime, current.SystemTime) is double wall && wall > 0
            && Delta(previous.IdleTime, current.IdleTime) is double idle)
            active = Math.Clamp(100d * (1d - idle / wall), 0, 100);
        // AvgDisksecPerTransfer is PERF_AVERAGE_TIMER: delta timer / frequency / delta operations.
        if (Delta(previous.ResponseTicks, current.ResponseTicks) is double responseTicks
            && Delta(previous.ResponseBase, current.ResponseBase) is double operations && operations > 0)
            response = responseTicks / current.Frequency.Value / operations * 1000d;
        return new PerfReading(active, Rate(previous.ReadBytes, current.ReadBytes, seconds),
            Rate(previous.WriteBytes, current.WriteBytes, seconds), queue, Finite(response));
    }

    internal static int? ParseCounterDiskNumber(string name)
    {
        Match match = Regex.Match(name, @"\A(?<number>\d+)(?:\s|\z)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
    }

    internal static double? TemperatureFromSensors(int diskNumber, IReadOnlyList<SensorReading>? sensors)
    {
        if (sensors is null) return null;
        foreach (string preferredName in new[] { "Composite Temperature", "Temperature" })
        {
            var readings = sensors.Where(sensor => sensor.Kind == "Temperature"
                && sensor.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase)
                && sensor.Value is > 0 and <= 150 && double.IsFinite(sensor.Value)
                && SensorDiskNumber(sensor.HardwareId) == diskNumber).ToArray();
            // An unexpected duplicate physical identifier is not a reason to guess a source.
            if (readings.Length == 1) return readings[0].Value;
            if (readings.Length > 1) return null;
        }
        return null;
    }

    private static int? SensorDiskNumber(string? hardwareId)
    {
        if (hardwareId is null) return null;
        Match match = Regex.Match(hardwareId, @"\A/(?:nvme|ssd|hdd)/(?<number>\d+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
    }

    internal static PhysicalIdentity? MatchPhysical(IReadOnlyList<PhysicalIdentity> items, string? uniqueId, string? serial)
    {
        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            var matches = items.Where(x => string.Equals(x.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) return null;
        }
        if (!string.IsNullOrWhiteSpace(serial))
        {
            var matches = items.Where(x => string.Equals(x.Serial, serial, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0];
        }
        return null;
    }

    internal static string? ReferenceKey(string? reference)
    {
        if (reference is null) return null;
        Match match = Regex.Match(reference, "DeviceID=\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : null;
    }

    internal static string Bus(ulong? value) => value switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "文件虚拟盘", 16 => "Storage Spaces", 17 => "NVMe", _ => "未知"
    };
    internal static string Media(ulong? value) => value switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "未知" };
    internal static string Health(ulong? value) => value switch { 0 => "正常（Windows 报告）", 1 => "警告（Windows 报告）", 2 => "不健康（Windows 报告）", _ => "未提供" };
    internal static string Operational(object? value)
    {
        // MSFT_Disk documents a UInt16; some WMI providers expose UInt16[].
        IEnumerable<ushort> codes = value switch
        {
            ushort scalar => new[] { scalar },
            ushort[] values => values,
            _ => Array.Empty<ushort>()
        };
        return string.Join(" / ", codes.Select(code => code switch
        {
            0 => "未知", 1 => "其他（厂商定义）", 2 => "OK", 3 => "降级", 4 => "需要关注",
            5 => "预测故障", 6 => "错误", 7 => "不可恢复错误", 8 => "启动中", 9 => "停止中",
            10 => "已停止", 11 => "维护中", 12 => "未建立通信", 13 => "失去通信", 14 => "已中止",
            15 => "未活动", 16 => "依赖设备或连接错误", 17 => "已完成",
            0xD010 => "在线", 0xD011 => "未就绪", 0xD012 => "无介质", 0xD013 => "离线", 0xD014 => "故障",
            _ => "状态代码 " + code.ToString(CultureInfo.InvariantCulture)
        }).DefaultIfEmpty("未提供"));
    }
    private static double? Delta(ulong? before, ulong? after) => before is ulong a && after is ulong b && b >= a ? b - a : null;
    private static double? Rate(ulong? before, ulong? after, double seconds) => Delta(before, after) is double delta && seconds > 0 ? Finite(delta / seconds) : null;
    private static double? Finite(double? value) => value is double number && double.IsFinite(number) && number >= 0 ? number : null;
    private static string? Join(string? first, string? second) => string.IsNullOrWhiteSpace(first) ? second : string.IsNullOrWhiteSpace(second) ? first : first + "；" + second;
    private static long Size(ManagementBaseObject item, string name) => U64(item, name) is ulong value && value <= long.MaxValue ? (long)value : 0;
    private static ulong? U64(ManagementBaseObject item, string name)
    {
        object? value = item.Properties[name]?.Value;
        return value is not null && ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong number) ? number : null;
    }
    private static double? Number(ManagementBaseObject item, string name)
    {
        object? value = item.Properties[name]?.Value;
        return value is not null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
    }
    private static string? TextOrNull(ManagementBaseObject item, string name) => Convert.ToString(item.Properties[name]?.Value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : null;
    private static string Text(ManagementBaseObject item, string name) => TextOrNull(item, name) ?? "未知";
    private static void Query(string scope, string query, Action<ManagementBaseObject> read)
    {
        var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(2) };
        var connection = new ConnectionOptions { Timeout = TimeSpan.FromSeconds(2) };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope, connection), new ObjectQuery(query), options);
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results) using (item) read(item);
    }
    private static bool Recoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _disks.Clear(); _previous.Clear(); }
    }

    internal sealed record RawSample(ulong? Timestamp, ulong? Frequency, ulong? SystemTime, ulong? IdleTime,
        ulong? ReadBytes, ulong? WriteBytes, double? QueueLength, ulong? ResponseTicks, ulong? ResponseBase);
    internal sealed record PerfReading(double? ActivePercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond, double? QueueLength, double? AverageResponseMs)
    {
        internal static PerfReading Empty { get; } = new(null, null, null, null, null);
    }
    internal sealed record PhysicalIdentity(string? UniqueId, string? Serial, string MediaType);
    private sealed class DiskMetadata(int number, string deviceId, string model, string manufacturer, string busType, long sizeBytes, string operational)
    {
        internal int Number = number;
        internal string DeviceId = deviceId, Model = model, Manufacturer = manufacturer, BusType = busType,
            MediaType = "未知", Volumes = "未提供映射", Health = "未提供", Operational = operational;
        internal long SizeBytes = sizeBytes;
        internal string? StoragePath, Warning;
        internal double? Temperature;
        internal uint? Wear;
        internal ulong? PowerOnHours, ReadErrors, WriteErrors;
    }
}

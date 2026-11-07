using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Roger.Models;

namespace Roger.Services;

/// <summary>
/// Reads sensors without inventing values for unsupported hardware. Call on a worker thread:
/// initialization, WMI inventory, and device drivers can take time. Calls are serialized with Dispose.
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, NetworkBaseline> _networkBaselines = new(StringComparer.Ordinal);
    private HashSet<string> _physicalNetworkIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _physicalNetworkIdsAuthoritative;
    private long _lastNetworkDiscovery;
    private Computer? _computer;
    private bool _initialized;
    private bool _disposed;
    private bool _cpuBaselineReady;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private string? _initializationWarning;
    private IReadOnlyList<HardwareInfo>? _inventory;
    private IReadOnlyList<DriveReading> _drives = Array.Empty<DriveReading>();
    private long _lastDriveRead;

    public void Initialize()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;

            _initialized = true;
            ReadNativeCpuLoad(); // Establish a baseline; the first result needs two samples.
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                    IsNetworkEnabled = false,
                    IsControllerEnabled = false
                };
                _computer.Open();
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                // Windows CPU/RAM/network and readable disks remain usable if a driver fails.
                _initializationWarning = "部分硬件传感器初始化失败，已保留 Windows 基础监控";
                Trace.TraceWarning("Hardware initialization: {0}", ex.Message);
            }
            RefreshPhysicalNetworkIds();
        }
    }

    public MonitorSnapshot Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Initialize();

            var warnings = new List<string>();
            if (_initializationWarning is not null)
                warnings.Add(_initializationWarning);

            var sensors = new List<SensorReading>();
            var hardwareSensors = new List<DeviceSensors>();
            if (_computer is not null)
            {
                try
                {
                    foreach (IHardware hardware in _computer.Hardware)
                        ReadHardware(hardware, hardware.HardwareType, hardware.Name, sensors, hardwareSensors, warnings);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    warnings.Add("部分硬件传感器暂时不可读取");
                    Trace.TraceWarning("Sensor enumeration: {0}", ex.Message);
                }
            }

            // GetSystemTimes supplies one aggregate across logical processors. Do not substitute
            // a single core's utilization when the package total is unavailable.
            double? nativeCpuLoad = ReadNativeCpuLoad();
            var cpuSensors = hardwareSensors.Where(x => x.Type == HardwareType.Cpu).SelectMany(x => x.Readings).ToArray();
            double? cpuLoad = Find(cpuSensors, "Load", "CPU Total", "Total") ?? nativeCpuLoad;
            double? cpuTemperature = Find(cpuSensors, "Temperature", "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)");
            cpuTemperature ??= MaxOrNull(cpuSensors.Where(x => x.Kind == "Temperature").Select(x => x.Value));

            // Keep one GPU's load, temperature, and VRAM together instead of mixing devices.
            DeviceSensors? gpu = hardwareSensors
                .Where(x => IsGpu(x.Type))
                .OrderBy(GpuPriority)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            SensorReading[] gpuSensors = gpu?.Readings.ToArray() ?? Array.Empty<SensorReading>();
            double? gpuLoad = Find(gpuSensors, "Load", "GPU Core", "D3D 3D", "GPU D3D 3D");
            gpuLoad ??= MaxOrNull(gpuSensors.Where(x => x.Kind == "Load" && x.Name.Contains("3D", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value));
            double? gpuTemperature = Find(gpuSensors, "Temperature", "GPU Core", "GPU");
            double? gpuMemoryUsed = FindMemoryMb(gpuSensors, "GPU Memory Used", "D3D Dedicated Memory Used");
            double? gpuMemoryTotal = FindMemoryMb(gpuSensors, "GPU Memory Total", "D3D Dedicated Memory Total");
            if (gpuMemoryTotal is <= 0)
                gpuMemoryTotal = null;

            if (cpuTemperature is null)
                warnings.Add("CPU 温度不可用（硬件支持、驱动或权限受限）");
            if (gpu is null)
                warnings.Add("GPU 传感器不可用");
            else if (gpuTemperature is null)
                warnings.Add("GPU 温度不可用");

            (double memoryUsed, double memoryTotal) = ReadMemory(warnings);
            (double download, double upload, string network) = ReadNetwork(warnings);
            ReadDrives(warnings);

            return new MonitorSnapshot(
                DateTimeOffset.Now, ClampPercent(cpuLoad), cpuTemperature,
                ClampPercent(gpuLoad), gpuTemperature, gpuMemoryUsed, gpuMemoryTotal,
                memoryUsed, memoryTotal, download, upload, network, _drives,
                sensors.AsReadOnly(), warnings.Count == 0 ? null : string.Join("；", warnings.Distinct()));
        }
    }

    public IReadOnlyList<HardwareInfo> GetHardwareInfo()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Initialize();
            if (_inventory is not null)
                return _inventory;

            var entries = new List<HardwareInfo>();
            QueryInventory(entries, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", "CPU", item =>
                new HardwareInfo("CPU", Text(item, "Name"), $"{Text(item, "NumberOfCores")} 核心 / {Text(item, "NumberOfLogicalProcessors")} 线程 · 最高 {Text(item, "MaxClockSpeed")} MHz"));
            QueryInventory(entries, "SELECT Name, DriverVersion, VideoProcessor FROM Win32_VideoController", "GPU", item =>
                // Win32_VideoController.AdapterRAM is a 32-bit property and overflows on >4 GB cards.
                // VRAM capacity is intentionally sourced from live hardware sensors instead.
                new HardwareInfo("GPU", Text(item, "Name"), $"{Text(item, "VideoProcessor")} · 驱动 {Text(item, "DriverVersion")}"));
            QueryInventory(entries, "SELECT Manufacturer, PartNumber, Capacity, Speed, DeviceLocator FROM Win32_PhysicalMemory", "内存", item =>
                new HardwareInfo("内存", $"{Text(item, "Manufacturer")} {Text(item, "PartNumber")}".Trim(), $"{Text(item, "DeviceLocator")} · {GiB(item, "Capacity")} GiB · {Text(item, "Speed")} MT/s"));
            QueryInventory(entries, "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", "主板", item =>
                new HardwareInfo("主板", $"{Text(item, "Manufacturer")} {Text(item, "Product")}".Trim(), $"版本 {Text(item, "Version")}"));
            QueryInventory(entries, "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", "BIOS", item =>
                new HardwareInfo("BIOS", Text(item, "Manufacturer"), $"{Text(item, "SMBIOSBIOSVersion")} · {WmiDate(item, "ReleaseDate")}"));
            QueryInventory(entries, "SELECT Model, Size, InterfaceType, MediaType, Status FROM Win32_DiskDrive", "磁盘", item =>
                new HardwareInfo("磁盘", Text(item, "Model"), $"{GiB(item, "Size")} GiB · {Text(item, "InterfaceType")} · {Text(item, "MediaType")} · 系统状态 {Text(item, "Status")}"));
            QueryInventory(entries, "SELECT Caption, Version, OSArchitecture, BuildNumber FROM Win32_OperatingSystem", "系统", item =>
                new HardwareInfo("系统", Text(item, "Caption"), $"{Text(item, "OSArchitecture")} · 版本 {Text(item, "Version")} · Build {Text(item, "BuildNumber")}"));
            QueryInventory(entries, "SELECT Name, DesignCapacity, FullChargeCapacity, BatteryStatus, EstimatedChargeRemaining FROM Win32_Battery", "电池", item =>
                new HardwareInfo("电池", Text(item, "Name"), $"剩余电量 {Text(item, "EstimatedChargeRemaining")}% · 系统状态代码 {Text(item, "BatteryStatus")}"));

            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Where(IsPhysicalNetwork).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(new HardwareInfo("网络", adapter.Name, $"{adapter.Description} · {adapter.OperationalStatus} · 链路 {adapter.Speed / 1_000_000d:0.##} Mbps"));
                }
            }
            catch (NetworkInformationException)
            {
                entries.Add(new HardwareInfo("网络", "网卡信息不可用", "Windows 未能返回网卡信息"));
            }

            // LHM may know a device even when the corresponding WMI provider is unavailable.
            if (_computer is not null)
            {
                foreach (IHardware hardware in _computer.Hardware)
                {
                    string category = Category(hardware.HardwareType);
                    if (!entries.Any(x => x.Category == category && x.Name.Equals(hardware.Name, StringComparison.OrdinalIgnoreCase)))
                        entries.Add(new HardwareInfo(category, hardware.Name, "传感器来源：LibreHardwareMonitor"));
                }
            }

            _inventory = entries.AsReadOnly();
            return _inventory;
        }
    }

    private static void ReadHardware(IHardware hardware, HardwareType rootType, string rootName,
        List<SensorReading> allReadings, List<DeviceSensors> devices, List<string> warnings)
    {
        var readings = new List<SensorReading>();
        try
        {
            hardware.Update();
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.Value is not float raw || !float.IsFinite(raw))
                    continue;
                string kind = sensor.SensorType.ToString();
                // Negative temperatures can be valid. Other metrics must not expose -1 sentinels.
                if (raw < 0 && kind != "Temperature")
                    continue;
                // Some unsupported CPU/GPU sensors stay activated with an uninitialized zero.
                // Exclude that value from both the summary and the inspectable sensor list.
                if (kind == "Temperature" && raw == 0 && (rootType == HardwareType.Cpu || IsGpu(rootType)))
                    continue;
                var reading = new SensorReading(hardware.Name, sensor.Name, kind, raw, Unit(kind));
                readings.Add(reading);
                allReadings.Add(reading);
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            warnings.Add($"{hardware.Name} 传感器读取失败");
            Trace.TraceWarning("Sensor update {0}: {1}", hardware.Name, ex.Message);
        }

        DeviceSensors? device = devices.FirstOrDefault(x => x.Type == rootType && x.Name == rootName);
        if (device is null)
            devices.Add(new DeviceSensors(rootType, rootName, readings));
        else
            device.Readings.AddRange(readings);

        try
        {
            foreach (IHardware child in hardware.SubHardware)
                ReadHardware(child, rootType, rootName, allReadings, devices, warnings);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            warnings.Add($"{hardware.Name} 子传感器不可用");
        }
    }

    private double? ReadNativeCpuLoad()
    {
        if (!GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime))
            return null;
        ulong idle = idleTime.Value;
        ulong kernel = kernelTime.Value;
        ulong user = userTime.Value;
        double? load = null;
        if (_cpuBaselineReady && idle >= _lastIdle && kernel >= _lastKernel && user >= _lastUser)
        {
            // Kernel time includes idle time per GetSystemTimes documentation.
            double total = (double)(kernel - _lastKernel) + (user - _lastUser);
            double inactive = idle - _lastIdle;
            if (total > 0)
                load = Math.Clamp((total - inactive) / total * 100d, 0, 100);
        }
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        _cpuBaselineReady = true;
        return load;
    }

    private static (double Used, double Total) ReadMemory(List<string> warnings)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            warnings.Add("物理内存统计不可用");
            return (0, 0);
        }
        ulong available = Math.Min(status.AvailablePhysical, status.TotalPhysical);
        return ((status.TotalPhysical - available) / BytesPerGiB, status.TotalPhysical / BytesPerGiB);
    }

    private (double Download, double Upload, string Name) ReadNetwork(List<string> warnings)
    {
        RefreshPhysicalNetworkIds();
        if (!_physicalNetworkIdsAuthoritative)
            warnings.Add("物理网卡识别使用兼容模式");
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        double download = 0;
        double upload = 0;
        bool anyMeasured = false;
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsPhysicalNetwork(adapter) || adapter.OperationalStatus != OperationalStatus.Up)
                    continue;
                try
                {
                    IPInterfaceStatistics stats = adapter.GetIPStatistics();
                    long timestamp = Stopwatch.GetTimestamp();
                    currentIds.Add(adapter.Id);
                    names.Add(adapter.Name);
                    if (_networkBaselines.TryGetValue(adapter.Id, out NetworkBaseline previous))
                    {
                        double elapsed = (timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
                        if (elapsed > 0 && stats.BytesReceived >= previous.Received && stats.BytesSent >= previous.Sent)
                        {
                            download += (stats.BytesReceived - previous.Received) / elapsed;
                            upload += (stats.BytesSent - previous.Sent) / elapsed;
                            anyMeasured = true;
                        }
                    }
                    _networkBaselines[adapter.Id] = new(stats.BytesReceived, stats.BytesSent, timestamp);
                }
                catch (NetworkInformationException)
                {
                    warnings.Add($"{adapter.Name} 流量统计暂不可用");
                }
            }
        }
        catch (NetworkInformationException)
        {
            warnings.Add("网卡流量读取失败");
        }

        // A disconnected/reconnected adapter starts a fresh interval instead of showing a spike.
        foreach (string id in _networkBaselines.Keys.Where(x => !currentIds.Contains(x)).ToArray())
            _networkBaselines.Remove(id);
        string name = names.Count switch
        {
            0 => "无活动物理网卡",
            1 => names[0],
            _ => string.Join(" + ", names) + "（合计）"
        };
        if (names.Count > 0 && !anyMeasured)
            name += " · 等待采样";
        return (download, upload, name);
    }

    private void ReadDrives(List<string> warnings)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastDriveRead != 0 && (now - _lastDriveRead) / (double)Stopwatch.Frequency < 10)
            return;
        _lastDriveRead = now;
        var drives = new List<DriveReading>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    // Network paths can block indefinitely when disconnected. Their traffic is
                    // still counted by the NIC reader; this section is local disk capacity only.
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                        continue;
                    drives.Add(new DriveReading(drive.Name, drive.VolumeLabel, drive.TotalSize, drive.TotalFreeSpace, drive.DriveFormat));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    warnings.Add($"{drive.Name} 容量不可读取");
                }
            }
            _drives = drives.AsReadOnly();
        }
        catch (IOException)
        {
            warnings.Add("磁盘容量读取失败");
            _drives = Array.Empty<DriveReading>();
        }
    }

    private void RefreshPhysicalNetworkIds()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastNetworkDiscovery != 0 && (now - _lastNetworkDiscovery) / (double)Stopwatch.Frequency < 60)
            return;
        _lastNetworkDiscovery = now;

        // The modern NDIS provider distinguishes the actual adapter from hidden WFP/QoS/native
        // Wi-Fi filter interfaces. Win32_NetworkAdapter.PhysicalAdapter is inaccurate on some
        // Wi-Fi 7 drivers (including devices with null GUIDs), so prefer MSFT_NetAdapter.
        if (TryReadPhysicalAdapterIds(modernProvider: true, out HashSet<string> modern))
        {
            _physicalNetworkIds = modern;
            _physicalNetworkIdsAuthoritative = true;
            return;
        }
        _physicalNetworkIdsAuthoritative = false;
        if (TryReadPhysicalAdapterIds(modernProvider: false, out HashSet<string> legacy))
            _physicalNetworkIds = legacy;
    }

    private static bool TryReadPhysicalAdapterIds(bool modernProvider, out HashSet<string> ids)
    {
        ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string scope = modernProvider ? "root\\StandardCimv2" : "root\\CIMV2";
            string query = modernProvider
                ? "SELECT InterfaceGuid FROM MSFT_NetAdapter WHERE HardwareInterface = TRUE AND Virtual = FALSE"
                : "SELECT GUID, PNPDeviceID, Name FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE";
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(3) };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    if (!modernProvider)
                    {
                        // VMware, TAP, and Wintun may claim PhysicalAdapter=true. Their software
                        // PNP paths are not physical buses and must not enter the legacy allowlist.
                        string pnp = Text(item, "PNPDeviceID");
                        if (!(pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                            || pnp.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                            || pnp.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase)
                            || pnp.StartsWith("PCMCIA\\", StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }
                    if (Guid.TryParse(Text(item, modernProvider ? "InterfaceGuid" : "GUID"), out Guid id))
                        ids.Add(id.ToString("D"));
                }
            }
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Trace.TraceWarning("Physical network discovery ({0}): {1}", modernProvider ? "NDIS" : "WMI", ex.Message);
            return false;
        }
    }

    private bool IsPhysicalNetwork(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT))
            return false;
        if (Guid.TryParse(adapter.Id, out Guid id) && _physicalNetworkIds.Contains(id.ToString("D")))
            return true;
        if (_physicalNetworkIdsAuthoritative)
            return false;

        // Last-resort compatibility path when WMI is unavailable. Hidden miniports and filter
        // interfaces can have Ethernet/Wireless80211 type and repeat the real NIC's counters.
        string description = adapter.Description + " " + adapter.Name;
        string[] virtualMarkers =
        {
            "virtual", "vethernet", "vswitch", "hyper-v", "vmware", "virtualbox", "tap-windows",
            "tun adapter", "tunnel", "vpn", "wi-fi direct", "miniport", "filter", "packet scheduler",
            "wfp", "native wifi", "kernel debug", "虚拟", "隧道", "过滤", "筛选"
        };
        return !virtualMarkers.Any(x => description.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static int GpuPriority(DeviceSensors gpu)
    {
        if (gpu.Type == HardwareType.GpuNvidia)
            return 0;
        if (gpu.Type == HardwareType.GpuAmd)
        {
            bool integrated = gpu.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
                || gpu.Name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(gpu.Name, @"Radeon\s+(?:\d{3,4}[MS]|Vega\s+\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return integrated ? 2 : 1;
        }
        return gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? 1 : 3;
    }

    private static double? Find(IEnumerable<SensorReading> readings, string kind, params string[] names)
    {
        SensorReading[] candidates = readings.Where(x => x.Kind == kind).ToArray();
        foreach (string name in names)
        {
            SensorReading? reading = candidates.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (reading is not null)
                return reading.Value;
        }
        return null;
    }

    private static double? FindMemoryMb(IEnumerable<SensorReading> readings, params string[] names)
        => Find(readings, "SmallData", names) ?? (Find(readings, "Data", names) is double gib ? gib * 1024 : null);

    private static double? MaxOrNull(IEnumerable<double> values)
    {
        double[] materialized = values.ToArray();
        return materialized.Length == 0 ? null : materialized.Max();
    }

    private static double? ClampPercent(double? value) => value is double actual && double.IsFinite(actual) ? Math.Clamp(actual, 0, 100) : null;
    private static bool IsGpu(HardwareType type) => type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
    private static string Category(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU", HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "内存", HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "主板",
        HardwareType.Storage => "磁盘", HardwareType.Network => "网络", HardwareType.Battery => "电池", _ => "其他"
    };

    private static string Unit(string kind) => kind switch
    {
        "Temperature" => "°C", "Load" or "Control" or "Level" => "%", "Clock" => "MHz", "Frequency" => "Hz",
        "Voltage" => "V", "Current" => "A", "Power" => "W", "Fan" => "RPM", "Flow" => "L/h",
        "Data" => "GiB", "SmallData" => "MiB", "Throughput" => "B/s", "TimeSpan" => "s",
        "Energy" => "mWh", "Noise" => "dBA", "Conductivity" => "µS/cm", _ => ""
    };

    private static void QueryInventory(List<HardwareInfo> entries, string query, string category, Func<ManagementBaseObject, HardwareInfo> convert)
    {
        try
        {
            // Semisynchronous enumeration allows the timeout to apply while waiting for WMI.
            // A synchronous Get() can ignore the enumeration timeout and stall a refresh worker.
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(6) };
            using var searcher = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"), new ObjectQuery(query), options);
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                    entries.Add(convert(item));
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            entries.Add(new HardwareInfo(category, "信息不可用", "Windows WMI 未能返回此类硬件信息"));
            Trace.TraceWarning("Inventory {0}: {1}", category, ex.Message);
        }
    }

    private static string Text(ManagementBaseObject item, string property)
    {
        object? value = item[property];
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : "未知";
    }

    private static string GiB(ManagementBaseObject item, string property)
        => double.TryParse(Text(item, property), NumberStyles.Any, CultureInfo.InvariantCulture, out double bytes)
            ? (bytes / BytesPerGiB).ToString("0.##", CultureInfo.InvariantCulture) : "未知";

    private static string WmiDate(ManagementBaseObject item, string property)
    {
        string value = Text(item, property);
        return value.Length >= 8 && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "日期未知";
    }

    private static bool IsRecoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _computer?.Close();
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                Trace.TraceWarning("Hardware shutdown: {0}", ex.Message);
            }
            _computer = null;
            _networkBaselines.Clear();
        }
        GC.SuppressFinalize(this);
    }

    private sealed record DeviceSensors(HardwareType Type, string Name, List<SensorReading> Readings);
    private readonly record struct NetworkBaseline(long Received, long Sent, long Timestamp);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

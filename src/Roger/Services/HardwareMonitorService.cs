using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;
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
    private HardwareAccessStatus? _hardwareAccess;
    private readonly List<InventoryDevice> _cpuInventory = new();
    private readonly List<InventoryDevice> _gpuInventory = new();
    private IReadOnlyList<MemoryModuleInfo> _memoryModules = Array.Empty<MemoryModuleInfo>();

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
            _hardwareAccess = HardwareAccessService.GetStatus();
        }
    }

    public MonitorSnapshot Sample(string? selectedGpuId = null)
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
                        ReadHardware(hardware, hardware.HardwareType, hardware.Identifier.ToString(), hardware.Name, GpuDeviceId(hardware), sensors, hardwareSensors, warnings);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    warnings.Add("部分硬件传感器暂时不可读取");
                    Trace.TraceWarning("Sensor enumeration: {0}", ex.Message);
                }
            }

            double? nativeCpuLoad = ReadNativeCpuLoad();
            var cpuReadings = BuildCpuReadings(hardwareSensors);
            var gpuReadings = BuildGpuReadings(hardwareSensors);
            GpuDeviceReading? gpu = HardwareSensorMapper.SelectGpu(gpuReadings, selectedGpuId);
            // Native total is the only correct fallback across multiple CPU packages.
            double? cpuLoad = cpuReadings.Count == 1 ? cpuReadings[0].Load ?? nativeCpuLoad : nativeCpuLoad;
            double? cpuTemperature = cpuReadings.Where(x => x.Temperature.HasValue)
                .Select(x => x.Temperature).DefaultIfEmpty(null).Max();
            double? gpuLoad = gpu?.Load;
            double? gpuTemperature = gpu?.Temperature;
            double? gpuMemoryUsed = gpu?.MemoryUsedMb;
            double? gpuMemoryTotal = gpu?.MemoryTotalMb;
            if (!string.IsNullOrWhiteSpace(selectedGpuId) && gpu?.Id != selectedGpuId)
                warnings.Add("选择的 GPU 当前不可用，已切换到自动选择");

            if (cpuTemperature is null)
                warnings.Add(HardwareAccessService.CpuTemperatureUnavailableMessage(_hardwareAccess ?? HardwareAccessService.GetStatus()));
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
                sensors.AsReadOnly(), warnings.Count == 0 ? null : string.Join("；", warnings.Distinct()))
            {
                CpuDevices = cpuReadings, GpuDevices = gpuReadings, MemoryModules = _memoryModules,
                SelectedGpuId = gpu?.Id
            };
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
            QueryInventory(entries, "SELECT DeviceID, Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", "CPU", item =>
            {
                string name = Text(item, "Name");
                string vendor = HardwareSensorMapper.Vendor(Text(item, "Manufacturer"));
                _cpuInventory.Add(new InventoryDevice(Text(item, "DeviceID"), name, vendor, null));
                return new HardwareInfo("CPU", name, $"{vendor} · {Text(item, "NumberOfCores")} 核心 / {Text(item, "NumberOfLogicalProcessors")} 线程 · 系统报告 {Text(item, "MaxClockSpeed")} MHz");
            });
            QueryInventory(entries, "SELECT DeviceID, PNPDeviceID, Name, AdapterCompatibility, DriverVersion, VideoProcessor FROM Win32_VideoController", "GPU", item =>
            {
                string name = Text(item, "Name");
                string vendor = HardwareSensorMapper.Vendor(Text(item, "AdapterCompatibility"));
                _gpuInventory.Add(new InventoryDevice(Text(item, "DeviceID"), name, vendor, OptionalText(item, "PNPDeviceID")));
                // AdapterRAM is 32-bit and overflows on large cards. Never use it as VRAM capacity.
                return new HardwareInfo("GPU", name, $"{vendor} · {Text(item, "VideoProcessor")} · 驱动 {Text(item, "DriverVersion")}");
            });
            var modules = new List<MemoryModuleInfo>();
            QueryInventory(entries, "SELECT Tag, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed, Speed, DeviceLocator FROM Win32_PhysicalMemory", "内存", item =>
            {
                var module = new MemoryModuleInfo(OptionalText(item, "Tag") ?? "memory/" + modules.Count,
                    HardwareSensorMapper.ModuleManufacturer(OptionalText(item, "Manufacturer")), OptionalText(item, "PartNumber") ?? "型号未报告",
                    Number(item, "Capacity") ?? 0, PositiveNumber(item, "ConfiguredClockSpeed"), PositiveNumber(item, "Speed"),
                    OptionalText(item, "DeviceLocator") ?? "插槽未报告", "Windows WMI / SMBIOS（固件报告，非实时频率）");
                modules.Add(module);
                string configured = module.ConfiguredSpeedMtPerSec is uint configuredSpeed ? configuredSpeed + " MT/s" : "未报告";
                string rated = module.RatedSpeedMtPerSec is uint reportedSpeed ? reportedSpeed + " MT/s" : "未报告";
                string capacity = module.CapacityBytes > 0 ? $"{module.CapacityGb:0.##} GiB" : "容量未报告";
                return new HardwareInfo("内存", module.Name, $"{module.Slot} · {capacity} · 当前配置 {configured} · 报告能力 {rated}");
            });
            _memoryModules = modules.AsReadOnly();
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
                    if ((hardware.HardwareType == HardwareType.Cpu && _cpuInventory.Count > 0)
                        || (hardware.HardwareType == HardwareType.Memory && _memoryModules.Count > 0)) continue;
                    if (!entries.Any(x => x.Category == category && HardwareSensorMapper.NormalizeModel(x.Name) == HardwareSensorMapper.NormalizeModel(hardware.Name)))
                        entries.Add(new HardwareInfo(category, hardware.Name, "传感器来源：LibreHardwareMonitor"));
                }
            }

            _inventory = entries.AsReadOnly();
            return _inventory;
        }
    }

    private static void ReadHardware(IHardware hardware, HardwareType rootType, string rootId, string rootName, string? pnpDeviceId,
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
                var reading = new SensorReading(hardware.Name, sensor.Name, kind, raw, Unit(kind))
                {
                    HardwareId = hardware.Identifier.ToString()
                };
                readings.Add(reading);
                allReadings.Add(reading);
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            warnings.Add($"{hardware.Name} 传感器读取失败");
            Trace.TraceWarning("Sensor update {0}: {1}", hardware.Name, ex.Message);
        }

        DeviceSensors? device = devices.FirstOrDefault(x => x.Id == rootId);
        if (device is null)
            devices.Add(new DeviceSensors(rootType, rootId, rootName, pnpDeviceId, readings));
        else
            device.Readings.AddRange(readings);

        try
        {
            foreach (IHardware child in hardware.SubHardware)
                ReadHardware(child, rootType, rootId, rootName, pnpDeviceId, allReadings, devices, warnings);
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

    private List<CpuDeviceReading> BuildCpuReadings(List<DeviceSensors> devices)
    {
        var cpu = devices.Where(x => x.Type == HardwareType.Cpu).ToArray();
        var readings = cpu.Select(x => HardwareSensorMapper.Cpu(x.Id, x.Name,
            HardwareSensorMapper.Vendor(x.Name + " " + x.Id, HardwareType.Cpu), x.Readings)).ToList();
        var remaining = UnrepresentedInventory(cpu, _cpuInventory, cpuMode: true);
        foreach (var item in remaining)
            readings.Add(new CpuDeviceReading("wmi/cpu/" + item.Id, item.Name, item.Vendor, null, null,
                "Windows WMI（设备配置）", "Windows 已识别该处理器；硬件驱动未提供此设备的传感器"));
        return readings;
    }

    private List<GpuDeviceReading> BuildGpuReadings(List<DeviceSensors> devices)
    {
        var gpu = devices.Where(x => IsGpu(x.Type)).ToArray();
        var readings = gpu.Select(x => HardwareSensorMapper.Gpu(x.Id, x.Name,
            HardwareSensorMapper.Vendor(x.Name, x.Type), HardwareSensorMapper.DiscreteHint(x.Type, x.Id, x.Name), x.Readings)).ToList();
        var remaining = UnrepresentedInventory(gpu, _gpuInventory, cpuMode: false);
        foreach (var item in remaining)
            readings.Add(new GpuDeviceReading("wmi/gpu/" + item.Id, item.Name, item.Vendor, null,
                null, null, null, null, null, null, "Windows WMI（设备配置）",
                "Windows 已识别该显卡；硬件驱动未提供此设备的遥测，无法显示负载、温度与显存"));
        return readings;
    }

    private static IReadOnlyList<InventoryDevice> UnrepresentedInventory(DeviceSensors[] detected,
        List<InventoryDevice> inventory, bool cpuMode)
    {
        var remaining = inventory.ToList();
        // Prefer exact PnP identity. Matching a unique model only suppresses the WMI duplicate;
        // readings are never moved or merged between physical devices, including identical models.
        var unmatched = new List<DeviceSensors>();
        foreach (var device in detected)
        {
            string pnp = HardwareSensorMapper.NormalizePnp(device.PnpDeviceId);
            var matches = pnp.Length > 0 ? remaining.Where(x => HardwareSensorMapper.NormalizePnp(x.PnpDeviceId) == pnp).ToArray() : Array.Empty<InventoryDevice>();
            if (matches.Length == 1) { remaining.Remove(matches[0]); continue; }
            if (cpuMode)
            {
                string index = device.Id[(device.Id.LastIndexOf('/') + 1)..];
                var socket = remaining.FirstOrDefault(x => x.Id.Equals("CPU" + index, StringComparison.OrdinalIgnoreCase));
                if (socket is not null) { remaining.Remove(socket); continue; }
            }
            unmatched.Add(device);
        }
        foreach (var group in unmatched.GroupBy(x => HardwareSensorMapper.NormalizeModel(x.Name)))
        {
            foreach (var item in remaining.Where(x => HardwareSensorMapper.NormalizeModel(x.Name) == group.Key)
                .OrderBy(x => x.Id, StringComparer.Ordinal).Take(group.Count()).ToArray()) remaining.Remove(item);
        }
        return remaining;
    }

    private static string? GpuDeviceId(IHardware hardware)
    {
        try { return (hardware as GenericGpu)?.DeviceId; }
        catch (Exception ex) when (IsRecoverable(ex)) { return null; }
    }

    private static string? OptionalText(ManagementBaseObject item, string property)
        => Convert.ToString(item[property], CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } value ? value : null;
    private static long? Number(ManagementBaseObject item, string property)
        => long.TryParse(OptionalText(item, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) && number >= 0 ? number : null;
    private static uint? PositiveNumber(ManagementBaseObject item, string property)
        => uint.TryParse(OptionalText(item, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number) && number > 0 ? number : null;

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

    private sealed record DeviceSensors(HardwareType Type, string Id, string Name, string? PnpDeviceId, List<SensorReading> Readings);
    private sealed record InventoryDevice(string Id, string Name, string Vendor, string? PnpDeviceId);
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Roger.Models;

namespace Roger.Services;

/// <summary>Maps actual LHM sensor names. Unsupported metrics remain null; vendor labels are not filters.</summary>
internal static class HardwareSensorMapper
{
    internal const string SensorSource = "LibreHardwareMonitor / 硬件驱动";

    internal static CpuDeviceReading Cpu(string id, string name, string vendor, IReadOnlyList<SensorReading> readings)
    {
        double? load = Find(readings, "Load", "CPU Total", "Total");
        // Prefer a physical die/package reading to Tctl, which can include a fan-control offset.
        double? temperature = Temperature(readings, "CPU Package", "Core (Tdie)", "Core (Tctl/Tdie)", "Core (Tctl)", "CPU Cores");
        var coreTemperatures = readings.Where(x => x.Kind == "Temperature" && ValidTemperature(x.Value)
            && !x.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
            && !x.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(x.Name, @"^(?:CPU )?Core(?: #\d+(?: \(.*\))?| Max| Average)$|^CCD\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Select(x => x.Value).ToArray();
        temperature ??= coreTemperatures.Length > 0 ? coreTemperatures.Max() : null;
        var missing = new List<string>();
        if (load is null) missing.Add("未提供此 CPU 的总负载");
        if (temperature is null) missing.Add("温度不可用，取决于驱动、管理员权限与硬件支持");
        return new CpuDeviceReading(id, name, vendor, Percent(load), temperature, SensorSource, Join(missing));
    }

    internal static GpuDeviceReading Gpu(string id, string name, string vendor, bool? discrete, IReadOnlyList<SensorReading> readings)
    {
        double? load = Find(readings, "Load", "GPU Core", "GPU Total", "GPU Global Activity", "GPU Render/Compute");
        string loadSource = "核心负载";
        if (load is null)
        {
            load = Find(readings, "Load", "D3D 3D", "GPU D3D 3D");
            loadSource = "3D 引擎负载";
        }
        if (load is null)
        {
            load = Max(readings.Where(x => x.Kind == "Load" && (x.Name.StartsWith("D3D 3D", StringComparison.OrdinalIgnoreCase)
                || x.Name.StartsWith("D3D Compute", StringComparison.OrdinalIgnoreCase))).Select(x => x.Value));
            loadSource = "3D/计算引擎负载";
        }
        double? temperature = Temperature(readings, "GPU Core", "GPU");
        var api = (Used: Memory(readings, "GPU Memory Used"), Total: Positive(Memory(readings, "GPU Memory Total")));
        var d3d = (Used: Memory(readings, "D3D Dedicated Memory Used"), Total: Positive(Memory(readings, "D3D Dedicated Memory Total")));
        // The driver reports physical VRAM; D3D reports an allocation/budget domain. Do not splice
        // the used value of one family with the capacity of the other, or substitute shared RAM.
        var dedicated = api.Used.HasValue && api.Total.HasValue ? api
            : d3d.Used.HasValue && d3d.Total.HasValue ? d3d
            : api.Used.HasValue || api.Total.HasValue ? api : d3d;
        double? sharedUsed = Memory(readings, "D3D Shared Memory Used");
        double? sharedTotal = Positive(Memory(readings, "D3D Shared Memory Total"));
        var missing = new List<string>();
        if (load is null) missing.Add("负载不可用");
        if (temperature is null) missing.Add("温度不可用");
        if (dedicated.Used is null || dedicated.Total is null) missing.Add("专用 GPU 内存数据不完整");
        if (dedicated.Used > dedicated.Total) missing.Add("驱动显存读数超过报告容量，请结合驱动工具复核");
        return new GpuDeviceReading(id, name, vendor, discrete, Percent(load), temperature,
            dedicated.Used, dedicated.Total, sharedUsed, sharedTotal,
            SensorSource + (load.HasValue ? " · " + loadSource : ""), Join(missing));
    }

    internal static GpuDeviceReading? SelectGpu(IReadOnlyList<GpuDeviceReading> devices, string? selectedId)
    {
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var selected = devices.FirstOrDefault(x => x.Id.Equals(selectedId, StringComparison.Ordinal));
            if (selected is not null) return selected;
        }
        // Stable, vendor-neutral default: known discrete, unclassified, then known integrated.
        // Capacity breaks ties, never the vendor name or a rapidly varying instantaneous load.
        return devices.OrderBy(x => x.IsDiscrete == true ? 0 : x.IsDiscrete is null ? 1 : 2)
            .ThenByDescending(x => x.MemoryTotalMb ?? -1)
            .ThenByDescending(x => x.Load.HasValue || x.Temperature.HasValue)
            .ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    internal static bool? DiscreteHint(HardwareType type, string id, string name)
    {
        if (id.StartsWith("/gpu-intel-integrated/", StringComparison.OrdinalIgnoreCase)) return false;
        if (type == HardwareType.GpuIntel && id.StartsWith("/gpu-intel/", StringComparison.OrdinalIgnoreCase)) return true;
        if (type == HardwareType.GpuNvidia) return true; // Windows NVIDIA adapter family.
        if (Regex.IsMatch(name, @"\bRadeon\s+(?:RX|Pro)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        if (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(name, @"\bRadeon(?:\(TM\))?\s+(?:\d{3,4}[MS]|Vega\s+\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        return null;
    }

    internal static string Vendor(string name, HardwareType? type = null)
    {
        if (type == HardwareType.GpuNvidia || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (type == HardwareType.GpuAmd || name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (type == HardwareType.GpuIntel || name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return type == HardwareType.Cpu || string.IsNullOrWhiteSpace(name) ? "未报告厂商" : name.Trim();
    }

    internal static string NormalizeModel(string value) => Regex.Replace(value, @"\(R\)|\(TM\)|\bProcessor\b|\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).ToUpperInvariant();
    internal static string NormalizePnp(string? value) => (value ?? "").Trim().Replace("\\\\?\\", "", StringComparison.Ordinal).Replace('#', '\\').ToUpperInvariant();
    internal static string ModuleManufacturer(string? value)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || text.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)) return "未报告厂商";
        // SMBIOS can return an untranslated JEDEC/vendor code. Retain it without guessing a brand.
        return Regex.IsMatch(text, @"^(?:0x)?[0-9A-Fa-f]{4,8}$", RegexOptions.CultureInvariant) ? text + "（固件代码）" : text;
    }

    private static string? Join(List<string> values) => values.Count == 0 ? null : string.Join("；", values);
    private static bool ValidTemperature(double value) => double.IsFinite(value) && value != 0;
    private static double? Temperature(IEnumerable<SensorReading> readings, params string[] names)
    {
        var values = readings.Where(x => ValidTemperature(x.Value));
        return Find(values, "Temperature", names);
    }
    private static double? Memory(IEnumerable<SensorReading> readings, params string[] names)
        => Find(readings, "SmallData", names) ?? (Find(readings, "Data", names) is double gib ? gib * 1024 : null);
    private static double? Positive(double? value) => value > 0 ? value : null;
    private static double? Find(IEnumerable<SensorReading> readings, string kind, params string[] names)
    {
        foreach (string name in names)
        {
            var reading = readings.FirstOrDefault(x => x.Kind == kind && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && double.IsFinite(x.Value) && (kind == "Temperature" || x.Value >= 0));
            if (reading is not null) return reading.Value;
        }
        return null;
    }
    private static double? Max(IEnumerable<double> values)
    {
        var finite = values.Where(x => double.IsFinite(x) && x >= 0).ToArray();
        return finite.Length == 0 ? null : finite.Max();
    }
    private static double? Percent(double? value) => value is double raw && double.IsFinite(raw) && raw >= 0 ? Math.Clamp(raw, 0, 100) : null;
}

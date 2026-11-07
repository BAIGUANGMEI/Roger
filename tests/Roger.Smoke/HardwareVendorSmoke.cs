using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Roger.Models;
using Roger.Services;

public static class HardwareVendorSmoke
{
    public static Task RunAsync()
    {
        var intel = HardwareSensorMapper.Cpu("/intelcpu/0", "Intel Core i7", "Intel", Readings(
            ("CPU Total", "Load", 22), ("CPU Package", "Temperature", 51), ("CPU Core #1 Distance to TjMax", "Temperature", 80)));
        Check(intel.Load == 22 && intel.Temperature == 51 && intel.Vendor == "Intel", "Intel package metrics");
        var coresOnly = HardwareSensorMapper.Cpu("/intelcpu/1", "Intel Xeon", "Intel", Readings(
            ("CPU Core #1", "Load", 99), ("CPU Core #1", "Temperature", 41), ("CPU Core #2", "Temperature", 43),
            ("CPU Core #1 Distance to TjMax", "Temperature", 90)));
        Check(coresOnly.Load is null && coresOnly.Temperature == 43, "do not relabel single-core load or TjMax distance as CPU total/temperature");
        var onlyDistance = HardwareSensorMapper.Cpu("/intelcpu/2", "Intel Core", "Intel", Readings(
            ("CPU Core #1 Distance to TjMax", "Temperature", 71), ("Core Max", "Temperature", 0)));
        Check(onlyDistance.Temperature is null && onlyDistance.Status is not null, "unavailable temperatures stay explicit");
        var amd = HardwareSensorMapper.Cpu("/amdcpu/0", "AMD Ryzen", "AMD", Readings(
            ("CPU Total", "Load", 15), ("Core (Tctl)", "Temperature", 83), ("Core (Tdie)", "Temperature", 63)));
        Check(amd.Temperature == 63 && amd.Load == 15, "AMD physical die preferred over control offset");

        var nvidia = HardwareSensorMapper.Gpu("/gpu-nvidia/0", "NVIDIA RTX", "NVIDIA", true, Readings(
            ("GPU Core", "Load", 17), ("GPU Core", "Temperature", 58), ("GPU Memory Used", "SmallData", 2048),
            ("GPU Memory Total", "SmallData", 8192), ("D3D Shared Memory Used", "SmallData", 512), ("D3D Shared Memory Total", "SmallData", 32768)));
        var radeon = HardwareSensorMapper.Gpu("/gpu-amd/0", "AMD Radeon RX", "AMD", true, Readings(
            ("GPU Core", "Load", 29), ("GPU Core", "Temperature", 61), ("GPU Memory Used", "Data", 4), ("GPU Memory Total", "Data", 16)));
        Check(nvidia.MemoryUsedMb == 2048 && nvidia.MemoryTotalMb == 8192 && nvidia.SharedMemoryTotalMb == 32768, "NVIDIA separate VRAM/shared domains");
        Check(radeon.MemoryUsedMb == 4096 && radeon.MemoryTotalMb == 16384, "AMD GiB sensor conversion");
        var integrated = HardwareSensorMapper.Gpu("/gpu-intel-integrated/pci", "Intel UHD", "Intel", false, Readings(
            ("D3D 3D", "Load", 7), ("GPU Memory", "Load", 95), ("GPU Memory", "Temperature", 80),
            ("D3D Dedicated Memory Used", "SmallData", 128), ("D3D Shared Memory Used", "SmallData", 1024), ("D3D Shared Memory Total", "SmallData", 32768)));
        Check(integrated.Load == 7 && integrated.Temperature is null, "Intel engine fallback does not use memory load/temperature");
        Check(integrated.MemoryUsedMb == 128 && integrated.MemoryTotalMb is null && integrated.SharedMemoryTotalMb == 32768,
            "integrated shared RAM must never become dedicated VRAM capacity");
        var arc = HardwareSensorMapper.Gpu("/gpu-intel/arc", "Intel Arc", "Intel", true, Readings(
            ("GPU Core", "Load", 31), ("GPU Core", "Temperature", 49), ("GPU Memory Used", "SmallData", 3000), ("GPU Memory Total", "SmallData", 12288)));
        var mixed = HardwareSensorMapper.Gpu("mixed", "GPU", "未报告", null, Readings(
            ("GPU Memory Used", "SmallData", 512), ("D3D Dedicated Memory Total", "SmallData", 8192), ("GPU Core", "Load", double.NaN)));
        Check(mixed.MemoryUsedMb == 512 && mixed.MemoryTotalMb is null && mixed.Load is null,
            "do not splice different driver memory families or accept NaN");

        var devices = new[] { integrated, nvidia, arc, radeon };
        Check(HardwareSensorMapper.SelectGpu(devices, null)?.Id == radeon.Id, "default ranking is vendor neutral and stable");
        Check(HardwareSensorMapper.SelectGpu(devices, integrated.Id)?.Id == integrated.Id, "explicit integrated GPU choice is honored");
        Check(HardwareSensorMapper.SelectGpu(devices, "disconnected")?.Id == radeon.Id, "missing selection falls back deterministically");
        Check(HardwareSensorMapper.SelectGpu(Array.Empty<GpuDeviceReading>(), null) is null, "no device is not fabricated");
        var sameModel = new[] { nvidia, nvidia with { Id = "/gpu-nvidia/1", MemoryUsedMb = 4096 } };
        Check(HardwareSensorMapper.SelectGpu(sameModel, "/gpu-nvidia/1")?.MemoryUsedMb == 4096, "same-model adapters remain distinct by id");
        Check(HardwareSensorMapper.DiscreteHint(HardwareType.GpuIntel, "/gpu-intel-integrated/0", "Intel Arc Graphics") == false,
            "identifier outranks ambiguous Arc branding for integrated devices");
        Check(HardwareSensorMapper.DiscreteHint(HardwareType.GpuAmd, "/gpu-amd/0", "AMD Radeon RX 7600M XT") == true,
            "mobile discrete Radeon is not mistaken for integrated M-series graphics");
        foreach (string brand in new[] { "Kingston", "Corsair", "G Skill Intl", "Crucial", "Samsung", "SK Hynix" })
            Check(HardwareSensorMapper.ModuleManufacturer(brand) == brand, "memory manufacturers must not require a whitelist");
        Check(HardwareSensorMapper.ModuleManufacturer("04CD").Contains("04CD", StringComparison.Ordinal), "retain unknown firmware vendor code without guessing");
        Check(HardwareSensorMapper.ModuleManufacturer(null) == "未报告厂商", "missing manufacturer is explicit");
        Check(HardwareSensorMapper.NormalizeModel("AMD Ryzen 9 Processor") == HardwareSensorMapper.NormalizeModel("AMD Ryzen 9"), "inventory canonicalization");
        Console.WriteLine("PASS multi-vendor CPU/GPU sensor mapping, same-model GPU identity/selection, VRAM/shared units and memory manufacturer handling");
        return Task.CompletedTask;
    }

    private static IReadOnlyList<SensorReading> Readings(params (string Name, string Kind, double Value)[] samples)
        => samples.Select(s => new SensorReading("fixture", s.Name, s.Kind, s.Value, "")).ToArray();
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Hardware vendor mapping: " + label);
    }
}

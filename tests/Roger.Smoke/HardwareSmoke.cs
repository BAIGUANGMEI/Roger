using System;
using System.Linq;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class HardwareSmoke
{
    public static async Task RunAsync()
    {
        var service = new HardwareMonitorService();
        try
        {
            await Task.Run(service.Initialize);
            MonitorSnapshot first = await Task.Run(service.Sample);
            await Task.Delay(350);
            MonitorSnapshot second = await Task.Run(service.Sample);

            Check(second.Timestamp >= first.Timestamp, "sample timestamp must advance");
            Check(second.CpuLoad is >= 0 and <= 100, "Windows must report an aggregate CPU load after two samples");
            CheckFinite(second.CpuLoad, "CPU load");
            CheckFinite(second.CpuTemperature, "CPU temperature");
            CheckFinite(second.GpuLoad, "GPU load");
            CheckFinite(second.GpuTemperature, "GPU temperature");
            CheckFinite(second.GpuMemoryUsedMb, "GPU memory used");
            CheckFinite(second.GpuMemoryTotalMb, "GPU memory capacity");
            Check(second.CpuTemperature is null || second.CpuTemperature != 0, "uninitialized zero CPU temperature must be unavailable");
            Check(second.GpuTemperature is null || second.GpuTemperature != 0, "uninitialized zero GPU temperature must be unavailable");
            Check(second.GpuLoad is null or >= 0 and <= 100, "GPU load range");
            Check(double.IsFinite(second.MemoryTotalGb) && second.MemoryTotalGb > 0, "Windows physical memory capacity");
            Check(double.IsFinite(second.MemoryUsedGb) && second.MemoryUsedGb >= 0 && second.MemoryUsedGb <= second.MemoryTotalGb, "physical memory usage range");
            Check(double.IsFinite(second.DownloadBytesPerSecond) && second.DownloadBytesPerSecond >= 0, "monotonic NIC download delta");
            Check(double.IsFinite(second.UploadBytesPerSecond) && second.UploadBytesPerSecond >= 0, "monotonic NIC upload delta");
            Check(!string.IsNullOrWhiteSpace(second.NetworkName), "network availability must have a label");
            foreach (string hiddenInterface in new[] { "WFP", "QoS Packet Scheduler", "Miniport", "Native WiFi Filter" })
                Check(!second.NetworkName.Contains(hiddenInterface, StringComparison.OrdinalIgnoreCase), "hidden network filter interfaces must not duplicate traffic");

            var nvidia = second.Sensors.Where(x => x.Hardware.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Hardware).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (nvidia is not null)
            {
                SensorReading? coreLoad = nvidia.FirstOrDefault(x => x.Kind == "Load" && x.Name == "GPU Core");
                SensorReading? coreTemperature = nvidia.FirstOrDefault(x => x.Kind == "Temperature" && x.Name == "GPU Core");
                SensorReading? memoryCapacity = nvidia.FirstOrDefault(x => x.Kind == "SmallData" && x.Name == "GPU Memory Total");
                if (coreLoad is not null) Check(second.GpuLoad == coreLoad.Value, "NVIDIA must be preferred over an integrated GPU");
                if (coreTemperature is not null) Check(second.GpuTemperature == coreTemperature.Value, "GPU summary temperature must belong to the selected NVIDIA device");
                if (memoryCapacity is not null) Check(second.GpuMemoryTotalMb == memoryCapacity.Value, "GPU summary VRAM must belong to the selected NVIDIA device");
            }

            foreach (DriveReading drive in second.Drives)
            {
                Check(drive.TotalBytes >= 0 && drive.FreeBytes >= 0 && drive.FreeBytes <= drive.TotalBytes, "disk free/total bounds");
                Check(double.IsFinite(drive.UsedPercent) && drive.UsedPercent is >= 0 and <= 100, "disk usage range");
            }
            foreach (SensorReading sensor in second.Sensors)
            {
                Check(double.IsFinite(sensor.Value), "raw sensor must be finite");
                Check(!string.IsNullOrWhiteSpace(sensor.Hardware) && !string.IsNullOrWhiteSpace(sensor.Name), "raw sensor identity");
            }

            var inventory = await Task.Run(service.GetHardwareInfo);
            Check(inventory.Count > 0 && inventory.Any(x => x.Category == "CPU"), "inventory must include CPU info or an explicit unavailable entry");
            Check(inventory.All(x => !string.IsNullOrWhiteSpace(x.Category) && !string.IsNullOrWhiteSpace(x.Name)), "inventory entries have identities");
            Check(ReferenceEquals(inventory, service.GetHardwareInfo()), "inventory must be cached instead of rerunning WMI each tick");
            if (second.CpuTemperature is null)
                Check(second.StatusMessage?.Contains("CPU 温度不可用", StringComparison.Ordinal) == true, "unsupported CPU temperature must be explicit");

            Console.WriteLine($"PASS hardware live sampling: CPU {second.CpuLoad:0.0}% ({second.CpuTemperature?.ToString("0.0") ?? "unavailable"}°C), GPU {second.GpuLoad:0.0}% ({second.GpuTemperature?.ToString("0.0") ?? "unavailable"}°C), VRAM {second.GpuMemoryUsedMb:0}/{second.GpuMemoryTotalMb:0} MiB, network {second.NetworkName}, RAM {second.MemoryUsedGb:0.0}/{second.MemoryTotalGb:0.0} GiB, {second.Sensors.Count} sensors, {second.Drives.Count} drives, {inventory.Count} inventory entries");
            if (second.StatusMessage is not null)
                Console.WriteLine("INFO hardware availability: " + second.StatusMessage);
        }
        finally
        {
            await Task.Run(service.Dispose);
        }

        service.Dispose(); // Idempotent shutdown.
        try
        {
            service.Sample();
            throw new InvalidOperationException("Hardware sampling must reject use after Dispose");
        }
        catch (ObjectDisposedException) { }
    }

    private static void CheckFinite(double? value, string name)
        => Check(value is null || double.IsFinite(value.Value), name + " must be unavailable or finite");

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Hardware smoke failed: " + message);
    }
}

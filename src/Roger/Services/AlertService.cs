using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Roger.Models;

namespace Roger.Services;

/// <summary>
/// Pure snapshot evaluation: notifications are a UI decision. Consecutive abnormal samples
/// must span the configured duration. Missing values, device changes and long gaps break it.
/// </summary>
public sealed class AlertService
{
    public const int RecordCapacity = 500;
    private const int MaximumStateCount = 128;
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly Dictionary<string, MetricState> _states = new(StringComparer.Ordinal);
    private readonly List<AlertEvent> _records = new();
    private AlertOptions _options = new();
    private DateTimeOffset? _lastSnapshotTime;
    private long _lastWriteAttempt;
    private bool _recordsDirty, _optionsCorrupt, _recordsCorrupt;
    private string? _loadWarning, _writeWarning;

    public string OptionsPath { get; }
    public string RecordsPath { get; }
    public AlertOptions Options { get { lock (_gate) return _options; } }
    public IReadOnlyList<AlertEvent> Records { get { lock (_gate) return _records.ToArray(); } }
    public string? LastPersistenceWarning
    {
        get { lock (_gate) return Join(_loadWarning, _writeWarning); }
    }

    public AlertService(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roger");
        OptionsPath = Path.Combine(Path.GetFullPath(directory), "alerts-options.json");
        RecordsPath = Path.Combine(Path.GetFullPath(directory), "alerts-records.json");
        LoadOptions();
        LoadRecords();
    }

    public void SaveOptions(AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        lock (_gate)
        {
            // Do not apply a configuration that failed to persist. UI callers can report the
            // error and the previously active options remain in effect.
            PreserveCorruptFile(OptionsPath, ref _optionsCorrupt);
            WriteAtomic(OptionsPath, options);
            _options = options;
            foreach (MetricState state in _states.Values) state.PendingSince = null;
        }
    }

    public IReadOnlyList<AlertEvent> Evaluate(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            DateTimeOffset now = snapshot.Timestamp;
            bool backwards = _lastSnapshotTime.HasValue && now < _lastSnapshotTime.Value;
            bool gap = _lastSnapshotTime.HasValue && now - _lastSnapshotTime.Value > MaximumSampleGap;
            if (backwards)
                _states.Clear();
            else if (gap)
                foreach (MetricState state in _states.Values) state.PendingSince = null;
            _lastSnapshotTime = now;

            var emitted = new List<AlertEvent>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (_options.Enabled)
            {
                foreach (Metric metric in Metrics(snapshot))
                {
                    if (!seen.Add(metric.Key)) continue;
                    if (!_states.TryGetValue(metric.Key, out MetricState? state))
                    {
                        DateTimeOffset? persisted = _records.Where(record => record.MetricKey == metric.Key && record.Timestamp <= now)
                            .Select(record => (DateTimeOffset?)record.Timestamp).Max();
                        state = new MetricState { LastEmitted = persisted };
                        _states.Add(metric.Key, state);
                    }
                    state.LastSeen = now;
                    if (!metric.Abnormal)
                    {
                        state.PendingSince = null;
                        continue;
                    }
                    state.PendingSince ??= now;
                    if (now - state.PendingSince.Value < TimeSpan.FromSeconds(_options.RequiredDurationSeconds)) continue;
                    if (state.LastEmitted.HasValue && now - state.LastEmitted.Value < TimeSpan.FromSeconds(_options.CooldownSeconds)) continue;
                    var item = new AlertEvent(Guid.NewGuid(), now, metric.Key, metric.Title,
                        metric.Description + $"；已持续至少 {_options.RequiredDurationSeconds} 秒。", metric.Value, metric.Threshold);
                    state.LastEmitted = now;
                    emitted.Add(item);
                    _records.Insert(0, item);
                    _recordsDirty = true;
                }
            }

            // A temporarily missing sensor must never accumulate time toward a later alert.
            // LastEmitted is retained per device so switching cards cannot borrow a cooldown.
            foreach (var pair in _states)
                if (!seen.Contains(pair.Key)) pair.Value.PendingSince = null;
            if (_states.Count > MaximumStateCount)
                foreach (string stale in _states.OrderBy(pair => pair.Value.LastSeen).Take(_states.Count - MaximumStateCount).Select(pair => pair.Key).ToArray())
                    _states.Remove(stale);
            if (_records.Count > RecordCapacity) _records.RemoveRange(RecordCapacity, _records.Count - RecordCapacity);
            TryPersistRecords(force: emitted.Count > 0);
            return emitted.AsReadOnly();
        }
    }

    /// <summary>Only clears the log. It does not reset cooldowns or re-emit ongoing incidents.</summary>
    public void ClearRecords()
    {
        lock (_gate)
        {
            PreserveCorruptFile(RecordsPath, ref _recordsCorrupt);
            WriteAtomic(RecordsPath, Array.Empty<AlertEvent>());
            _records.Clear();
            _recordsDirty = false;
            _writeWarning = null;
        }
    }

    private IEnumerable<Metric> Metrics(MonitorSnapshot snapshot)
    {
        if (_options.CpuTemperatureEnabled)
        {
            var devices = snapshot.CpuDevices.Where(device => !string.IsNullOrWhiteSpace(device.Id) && ValidPositive(device.Temperature).HasValue).ToArray();
            foreach (CpuDeviceReading device in devices)
            {
                double temperature = device.Temperature!.Value;
                string label = DeviceLabel(device.Name, device.Id);
                yield return new Metric("cpu-temperature:device:" + device.Id, "CPU 持续高温 · " + label, temperature, _options.CpuTemperatureThresholdCelsius,
                    temperature >= _options.CpuTemperatureThresholdCelsius, $"CPU {label} 温度 {N(temperature)}°C，提醒阈值 {N(_options.CpuTemperatureThresholdCelsius)}°C");
            }
            // Older snapshots may only have summary fields. Keep their identity separate so
            // missing device data never borrows a previous device's sustained interval.
            if (devices.Length == 0 && ValidPositive(snapshot.CpuTemperature) is double cpu)
                yield return new Metric("cpu-temperature:summary", "CPU 持续高温", cpu, _options.CpuTemperatureThresholdCelsius,
                    cpu >= _options.CpuTemperatureThresholdCelsius, $"CPU 温度 {N(cpu)}°C，提醒阈值 {N(_options.CpuTemperatureThresholdCelsius)}°C");
        }
        if (_options.GpuTemperatureEnabled)
        {
            var devices = snapshot.GpuDevices.Where(device => !string.IsNullOrWhiteSpace(device.Id) && ValidPositive(device.Temperature).HasValue).ToArray();
            foreach (GpuDeviceReading device in devices)
            {
                double temperature = device.Temperature!.Value;
                string label = DeviceLabel(device.Name, device.Id);
                yield return new Metric("gpu-temperature:device:" + device.Id, "GPU 持续高温 · " + label, temperature, _options.GpuTemperatureThresholdCelsius,
                    temperature >= _options.GpuTemperatureThresholdCelsius, $"GPU {label} 温度 {N(temperature)}°C，提醒阈值 {N(_options.GpuTemperatureThresholdCelsius)}°C");
            }
            if (devices.Length == 0 && ValidPositive(snapshot.GpuTemperature) is double gpu)
                yield return new Metric("gpu-temperature:summary:" + (snapshot.SelectedGpuId ?? "unspecified"), "GPU 持续高温", gpu, _options.GpuTemperatureThresholdCelsius,
                    gpu >= _options.GpuTemperatureThresholdCelsius, $"当前显卡温度 {N(gpu)}°C，提醒阈值 {N(_options.GpuTemperatureThresholdCelsius)}°C");
        }
        if (_options.MemoryEnabled && double.IsFinite(snapshot.MemoryTotalGb) && snapshot.MemoryTotalGb > 0
            && double.IsFinite(snapshot.MemoryUsedGb) && snapshot.MemoryUsedGb >= 0 && snapshot.MemoryUsedGb <= snapshot.MemoryTotalGb)
        {
            double memory = snapshot.MemoryPercent;
            yield return new Metric("memory-percent", "内存持续紧张", memory, _options.MemoryPercentThreshold,
                memory >= _options.MemoryPercentThreshold, $"内存占用 {N(memory)}%，提醒阈值 {N(_options.MemoryPercentThreshold)}%");
        }
        if (!_options.DiskSpaceEnabled) yield break;
        foreach (DriveReading drive in snapshot.Drives)
        {
            if (drive.TotalBytes <= 0 || drive.FreeBytes < 0 || drive.FreeBytes > drive.TotalBytes || string.IsNullOrWhiteSpace(drive.Name)) continue;
            double free = drive.FreeBytes * 100d / drive.TotalBytes;
            yield return new Metric("disk-free:" + drive.Name.ToUpperInvariant(), "磁盘空间不足", free, _options.DiskFreePercentThreshold,
                free < _options.DiskFreePercentThreshold, $"{drive.Name} 可用空间 {N(free)}%（{N(drive.FreeBytes / (1024d * 1024 * 1024))} GiB），提醒阈值低于 {N(_options.DiskFreePercentThreshold)}%");
        }
    }

    private void TryPersistRecords(bool force)
    {
        if (!_recordsDirty || (!force && _lastWriteAttempt != 0 && Stopwatch.GetElapsedTime(_lastWriteAttempt) < TimeSpan.FromSeconds(10))) return;
        _lastWriteAttempt = Stopwatch.GetTimestamp();
        try
        {
            PreserveCorruptFile(RecordsPath, ref _recordsCorrupt);
            WriteAtomic(RecordsPath, _records);
            _recordsDirty = false;
            _writeWarning = null;
        }
        catch (Exception ex) when (PersistenceFailure(ex))
        {
            _writeWarning = "提醒记录暂未保存，仍保留在本次运行中：" + ex.Message;
        }
    }

    private void LoadOptions()
    {
        if (!File.Exists(OptionsPath)) return;
        try
        {
            AlertOptions options = ReadJson<AlertOptions>(OptionsPath, 64 * 1024);
            Validate(options);
            _options = options;
        }
        catch (Exception ex) when (PersistenceFailure(ex) || ex is ArgumentException)
        {
            _optionsCorrupt = true;
            _loadWarning = Join(_loadWarning, "提醒配置不可读取，已使用默认值；原文件保留。" + ex.Message);
        }
    }

    private void LoadRecords()
    {
        if (!File.Exists(RecordsPath)) return;
        try
        {
            var records = ReadJson<AlertEvent[]>(RecordsPath, 2 * 1024 * 1024);
            if (records.Length > 10_000 || records.Any(item => item is null || item.Id == Guid.Empty || item.Timestamp == default
                || string.IsNullOrWhiteSpace(item.MetricKey) || item.MetricKey.Length > 2048
                || string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 200 || item.Message is null || item.Message.Length > 2000
                || !double.IsFinite(item.Value) || !double.IsFinite(item.Threshold)))
                throw new JsonException("提醒记录字段无效。");
            _records.AddRange(records.OrderByDescending(item => item.Timestamp).Take(RecordCapacity));
        }
        catch (Exception ex) when (PersistenceFailure(ex))
        {
            _recordsCorrupt = true;
            _loadWarning = Join(_loadWarning, "提醒记录不可读取，原文件保留。" + ex.Message);
        }
    }

    public static void Validate(AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CheckRange(options.CpuTemperatureThresholdCelsius, 1, 150, "CPU 温度阈值");
        CheckRange(options.GpuTemperatureThresholdCelsius, 1, 150, "GPU 温度阈值");
        CheckRange(options.MemoryPercentThreshold, 1, 100, "内存占用阈值");
        CheckRange(options.DiskFreePercentThreshold, 1, 99, "磁盘可用空间阈值");
        if (options.RequiredDurationSeconds is < 1 or > 3600) throw new ArgumentException("持续时间必须为 1–3600 秒。");
        if (options.CooldownSeconds is < 1 or > 86400) throw new ArgumentException("提醒冷却时间必须为 1–86400 秒。");
    }

    private static void CheckRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name}必须在 {minimum}–{maximum} 之间。");
    }
    private static double? ValidPositive(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;
    private static string DeviceLabel(string? name, string id)
    {
        string label = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        return label.Length <= 160 ? label : label[..157] + "…";
    }
    private static string N(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);
    private static string? Join(string? first, string? second) => string.IsNullOrWhiteSpace(first) ? second : string.IsNullOrWhiteSpace(second) ? first : first + "\n" + second;
    private static bool PersistenceFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException;
    private static T ReadJson<T>(string path, long maximumBytes)
    {
        if (new FileInfo(path).Length > maximumBytes) throw new JsonException("文件超过允许大小。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException("文件内容为空。");
    }
    private static void PreserveCorruptFile(string path, ref bool corrupt)
    {
        if (!corrupt) return;
        if (File.Exists(path))
            File.Copy(path, path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"), overwrite: false);
        corrupt = false;
    }
    private static void WriteAtomic<T>(string path, T value)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "alerts-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Metric(string Key, string Title, double Value, double Threshold, bool Abnormal, string Description);
    private sealed class MetricState
    {
        public DateTimeOffset? PendingSince;
        public DateTimeOffset? LastEmitted;
        public DateTimeOffset LastSeen;
    }
}

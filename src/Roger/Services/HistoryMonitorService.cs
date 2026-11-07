using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Roger.Models;

namespace Roger.Services;

/// <summary>Local, seven-day minute aggregates. Never stores process names or user files.</summary>
public sealed class HistoryMonitorService : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<HistoryBucket> _buckets = new();
    private readonly Queue<HistoryBucket> _pending = new();
    private readonly Guid _sessionId = Guid.NewGuid();
    private Accumulator? _current;
    private DateTimeOffset? _lastSample;
    private DateTimeOffset _lastTrim = DateTimeOffset.MinValue;
    private bool _initialized, _disposed;
    private string? _loadWarning, _writeWarning, _trimWarning;
    public string? LastPersistenceWarning
    {
        get { lock (_gate) return string.Join("\n", new[] { _loadWarning, _writeWarning, _trimWarning }.Where(v => !string.IsNullOrEmpty(v))) is { Length: > 0 } message ? message : null; }
    }
    public string DirectoryPath => _directory;

    public HistoryMonitorService(string? directory = null, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roger", "history");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Initialize()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            _initialized = true;
            var seen = new HashSet<Guid>();
            var cutoff = _clock().AddDays(-7);
            for (int offset = 7; offset >= 0; offset--)
            {
                string file = DayPath(_clock().AddDays(-offset));
                if (!File.Exists(file)) continue;
                try
                {
                    if (new FileInfo(file).Length > 16 * 1024 * 1024) { _loadWarning = "部分历史文件过大，已跳过；原文件保留。"; continue; }
                    foreach (string line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var bucket = JsonSerializer.Deserialize<HistoryBucket>(line);
                            if (bucket is null || !Valid(bucket)) { _loadWarning = "部分历史记录无效，已跳过；原文件保留。"; continue; }
                            if (bucket.Timestamp >= cutoff && seen.Add(bucket.Id)) _buckets.Add(bucket);
                        }
                        catch (JsonException) { _loadWarning = "部分历史记录不完整，已跳过；原文件保留。"; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _loadWarning = "历史数据读取未完成：" + ex.Message; }
            }
            _buckets.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            Trim();
        }
    }

    public void Record(MonitorSnapshot sample, StorageSnapshot? storage = null)
    {
        lock (_gate)
        {
            Initialize();
            if (sample.Timestamp <= _lastSample || sample.Timestamp < _clock().AddDays(-7) || sample.Timestamp > _clock().AddMinutes(5)) return;
            _lastSample = sample.Timestamp;
            var utc = sample.Timestamp.ToUniversalTime();
            var minute = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
            if (_current is not null && (_current.Timestamp != minute || _current.GpuId != sample.SelectedGpuId)) CompleteCurrent();
            _current ??= new Accumulator(minute, sample.SelectedGpuId, _sessionId,
                sample.GpuDevices.FirstOrDefault(g => g.Id == sample.SelectedGpuId)?.Name ?? "GPU 未识别");
            _current.SampleCount++;
            _current.Add(HistoryMetrics.CpuLoad, sample.CpuLoad);
            _current.Add(HistoryMetrics.CpuTemperature, sample.CpuTemperature == 0 ? null : sample.CpuTemperature);
            _current.Add(HistoryMetrics.GpuLoad, sample.GpuLoad);
            _current.Add(HistoryMetrics.GpuTemperature, sample.GpuTemperature == 0 ? null : sample.GpuTemperature);
            _current.Add(HistoryMetrics.Memory, sample.MemoryTotalGb > 0 ? sample.MemoryPercent : null);
            // Network baseline is not a genuine zero-throughput measurement.
            bool networkReady = !sample.NetworkName.Contains("等待", StringComparison.Ordinal) && !sample.NetworkName.Contains("无活动", StringComparison.Ordinal);
            _current.Add(HistoryMetrics.Download, networkReady ? sample.DownloadBytesPerSecond : null);
            _current.Add(HistoryMetrics.Upload, networkReady ? sample.UploadBytesPerSecond : null);
            if (storage is not null && Math.Abs((storage.Timestamp - sample.Timestamp).TotalSeconds) <= 30 && storage.Disks.Count > 0)
            {
                _current.Add(HistoryMetrics.DiskRead, SumAvailable(storage.Disks.Select(d => d.ReadBytesPerSecond)));
                _current.Add(HistoryMetrics.DiskWrite, SumAvailable(storage.Disks.Select(d => d.WriteBytesPerSecond)));
            }
            PersistPending();
            Trim();
        }
    }

    public IReadOnlyList<HistoryBucket> Query(TimeSpan range)
    {
        if (range <= TimeSpan.Zero || range > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(range));
        lock (_gate)
        {
            Initialize();
            DateTimeOffset cutoff = _clock() - range;
            return _buckets.Concat(_current is null ? Array.Empty<HistoryBucket>() : new[] { _current.Snapshot() })
                .Where(b => b.Timestamp >= cutoff).OrderBy(b => b.Timestamp).Select(Clone).ToArray();
        }
    }

    public static HistoryStatistic? Summarize(IEnumerable<HistoryBucket> buckets, string metric, string? gpuId = null)
    {
        var values = buckets.Where(b => !metric.StartsWith("gpu.", StringComparison.Ordinal) || b.GpuId == gpuId)
            .Select(b => b.Metrics.GetValueOrDefault(metric)).Where(v => v is not null).Select(v => v!).ToArray();
        return values.Length == 0 ? null : new(values.Sum(v => v.Count), values.Sum(v => v.Sum), values.Min(v => v.Minimum), values.Max(v => v.Maximum));
    }

    public static void ExportCsv(string path, IReadOnlyList<HistoryBucket> buckets)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("时间,GPU标识,GPU型号,采样数," + string.Join(",", HistoryMetrics.All.SelectMany(m => new[] { m + ".average", m + ".maximum" })));
        foreach (var bucket in buckets)
            writer.WriteLine(string.Join(",", new[] { Escape(bucket.Timestamp.ToString("O", CultureInfo.InvariantCulture)), Escape(bucket.GpuId ?? ""), Escape(bucket.GpuName), bucket.SampleCount.ToString(CultureInfo.InvariantCulture) }
                .Concat(HistoryMetrics.All.SelectMany(m => bucket.Metrics.TryGetValue(m, out var value)
                    ? new[] { value.Average.ToString("R", CultureInfo.InvariantCulture), value.Maximum.ToString("R", CultureInfo.InvariantCulture) } : new[] { "", "" }))));
    }

    public void Flush()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); if (!_initialized) return; CompleteCurrent(); PersistPending(); Trim(); }
    }
    private void CompleteCurrent()
    {
        if (_current is null) return;
        var bucket = _current.Snapshot(); _current = null;
        _buckets.Add(bucket); _pending.Enqueue(bucket);
        while (_pending.Count > 24000) _pending.Dequeue();
    }
    private void PersistPending()
    {
        if (_pending.Count == 0) return;
        try
        {
            Directory.CreateDirectory(_directory);
            while (_pending.TryPeek(out var bucket))
            {
                // A leading newline isolates a truncated prior append; IDs deduplicate retries.
                using (var stream = new FileStream(DayPath(bucket.Timestamp), FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(bucket));
                    stream.Write(bytes); stream.Flush(true);
                }
                _pending.Dequeue();
            }
            _writeWarning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _writeWarning = "历史数据暂未写入磁盘，将在后续采样重试：" + ex.Message; }
    }
    private void Trim()
    {
        if (_clock() - _lastTrim < TimeSpan.FromHours(1)) return;
        _lastTrim = _clock();
        var cutoff = _clock().AddDays(-7);
        _buckets.RemoveAll(b => b.Timestamp < cutoff);
        if (_buckets.Count > 24000) _buckets.RemoveRange(0, _buckets.Count - 24000);
        if (!Directory.Exists(_directory)) return;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory, "history-????????.jsonl"))
            {
                string stamp = Path.GetFileNameWithoutExtension(file)[8..];
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && day.Date < cutoff.UtcDateTime.Date) File.Delete(file);
            }
            _trimWarning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _trimWarning = "过期历史文件暂未移除：" + ex.Message; }
    }
    private string DayPath(DateTimeOffset date) => Path.Combine(_directory, "history-" + date.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
    private bool Valid(HistoryBucket bucket) => bucket.Id != Guid.Empty && bucket.Timestamp <= _clock().AddMinutes(5) && bucket.SampleCount is > 0 and <= 10000
        && bucket.Metrics is not null && bucket.Metrics.Count <= HistoryMetrics.All.Count && bucket.GpuName is { Length: <= 512 }
        && bucket.GpuId is not { Length: > 512 } && bucket.Metrics.All(p => HistoryMetrics.All.Contains(p.Key) && p.Value is not null
            && p.Value.Count > 0 && p.Value.Count <= bucket.SampleCount && double.IsFinite(p.Value.Sum) && double.IsFinite(p.Value.Minimum)
            && double.IsFinite(p.Value.Maximum) && p.Value.Maximum >= p.Value.Minimum);
    private static HistoryBucket Clone(HistoryBucket bucket) => bucket with { Metrics = new(bucket.Metrics) };
    private static string Escape(string value)
    {
        string text = value.Replace("\r", " ").Replace("\n", " ");
        if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    private static double? SumAvailable(IEnumerable<double?> values)
    { var array = values.ToArray(); return array.All(v => v.HasValue && double.IsFinite(v.Value) && v >= 0) ? array.Sum(v => v!.Value) : null; }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; Flush(); _disposed = true; }
    }
    private sealed class Accumulator(DateTimeOffset timestamp, string? gpuId, Guid sessionId, string gpuName)
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly Dictionary<string, HistoryStatistic> _metrics = new();
        public DateTimeOffset Timestamp { get; } = timestamp;
        public string? GpuId { get; } = gpuId;
        public int SampleCount;
        public void Add(string key, double? value)
        {
            if (value is not double number || !double.IsFinite(number) || number < 0 && !key.EndsWith("temperature", StringComparison.Ordinal)) return;
            _metrics[key] = _metrics.TryGetValue(key, out var old) ? new(old.Count + 1, old.Sum + number, Math.Min(old.Minimum, number), Math.Max(old.Maximum, number)) : new(1, number, number, number);
        }
        public HistoryBucket Snapshot() => new(_id, Timestamp, GpuId, gpuName, SampleCount, new(_metrics)) { SessionId = sessionId };
    }
}

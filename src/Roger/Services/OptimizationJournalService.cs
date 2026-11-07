using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roger.Models;

namespace Roger.Services;

/// <summary>Local write-ahead journal. Construction and reload never change Windows settings.</summary>
public sealed class OptimizationJournalService
{
    private const int MaximumEntries = 200;
    private readonly object _gate = new();
    private readonly OptimizationService _optimization;
    private readonly Action<IReadOnlyList<OptimizationJournalEntry>>? _persistOverride;
    private List<OptimizationJournalEntry> _entries = new();
    private bool _preserveInvalidFile;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string StoragePath { get; }
    public string? LastLoadWarning { get; private set; }
    public IReadOnlyList<OptimizationJournalEntry> Entries { get { lock (_gate) return _entries.AsEnumerable().Reverse().ToArray(); } }

    public OptimizationJournalService(OptimizationService optimization, string? storagePath = null)
        : this(optimization, storagePath, null) { }

    internal OptimizationJournalService(OptimizationService optimization, string? storagePath,
        Action<IReadOnlyList<OptimizationJournalEntry>>? persistOverride)
    {
        _optimization = optimization ?? throw new ArgumentNullException(nameof(optimization));
        StoragePath = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roger", "optimization-journal.json");
        _persistOverride = persistOverride;
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            LastLoadWarning = null;
            _preserveInvalidFile = false;
            if (!File.Exists(StoragePath)) { _entries = new(); return; }
            try
            {
                if (new FileInfo(StoragePath).Length > 8 * 1024 * 1024) throw new InvalidDataException("记录文件超过大小限制。");
                var records = JsonSerializer.Deserialize<List<OptimizationJournalEntry>>(File.ReadAllText(StoragePath), JsonOptions)
                    ?? throw new InvalidDataException("记录文件为空。");
                if (records.Count > MaximumEntries || records.Any(e => e is null || !Valid(e))
                    || records.Select(e => e.Id).Distinct().Count() != records.Count) throw new InvalidDataException("记录内容无效。");
                _entries = records;
                if (_entries.Any(e => e.Status is OptimizationEntryStatus.Pending or OptimizationEntryStatus.Restoring or OptimizationEntryStatus.Uncertain))
                    LastLoadWarning = "有上次操作的结果尚未核对。恢复时会先检查 Windows 当前值，不会在启动时自动更改设置。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                _entries = new();
                _preserveInvalidFile = true;
                LastLoadWarning = "优化记录无法读取，原文件保留；下次记录前会另存原始副本。" + ex.Message;
            }
        }
    }

    public OptimizationJournalEntry? LatestRestorable(OptimizationChangeKind kind)
    {
        lock (_gate) return _entries.LastOrDefault(e => e.Kind == kind && e.CanRestore);
    }

    public OptimizationJournalEntry ApplyPowerPlan(Guid id, string name)
    {
        if (id == Guid.Empty) throw new ArgumentException("电源计划标识无效。", nameof(id));
        lock (_gate) return Apply(OptimizationChangeKind.PowerPlan, "电源计划 · " + Limit(name, 100), id.ToString("D"));
    }

    public OptimizationJournalEntry ApplyAnimations(bool enabled)
    {
        lock (_gate) return Apply(OptimizationChangeKind.ClientAreaAnimations, "Windows 客户区域动画", enabled ? "true" : "false");
    }

    private OptimizationJournalEntry Apply(OptimizationChangeKind kind, string title, string target)
    {
        string before;
        OptimizationState state;
        try { state = _optimization.GetState(); before = ReadValue(kind, state); }
        catch (Exception ex) when (Recoverable(ex))
        { return Add(New(kind, title, null, target, OptimizationEntryStatus.Failed, "未取得原值，未执行更改：" + ex.Message)); }
        var proposed = New(kind, title, before, target, OptimizationEntryStatus.Pending, "已保存原值，等待执行与核对。") with
        {
            BeforeLabel = Label(kind, before, state), AfterLabel = Label(kind, target, state)
        };
        if (before == target) return Add(proposed with { Status = OptimizationEntryStatus.NoChange, Message = "当前已是所选值，未执行更改。" });
        // Failure here prevents every native write. The durable entry contains the original value.
        var entry = Add(proposed);
        try
        {
            WriteValue(kind, before, target);
            string actual = ReadValue(kind);
            if (actual != target) return Update(entry with { Status = OptimizationEntryStatus.Uncertain, Message = "Windows 当前值未保留目标设置。请刷新状态后核对。" });
            return Update(entry with { Status = OptimizationEntryStatus.Applied, Message = "已应用并核对 Windows 当前值；原值已保存，可在后续启动中恢复。" });
        }
        catch (JournalPersistenceException) { throw; }
        catch (OptimizationService.StateConflictException ex)
        { return Update(entry with { Status = OptimizationEntryStatus.Failed, Message = ex.Message }, nativeMayHaveChanged: false); }
        catch (Exception ex) when (Recoverable(ex))
        {
            bool unchanged = false;
            try { unchanged = ReadValue(kind) == before; } catch (Exception readError) when (Recoverable(readError)) { }
            return Update(entry with { Status = unchanged ? OptimizationEntryStatus.Failed : OptimizationEntryStatus.Uncertain,
                Message = (unchanged ? "操作未完成，Windows 当前值仍为原值：" : "操作返回错误，Windows 设置可能已改变，请核对：") + ex.Message });
        }
    }

    public OptimizationJournalEntry Restore(Guid entryId)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == entryId) ?? throw new ArgumentException("所选记录已不存在，请刷新列表。");
            if (!entry.CanRestore) throw new InvalidOperationException("此记录没有可恢复的设置；DNS 缓存刷新不能撤销。");
            if (LatestRestorable(entry.Kind)?.Id != entry.Id)
                throw new InvalidOperationException("请先恢复同一设置较新的变更，避免覆盖后续操作。");
            string current;
            try { current = ReadValue(entry.Kind); }
            catch (Exception ex) when (Recoverable(ex))
            { return Update(entry with { Status = OptimizationEntryStatus.RestoreFailed, Message = "无法读取当前值，未执行恢复：" + ex.Message }); }
            if (current == entry.BeforeValue)
                return Update(entry with { Status = OptimizationEntryStatus.Restored, Message = "Windows 当前已是记录的原值，已核对；无需再次更改。" });
            if (current != entry.AfterValue)
                return Update(entry with { Status = OptimizationEntryStatus.Conflict, Message = "Windows 当前值已被其他操作更改，未覆盖该值。请检查当前状态。" });
            entry = Update(entry with { Status = OptimizationEntryStatus.Restoring, Message = "已记录恢复请求，等待执行与核对。" });
            try
            {
                WriteValue(entry.Kind, entry.AfterValue!, entry.BeforeValue!);
                if (ReadValue(entry.Kind) != entry.BeforeValue)
                    return Update(entry with { Status = OptimizationEntryStatus.Uncertain, Message = "恢复结果未能确认，请刷新 Windows 状态后核对。" });
                return Update(entry with { Status = OptimizationEntryStatus.Restored, Message = "已恢复记录的原值，并核对 Windows 当前状态。" });
            }
            catch (JournalPersistenceException) { throw; }
            catch (OptimizationService.StateConflictException ex)
            { return Update(entry with { Status = OptimizationEntryStatus.Conflict, Message = ex.Message }, nativeMayHaveChanged: false); }
            catch (Exception ex) when (Recoverable(ex))
            { return Update(entry with { Status = OptimizationEntryStatus.RestoreFailed, Message = "恢复未完成，请核对当前状态：" + ex.Message }); }
        }
    }

    public OptimizationJournalEntry RecordDnsResult(OptimizationEntryStatus status, string message)
    {
        if (status is not (OptimizationEntryStatus.Completed or OptimizationEntryStatus.Failed or OptimizationEntryStatus.Cancelled)) throw new ArgumentException("DNS 操作状态无效。");
        lock (_gate) return Add(New(OptimizationChangeKind.DnsCache, "刷新 DNS 缓存（不可撤销）", null, null, status, message));
    }

    private string ReadValue(OptimizationChangeKind kind)
    {
        var state = _optimization.GetState();
        return ReadValue(kind, state);
    }
    private static string ReadValue(OptimizationChangeKind kind, OptimizationState state)
    {
        return kind switch
        {
            OptimizationChangeKind.PowerPlan when state.ActivePowerPlanId.HasValue => state.ActivePowerPlanId.Value.ToString("D"),
            OptimizationChangeKind.ClientAreaAnimations when state.ClientAreaAnimationsEnabled.HasValue => state.ClientAreaAnimationsEnabled.Value ? "true" : "false",
            _ => throw new InvalidOperationException(state.StatusMessage ?? "Windows 未能读取当前设置。")
        };
    }
    private static string? Label(OptimizationChangeKind kind, string value, OptimizationState state)
    {
        string? name = kind == OptimizationChangeKind.PowerPlan ? state.PowerPlans.FirstOrDefault(p => p.Id.ToString("D") == value)?.Name : null;
        return name is null ? null : Limit(name, 1024);
    }

    private void WriteValue(OptimizationChangeKind kind, string expected, string value)
    {
        switch (kind)
        {
            case OptimizationChangeKind.PowerPlan: _optimization.SetPowerPlanIfCurrent(Guid.Parse(expected), Guid.Parse(value)); break;
            case OptimizationChangeKind.ClientAreaAnimations: _optimization.SetAnimationsIfCurrent(bool.Parse(expected), bool.Parse(value)); break;
            default: throw new InvalidOperationException("此操作不可恢复。");
        }
    }

    private OptimizationJournalEntry Add(OptimizationJournalEntry entry)
    {
        var next = _entries.Append(entry).TakeLast(MaximumEntries).ToList();
        Persist(next, false);
        _entries = next;
        return entry;
    }
    private OptimizationJournalEntry Update(OptimizationJournalEntry entry, bool nativeMayHaveChanged = true)
    {
        entry = entry with { UpdatedAt = DateTimeOffset.Now, Message = Limit(entry.Message, 2000) };
        var next = _entries.Select(e => e.Id == entry.Id ? entry : e).ToList();
        try { Persist(next, nativeMayHaveChanged); _entries = next; }
        catch (JournalPersistenceException)
        {
            // Keep the uncertain state visible in this process; disk retains write-ahead state.
            _entries = next.Select(e => e.Id == entry.Id ? e with
            {
                Status = nativeMayHaveChanged ? OptimizationEntryStatus.Uncertain : e.Status,
                Message = nativeMayHaveChanged ? "Windows 操作可能已执行，但结果记录保存失败。请核对当前值；磁盘中的原值记录仍保留。"
                    : e.Message + " 本次结果记录保存失败；未执行 Windows 设置写入。"
            } : e).ToList();
            throw;
        }
        return entry;
    }
    private void Persist(IReadOnlyList<OptimizationJournalEntry> next, bool mayHaveChanged)
    {
        try
        {
            if (_persistOverride is not null) { _persistOverride(next); return; }
            string directory = Path.GetDirectoryName(Path.GetFullPath(StoragePath))!;
            Directory.CreateDirectory(directory);
            if (_preserveInvalidFile && File.Exists(StoragePath))
            {
                string preserved = StoragePath + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
                File.Copy(StoragePath, preserved, overwrite: false);
                LastLoadWarning = "无法读取的原始记录已保存在 " + preserved;
                _preserveInvalidFile = false;
            }
            string temporary = Path.Combine(directory, "optimization-journal." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { JsonSerializer.Serialize(stream, next, JsonOptions); stream.Flush(flushToDisk: true); }
                if (File.Exists(StoragePath)) File.Replace(temporary, StoragePath, StoragePath + ".bak", ignoreMetadataErrors: true);
                else File.Move(temporary, StoragePath);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        catch (Exception ex) when (Recoverable(ex))
        { throw new JournalPersistenceException(mayHaveChanged ? "Windows 设置可能已更改，但结果记录保存失败；请核对当前状态。" : "无法保存操作前记录，未执行 Windows 设置更改。", ex); }
    }

    private static OptimizationJournalEntry New(OptimizationChangeKind kind, string title, string? before, string? after, OptimizationEntryStatus status, string message)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), now, now, kind, title, before, after, status, Limit(message, 2000));
    }
    private static bool Valid(OptimizationJournalEntry e) => e.Id != Guid.Empty && Enum.IsDefined(e.Kind) && Enum.IsDefined(e.Status)
        && e.Timestamp != default && e.UpdatedAt != default && !string.IsNullOrWhiteSpace(e.Title) && e.Title.Length <= 200 && e.Message is { Length: <= 2000 }
        && (e.BeforeLabel is null or { Length: <= 1024 }) && (e.AfterLabel is null or { Length: <= 1024 })
        && ValidValue(e.Kind, e.BeforeValue) && ValidValue(e.Kind, e.AfterValue);
    private static bool ValidValue(OptimizationChangeKind kind, string? value) => value is null || kind switch
    {
        OptimizationChangeKind.PowerPlan => Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty,
        OptimizationChangeKind.ClientAreaAnimations => value is "true" or "false",
        _ => false
    };
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    private static bool Recoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private sealed class JournalPersistenceException(string message, Exception inner) : IOException(message, inner);
}

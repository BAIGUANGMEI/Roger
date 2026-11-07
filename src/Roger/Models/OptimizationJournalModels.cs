using System;
using System.Text.Json.Serialization;

namespace Roger.Models;

public enum OptimizationChangeKind { PowerPlan, ClientAreaAnimations, DnsCache }
public enum OptimizationEntryStatus { Pending, Applied, NoChange, Failed, Uncertain, Restoring, Restored, RestoreFailed, Conflict, Completed, Cancelled }

public sealed record OptimizationJournalEntry(
    Guid Id, DateTimeOffset Timestamp, DateTimeOffset UpdatedAt, OptimizationChangeKind Kind,
    string Title, string? BeforeValue, string? AfterValue, OptimizationEntryStatus Status, string Message)
{
    public string? BeforeLabel { get; init; }
    public string? AfterLabel { get; init; }
    [JsonIgnore]
    public string TimestampText => Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");
    [JsonIgnore]
    public string BeforeText => BeforeLabel ?? DisplayValue(BeforeValue);
    [JsonIgnore]
    public string AfterText => AfterLabel ?? DisplayValue(AfterValue);
    [JsonIgnore]
    public string UpdatedAtText => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore]
    public string StatusText => Status switch
    {
        OptimizationEntryStatus.Pending => "待核对", OptimizationEntryStatus.Applied => "已应用",
        OptimizationEntryStatus.NoChange => "无需更改", OptimizationEntryStatus.Failed => "未完成",
        OptimizationEntryStatus.Uncertain => "结果待核对", OptimizationEntryStatus.Restoring => "恢复待核对",
        OptimizationEntryStatus.Restored => "已恢复", OptimizationEntryStatus.RestoreFailed => "恢复未完成",
        OptimizationEntryStatus.Conflict => "当前值冲突", OptimizationEntryStatus.Completed => "已完成",
        OptimizationEntryStatus.Cancelled => "已取消", _ => "未知"
    };
    [JsonIgnore]
    public bool CanRestore => Kind != OptimizationChangeKind.DnsCache && BeforeValue is not null && AfterValue is not null
        && BeforeValue != AfterValue && Status is OptimizationEntryStatus.Pending or OptimizationEntryStatus.Applied
            or OptimizationEntryStatus.Uncertain or OptimizationEntryStatus.Restoring or OptimizationEntryStatus.RestoreFailed or OptimizationEntryStatus.Conflict;
    private string DisplayValue(string? value) => value is null ? "未取得" : Kind == OptimizationChangeKind.ClientAreaAnimations
        ? value == "true" ? "开启" : "关闭" : value;
}

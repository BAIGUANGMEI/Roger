using System;
using System.Collections.Generic;

namespace Roger.Models;

public sealed record PowerPlanInfo(Guid Id, string Name, bool IsActive, bool IsBalanced);
public sealed record OptimizationState(IReadOnlyList<PowerPlanInfo> PowerPlans, Guid? ActivePowerPlanId,
    bool? ClientAreaAnimationsEnabled, string? StatusMessage);
public sealed record OptimizationCommandResult(int ExitCode, string Summary, string Report, TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;
}
public enum OptimizationToolId { StartupApps, StorageSense, GameMode, GraphicsSettings, PowerSleep, OptimizeDrives, DiskCleanup }
public sealed record OptimizationTool(OptimizationToolId Id, string Title, string Description, string ActionLabel);

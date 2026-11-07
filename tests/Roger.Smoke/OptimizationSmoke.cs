using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class OptimizationSmoke
{
    public static async Task RunAsync()
    {
        VerifyStateAndReversibleChanges();
        VerifyToolWhitelist();
        await VerifyCommandsAsync();
        Console.WriteLine("PASS optimization: read-only state, existing-plan validation, reversible changes, fixed tool destinations, bounded hidden DNS command and cancel/failure handling (fake OS boundary)");
    }

    private static void VerifyStateAndReversibleChanges()
    {
        var backend = new FakeBackend();
        var service = NewService(backend);
        OptimizationState state = service.GetState();
        Check(state.PowerPlans.Count == 2 && state.ActivePowerPlanId == backend.Balanced && state.ClientAreaAnimationsEnabled == true, "read existing Windows state");
        Check(state.PowerPlans.Count(p => p.IsBalanced) == 1 && state.PowerPlans.Count(p => p.IsActive) == 1, "mark actual balanced and current plans");
        Check(backend.PowerWrites == 0 && backend.AnimationWrites == 0, "state reads never optimize automatically");
        Check(state.PowerPlans.All(p => p.Id != Guid.Empty), "only enumerated plans, no invented high-performance plan");

        foreach (Guid invalid in new[] { Guid.Empty, Guid.NewGuid() })
            Expect<ArgumentException>(() => service.SetPowerPlan(invalid));
        Check(backend.PowerWrites == 0, "unknown or removed scheme rejected before native write");
        Check(service.SetPowerPlan(backend.Balanced) == backend.Balanced && backend.PowerWrites == 0, "same plan remains a no-op");
        Guid previous = service.SetPowerPlan(backend.Custom);
        Check(previous == backend.Balanced && backend.Active == backend.Custom && backend.PowerWrites == 1, "return exact previous scheme for undo");
        Check(service.SetPowerPlan(previous) == backend.Custom && backend.Active == backend.Balanced, "undo applies previous available plan");
        bool oldAnimations = service.SetAnimations(false);
        Check(oldAnimations && !backend.Animations && backend.AnimationWrites == 1, "return previous animation value");
        Check(!service.SetAnimations(true) && backend.Animations, "animation undo restores its exact previous value");
        int prior = backend.AnimationWrites;
        service.SetAnimations(true);
        Check(backend.AnimationWrites == prior, "same animation value is a no-op");

        backend.IgnorePowerWrites = true;
        Expect<InvalidOperationException>(() => service.SetPowerPlan(backend.Custom));
        backend.IgnoreAnimationWrites = true;
        Expect<InvalidOperationException>(() => service.SetAnimations(false));

        var unavailable = new FakeBackend { FailPowerRead = true, FailAnimationRead = true };
        state = NewService(unavailable).GetState();
        Check(state.ActivePowerPlanId is null && state.ClientAreaAnimationsEnabled is null && state.StatusMessage?.Contains("不可读取") == true, "read failures are explicit, not false states");
        Check(unavailable.PowerWrites == 0 && unavailable.AnimationWrites == 0, "read failure does not trigger a repair");
        Expect<Win32Exception>(() => NewService(unavailable).SetAnimations(false));
        Check(unavailable.AnimationWrites == 0, "failed prior-state read blocks a write that could not be undone");
    }

    private static void VerifyToolWhitelist()
    {
        var expected = new Dictionary<OptimizationToolId, string>
        {
            [OptimizationToolId.StartupApps] = "ms-settings:startupapps",
            [OptimizationToolId.StorageSense] = "ms-settings:storagesense",
            [OptimizationToolId.GameMode] = "ms-settings:gaming-gamemode",
            [OptimizationToolId.GraphicsSettings] = "ms-settings:display-advancedgraphics",
            [OptimizationToolId.PowerSleep] = "ms-settings:powersleep",
            [OptimizationToolId.OptimizeDrives] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dfrgui.exe"),
            [OptimizationToolId.DiskCleanup] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe")
        };
        Check(OptimizationService.Tools.Count == expected.Count && OptimizationService.Tools.Select(t => t.Id).Distinct().Count() == expected.Count, "tool list matches fixed destination allowlist");
        int launches = 0;
        var service = new OptimizationService(new FakeBackend(), (_, _) => throw new InvalidOperationException("Must not run network tool"), start =>
        {
            launches++;
            Check(expected.Values.Contains(start.FileName) && start.ArgumentList.Count == 0 && start.Arguments.Length == 0, "tool launch uses only a fixed destination with no cleanup switches");
            Check(start.UseShellExecute && start.WindowStyle == ProcessWindowStyle.Normal, "Windows settings remain interactive");
        });
        _ = service.GetState();
        Check(launches == 0, "constructing/reading toolbox never opens a tool");
        foreach (OptimizationToolId id in expected.Keys) service.OpenTool(id);
        Check(launches == expected.Count, "dispatch all seven known tools to fake launch boundary");
        Expect<ArgumentOutOfRangeException>(() => service.OpenTool((OptimizationToolId)999));
        Check(launches == expected.Count, "invalid tool never reaches process boundary");
    }

    private static async Task VerifyCommandsAsync()
    {
        var start = OptimizationService.CreateFlushDnsStartInfo();
        Check(Path.IsPathFullyQualified(start.FileName) && Path.GetFileName(start.FileName).Equals("ipconfig.exe", StringComparison.OrdinalIgnoreCase), "fixed Windows DNS executable");
        Check(start.ArgumentList.SequenceEqual(new[] { "/flushdns" }) && start.Arguments.Length == 0, "only resolver-cache flush; never IP release/renew or network reset");
        Check(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden && start.RedirectStandardOutput && start.RedirectStandardError && start.RedirectStandardInput, "DNS process has hidden, captured I/O");
        Check(start.StandardOutputEncoding is not null && start.StandardErrorEncoding == start.StandardOutputEncoding, "Chinese Windows command encoding provided");
        int runs = 0;
        var service = new OptimizationService(new FakeBackend(), (info, token) =>
        {
            runs++; token.ThrowIfCancellationRequested();
            return Task.FromResult(new OptimizationService.CommandOutput(0, "Windows IP 配置\r\n成功刷新 DNS 解析缓存。", ""));
        });
        _ = service.GetState();
        Check(runs == 0, "state read does not flush cache");
        var result = await service.FlushDnsAsync();
        Check(result.Succeeded && result.Report.Contains("成功刷新") && !result.Report.Contains('\r') && result.Elapsed >= TimeSpan.Zero, "command success preserves localized report");
        var failed = new OptimizationService(new FakeBackend(), (_, _) => Task.FromResult(new OptimizationService.CommandOutput(5, "Windows IP 配置", "需要管理员权限")));
        result = await failed.FlushDnsAsync();
        Check(!result.Succeeded && result.ExitCode == 5 && result.Report.Contains("需要管理员权限") && !result.Summary.Contains("已清空"), "nonzero exit is not a success");

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => service.FlushDnsAsync(alreadyCancelled.Token));
        Check(runs == 1, "pre-cancel request never starts DNS command");

        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new OptimizationService(new FakeBackend(), async (_, token) =>
        {
            begun.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
            return new OptimizationService.CommandOutput(0, "", "");
        });
        using var cancel = new CancellationTokenSource();
        Task<OptimizationCommandResult> operation = pending.FlushDnsAsync(cancel.Token);
        await begun.Task;
        await ExpectAsync<InvalidOperationException>(() => pending.FlushDnsAsync());
        cancel.Cancel();
        await ExpectAsync<OperationCanceledException>(() => operation);

        var timeout = new OptimizationService(new FakeBackend(), async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token); return new OptimizationService.CommandOutput(0, "", "");
        }, commandTimeout: TimeSpan.FromMilliseconds(30));
        await ExpectAsync<TimeoutException>(() => timeout.FlushDnsAsync());
    }

    private static OptimizationService NewService(FakeBackend backend) => new(backend,
        (_, _) => throw new InvalidOperationException("No command should execute in a state/change test"));
    private sealed class FakeBackend : OptimizationService.IOptimizationBackend
    {
        public Guid Balanced { get; } = OptimizationService.BalancedSchemeId;
        public Guid Custom { get; } = new("e375d8e5-48ba-45eb-b378-7130098aff3d");
        public Guid Active { get; private set; } = OptimizationService.BalancedSchemeId;
        public bool Animations { get; private set; } = true;
        public int PowerWrites { get; private set; }
        public int AnimationWrites { get; private set; }
        public bool IgnorePowerWrites, IgnoreAnimationWrites, FailPowerRead, FailAnimationRead;
        public IReadOnlyList<OptimizationService.NativePowerPlan> EnumeratePowerPlans() => new[]
        { new OptimizationService.NativePowerPlan(Balanced, "平衡"), new OptimizationService.NativePowerPlan(Custom, "设备已有方案") };
        public Guid GetActivePowerPlan() => FailPowerRead ? throw new Win32Exception(5) : Active;
        public void SetActivePowerPlan(Guid id) { PowerWrites++; if (!IgnorePowerWrites) Active = id; }
        public bool GetClientAreaAnimations() => FailAnimationRead ? throw new Win32Exception(5) : Animations;
        public void SetClientAreaAnimations(bool enabled) { AnimationWrites++; if (!IgnoreAnimationWrites) Animations = enabled; }
    }

    private static void Check(bool condition, string detail) { if (!condition) throw new InvalidOperationException("Optimization smoke: " + detail); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}

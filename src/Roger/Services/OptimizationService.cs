using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;

namespace Roger.Services;

/// <summary>
/// Reads state by default; changes occur only in explicit Set/Flush/Open calls.
/// Uses documented Power Management, SystemParametersInfo, and Windows Settings APIs.
/// </summary>
public sealed class OptimizationService
{
    public static readonly Guid BalancedSchemeId = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private readonly IOptimizationBackend _backend;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<CommandOutput>> _runCommand;
    private readonly Action<ProcessStartInfo> _openTool;
    private readonly TimeSpan _commandTimeout;
    private readonly object _gate = new();
    private int _flushing;

    public static IReadOnlyList<OptimizationTool> Tools { get; } = Array.AsReadOnly(new[]
    {
        new OptimizationTool(OptimizationToolId.StartupApps, "启动应用", "打开 Windows 启动应用设置，由你选择登录时需要启动的应用。", "打开设置"),
        new OptimizationTool(OptimizationToolId.StorageSense, "存储与存储感知", "查看空间占用并配置 Windows 存储感知；此入口不会自动删除文件。", "打开设置"),
        new OptimizationTool(OptimizationToolId.GameMode, "游戏模式", "打开 Windows 游戏模式设置，按使用场景选择。", "打开设置"),
        new OptimizationTool(OptimizationToolId.GraphicsSettings, "应用图形首选项", "由 Windows 为应用选择节能或高性能 GPU；设备不支持时该页面可能不可用。", "打开设置"),
        new OptimizationTool(OptimizationToolId.PowerSleep, "电源与睡眠", "打开 Windows 电源和睡眠设置，调整屏幕关闭及睡眠时间。", "打开设置"),
        new OptimizationTool(OptimizationToolId.OptimizeDrives, "优化驱动器", "打开 Windows 优化驱动器工具，由系统按 SSD / HDD 选择合适的优化方式；不会在此自动执行优化。", "打开系统工具"),
        new OptimizationTool(OptimizationToolId.DiskCleanup, "磁盘清理", "打开 Windows 磁盘清理窗口，由你查看并选择清理项目。", "打开系统工具")
    });

    public OptimizationService() : this(new NativeOptimizationBackend(), RunCommandAsync, StartTool) { }

    internal OptimizationService(IOptimizationBackend backend,
        Func<ProcessStartInfo, CancellationToken, Task<CommandOutput>> runCommand,
        Action<ProcessStartInfo>? openTool = null, TimeSpan? commandTimeout = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _runCommand = runCommand ?? throw new ArgumentNullException(nameof(runCommand));
        _openTool = openTool ?? StartTool;
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(20);
        if (_commandTimeout <= TimeSpan.Zero || _commandTimeout > TimeSpan.FromSeconds(20))
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
    }

    public OptimizationState GetState()
    {
        lock (_gate)
        {
            var errors = new List<string>();
            Guid? active = null;
            bool? animations = null;
            IReadOnlyList<NativePowerPlan> nativePlans = Array.Empty<NativePowerPlan>();
            try { active = _backend.GetActivePowerPlan(); }
            catch (Exception ex) when (IsReadFailure(ex)) { errors.Add("当前电源计划不可读取：" + ex.Message); }
            try { nativePlans = _backend.EnumeratePowerPlans(); }
            catch (Exception ex) when (IsReadFailure(ex)) { errors.Add("电源计划列表不可读取：" + ex.Message); }
            if (nativePlans.Count == 0 && !errors.Any(e => e.StartsWith("电源计划列表", StringComparison.Ordinal)))
                errors.Add("Windows 未返回可用的电源计划；Roger 不会创建或解锁隐藏计划。");
            try { animations = _backend.GetClientAreaAnimations(); }
            catch (Exception ex) when (IsReadFailure(ex)) { errors.Add("客户端动画状态不可读取：" + ex.Message); }
            var plans = nativePlans.Select(p => new PowerPlanInfo(p.Id, p.Name, p.Id == active, p.Id == BalancedSchemeId)).ToArray();
            return new OptimizationState(plans, active, animations, errors.Count == 0 ? null : string.Join("；", errors));
        }
    }

    public IReadOnlyList<PowerPlanInfo> EnumeratePowerPlans()
    {
        lock (_gate)
        {
            Guid active = _backend.GetActivePowerPlan();
            return _backend.EnumeratePowerPlans().Select(p => new PowerPlanInfo(p.Id, p.Name, p.Id == active, p.Id == BalancedSchemeId)).ToArray();
        }
    }

    /// <summary>Returns the immediately previous scheme, which the caller may offer to restore.</summary>
    public Guid SetPowerPlan(Guid id) => SetPowerPlan(id, null);

    internal Guid SetPowerPlanIfCurrent(Guid expected, Guid target) => SetPowerPlan(target, expected);

    private Guid SetPowerPlan(Guid id, Guid? expected)
    {
        lock (_gate)
        {
            if (id == Guid.Empty || !_backend.EnumeratePowerPlans().Any(p => p.Id == id))
                throw new ArgumentException("所选电源计划已不存在，请刷新列表后重新选择。", nameof(id));
            Guid previous = _backend.GetActivePowerPlan();
            if (expected.HasValue && previous != expected.Value)
                throw new StateConflictException("电源计划在操作前已发生变化，未覆盖当前计划。");
            if (previous != id)
            {
                _backend.SetActivePowerPlan(id);
                if (_backend.GetActivePowerPlan() != id)
                    throw new InvalidOperationException("Windows 未保留所选电源计划，可能受设备或组织策略限制，请刷新状态。");
            }
            return previous;
        }
    }

    /// <summary>Only the supported client-area animation flag is changed; returns its prior state.</summary>
    public bool SetAnimations(bool enabled) => SetAnimations(enabled, null);

    internal bool SetAnimationsIfCurrent(bool expected, bool target) => SetAnimations(target, expected);

    private bool SetAnimations(bool enabled, bool? expected)
    {
        lock (_gate)
        {
            bool previous = _backend.GetClientAreaAnimations();
            if (expected.HasValue && previous != expected.Value)
                throw new StateConflictException("客户端动画在操作前已发生变化，未覆盖当前设置。");
            if (previous != enabled)
            {
                _backend.SetClientAreaAnimations(enabled);
                if (_backend.GetClientAreaAnimations() != enabled)
                    throw new InvalidOperationException("Windows 未保留客户端动画设置，请刷新状态。其他动画选项未被更改。");
            }
            return previous;
        }
    }

    public async Task<OptimizationCommandResult> FlushDnsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
            throw new InvalidOperationException("DNS 缓存刷新正在进行，请稍后再试。");
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_commandTimeout);
        try
        {
            CommandOutput result = await _runCommand(CreateFlushDnsStartInfo(), timeout.Token).ConfigureAwait(false);
            string report = Normalize(result.StandardOutput);
            if (!string.IsNullOrWhiteSpace(result.StandardError)) report += "\n" + Normalize(result.StandardError);
            string summary = result.ExitCode == 0 ? "Windows DNS 解析缓存已清空；之后的名称解析会重新建立缓存。" : "DNS 缓存未能清空，请查看输出和权限状态。";
            return new OptimizationCommandResult(result.ExitCode, summary, report.Trim(), watch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("DNS 缓存刷新超时，已停止本次命令。"); }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    public void OpenTool(OptimizationToolId id) => _openTool(CreateToolStartInfo(id));

    internal static ProcessStartInfo CreateToolStartInfo(OptimizationToolId id)
    {
        string destination = id switch
        {
            OptimizationToolId.StartupApps => "ms-settings:startupapps",
            OptimizationToolId.StorageSense => "ms-settings:storagesense",
            OptimizationToolId.GameMode => "ms-settings:gaming-gamemode",
            OptimizationToolId.GraphicsSettings => "ms-settings:display-advancedgraphics",
            OptimizationToolId.PowerSleep => "ms-settings:powersleep",
            OptimizationToolId.OptimizeDrives => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dfrgui.exe"),
            OptimizationToolId.DiskCleanup => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(id), "不支持的系统工具。")
        };
        return new ProcessStartInfo(destination) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
    }

    internal static ProcessStartInfo CreateFlushDnsStartInfo()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(checked((int)GetOEMCP()));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding
        };
        start.ArgumentList.Add("/flushdns");
        return start;
    }

    private static void StartTool(ProcessStartInfo start)
    {
        if (Path.IsPathFullyQualified(start.FileName) && !File.Exists(start.FileName))
            throw new FileNotFoundException("这台 Windows 没有提供此工具，可改用存储设置。", start.FileName);
        using Process? process = Process.Start(start);
    }

    private static async Task<CommandOutput> RunCommandAsync(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动 Windows 网络缓存工具。");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return new CommandOutput(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "");
    private static bool IsReadFailure(Exception ex) => ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException;
    internal sealed record CommandOutput(int ExitCode, string StandardOutput, string StandardError);
    internal sealed class StateConflictException(string message) : InvalidOperationException(message);
    internal sealed record NativePowerPlan(Guid Id, string Name);
    internal interface IOptimizationBackend
    {
        IReadOnlyList<NativePowerPlan> EnumeratePowerPlans();
        Guid GetActivePowerPlan();
        void SetActivePowerPlan(Guid id);
        bool GetClientAreaAnimations();
        void SetClientAreaAnimations(bool enabled);
    }

    private sealed class NativeOptimizationBackend : IOptimizationBackend
    {
        public IReadOnlyList<NativePowerPlan> EnumeratePowerPlans()
        {
            var result = new List<NativePowerPlan>();
            for (uint index = 0; index < 1024; index++)
            {
                var buffer = new byte[16]; uint size = 16;
                uint code = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, buffer, ref size);
                if (code == 259) return result; // ERROR_NO_MORE_ITEMS.
                ThrowError(code, "读取电源计划列表");
                if (size != 16) throw new InvalidDataException("Windows 返回了无效的电源计划标识。");
                var id = new Guid(buffer);
                result.Add(new NativePowerPlan(id, ReadName(id)));
            }
            throw new InvalidDataException("电源计划数量异常，无法完成列表读取。");
        }
        public Guid GetActivePowerPlan()
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                ThrowError(PowerGetActiveScheme(IntPtr.Zero, out buffer), "读取当前电源计划");
                if (buffer == IntPtr.Zero) throw new InvalidDataException("Windows 未返回当前电源计划标识。");
                return Marshal.PtrToStructure<Guid>(buffer);
            }
            finally { if (buffer != IntPtr.Zero) LocalFree(buffer); }
        }
        public void SetActivePowerPlan(Guid id) => ThrowError(PowerSetActiveScheme(IntPtr.Zero, ref id), "应用电源计划");
        public bool GetClientAreaAnimations()
        {
            if (!SystemParametersInfoGet(0x1042, 0, out int enabled, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取客户端动画状态。");
            return enabled != 0;
        }
        public void SetClientAreaAnimations(bool enabled)
        {
            // SPI_SETCLIENTAREAANIMATION takes the BOOL value itself in pvParam, not a pointer.
            if (!SystemParametersInfoSet(0x1043, 0, enabled ? new IntPtr(1) : IntPtr.Zero, 0x1 | 0x2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法应用客户端动画设置。");
        }
        private static string ReadName(Guid id)
        {
            uint size = 0;
            uint code = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (code != 0 && code != 234 && code != 122 || size < 2 || size > 1024 * 1024) return "电源计划 " + id;
            var data = new byte[size];
            code = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, data, ref size);
            if (code != 0 || size > data.Length) return "电源计划 " + id;
            string name = Encoding.Unicode.GetString(data, 0, checked((int)size)).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(name) ? "电源计划 " + id : name;
        }
        private static void ThrowError(uint code, string action)
        { if (code != 0) throw new Win32Exception(unchecked((int)code), action + "失败（Windows 代码 " + code + "）。"); }
    }

    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint access, uint index, [Out] byte[] buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, [Out] byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr buffer);
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoGet(uint action, uint parameter, out int value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoSet(uint action, uint parameter, IntPtr value, uint flags);
}

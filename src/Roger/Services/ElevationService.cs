using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;

namespace Roger.Services;

/// <summary>Administrator restart support. Roger.exe requests elevation through its application manifest.</summary>
public static class ElevationService
{
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>After a successful launch, the current application must finish its normal shutdown.</summary>
    public static Process? StartElevatedInstance()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("无法识别 Roger 的启动位置。");
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从 Roger.exe 启动后使用管理员模式。");
        return Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "--wait-for-exit " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            WorkingDirectory = AppContext.BaseDirectory
        });
    }

    /// <summary>Call at application startup before acquiring the single-instance mutex.</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        int index = Array.IndexOf(args, "--wait-for-exit");
        if (index < 0) return;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || processId <= 0 || processId == Environment.ProcessId)
            throw new ArgumentException("管理员重启参数无效。");

        try
        {
            using var previous = Process.GetProcessById(processId);
            if (previous.HasExited) return;
            string? previousPath;
            try { previousPath = previous.MainModule?.FileName; }
            catch (InvalidOperationException) when (previous.HasExited) { return; }
            if (!string.Equals(previousPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法确认正在退出的 Roger 实例。");
            if (!previous.WaitForExit(45_000))
                throw new TimeoutException("旧 Roger 实例尚未完成退出，请稍后重试。");
        }
        catch (ArgumentException) { /* The old instance already exited. */ }
    }
}

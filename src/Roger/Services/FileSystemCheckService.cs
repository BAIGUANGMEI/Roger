using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;

namespace Roger.Services;

/// <summary>
/// Runs the Windows file-system checker in read-only mode and captures its report without a console.
/// No repair, dismount, scheduling or surface-scan switches are passed.
/// Exit-code reference: https://learn.microsoft.com/windows-server/administration/windows-commands/chkdsk
/// </summary>
public sealed class FileSystemCheckService
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(10);
    private readonly Func<bool> _isAdministrator;
    private readonly Func<string, IProgress<string>?, CancellationToken, Task<CommandResult>> _run;
    private readonly Action<string> _validateDrive;
    private readonly TimeSpan _timeout;
    private int _running;

    public FileSystemCheckService() : this(() => IsAdministrator, RunCommandAsync, ValidateDrive, MaximumDuration) { }

    // Offline tests inject the process boundary; production always uses the hidden Windows executable below.
    internal FileSystemCheckService(
        Func<bool> isAdministrator,
        Func<string, IProgress<string>?, CancellationToken, Task<CommandResult>> run,
        Action<string>? validateDrive = null,
        TimeSpan? timeout = null)
    {
        _isAdministrator = isAdministrator;
        _run = run;
        _validateDrive = validateDrive ?? (_ => { });
        _timeout = timeout ?? MaximumDuration;
        if (_timeout <= TimeSpan.Zero || _timeout > MaximumDuration) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Accepts a local drive or a path on that drive and returns exactly one drive letter plus a colon.</summary>
    public static string NormalizeVolume(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择要检查的本地磁盘。", nameof(path));
        path = path.Trim();
        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':'
            || (path.Length > 2 && path[2] != '\\' && path[2] != '/'))
            throw new ArgumentException("文件系统检查只支持本地盘符，例如 C:\\。不支持网络共享和相对路径。", nameof(path));
        // Validate the complete input as a path, but never append it to the process arguments.
        _ = Path.GetFullPath(path.Length == 2 ? path + "\\" : path);
        return char.ToUpperInvariant(path[0]) + ":";
    }

    public async Task<FileSystemCheckResult> CheckAsync(string path,
        IProgress<FileSystemCheckProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string volume = NormalizeVolume(path);
        if (!_isAdministrator())
            throw new UnauthorizedAccessException("文件系统检查需要管理员权限。请使用 Roger 中的“以管理员身份重新启动”后再试。");
        _validateDrive(volume);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("已有文件系统检查正在进行，请等待完成或先取消。");

        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            progress?.Report(new FileSystemCheckProgress($"正在对 {volume} 执行只读文件系统检查…\n", watch.Elapsed));
            var result = await _run(volume, new InlineProgress<string>(chunk =>
                progress?.Report(new FileSystemCheckProgress(NormalizeOutput(chunk), watch.Elapsed))), timeout.Token).ConfigureAwait(false);
            watch.Stop();
            string report = NormalizeOutput(result.StandardOutput);
            if (!string.IsNullOrWhiteSpace(result.StandardError))
                report += (report.EndsWith('\n') ? "" : "\n") + "[错误输出]\n" + NormalizeOutput(result.StandardError);
            report = report.Trim();
            var (state, summary) = DescribeExitCode(result.ExitCode, !string.IsNullOrWhiteSpace(report), !string.IsNullOrWhiteSpace(result.StandardError));
            if (report.Length == 0) report = "CHKDSK 没有返回可显示的报告。";
            return new FileSystemCheckResult(volume, result.ExitCode, state, summary, report, watch.Elapsed, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"检查超过 {_timeout.TotalMinutes:0.#} 分钟，已停止本次只读检查。当前输出为未完成报告。");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    internal static (FileSystemCheckState State, string Summary) DescribeExitCode(int exitCode, bool hasReport, bool hasStandardError = false)
    {
        if (!hasReport) return (FileSystemCheckState.Incomplete, "检查未返回报告，无法判断文件系统状态。");
        return exitCode switch
        {
            0 when !hasStandardError => (FileSystemCheckState.Healthy, "检查完成，Windows 未发现文件系统错误。"),
            0 => (FileSystemCheckState.AttentionRequired, "检查结束，但工具返回了错误输出，请查看完整报告。"),
            1 => (FileSystemCheckState.AttentionRequired, "工具报告检测到问题，请查看完整报告。本次检查未请求修复。"),
            2 => (FileSystemCheckState.AttentionRequired, "工具返回清理或待清理状态，请查看完整报告。本次检查未请求修复。"),
            3 => (FileSystemCheckState.Incomplete, "未能完成检查，或存在未修复的问题，请查看完整报告。"),
            _ => (FileSystemCheckState.Incomplete, $"检查进程返回代码 {exitCode}，无法确认文件系统状态，请查看完整报告。")
        };
    }

    internal static string NormalizeOutput(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "");

    internal static ProcessStartInfo CreateStartInfo(string volume)
    {
        // Defense in depth: even internal callers can pass only a drive letter, never a shell command or switches.
        if (volume.Length != 2 || !char.IsAsciiLetter(volume[0]) || volume[1] != ':')
            throw new ArgumentException("无效的本地盘符。", nameof(volume));
        var encoding = OutputEncoding();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "chkdsk.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };
        start.ArgumentList.Add(volume);
        return start;
    }

    private static Encoding OutputEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(checked((int)GetOEMCP())); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    private static void ValidateDrive(string volume)
    {
        var drive = new DriveInfo(volume + "\\");
        if (drive.DriveType == DriveType.Network) throw new ArgumentException("不能对网络映射磁盘执行文件系统检查。");
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new ArgumentException("请选择可用的本地磁盘或可移动磁盘。");
        if (!drive.IsReady) throw new IOException("磁盘未就绪，请连接磁盘后重试。");
    }

    private static async Task<CommandResult> RunCommandAsync(string volume, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(volume) };
        if (!process.Start()) throw new IOException("无法启动 Windows 文件系统检查。");
        // Read-only CHKDSK requires no input. EOF prevents any unexpected prompt from waiting invisibly.
        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputGate = new object();
        var pending = new StringBuilder();
        var lastReport = Stopwatch.StartNew();

        void Append(StringBuilder target, string chunk)
        {
            lock (outputGate)
            {
                target.Append(chunk);
                pending.Append(chunk);
                if (lastReport.ElapsedMilliseconds >= 100 || pending.Length >= 16 * 1024)
                {
                    progress?.Report(pending.ToString());
                    pending.Clear();
                    lastReport.Restart();
                }
            }
        }

        async Task DrainAsync(StreamReader reader, StringBuilder target)
        {
            var buffer = new char[2048];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                Append(target, new string(buffer, 0, read));
        }

        Task readOutput = DrainAsync(process.StandardOutput, stdout);
        Task readError = DrainAsync(process.StandardError, stderr);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(readOutput, readError).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await Task.WhenAll(readOutput, readError).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException or IOException)
            {
                throw new IOException("已请求停止检查，但 Windows 未确认检查进程已完全退出。请稍后再试。", ex);
            }
            throw;
        }
        finally
        {
            lock (outputGate)
            {
                if (pending.Length > 0) progress?.Report(pending.ToString());
            }
        }
    }

    internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
}

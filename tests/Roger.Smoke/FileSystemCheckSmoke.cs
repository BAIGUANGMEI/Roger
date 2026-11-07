using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

internal static class FileSystemCheckSmoke
{
    public static async Task RunAsync()
    {
        VerifyInvocation();
        await VerifyReportAndStatusAsync();
        await VerifyCancellationAndConcurrencyAsync();
        Console.WriteLine("PASS integrated filesystem check: hidden read-only invocation, input/elevation gates, localized report, exit-code semantics, cancel, timeout and concurrent-run guard");
    }

    private static void VerifyInvocation()
    {
        Check(FileSystemCheckService.NormalizeVolume("c:") == "C:", "bare local drive normalization");
        Check(FileSystemCheckService.NormalizeVolume(" d:\\目录\\文件.txt ") == "D:", "local path selects whole volume");
        foreach (string path in new[] { "", " ", "folder", "C:relative", "C: /f", "\\\\server\\share", "\\\\?\\C:\\", "file:///C:/", "C:\0bad" })
            Expect<ArgumentException>(() => FileSystemCheckService.NormalizeVolume(path));

        var start = FileSystemCheckService.CreateStartInfo("C:");
        Check(Path.IsPathFullyQualified(start.FileName) && Path.GetFileName(start.FileName).Equals("chkdsk.exe", StringComparison.OrdinalIgnoreCase), "fixed Windows executable, no command shell");
        Check(start.ArgumentList.SequenceEqual(new[] { "C:" }) && start.Arguments.Length == 0, "exactly one argument, no repair flags");
        Check(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden, "no terminal or shell window");
        Check(start.RedirectStandardOutput && start.RedirectStandardError && start.RedirectStandardInput, "all tool I/O stays in application");
        Check(start.StandardOutputEncoding is not null && start.StandardErrorEncoding == start.StandardOutputEncoding, "localized Windows output encoding supplied");
        foreach (string argument in new[] { "C: /f", "C:&calc", "\\\\server\\share", "C:\\", "" })
            Expect<ArgumentException>(() => FileSystemCheckService.CreateStartInfo(argument));
    }

    private static async Task VerifyReportAndStatusAsync()
    {
        int executions = 0;
        var denied = new FileSystemCheckService(() => false, (_, _, _) =>
        {
            executions++;
            return Task.FromResult(new FileSystemCheckService.CommandResult(0, "report", ""));
        });
        await ExpectAsync<UnauthorizedAccessException>(() => denied.CheckAsync("C:"));
        Check(executions == 0, "non-admin request never starts a process");

        var chunks = new List<FileSystemCheckProgress>();
        var resultData = new FileSystemCheckService.CommandResult(0, "阶段 1\r\n检查完成\r未发现问题\0", "");
        var service = new FileSystemCheckService(() => true, (volume, progress, token) =>
        {
            executions++;
            Check(volume == "D:", "only normalized volume crosses process boundary");
            progress?.Report("阶段 1\r\n");
            return Task.FromResult(resultData);
        });
        var result = await service.CheckAsync("D:\\some-folder", new ImmediateProgress<FileSystemCheckProgress>(chunks.Add));
        Check(result.State == FileSystemCheckState.Healthy && result.ExitCode == 0 && result.Volume == "D:", "successful report status");
        Check(result.Report == "阶段 1\n检查完成\n未发现问题" && chunks.Any(p => p.OutputChunk == "阶段 1\n"), "Chinese report and line breaks preserved");
        Check(result.Elapsed >= TimeSpan.Zero && result.CompletedAt > DateTimeOffset.Now.AddMinutes(-1), "completion metadata");

        foreach (int code in new[] { 1, 2, 3, 8, -1 })
        {
            resultData = new FileSystemCheckService.CommandResult(code, "工具报告", "详细诊断");
            result = await service.CheckAsync("D:");
            Check(result.State != FileSystemCheckState.Healthy && result.Report.Contains("详细诊断"), "nonzero return is never silently healthy and stderr is retained");
            Check(!result.Summary.Contains("已修复"), "read-only request never claims a repair");
        }
        resultData = new FileSystemCheckService.CommandResult(0, "", "");
        result = await service.CheckAsync("D:");
        Check(result.State == FileSystemCheckState.Incomplete, "empty output cannot verify healthy state");
        resultData = new FileSystemCheckService.CommandResult(0, "报告", "error");
        Check((await service.CheckAsync("D:")).State == FileSystemCheckState.AttentionRequired, "stderr prevents an unconditional green result");

        var driveRejected = new FileSystemCheckService(() => true, (_, _, _) => throw new Exception("must not execute"), _ => throw new ArgumentException("network drive"));
        await ExpectAsync<ArgumentException>(() => driveRejected.CheckAsync("Z:"));
    }

    private static async Task VerifyCancellationAndConcurrencyAsync()
    {
        int executions = 0;
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileSystemCheckService(() => true, async (_, _, token) =>
        {
            executions++;
            began.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new FileSystemCheckService.CommandResult(0, "unreachable", "");
        });
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => service.CheckAsync("C:", cancellationToken: canceled.Token));
            Check(executions == 0, "pre-cancel does not start a check");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            Task<FileSystemCheckResult> pending = service.CheckAsync("C:", cancellationToken: cancellation.Token);
            await began.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ExpectAsync<InvalidOperationException>(() => service.CheckAsync("C:"));
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending);
        }
        var timeout = new FileSystemCheckService(() => true, async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new FileSystemCheckService.CommandResult(0, "unreachable", "");
        }, timeout: TimeSpan.FromMilliseconds(40));
        await ExpectAsync<TimeoutException>(() => timeout.CheckAsync("C:"));
        await ExpectAsync<TimeoutException>(() => timeout.CheckAsync("C:"));
        Check(executions == 1, "concurrency and cancellation did not start extra processes");
    }

    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Filesystem check smoke failed: " + name); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}

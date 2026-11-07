using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

internal static class DiskNetworkSmoke
{
    public static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "Roger-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await VerifyScanAsync(path);
            await VerifySpeedTestAsync();
            Console.WriteLine("PASS disk accounting, largest100, cancellation, skipped links/permissions, bounded speed-test traffic and HTTP failure");
        }
        finally { Directory.Delete(path, true); }
    }

    private static async Task VerifyScanAsync(string root)
    {
        var folderA = Directory.CreateDirectory(Path.Combine(root, "A"));
        var folderB = Directory.CreateDirectory(Path.Combine(root, "B"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        await File.WriteAllBytesAsync(Path.Combine(root, "root.bin"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(folderA.FullName, "large.bin"), new byte[4096]);
        long expectedTotal = 4196;
        for (var i = 1; i <= 110; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(folderB.FullName, $"{i:D3}.bin"), new byte[i]);
            expectedTotal += i;
        }

        var analyzer = new DiskAnalyzer();
        var result = await analyzer.ScanAsync(root);
        Check(result.TotalBytes == expectedTotal, "logical sizes must equal actual lengths");
        Check(result.Children.Sum(x => x.Bytes) == result.TotalBytes, "root file group and child directories must sum to scan total");
        Check(result.FileCount == 112 && result.DirectoryCount == 4, "file/directory counts");
        Check(result.LargestFiles.Count == 100 && result.LargestFiles[0].Bytes == 4096, "top100 retention");
        Check(result.Children.Single(x => x.Name == "[根目录文件]").Bytes == 100, "root files grouped");
        Check(result.Children.Single(x => x.Name == "empty").FileCount == 0, "empty directories retained");
        Check(result.SkippedEntries == 0, "normal scan must not silently skip entries");
        Check(new FileInfo(Path.Combine(folderA.FullName, "large.bin")).Length == 4096, "scan must preserve source");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => analyzer.ScanAsync(root, cancellationToken: cancelled.Token));
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var progress = new ImmediateProgress<DiskScanProgress>(_ => cancelled.Cancel());
            await ExpectAsync<OperationCanceledException>(() => analyzer.ScanAsync(root, progress, cancelled.Token));
        }
        await ExpectAsync<DirectoryNotFoundException>(() => analyzer.ScanAsync(Path.Combine(root, "missing")));

        // Symlink tests are conditional because Windows requires Developer Mode or the symlink privilege.
        var link = Path.Combine(root, "loop-link");
        var canLink = false;
        try { Directory.CreateSymbolicLink(link, root); canLink = true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { Console.WriteLine("SKIP symlink creation privilege unavailable"); }
        if (canLink)
        {
            try
            {
                result = await analyzer.ScanAsync(root);
                Check(result.TotalBytes == expectedTotal && result.SkippedEntries == 1, "symlink loop must be skipped without double count");
                await ExpectAsync<IOException>(() => analyzer.ScanAsync(link));
            }
            finally { Directory.Delete(link); }
        }

        if (OperatingSystem.IsWindows())
        {
            var denied = Directory.CreateDirectory(Path.Combine(root, "denied"));
            await File.WriteAllBytesAsync(Path.Combine(denied.FullName, "hidden.bin"), new byte[17]);
            var original = denied.GetAccessControl(AccessControlSections.Access);
            var changed = false;
            try
            {
                var restricted = denied.GetAccessControl(AccessControlSections.Access);
                restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.ListDirectory, AccessControlType.Deny));
                denied.SetAccessControl(restricted);
                changed = true;
                result = await analyzer.ScanAsync(root);
                Check(result.TotalBytes == expectedTotal, "denied folder contents must not be counted");
                Check(result.SkippedEntries == 1 && result.Errors.Count == 1, "access denied must be visible");
            }
            finally
            {
                if (changed)
                {
                    // A freshly read DirectorySecurity has no dirty flags; reapplying its SDDL marks it modified.
                    var restore = new DirectorySecurity();
                    restore.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                        AccessControlSections.Access);
                    denied.SetAccessControl(restore);
                }
            }
        }
    }

    private static async Task VerifySpeedTestAsync()
    {
        var handler = new TestHandler();
        using (var service = new NetworkSpeedTestService(handler))
        {
            var progressValues = new List<SpeedTestProgress>();
            var result = await service.RunAsync(new ImmediateProgress<SpeedTestProgress>(progressValues.Add));
            Check(result.DownloadBytes == NetworkSpeedTestService.MaximumDownloadBytes, "download byte cap");
            Check(result.UploadBytes == NetworkSpeedTestService.MaximumUploadBytes, "upload byte cap");
            Check(handler.UploadedBytes == result.UploadBytes, "confirmed uploaded bytes");
            Check(result.DownloadMbps > 0 && result.UploadMbps > 0, "throughput calculation");
            Check(progressValues[^1].Percent == 100, "completion progress");
            Check(progressValues.All(x => x.Percent is >= 0 and <= 100), "progress range");
        }
        using (var service = new NetworkSpeedTestService(new TestHandler { Status = HttpStatusCode.Forbidden }))
            await ExpectAsync<HttpRequestException>(() => service.RunAsync());
        using (var cancelled = new CancellationTokenSource())
        using (var service = new NetworkSpeedTestService(new TestHandler()))
        {
            cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => service.RunAsync(cancellationToken: cancelled.Token));
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Smoke assertion failed: " + message);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public long UploadedBytes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Post)
                UploadedBytes += (await request.Content!.ReadAsByteArrayAsync(cancellationToken)).LongLength;
            var bytes = 0;
            if (request.Method == HttpMethod.Get)
            {
                var query = request.RequestUri!.Query;
                bytes = int.Parse(query.Split('&')[0].Split('=')[1]);
            }
            return new HttpResponseMessage(Status) { Content = new ByteArrayContent(new byte[bytes]) };
        }
    }
}

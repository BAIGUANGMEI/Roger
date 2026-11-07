using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class UpdateDownloadSmoke
{
    private static readonly string Tag = "v" + (UpdateService.CurrentVersion.Major + 1) + ".0.0";
    private static readonly string InstallerName = "Roger-Setup-" + UpdateService.ParseVersion(Tag).ToString(3) + "-x64.exe";
    private const string Repo = "example/Roger";
    private static readonly string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private static string Download(string name) => "https://github.com/" + Repo + "/releases/download/" + Tag + "/" + name;
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("test installer payload; never executed");
    private static readonly byte[] Portable = Encoding.UTF8.GetBytes("test portable payload; never extracted");

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Roger-Update-Download-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifyHappyPathAsync(Path.Combine(root, "success"));
            await VerifyMetadataAsync(Path.Combine(root, "metadata"));
            await VerifyForgedResultsAsync(Path.Combine(root, "forged"));
            await VerifyIntegrityAsync(Path.Combine(root, "integrity"));
            await VerifyTransfersAndRetryAsync(Path.Combine(root, "transfers"));
            await VerifyRedirectsAsync(Path.Combine(root, "redirects"));
            await VerifyCancellationAndConcurrencyAsync(Path.Combine(root, "cancel"));
            Console.WriteLine("PASS updater download: canonical GitHub assets, size/SHA-256/digest checks, bounded redirects, corrupt/truncated response rejection, cancellation/retry/concurrency and no execution");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task VerifyHappyPathAsync(string directory)
    {
        var fixture = new Fixture();
        using var client = new HttpClient(fixture);
        using var service = new UpdateService(client, directory);
        UpdateResult release = await service.CheckAsync(Repo);
        Check(release.Repository == Repo && release.CanUpdate && release.CanPrepare(UpdatePackageKind.Installer) && release.CanPrepare(UpdatePackageKind.Portable), "both update formats are available with a checksum manifest");
        Check(release.Installer?.DownloadUrl == Download(InstallerName) && release.Checksums?.DownloadUrl == Download("SHA256SUMS.txt"), "asset URLs are rebuilt rather than trusting browser_download_url");
        Check(!Directory.Exists(directory), "check does not create download files");
        var progress = new List<UpdateDownloadProgress>();
        DownloadedUpdate installed = await service.PrepareAsync(release, UpdatePackageKind.Installer, new InlineProgress(progress.Add));
        Check(Path.IsPathFullyQualified(installed.FilePath) && installed.FilePath.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "prepared package stays inside the owned download directory");
        Check(installed.AssetName == InstallerName && installed.Tag == Tag && installed.Repository == Repo && installed.Kind == UpdatePackageKind.Installer, "prepared package identity is retained");
        Check(File.ReadAllBytes(installed.FilePath).SequenceEqual(Installer) && installed.Sha256 == Hash(Installer) && installed.SizeBytes == Installer.Length, "prepared bytes and digest are exact");
        Check(progress.Any(p => p.Stage == "下载更新包" && p.BytesReceived == Installer.Length && p.Percent == 100) && progress.Last().Stage == "验证 SHA-256", "progress includes bytes and validation stage");
        DownloadedUpdate portable = await service.PrepareAsync(release, UpdatePackageKind.Portable);
        Check(File.ReadAllBytes(portable.FilePath).SequenceEqual(Portable) && portable.Kind == UpdatePackageKind.Portable, "portable download uses its own checksum");
        Check(!Directory.EnumerateFiles(directory, "*.download", SearchOption.AllDirectories).Any(), "successful download leaves no partial file");
        Check(fixture.Requests.All(uri => uri.Host is "api.github.com" or "github.com"), "malicious metadata URLs are never fetched");
    }

    private static async Task VerifyMetadataAsync(string directory)
    {
        var fixture = new Fixture();
        using var client = new HttpClient(fixture);
        using var service = new UpdateService(client, directory);
        fixture.ReleaseOverride = JsonSerializer.Serialize(new { tag_name = Tag, assets = Array.Empty<object>() });
        UpdateResult release = await service.CheckAsync(Repo);
        Check(release.IsNewer && !release.CanUpdate && release.UnavailableReason is not null, "old releases without update assets remain inspectable but cannot install");
        await ExpectAsync<InvalidOperationException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer));
        foreach (object[] assets in new[]
        {
            new[] { fixture.Asset(InstallerName, Installer), fixture.Asset(InstallerName, Installer), fixture.Asset("SHA256SUMS.txt", fixture.Manifest) },
            new[] { fixture.Asset(InstallerName, Installer, size: UpdateService.MaximumPackageBytes + 1), fixture.Asset("SHA256SUMS.txt", fixture.Manifest) },
            new[] { fixture.Asset(InstallerName, Installer, state: "new"), fixture.Asset("SHA256SUMS.txt", fixture.Manifest) },
            new[] { fixture.Asset(InstallerName, Installer, digest: "md5:bad"), fixture.Asset("SHA256SUMS.txt", fixture.Manifest) }
        })
        {
            fixture.ReleaseOverride = JsonSerializer.Serialize(new { tag_name = Tag, assets });
            Check(!(await service.CheckAsync(Repo)).CanUpdate, "invalid/duplicate update metadata disables in-app preparation");
        }
        fixture.ReleaseOverride = new string(' ', 2 * 1024 * 1024 + 1);
        await ExpectAsync<InvalidDataException>(() => service.CheckAsync(Repo));
        Check(!Directory.Exists(directory), "all metadata rejections precede filesystem writes");
    }

    private static async Task VerifyForgedResultsAsync(string directory)
    {
        var fixture = new Fixture();
        using var client = new HttpClient(fixture);
        using var service = new UpdateService(client, directory);
        UpdateResult release = await service.CheckAsync(Repo);
        int count = fixture.Requests.Count;
        foreach (UpdateResult forged in new[]
        {
            release with { Installer = release.Installer! with { Name = "../Roger.exe" } },
            release with { Installer = release.Installer! with { DownloadUrl = "https://evil.example/update.exe" } },
            release with { Checksums = release.Checksums! with { DownloadUrl = "https://github.com/another/repo/SHA256SUMS.txt" } },
            release with { Installer = release.Installer! with { SizeBytes = 0 } },
            release with { Installer = release.Installer! with { Sha256Digest = "not-a-digest" } }
        }) await ExpectAsync<InvalidDataException>(() => service.PrepareAsync(forged, UpdatePackageKind.Installer));
        await ExpectAsync<ArgumentException>(() => service.PrepareAsync(release with { Repository = "../repo" }, UpdatePackageKind.Installer));
        await ExpectAsync<InvalidOperationException>(() => service.PrepareAsync(release with { Tag = "v0.0.0" }, UpdatePackageKind.Installer));
        await ExpectAsync<InvalidOperationException>(() => service.PrepareAsync(release with { IsNewer = false }, UpdatePackageKind.Installer));
        await ExpectAsync<ArgumentOutOfRangeException>(() => service.PrepareAsync(release, (UpdatePackageKind)42));
        Check(fixture.Requests.Count == count && !Directory.Exists(directory), "forged public records fail before HTTP or file creation");
    }

    private static async Task VerifyIntegrityAsync(string directory)
    {
        foreach (string manifest in new[]
        {
            new string('a', 64) + "  " + InstallerName + "\n",
            Hash(Installer) + "  " + InstallerName + "\n" + Hash(Installer) + "  " + InstallerName,
            Hash(Installer) + "  other.exe\n",
            Hash(Installer) + "  ../" + InstallerName,
            "malformed"
        })
        {
            var fixture = new Fixture { ManifestOverride = Encoding.UTF8.GetBytes(manifest), IncludeDigest = false };
            using var client = new HttpClient(fixture);
            using var service = new UpdateService(client, directory);
            var release = await service.CheckAsync(Repo);
            await ExpectAsync<InvalidDataException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer));
            CheckNoPackage(directory);
        }
        {
            var fixture = new Fixture { ManifestOverride = Encoding.UTF8.GetBytes(new string('b', 64) + "  " + InstallerName) };
            using var client = new HttpClient(fixture);
            using var service = new UpdateService(client, directory);
            await ExpectAsync<InvalidDataException>(() => PrepareInstallerAsync(service));
            Check(!fixture.Requests.Any(uri => uri.AbsoluteUri == Download(InstallerName)), "GitHub digest disagrees with checksum manifest before package download");
        }
        {
            var fixture = new Fixture();
            using var client = new HttpClient(fixture);
            using var service = new UpdateService(client, directory);
            var release = await service.CheckAsync(Repo);
            fixture.ManifestOverride = Encoding.UTF8.GetBytes("tampered checksum bytes");
            await ExpectAsync<InvalidDataException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer));
            CheckNoPackage(directory);
        }
    }

    private static async Task VerifyTransfersAndRetryAsync(string directory)
    {
        var fixture = new Fixture();
        using var client = new HttpClient(fixture);
        using var service = new UpdateService(client, directory);
        UpdateResult release = await service.CheckAsync(Repo);
        fixture.PackageOverride = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await ExpectAsync<HttpRequestException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer));
        fixture.PackageOverride = (_, _) =>
        {
            var response = Bytes(Installer[..^1]); response.Content.Headers.ContentLength = Installer.Length;
            return Task.FromResult(response);
        };
        await ExpectAsync<InvalidDataException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer));
        fixture.PackageOverride = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(Installer.Concat(new byte[] { 42 }).ToArray())) });
        await ExpectAsync<InvalidDataException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer));
        CheckNoPackage(directory);
        fixture.PackageOverride = null;
        var retried = await service.PrepareAsync(release, UpdatePackageKind.Installer);
        Check(File.Exists(retried.FilePath), "failure clears the active-operation guard and a fresh retry succeeds");
    }

    private static async Task VerifyRedirectsAsync(string directory)
    {
        foreach (string destination in new[] { "https://evil.example/file", "http://github.com/file", "https://github.com:444/file", "https://user:password@github.com/file" })
        {
            var fixture = new Fixture { PackageOverride = (_, _) => Task.FromResult(Redirect(destination)) };
            using var client = new HttpClient(fixture);
            using var service = new UpdateService(client, directory);
            await ExpectAsync<InvalidDataException>(() => PrepareInstallerAsync(service));
            Check(!fixture.Requests.Any(uri => uri.AbsoluteUri == destination), "unsafe redirect is rejected before dispatch");
            CheckNoPackage(directory);
        }
        {
            const string assetCdn = "https://release-assets.githubusercontent.com/github-production-release-asset/1/file?token=opaque";
            var fixture = new Fixture { PackageOverride = (_, _) => Task.FromResult(Redirect(assetCdn)) };
            using var client = new HttpClient(fixture);
            using var service = new UpdateService(client, directory);
            Check(File.Exists((await PrepareInstallerAsync(service)).FilePath) && fixture.Requests.Any(uri => uri.AbsoluteUri == assetCdn), "official release CDN redirect remains supported");
        }
        {
            var fixture = new Fixture { PackageOverride = (_, _) => Task.FromResult(Redirect(Download(InstallerName))) };
            using var client = new HttpClient(fixture);
            using var service = new UpdateService(client, Path.Combine(directory, "loop"));
            await ExpectAsync<HttpRequestException>(() => PrepareInstallerAsync(service));
            Check(fixture.Requests.Count(uri => uri.AbsoluteUri == Download(InstallerName)) == 6, "redirect loop is bounded");
            CheckNoPackage(Path.Combine(directory, "loop"));
        }
    }

    private static async Task VerifyCancellationAndConcurrencyAsync(string directory)
    {
        var fixture = new Fixture { InstallerBytes = Enumerable.Repeat((byte)77, 150_000).ToArray() };
        using var client = new HttpClient(fixture);
        using var service = new UpdateService(client, directory);
        var release = await service.CheckAsync(Repo);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer, token: cancel.Token));
        }
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new InlineProgress(p => { if (p.Stage == "下载更新包" && p.BytesReceived > 0) cancel.Cancel(); });
            await ExpectAsync<OperationCanceledException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer, progress, cancel.Token));
        }
        CheckNoPackage(directory);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PackageOverride = async (_, token) => { requested.TrySetResult(); return await reply.Task.WaitAsync(token); };
        Task<DownloadedUpdate> first = service.PrepareAsync(release, UpdatePackageKind.Installer);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ExpectAsync<InvalidOperationException>(() => service.PrepareAsync(release, UpdatePackageKind.Installer));
        reply.SetResult(Bytes(fixture.InstallerBytes));
        Check(File.Exists((await first).FilePath), "parallel request cannot interrupt the original download");
        var timeoutFixture = new Fixture { PackageOverride = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Bytes(Installer); } };
        using var timeoutClient = new HttpClient(timeoutFixture);
        using var timed = new UpdateService(timeoutClient, Path.Combine(directory, "timeout"), TimeSpan.FromMilliseconds(50));
        await ExpectAsync<TimeoutException>(() => PrepareInstallerAsync(timed));
        CheckNoPackage(Path.Combine(directory, "timeout"));
    }

    private static async Task<DownloadedUpdate> PrepareInstallerAsync(UpdateService service) => await service.PrepareAsync(await service.CheckAsync(Repo), UpdatePackageKind.Installer);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string location) { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(location); return response; }
    private static void CheckNoPackage(string directory) => Check(!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(), "rejected or canceled packages leave no prepared executable or partial bytes");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Update download smoke failed: " + message); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected update download exception " + typeof(T).Name); }
    private sealed class InlineProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    { public void Report(UpdateDownloadProgress value) => report(value); }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }

    private sealed class Fixture : HttpMessageHandler
    {
        public byte[] InstallerBytes = Installer;
        public string? ReleaseOverride;
        public byte[]? ManifestOverride;
        public bool IncludeDigest = true;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? PackageOverride;
        public List<Uri> Requests { get; } = new();
        public byte[] Manifest => ManifestOverride ?? Encoding.UTF8.GetBytes(Hash(InstallerBytes) + "  " + InstallerName + "\n" + Hash(Portable) + "  Roger-win-x64.zip\n");
        public object Asset(string name, byte[] content, long? size = null, string state = "uploaded", string? digest = null) => new
        {
            name, size = size ?? content.Length, state,
            digest = digest ?? (IncludeDigest ? "sha256:" + Hash(content) : null),
            browser_download_url = "https://evil.example/should-never-be-requested"
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri!; Requests.Add(uri);
            if (uri.AbsoluteUri == Api)
            {
                string json = ReleaseOverride ?? JsonSerializer.Serialize(new
                {
                    tag_name = Tag, body = "notes", html_url = "https://evil.example/html",
                    assets = new[] { Asset(InstallerName, InstallerBytes), Asset("Roger-win-x64.zip", Portable), Asset("SHA256SUMS.txt", Manifest) }
                });
                return Task.FromResult(Bytes(Encoding.UTF8.GetBytes(json)));
            }
            if (uri.AbsoluteUri == Download("SHA256SUMS.txt")) return Task.FromResult(Bytes(Manifest));
            if (uri.AbsoluteUri == Download(InstallerName)) return PackageOverride?.Invoke(request, token) ?? Task.FromResult(Bytes(InstallerBytes));
            if (uri.AbsoluteUri == Download("Roger-win-x64.zip")) return Task.FromResult(Bytes(Portable));
            if (uri.Host == "release-assets.githubusercontent.com") return Task.FromResult(Bytes(InstallerBytes));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

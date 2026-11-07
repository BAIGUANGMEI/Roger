using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class UpdateInstallSmoke
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Roger-Update-Install-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyArguments(root);
            await VerifyLeaseAsync(root);
            VerifyCacheRetention(root);
            await VerifyInstallerAsync(root);
            await VerifyPortableAsync(root);
            Console.WriteLine("PASS update handoff: explicit commit, parent-exit/cancel gates, protected-path rules, fixed no-reboot installer arguments, result/version checks, portable traversal rejection and rollback (fake installer only)");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void VerifyArguments(string root)
    {
        string package = Path.Combine(root, "installer.exe"), target = Path.Combine(root, "app with spaces"), log = Path.Combine(root, "install.log");
        var start = UpdateInstallEngine.BuildInstallerStartInfo(package, target, log);
        Check(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden, "installer is direct, hidden, no shell");
        foreach (string argument in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/SP-", "/NORESTART", "/RESTARTEXITCODE=3010", "/NOCLOSEAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", "/DIR=" + target, "/LOG=" + log })
            Check(start.ArgumentList.Contains(argument), "fixed installer argument: " + argument);
        foreach (string name in new[] { "../evil", "/absolute", "C:/Windows/evil", "dir/../../evil", "a//b", "a./b", "a /b", "dir/CON.txt", "dir/NUL", "dir/file:stream" })
            Expect<InvalidDataException>(() => UpdateInstallEngine.SafeArchiveName(name));
        Check(UpdateInstallEngine.SafeArchiveName("docs/read me.md") == "docs/read me.md", "ordinary relative paths remain valid");
        Expect<InvalidDataException>(() => UpdateInstallEngine.ChildPath(root, "../escape"));
        string acceptedLog = Path.Combine(UpdateInstallEngine.CacheRoot, Guid.NewGuid().ToString("N"), "install.log");
        Check(UpdateInstallEngine.IsResultLogPath(acceptedLog), "result log is a fixed cache-stage install.log");
        Check(!UpdateInstallEngine.IsResultLogPath(package) && !UpdateInstallEngine.IsResultLogPath(Path.Combine(root, "install.log")), "result cannot point UI at another local file");
    }

    private static async Task VerifyLeaseAsync(string root)
    {
        using (var lease = UpdateInstallEngine.AcquireInstallLease(root))
        {
            await Task.Delay(1).ConfigureAwait(false);
            Expect<IOException>(() => { using var duplicate = UpdateInstallEngine.AcquireInstallLease(root); });
        }
        using var reacquired = UpdateInstallEngine.AcquireInstallLease(root);
        Check(reacquired.CanWrite, "install lease can be released across await and reacquired");
    }
    private static void VerifyCacheRetention(string root)
    {
        string cache = Path.Combine(root, "cache-retention"); Directory.CreateDirectory(cache);
        var directories = Enumerable.Range(0, 5).Select(_ => Path.Combine(cache, Guid.NewGuid().ToString("N"))).ToArray();
        for (int i = 0; i < directories.Length; i++)
        {
            Directory.CreateDirectory(directories[i]); File.WriteAllText(Path.Combine(directories[i], "claimed"), "claimed");
            Directory.SetLastWriteTimeUtc(directories[i], DateTime.UtcNow.AddDays(-10 - i));
        }
        Directory.CreateDirectory(Path.Combine(cache, "unrelated-data"));
        using (var active = new FileStream(Path.Combine(directories[3], "claimed"), FileMode.Open, FileAccess.Read, FileShare.None))
            UpdateInstallEngine.CleanupOldStages(cache);
        Check(Directory.Exists(directories[0]) && Directory.Exists(directories[1]), "latest two rollback stages are retained");
        Check(!Directory.Exists(directories[2]) && Directory.Exists(directories[3]) && !Directory.Exists(directories[4]), "old inactive stages expire but a running helper is never cleaned");
        Check(Directory.Exists(Path.Combine(cache, "unrelated-data")), "unknown cache directories are not deleted");
    }

    private static async Task VerifyInstallerAsync(string root)
    {
        foreach (int code in new[] { 0, 1, 3010 })
        {
            var f = Fixture(root, "installer-" + code, UpdateInstallKind.Installer);
            f.Platform.InstallerExit = code;
            f.Platform.UpdateOnInstall = code == 0;
            int exit = await UpdateInstallEngine.RunCoreAsync(f.Request, f.Stage, f.Results, f.Platform, CancellationToken.None);
            var result = Result(f.Results);
            Check(f.Platform.WaitCalls == 1 && f.Platform.InstallCalls == 1, "installer waits for original process exit before execution");
            Check(result.Succeeded == (code == 0) && result.RestartRequired == (code == 3010), "installer exit status is preserved");
            Check(code == 3010 ? f.Platform.Restarts == 0 && exit == 3010 : f.Platform.Restarts == 1, "restart-required never auto-restarts Windows or app");
        }
        var unchanged = Fixture(root, "installer-version-unchanged", UpdateInstallKind.Installer);
        unchanged.Platform.UpdateOnInstall = false;
        await UpdateInstallEngine.RunCoreAsync(unchanged.Request, unchanged.Stage, unchanged.Results, unchanged.Platform, CancellationToken.None);
        Check(!Result(unchanged.Results).Succeeded && Result(unchanged.Results).Message.Contains("版本", StringComparison.Ordinal), "exit zero alone cannot claim success");

        foreach (string fault in new[] { "no-commit", "cancelled", "still-running", "wrong-hash", "changed-after-ready" })
        {
            var f = Fixture(root, fault, UpdateInstallKind.Installer);
            if (fault == "no-commit") File.Delete(Path.Combine(f.Stage, "commit.json"));
            if (fault == "cancelled") File.WriteAllText(Path.Combine(f.Stage, "cancel"), "cancelled");
            if (fault == "still-running") f.Platform.ParentExited = false;
            if (fault == "wrong-hash") File.AppendAllText(UpdateInstallEngine.PackagePath(f.Stage, f.Request.Kind), "tampered");
            if (fault == "changed-after-ready") f.Platform.AfterParentExit = () => File.AppendAllText(UpdateInstallEngine.PackagePath(f.Stage, f.Request.Kind), "tampered");
            await UpdateInstallEngine.RunCoreAsync(f.Request, f.Stage, f.Results, f.Platform, CancellationToken.None);
            Check(f.Platform.InstallCalls == 0, "no installer is executed on " + fault);
            if (fault is "no-commit" or "cancelled" or "still-running")
                Check(f.Platform.Restarts == 0 && f.Platform.Notifications == 0 && !Directory.Exists(f.Results), "uncommitted cancellation never reopens Roger, notifies or replaces the shared outcome");
            else Check(!Result(f.Results).Succeeded, "validation failure is not success");
            Check(fault != "still-running" || f.Platform.Restarts == 0, "waiting parent is never duplicated or killed");
        }
    }

    private static async Task VerifyPortableAsync(string root)
    {
        var good = Fixture(root, "portable-good", UpdateInstallKind.Portable);
        WriteZip(good, ("Roger.exe", "new exe"), ("Roger.dll", "new dll"), ("version.txt", "1.0.0"), ("docs/new.txt", "new docs"));
        await UpdateInstallEngine.RunCoreAsync(good.Request, good.Stage, good.Results, good.Platform, CancellationToken.None);
        Check(Result(good.Results).Succeeded && File.ReadAllText(Path.Combine(good.Target, "Roger.exe")) == "new exe", "portable replaces complete validated payload");
        Check(File.ReadAllText(Path.Combine(good.Target, "user-file.txt")) == "keep", "portable preserves unrelated existing files");
        Check(File.ReadAllText(Path.Combine(good.Stage, "backup/Roger.exe")) == "old exe", "portable retains original backup");

        var rollback = Fixture(root, "portable-rollback", UpdateInstallKind.Portable);
        Directory.CreateDirectory(Path.Combine(rollback.Target, "blocked.txt"));
        WriteZip(rollback, ("Roger.exe", "new exe"), ("blocked.txt", "will fail"), ("version.txt", "1.0.0"));
        await UpdateInstallEngine.RunCoreAsync(rollback.Request, rollback.Stage, rollback.Results, rollback.Platform, CancellationToken.None);
        Check(!Result(rollback.Results).Succeeded && File.ReadAllText(Path.Combine(rollback.Target, "Roger.exe")) == "old exe", "failed portable replacement rolls back files already changed");
        Check(File.ReadAllText(Path.Combine(rollback.Target, "version.txt")) == "0.3.0", "failed portable leaves original version");

        foreach (string malicious in new[] { "../escaped.txt", "C:/escaped.txt", "dir/CON.txt", "Roger.exe/" })
        {
            var bad = Fixture(root, "portable-bad-" + Guid.NewGuid().ToString("N"), UpdateInstallKind.Portable);
            WriteZip(bad, ("Roger.exe", "new exe"), ("version.txt", "1.0.0"), (malicious, "evil"));
            await UpdateInstallEngine.RunCoreAsync(bad.Request, bad.Stage, bad.Results, bad.Platform, CancellationToken.None);
            Check(!Result(bad.Results).Succeeded && File.ReadAllText(Path.Combine(bad.Target, "Roger.exe")) == "old exe", "malformed archive is rejected before any target changes");
        }
        var duplicate = Fixture(root, "portable-duplicate", UpdateInstallKind.Portable);
        WriteZip(duplicate, ("Roger.exe", "new exe"), ("roger.EXE", "conflict"), ("version.txt", "1.0.0"));
        await UpdateInstallEngine.RunCoreAsync(duplicate.Request, duplicate.Stage, duplicate.Results, duplicate.Platform, CancellationToken.None);
        Check(!Result(duplicate.Results).Succeeded, "Windows case-insensitive duplicate archive entries are refused");
    }

    private static TestFixture Fixture(string root, string name, UpdateInstallKind kind)
    {
        string directory = Path.Combine(root, name), target = Path.Combine(directory, "app"), stage = Path.Combine(directory, "stage"), results = Path.Combine(directory, "results");
        Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(target, "Roger.exe"), "old exe"); File.WriteAllText(Path.Combine(target, "Roger.dll"), "old dll");
        File.WriteAllText(Path.Combine(target, "version.txt"), "0.3.0"); File.WriteAllText(Path.Combine(target, "user-file.txt"), "keep");
        string package = UpdateInstallEngine.PackagePath(stage, kind); File.WriteAllText(package, "fake installer, never executed");
        var request = new UpdateInstallRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, target, "1.0.0",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))), new FileInfo(package).Length,
            int.MaxValue, 1, Path.Combine(directory, "fake-parent.exe"));
        UpdateInstallEngine.WriteJson(Path.Combine(stage, "commit.json"), new UpdateInstallService.ReadyState(request.Id));
        return new(target, stage, results, request, new FakePlatform(target));
    }
    private static void WriteZip(TestFixture fixture, params (string Name, string Content)[] entries)
    {
        string package = UpdateInstallEngine.PackagePath(fixture.Stage, fixture.Request.Kind);
        using (var stream = new FileStream(package, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var item in entries) { var entry = archive.CreateEntry(item.Name); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(item.Content); }
        fixture.Request = fixture.Request with { PackageSize = new FileInfo(package).Length, PackageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))) };
    }
    private static UpdateInstallResult Result(string directory) => JsonSerializer.Deserialize<UpdateInstallResult>(File.ReadAllText(Path.Combine(directory, "install-result.json")))!;
    private sealed class TestFixture(string target, string stage, string results, UpdateInstallRequest request, FakePlatform platform)
    { public string Target = target, Stage = stage, Results = results; public UpdateInstallRequest Request = request; public FakePlatform Platform = platform; }
    private sealed class FakePlatform(string target) : UpdateInstallEngine.IUpdatePlatform
    {
        public int InstallerExit, WaitCalls, InstallCalls, Restarts, Notifications;
        public bool ParentExited = true, UpdateOnInstall = true;
        public Action? AfterParentExit;
        public Task<bool> WaitForParentExitAsync(UpdateInstallRequest request, string stage, CancellationToken token) { WaitCalls++; AfterParentExit?.Invoke(); return Task.FromResult(ParentExited); }
        public Task<int> RunInstallerAsync(ProcessStartInfo start, CancellationToken token)
        {
            InstallCalls++;
            Check(WaitCalls == 1, "installation requires a completed parent wait");
            if (UpdateOnInstall && InstallerExit == 0) File.WriteAllText(Path.Combine(target, "version.txt"), "1.0.0");
            return Task.FromResult(InstallerExit);
        }
        public Version? ReadVersion(string directory) => File.Exists(Path.Combine(directory, "Roger.exe")) && File.Exists(Path.Combine(directory, "version.txt")) ? Version.Parse(File.ReadAllText(Path.Combine(directory, "version.txt"))) : null;
        public void Restart(string executable) { Check(executable == Path.Combine(target, "Roger.exe"), "restart is only fixed target Roger.exe"); Restarts++; }
        public void Notify(string message) { Notifications++; }
    }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Update install smoke: " + message); }
}

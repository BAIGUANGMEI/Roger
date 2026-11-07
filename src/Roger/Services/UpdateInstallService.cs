using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Roger.Models;

namespace Roger.Services;

/// <summary>Stages a verified update; the independent helper waits for normal app shutdown.</summary>
public sealed class UpdateInstallService
{
    private readonly string _applicationDirectory;
    public UpdateInstallService(string? applicationDirectory = null) => _applicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
    public UpdateInstallKind GetCurrentKind()
    {
        const string keyName = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ECFB83A5-0B93-4F01-89AD-0D54EBA6072F}_is1";
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(keyName);
                if (key?.GetValue("InstallLocation") is string path && SamePath(path, _applicationDirectory)) return UpdateInstallKind.Installer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return File.Exists(Path.Combine(_applicationDirectory, "unins000.exe")) ? UpdateInstallKind.Installer : UpdateInstallKind.Portable;
    }

    public UpdateInstallResult? ReadLastResult()
    {
        string path = Path.Combine(UpdateInstallEngine.ResultDirectory, "install-result.json");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("更新结果文件过大。");
            var result = JsonSerializer.Deserialize<UpdateInstallResult>(File.ReadAllText(path));
            if (result is null || result.RequestId == Guid.Empty || !Enum.IsDefined(result.Status)
                || result.Message is not { Length: <= 4000 } || result.LogPath is not { Length: <= 2048 }) throw new InvalidDataException("更新结果无效。");
            UpdateInstallEngine.ParseVersion(result.TargetVersion);
            if (result.LogPath.Length > 0 && !UpdateInstallEngine.IsResultLogPath(result.LogPath)) throw new InvalidDataException("更新日志路径无效。");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(Guid.Empty, DateTimeOffset.Now, "0.0.0", UpdateInstallStatus.Failed, "上次更新结果无法读取：" + ex.Message, "", null); }
    }

    public async Task<UpdateInstallLaunch> StartAsync(string packagePath, string expectedSha256, long packageSize,
        string targetVersion, UpdateInstallKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expectedSha256);
        if (!UpdateInstallEngine.IsAdministrator()) throw new UnauthorizedAccessException("请以管理员模式运行 Roger 后安装更新。");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind != GetCurrentKind()) throw new InvalidOperationException("当前安装类型已变化，请重新检查更新。");
        string helper = Path.Combine(_applicationDirectory, "Roger.Updater.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("此开发构建尚未部署更新助手，请安装包含更新助手的 Roger 版本。", helper);
        // This product ships a self-contained single-file helper. A normal framework-
        // dependent apphost is tiny and would require sidecars that are not staged here.
        if (new FileInfo(helper).Length < 10L * 1024 * 1024) throw new InvalidDataException("更新助手不是完整的独立运行版本，请重新安装 Roger。");
        Version current = UpdateInstallEngine.ReadRogerVersion(_applicationDirectory) ?? throw new InvalidDataException("当前 Roger 程序目录无效。");
        if (UpdateInstallEngine.ParseVersion(targetVersion) <= current) throw new InvalidOperationException("只能安装比当前版本更新的正式版本。");
        if (!Regex.IsMatch(expectedSha256, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("更新校验值无效。");
        string originalPackage = Path.GetFullPath(packagePath);
        if (packageSize is <= 0 or > UpdateInstallEngine.MaximumPackageBytes || new FileInfo(originalPackage).Length != packageSize) throw new InvalidDataException("更新包大小不匹配。");
        UpdateInstallEngine.RejectReparsePath(originalPackage);
        Guid id = Guid.NewGuid();
        string cache = UpdateInstallEngine.CacheRoot;
        string stage = Path.Combine(cache, id.ToString("N"));
        UpdateInstallEngine.CreateProtectedDirectory(Path.GetDirectoryName(cache)!);
        UpdateInstallEngine.CreateProtectedDirectory(cache);
        UpdateInstallEngine.CleanupOldStages(cache);
        UpdateInstallEngine.CreateProtectedDirectory(stage);
        using var currentProcess = Process.GetCurrentProcess();
        var request = new UpdateInstallRequest(id, DateTimeOffset.UtcNow, kind, _applicationDirectory, targetVersion,
            expectedSha256.ToUpperInvariant(), packageSize, Environment.ProcessId, currentProcess.StartTime.ToUniversalTime().Ticks,
            Environment.ProcessPath ?? throw new InvalidOperationException("无法识别当前 Roger 进程。"));
        try
        {
            await Task.Run(() =>
            {
                UpdateInstallEngine.RejectReparsePath(_applicationDirectory);
                string stagedPackage = UpdateInstallEngine.PackagePath(stage, kind);
                File.Copy(originalPackage, stagedPackage, overwrite: false);
                UpdateInstallEngine.VerifyPackage(stagedPackage, packageSize, expectedSha256);
                File.Copy(helper, Path.Combine(stage, "Roger.Updater.exe"), overwrite: false);
                using var before = File.OpenRead(helper);
                using var after = File.OpenRead(Path.Combine(stage, "Roger.Updater.exe"));
                if (!SHA256.HashData(before).SequenceEqual(SHA256.HashData(after))) throw new InvalidDataException("更新助手复制校验失败。");
                UpdateInstallEngine.WriteJson(Path.Combine(stage, "request.json"), request);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Path.Combine(stage, "Roger.Updater.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = stage };
            start.ArgumentList.Add("--request"); start.ArgumentList.Add(Path.Combine(stage, "request.json"));
            // Single-file native extraction must not happen in a medium-integrity writable Temp.
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(stage, "runtime");
            using var process = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(stage, "ready.json")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("更新助手未能就绪，请查看更新结果或重新安装 Roger。");
                if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("更新助手启动超时；Roger 尚未退出。");
                await Task.Delay(100, cancellationToken);
            }
            var ready = JsonSerializer.Deserialize<ReadyState>(File.ReadAllText(Path.Combine(stage, "ready.json")));
            if (ready?.Id != id) throw new InvalidDataException("更新助手响应与本次请求不匹配。");
            cancellationToken.ThrowIfCancellationRequested();
            return new(id, stage);
        }
        catch
        {
            // No installation is allowed after a cancelled/failed handoff, even if the user
            // later exits Roger while the helper is still waiting for the original process.
            try { File.WriteAllText(Path.Combine(stage, "cancel"), "cancelled"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
    public void Commit(UpdateInstallLaunch launch)
    {
        string stage = Path.GetFullPath(launch.StageDirectory);
        if (!SamePath(Path.GetDirectoryName(stage)!, UpdateInstallEngine.CacheRoot) || Path.GetFileName(stage) != launch.RequestId.ToString("N")) throw new InvalidDataException("更新交接路径无效。");
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(File.ReadAllText(Path.Combine(stage, "request.json")));
        var ready = JsonSerializer.Deserialize<ReadyState>(File.ReadAllText(Path.Combine(stage, "ready.json")));
        if (request?.Id != launch.RequestId || ready?.Id != launch.RequestId || request.ParentProcessId != Environment.ProcessId
            || File.Exists(Path.Combine(stage, "cancel"))) throw new InvalidDataException("更新交接已取消或不属于当前 Roger。");
        UpdateInstallEngine.WriteJson(Path.Combine(stage, "commit.json"), new ReadyState(launch.RequestId));
    }
    public void Cancel(UpdateInstallLaunch launch)
    {
        string stage = Path.GetFullPath(launch.StageDirectory);
        if (!SamePath(Path.GetDirectoryName(stage)!, UpdateInstallEngine.CacheRoot) || Path.GetFileName(stage) != launch.RequestId.ToString("N")) throw new InvalidDataException("更新交接路径无效。");
        File.WriteAllText(Path.Combine(stage, "cancel"), "cancelled");
    }
    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    internal sealed record ReadyState(Guid Id);
}

/// <summary>Shared with the separate WinExe helper. UI tests inject process actions, never install.</summary>
public static class UpdateInstallEngine
{
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    public static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roger", "updates");
    public static string ResultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roger", "updates");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!IsAdministrator()) throw new UnauthorizedAccessException("更新助手需要管理员权限。");
            if (args.Length != 2 || args[0] != "--request") throw new ArgumentException("更新助手参数无效。");
            string path = Path.GetFullPath(args[1]);
            string stage = Path.GetDirectoryName(path)!;
            if (Path.GetFileName(path) != "request.json" || !SamePath(Path.GetDirectoryName(stage)!, CacheRoot)
                || !Guid.TryParseExact(Path.GetFileName(stage), "N", out Guid id)) throw new InvalidDataException("更新请求不在受保护缓存中。");
            VerifyProtectedDirectory(Path.GetDirectoryName(CacheRoot)!);
            VerifyProtectedDirectory(CacheRoot); VerifyProtectedDirectory(stage);
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("更新请求过大。");
            var request = JsonSerializer.Deserialize<UpdateInstallRequest>(File.ReadAllText(path)) ?? throw new InvalidDataException("更新请求为空。");
            if (request.Id != id) throw new InvalidDataException("更新请求标识无效。");
            using var claim = new FileStream(Path.Combine(stage, "claimed"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // A file lease remains valid across await continuations; OS mutex ownership
            // belongs to a thread and therefore must not span an asynchronous install.
            using var single = AcquireInstallLease(CacheRoot);
            return await RunCoreAsync(request, stage, ResultDirectory, new NativeUpdatePlatform(), CancellationToken.None);
        }
        catch (Exception ex) when (Recoverable(ex))
        { NativeUpdatePlatform.ShowMessage("Roger 更新未启动：" + ex.Message); return 1; }
    }

    internal static async Task<int> RunCoreAsync(UpdateInstallRequest request, string stage, string results,
        IUpdatePlatform platform, CancellationToken token)
    {
        string log = Path.Combine(stage, "install.log");
        string package = PackagePath(stage, request.Kind);
        bool parentExited = false;
        bool committed = false;
        int? exitCode = null;
        try
        {
            File.AppendAllText(log, DateTimeOffset.UtcNow.ToString("O") + " Update handoff validating " + request.TargetVersion + Environment.NewLine);
            ValidateRequest(request, platform);
            VerifyPackage(package, request.PackageSize, request.PackageSha256);
            WriteJson(Path.Combine(stage, "ready.json"), new UpdateInstallService.ReadyState(request.Id));
            parentExited = await platform.WaitForParentExitAsync(request, stage, token);
            if (!parentExited) throw new TimeoutException("Roger 尚未正常退出或更新已取消，未修改程序文件。");
            if (File.Exists(Path.Combine(stage, "cancel"))) throw new OperationCanceledException("更新交接已取消。");
            string commitPath = Path.Combine(stage, "commit.json");
            if (!File.Exists(commitPath) || JsonSerializer.Deserialize<UpdateInstallService.ReadyState>(File.ReadAllText(commitPath))?.Id != request.Id)
                throw new OperationCanceledException("Roger 未确认安装交接，未修改程序文件。");
            committed = true;
            RejectReparsePath(request.TargetDirectory);
            VerifyPackage(package, request.PackageSize, request.PackageSha256);
            if (request.Kind == UpdateInstallKind.Installer)
            {
                exitCode = await platform.RunInstallerAsync(BuildInstallerStartInfo(package, request.TargetDirectory, log), token);
                if (exitCode == 3010)
                {
                    SaveResult(results, new(request.Id, DateTimeOffset.Now, request.TargetVersion, UpdateInstallStatus.RestartRequired,
                        "更新安装程序要求重启 Windows。请保存工作后手动重启，再打开 Roger。", log, exitCode));
                    platform.Notify("Roger 更新需要重启 Windows。请保存工作后手动重启；不会自动重启电脑。");
                    return 3010;
                }
                if (exitCode != 0) throw new IOException("安装更新未完成，安装程序退出代码 " + exitCode + "。可在 Roger 内重试或查看安装日志。");
            }
            else
            {
                File.AppendAllText(log, "Applying validated portable payload after parent exit." + Environment.NewLine);
                ApplyPortable(package, request, stage, platform);
                exitCode = 0;
            }
            if (platform.ReadVersion(request.TargetDirectory) != ParseVersion(request.TargetVersion))
                throw new InvalidDataException("安装程序结束，但 Roger 版本未更新到目标版本；未标记为更新成功。");
            SaveResult(results, new(request.Id, DateTimeOffset.Now, request.TargetVersion, UpdateInstallStatus.Succeeded,
                "Roger 已更新到 " + request.TargetVersion + "。", log, exitCode));
            File.AppendAllText(log, "Target version verified. Restarting Roger." + Environment.NewLine);
            platform.Restart(Path.Combine(request.TargetDirectory, "Roger.exe"));
            return 0;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            string message = "更新未完成：" + ex.Message;
            try { File.AppendAllText(log, message + Environment.NewLine); } catch (Exception logError) when (Recoverable(logError)) { }
            // Closing/cancelling Roger during preparation is not permission to reopen it.
            // Keep the shared receipt intact for an uncommitted user cancellation.
            if (!committed && ex is OperationCanceledException or TimeoutException) return 2;
            try { SaveResult(results, new(request.Id, DateTimeOffset.Now, request.TargetVersion, UpdateInstallStatus.Failed, Limit(message, 3900), log, exitCode)); }
            catch (Exception saveError) when (Recoverable(saveError)) { message += "\n结果保存失败：" + saveError.Message; }
            if (committed && parentExited && File.Exists(Path.Combine(request.TargetDirectory, "Roger.exe")))
            {
                try { platform.Restart(Path.Combine(request.TargetDirectory, "Roger.exe")); }
                catch (Exception restartError) when (Recoverable(restartError)) { message += "\n请手动打开 Roger：" + restartError.Message; }
            }
            if (committed) platform.Notify(Limit(message, 3900));
            return 1;
        }
    }

    internal static ProcessStartInfo BuildInstallerStartInfo(string package, string target, string log)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(package))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(package)! };
        foreach (string arg in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/SP-", "/NORESTART", "/RESTARTEXITCODE=3010", "/NOCLOSEAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", "/LOG=" + Path.GetFullPath(log), "/DIR=" + Path.GetFullPath(target) }) start.ArgumentList.Add(arg);
        return start;
    }

    private static void ApplyPortable(string package, UpdateInstallRequest request, string stage, IUpdatePlatform platform)
    {
        string payload = Path.Combine(stage, "payload"), backup = Path.Combine(stage, "backup");
        Directory.CreateDirectory(payload); Directory.CreateDirectory(backup);
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using (var archive = ZipFile.OpenRead(package))
        {
            if (archive.Entries.Count is < 1 or > 20000) throw new InvalidDataException("便携包文件数量异常。");
            foreach (var entry in archive.Entries)
            {
                string name = SafeArchiveName(entry.FullName);
                if (!seen.Add(name)) throw new InvalidDataException("便携包包含重复路径。");
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("便携包不能包含符号链接。");
                string destination = ChildPath(payload, name);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(destination); continue; }
                total = checked(total + entry.Length);
                if (entry.Length > MaximumPackageBytes || total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("便携包解压大小超出限制。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false); files.Add(name);
            }
        }
        if (!File.Exists(Path.Combine(payload, "Roger.exe")) || platform.ReadVersion(payload) != ParseVersion(request.TargetVersion)) throw new InvalidDataException("便携包缺少目标版本 Roger 程序。");
        var applied = new List<(string Name, bool Existed)>();
        try
        {
            foreach (string name in files)
            {
                string target = ChildPath(request.TargetDirectory, name);
                RejectReparsePath(target);
                bool existed = File.Exists(target);
                string saved = ChildPath(backup, name);
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(target, saved); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Track before replacement so a fault after the atomic move is also reversible.
                applied.Add((name, existed));
                ReplaceFrom(ChildPath(payload, name), target);
            }
            if (platform.ReadVersion(request.TargetDirectory) != ParseVersion(request.TargetVersion)) throw new InvalidDataException("便携更新后的程序版本校验失败。");
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            var rollbackErrors = new List<string>();
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try
                {
                    string target = ChildPath(request.TargetDirectory, item.Name); RejectReparsePath(target);
                    if (item.Existed) ReplaceFrom(ChildPath(backup, item.Name), target);
                    else if (File.Exists(target)) File.Delete(target);
                }
                catch (Exception rollback) when (Recoverable(rollback)) { rollbackErrors.Add(item.Name); }
            }
            if (rollbackErrors.Count > 0) throw new IOException("便携更新失败，部分文件未能恢复。备份位于 " + backup + "；请查看日志或重新解压程序。", ex);
            throw new IOException("便携更新失败，已恢复本次替换前的文件。", ex);
        }
    }

    private static void ReplaceFrom(string source, string target)
    {
        string temporary = target + ".roger-update-" + Guid.NewGuid().ToString("N");
        try { File.Copy(source, temporary); File.Move(temporary, target, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static string SafeArchiveName(string name)
    {
        string normalized = name.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length is < 1 or > 1024 || normalized.StartsWith('/') || normalized.Contains(':')) throw new InvalidDataException("便携包包含非法路径。");
        foreach (string part in normalized.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException("便携包包含非法路径。");
        return normalized;
    }
    internal static string ChildPath(string parent, string relative)
    {
        string root = Path.GetFullPath(parent);
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出更新目录。");
        return path;
    }
    internal static void ValidateRequest(UpdateInstallRequest request, IUpdatePlatform platform)
    {
        if (request.Id == Guid.Empty || !Enum.IsDefined(request.Kind) || request.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(5)
            || request.CreatedAt < DateTimeOffset.UtcNow.AddDays(-1) || request.ParentProcessId <= 0 || request.ParentProcessId == Environment.ProcessId
            || request.ParentStartTimeUtcTicks <= 0 || !Path.IsPathFullyQualified(request.ParentExecutable)) throw new InvalidDataException("更新请求身份或时间无效。");
        if (!Regex.IsMatch(request.PackageSha256 ?? "", @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant) || request.PackageSize is <= 0 or > MaximumPackageBytes) throw new InvalidDataException("更新包元信息无效。");
        string target = Path.GetFullPath(request.TargetDirectory);
        if (!Path.IsPathFullyQualified(request.TargetDirectory) || target.StartsWith(@"\\", StringComparison.Ordinal) || SamePath(target, Path.GetPathRoot(target)!)
            || SamePath(target, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) || target.StartsWith(CacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新目标目录无效。");
        RejectReparsePath(target);
        Version current = platform.ReadVersion(target) ?? throw new InvalidDataException("更新目标不是可识别的 Roger 程序目录。");
        if (ParseVersion(request.TargetVersion) <= current) throw new InvalidDataException("更新目标版本必须高于当前版本。");
    }
    internal static Version ParseVersion(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new InvalidDataException("更新版本号为空。");
        string text = value.StartsWith('v') || value.StartsWith('V') ? value[1..] : value;
        if (!Regex.IsMatch(text, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant) || !Version.TryParse(text, out Version? version)) throw new InvalidDataException("更新版本号无效。");
        return version;
    }
    internal static Version? ReadRogerVersion(string directory)
    {
        try { var name = AssemblyName.GetAssemblyName(Path.Combine(directory, "Roger.dll")); return name.Name == "Roger" && File.Exists(Path.Combine(directory, "Roger.exe")) && name.Version is Version v ? new Version(v.Major, v.Minor, v.Build) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { return null; }
    }
    internal static void VerifyPackage(string path, long size, string sha256)
    {
        RejectReparsePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != size || stream.Length is <= 0 or > MaximumPackageBytes) throw new InvalidDataException("更新包大小校验失败。");
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，未执行更新。");
    }
    internal static string PackagePath(string stage, UpdateInstallKind kind) => Path.Combine(stage, kind == UpdateInstallKind.Installer ? "package.exe" : "package.zip");
    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { JsonSerializer.Serialize(stream, value, JsonOptions); stream.Flush(true); } File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void SaveResult(string directory, UpdateInstallResult result) => WriteJson(Path.Combine(directory, "install-result.json"), result);
    internal static FileStream AcquireInstallLease(string directory) => new(Path.Combine(directory, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    internal static void CleanupOldStages(string directory)
    {
        // Keep at least the two latest update/rollback records. Older inactive stages
        // expire after a week; unknown directory names and reparse trees are untouched.
        var stages = new DirectoryInfo(directory).EnumerateDirectories().Where(d => Guid.TryParseExact(d.Name, "N", out _))
            .OrderByDescending(d => d.LastWriteTimeUtc).ToArray();
        foreach (var candidate in stages.Skip(2).Where(d => d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)))
        {
            try
            {
                if (!SamePath(candidate.Parent!.FullName, directory) || HasReparseEntry(candidate.FullName)) continue;
                string claim = Path.Combine(candidate.FullName, "claimed");
                if (File.Exists(claim))
                {
                    // An active helper holds this file with FileShare.None throughout
                    // the whole transaction. Do not start deleting any of its files.
                    using var probe = new FileStream(claim, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                candidate.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static bool HasReparseEntry(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry is DirectoryInfo child && HasReparseEntry(child.FullName)) return true;
        return false;
    }
    internal static bool IsResultLogPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string stage = Path.GetDirectoryName(full)!;
            if (Path.GetFileName(full) != "install.log" || !SamePath(Path.GetDirectoryName(stage)!, CacheRoot)
                || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) return false;
            RejectReparsePath(full);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
    internal static bool IsAdministrator() { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    internal static void RejectReparsePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新路径不能经过符号链接或目录联接。");
    }
    internal static void CreateProtectedDirectory(string path)
    {
        RejectReparsePath(path); Directory.CreateDirectory(path);
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(admin);
        foreach (var sid in new[] { admin, system }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    private static void VerifyProtectedDirectory(string path)
    {
        RejectReparsePath(path);
        var acl = new DirectoryInfo(path).GetAccessControl();
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!acl.AreAccessRulesProtected || acl.GetOwner(typeof(SecurityIdentifier))?.Equals(admin) != true) throw new InvalidDataException("更新缓存权限无效。");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(admin) && !rule.IdentityReference.Equals(system)) throw new InvalidDataException("更新缓存包含非预期的访问权限。");
    }
    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    private static bool Recoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    internal interface IUpdatePlatform
    {
        Task<bool> WaitForParentExitAsync(UpdateInstallRequest request, string stage, CancellationToken token);
        Task<int> RunInstallerAsync(ProcessStartInfo start, CancellationToken token);
        Version? ReadVersion(string directory);
        void Restart(string executable);
        void Notify(string message);
    }
    private sealed class NativeUpdatePlatform : IUpdatePlatform
    {
        public async Task<bool> WaitForParentExitAsync(UpdateInstallRequest request, string stage, CancellationToken token)
        {
            Process parent;
            try { parent = Process.GetProcessById(request.ParentProcessId); }
            catch (ArgumentException) { return true; }
            using (parent)
            {
                try
                {
                    if (parent.HasExited) return true;
                    if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTimeUtcTicks
                        || !SamePath(parent.MainModule?.FileName ?? "", request.ParentExecutable)) throw new InvalidDataException("更新父进程身份已变化，未执行更新。");
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception && HasExited(parent))
                { return true; }
                var watch = Stopwatch.StartNew();
                while (!parent.HasExited)
                {
                    if (File.Exists(Path.Combine(stage, "cancel")) || watch.Elapsed > TimeSpan.FromMinutes(2)) return false;
                    await Task.Delay(200, token);
                }
            }
            return true;
        }
        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
        public async Task<int> RunInstallerAsync(ProcessStartInfo start, CancellationToken token)
        {
            using var process = Process.Start(start) ?? throw new IOException("无法启动更新安装程序。");
            // Once Windows installation starts, do not kill it or trigger rollback concurrently.
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode;
        }
        public Version? ReadVersion(string directory) => ReadRogerVersion(directory);
        public void Restart(string executable) { using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! }); }
        public void Notify(string message) => ShowMessage(message);
        internal static void ShowMessage(string message) => MessageBox(IntPtr.Zero, message, "Roger 更新", 0x00000040);
        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    }
}

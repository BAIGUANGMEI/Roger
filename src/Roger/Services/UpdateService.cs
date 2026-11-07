using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;

namespace Roger.Services;
public sealed record UpdateResult(bool IsNewer, string Tag, string ReleaseUrl, string Notes)
{
    public string Repository { get; init; } = "";
    public UpdateAsset? Installer { get; init; }
    public UpdateAsset? Portable { get; init; }
    public UpdateAsset? Checksums { get; init; }
    public string? UnavailableReason { get; init; }
    public bool CanUpdate => IsNewer && Checksums is not null && (Installer is not null || Portable is not null);
    public bool CanPrepare(UpdatePackageKind kind) => IsNewer && Checksums is not null && (kind switch
    { UpdatePackageKind.Installer => Installer is not null, UpdatePackageKind.Portable => Portable is not null, _ => false });
}
public sealed class UpdateService : IDisposable
{
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const int MaximumJsonBytes = 2 * 1024 * 1024, MaximumChecksumBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _downloadTimeout = TimeSpan.FromMinutes(10);
    private int _preparing;
    private bool _disposed;
    public string StorageDirectory { get; }
    public static Version CurrentVersion
    {
        get
        {
            Version version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0);
            return new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
        }
    }

    /// <summary>A supplied HttpClient remains owned by its caller, enabling offline validation.</summary>
    public UpdateService(HttpClient? httpClient = null, string? updateDirectory = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        StorageDirectory = Path.GetFullPath(updateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roger", "updates"));
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Roger/" + CurrentVersion);
        if (_http.DefaultRequestHeaders.Accept.Count == 0)
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal UpdateService(HttpClient httpClient, string updateDirectory, TimeSpan downloadTimeout)
        : this(httpClient, updateDirectory)
    {
        if (downloadTimeout <= TimeSpan.Zero || downloadTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        _downloadTimeout = downloadTimeout;
    }

    public static string ValidateRepository(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请输入 GitHub 仓库，格式为 owner/repository。");
        value = value.Trim().TrimEnd('/');
        if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) value = value[19..];
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)
            || value[(value.IndexOf('/') + 1)..] is "." or "..")
            throw new ArgumentException("请输入 GitHub 仓库，格式为 owner/repository。");
        return value;
    }
    public static Version ParseVersion(string tag)
    {
        if (string.IsNullOrEmpty(tag)
            || !Regex.IsMatch(tag, @"\A[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)
            || !Version.TryParse(tag[0] is 'v' or 'V' ? tag[1..] : tag, out var version))
            throw new InvalidOperationException("Release 标签需要使用 v0.1.0 这样的三段正式版本号。");
        return version;
    }
    public async Task<UpdateResult> CheckAsync(string repository, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string repo = ValidateRepository(repository);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await GetResponseAsync(new Uri("https://api.github.com/repos/" + repo + "/releases/latest"), true, timeout.Token).ConfigureAwait(false);
        byte[] json = await ReadBoundedAsync(response, MaximumJsonBytes, null, timeout.Token).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<Release>(json) ?? throw new InvalidOperationException("GitHub 未返回 Release。");
        if (release.Draft || release.Prerelease)
            throw new InvalidOperationException("GitHub 未返回可用的正式 Release。");
        var latest = ParseVersion(release.Tag);
        // Construct the browser destination from the validated repository and release tag.
        string url = "https://github.com/" + repo + "/releases/tag/" + Uri.EscapeDataString(release.Tag);
        UpdateAsset? installer = null, portable = null, checksums = null;
        string? reason = null;
        try
        {
            if (release.Assets is { Count: > 1000 }) throw new InvalidDataException("Release 附件清单过大。");
            installer = FindAsset(release, repo, "Roger-Setup-" + latest.ToString(3) + "-x64.exe", MaximumPackageBytes);
            portable = FindAsset(release, repo, "Roger-win-x64.zip", MaximumPackageBytes);
            checksums = FindAsset(release, repo, "SHA256SUMS.txt", MaximumChecksumBytes);
            if (checksums is null) reason = "此版本缺少有效的 SHA256SUMS.txt，无法在软件内更新。";
            else if (installer is null && portable is null) reason = "此版本没有可用的 Windows x64 更新附件。";
        }
        catch (InvalidDataException ex) { installer = portable = checksums = null; reason = ex.Message; }
        return new UpdateResult(latest > CurrentVersion, release.Tag, url, release.Body ?? "暂无更新说明。")
        { Repository = repo, Installer = installer, Portable = portable, Checksums = checksums, UnavailableReason = reason };
    }

    /// <summary>Downloads and validates only. Installation always remains a separate explicit action.</summary>
    public async Task<DownloadedUpdate> PrepareAsync(UpdateResult result, UpdatePackageKind kind,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        string repo = ValidateRepository(result.Repository);
        Version version = ParseVersion(result.Tag);
        if (!result.IsNewer || version <= CurrentVersion) throw new InvalidOperationException("只能下载比当前版本更新的正式版本。");
        string expectedName = kind switch
        {
            UpdatePackageKind.Installer => "Roger-Setup-" + version.ToString(3) + "-x64.exe",
            UpdatePackageKind.Portable => "Roger-win-x64.zip",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        UpdateAsset asset = (kind == UpdatePackageKind.Installer ? result.Installer : result.Portable)
            ?? throw new InvalidOperationException(result.UnavailableReason ?? "此版本没有所需的更新附件。");
        UpdateAsset manifest = result.Checksums ?? throw new InvalidOperationException("此版本缺少 SHA-256 校验文件。");
        ValidateAsset(asset, repo, result.Tag, expectedName, MaximumPackageBytes);
        ValidateAsset(manifest, repo, result.Tag, "SHA256SUMS.txt", MaximumChecksumBytes);
        if (Interlocked.CompareExchange(ref _preparing, 1, 0) != 0) throw new InvalidOperationException("更新包正在下载，请等待或取消当前任务。");

        string? operationDirectory = null, partial = null, completed = null;
        bool succeeded = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(_downloadTimeout);
        try
        {
            progress?.Report(new UpdateDownloadProgress(0, null, "下载校验文件"));
            using var checksumResponse = await GetResponseAsync(new Uri(manifest.DownloadUrl), false, timeout.Token).ConfigureAwait(false);
            byte[] checksumBytes = await ReadBoundedAsync(checksumResponse, MaximumChecksumBytes, manifest.SizeBytes, timeout.Token).ConfigureAwait(false);
            VerifyDigest(checksumBytes, manifest.Sha256Digest);
            string checksumText = new UTF8Encoding(false, true).GetString(checksumBytes);
            string expectedHash = ParseChecksums(checksumText, asset.Name);
            if (asset.Sha256Digest is string publishedHash && !publishedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub 附件摘要与 SHA256SUMS.txt 不一致，未下载或安装更新。");

            operationDirectory = Path.Combine(StorageDirectory, version.ToString(3) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationDirectory);
            partial = Path.Combine(operationDirectory, asset.Name + ".download");
            completed = Path.Combine(operationDirectory, asset.Name);
            progress?.Report(new UpdateDownloadProgress(0, asset.SizeBytes, "下载更新包"));
            using var packageResponse = await GetResponseAsync(new Uri(asset.DownloadUrl), false, timeout.Token).ConfigureAwait(false);
            if (packageResponse.Content.Headers.ContentLength is long advertised && advertised != asset.SizeBytes)
                throw new InvalidDataException("更新包大小与发布信息不一致。");
            long count = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            await using (var source = await packageResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                    if (count > asset.SizeBytes || count > MaximumPackageBytes) throw new InvalidDataException("更新包超过公布的大小限制。");
                    hasher.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new UpdateDownloadProgress(count, asset.SizeBytes, "下载更新包"));
                }
                await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            if (count != asset.SizeBytes) throw new InvalidDataException("更新包下载不完整，请重试。");
            progress?.Report(new UpdateDownloadProgress(count, count, "验证 SHA-256"));
            string actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，未执行安装。");
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, completed);
            succeeded = true;
            return new DownloadedUpdate(Path.GetFullPath(completed), actualHash, count, repo, result.Tag, version, kind, asset.Name);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("更新包下载超时，请检查网络后重试。"); }
        finally
        {
            if (!succeeded)
            {
                DeleteIfPresent(partial); DeleteIfPresent(completed);
                if (operationDirectory is not null) { try { Directory.Delete(operationDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            Interlocked.Exchange(ref _preparing, 0);
        }
    }

    private static UpdateAsset? FindAsset(Release release, string repo, string name, long maximum)
    {
        var matches = (release.Assets ?? new()).OfType<ReleaseAsset>().Where(a => a.Name == name).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Release 中存在重复的更新附件名称。");
        if (matches.Length == 0) return null;
        var found = matches[0];
        if (found.State != "uploaded" || found.Size is not > 0 || found.Size > maximum)
            throw new InvalidDataException("Release 附件大小或上传状态无效。");
        return new UpdateAsset(name, found.Size.Value, AssetUrl(repo, release.Tag, name), NormalizeDigest(found.Digest));
    }
    private static void ValidateAsset(UpdateAsset asset, string repo, string tag, string name, long maximum)
    {
        if (asset.Name != name || asset.SizeBytes <= 0 || asset.SizeBytes > maximum || asset.DownloadUrl != AssetUrl(repo, tag, name))
            throw new InvalidDataException("更新附件来源、名称或大小无效。");
        if (asset.Sha256Digest is not null && !Regex.IsMatch(asset.Sha256Digest, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("更新附件 SHA-256 摘要格式无效。");
    }
    private static string AssetUrl(string repo, string tag, string name) => "https://github.com/" + repo + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);
    private static string? NormalizeDigest(string? digest)
    {
        if (digest is null) return null;
        if (!Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            throw new InvalidDataException("GitHub 附件摘要格式无效。");
        return digest[7..].ToLowerInvariant();
    }
    internal static string ParseChecksums(string text, string expectedName)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.TrimStart('\uFEFF').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = Regex.Match(line.TrimEnd('\r'), @"\A(?<hash>[0-9a-fA-F]{64}) [ *](?<name>[A-Za-z0-9][A-Za-z0-9._-]{0,199})\z", RegexOptions.CultureInvariant);
            if (!match.Success || !entries.TryAdd(match.Groups["name"].Value, match.Groups["hash"].Value.ToLowerInvariant()))
                throw new InvalidDataException("SHA256SUMS.txt 格式无效或存在重复条目。");
        }
        return entries.TryGetValue(expectedName, out string? hash) ? hash : throw new InvalidDataException("校验文件中缺少对应更新包的 SHA-256。");
    }
    private async Task<HttpResponseMessage> GetResponseAsync(Uri initial, bool api, CancellationToken token)
    {
        Uri uri = initial;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            ValidateRequestUri(uri, api);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                if (response.RequestMessage?.RequestUri is Uri actual) ValidateRequestUri(actual, api);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    Uri? location = response.Headers.Location;
                    if (location is null || redirects == 5) throw new HttpRequestException("GitHub 下载重定向无效或次数过多。");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("GitHub 下载重定向次数过多。");
    }
    private static void ValidateRequestUri(Uri uri, bool api)
    {
        bool knownHost = api ? uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            : new[] { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !knownHost)
            throw new InvalidDataException("更新下载被重定向到未受信任的地址。");
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, long? expected, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && (length > maximum || expected.HasValue && length != expected.Value))
            throw new InvalidDataException("GitHub 响应大小无效。");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximum || expected.HasValue && output.Length + count > expected.Value)
                throw new InvalidDataException("GitHub 响应超过允许的大小。");
            output.Write(buffer, 0, count);
        }
        if (expected.HasValue && output.Length != expected.Value) throw new InvalidDataException("GitHub 响应不完整。");
        return output.ToArray();
    }
    private static void VerifyDigest(byte[] content, string? expected)
    {
        if (expected is not null && !Convert.ToHexString(SHA256.HashData(content)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub 校验附件摘要不匹配。");
    }
    private static void DeleteIfPresent(string? file)
    { if (file is not null) { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<ReleaseAsset?>? Assets { get; set; }
    }
    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}

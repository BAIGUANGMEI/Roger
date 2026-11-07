using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Roger.Services;
public sealed record UpdateResult(bool IsNewer, string Tag, string ReleaseUrl, string Notes);
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private bool _disposed;
    public static Version CurrentVersion
    {
        get
        {
            Version version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0);
            return new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
        }
    }

    /// <summary>A supplied HttpClient remains owned by its caller, enabling offline validation.</summary>
    public UpdateService(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Roger/" + CurrentVersion);
        if (_http.DefaultRequestHeaders.Accept.Count == 0)
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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
        var release = await _http.GetFromJsonAsync<Release>("https://api.github.com/repos/" + repo + "/releases/latest", token) ?? throw new InvalidOperationException("GitHub 未返回 Release。");
        if (release.Draft || release.Prerelease)
            throw new InvalidOperationException("GitHub 未返回可用的正式 Release。");
        var latest = ParseVersion(release.Tag);
        // Construct the browser destination from the validated repository and release tag.
        string url = "https://github.com/" + repo + "/releases/tag/" + Uri.EscapeDataString(release.Tag);
        return new UpdateResult(latest > CurrentVersion, release.Tag, url, release.Body ?? "暂无更新说明。");
    }
    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }
}

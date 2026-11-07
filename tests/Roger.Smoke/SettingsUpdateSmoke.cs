using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class SettingsUpdateSmoke
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Roger-Settings-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifySettings(root);
            VerifyVersionAndRepositoryParsing();
            await VerifyUpdatesAsync();
            Console.WriteLine("PASS settings roundtrip, replacement backup, corrupt-file preservation, validation; offline update URL, version, error and cancellation handling");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifySettings(string root)
    {
        string path = Path.Combine(root, "nested", "settings.json");
        var service = new SettingsService(path);
        AppSettings defaults = service.Load();
        Check(defaults.RefreshIntervalSeconds == 2 && defaults.ShowDock && service.LastLoadWarning is null, "missing settings use defaults without warning");
        Check(!File.Exists(path), "loading defaults must not create or overwrite a file");

        var original = new AppSettings
        {
            Theme = AppTheme.Light,
            SelectedGpuId = "/gpu-test/1",
            ShowCpu = false,
            ShowGpu = false,
            ShowTemperature = false,
            DockPosition = DockPosition.Free,
            DockLeft = -125.5,
            DockTop = 72.25,
            DockOpacity = 0.65,
            RefreshIntervalSeconds = 3,
            CompactDock = true,
            MinimizeToTray = false,
            CheckUpdatesOnStartup = true,
            UpdateRepository = "  https://github.com/example/Roger/  "
        };
        service.Save(original);
        string firstJson = File.ReadAllText(path);
        AppSettings loaded = new SettingsService(path).Load();
        Check(loaded.DockPosition == DockPosition.Free && loaded.DockLeft == -125.5 && loaded.DockTop == 72.25, "dock position roundtrip");
        Check(loaded.DockOpacity == 0.65 && loaded.RefreshIntervalSeconds == 3, "sampling and opacity roundtrip");
        Check(loaded.Theme == AppTheme.Light, "light theme persists across launches");
        Check(loaded.SelectedGpuId == "/gpu-test/1", "selected GPU identifier persists across launches");
        Check(!loaded.ShowCpu && !loaded.ShowGpu && loaded.ShowMemory && !loaded.ShowTemperature && loaded.CompactDock && !loaded.MinimizeToTray, "visibility flags roundtrip");
        Check(loaded.UpdateRepository == "example/Roger" && loaded.CheckUpdatesOnStartup, "repository URL normalized before persistence");
        Check(!File.Exists(path + ".bak"), "first save has no prior version to back up");

        loaded.RefreshIntervalSeconds = 10;
        loaded.DockOpacity = 1;
        service.Save(loaded);
        Check(File.ReadAllText(path + ".bak") == firstJson, "replacement preserves exact previous configuration");
        string secondJson = File.ReadAllText(path);
        Check(service.Load().RefreshIntervalSeconds == 10, "replacement installs the new configuration");
        loaded.RefreshIntervalSeconds = 1;
        loaded.DockOpacity = 0.4;
        service.Save(loaded);
        Check(File.ReadAllText(path + ".bak") == secondJson, "backup advances to immediately preceding save");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "successful saves leave no temporary files");

        string beforeInvalidSave = File.ReadAllText(path);
        AppSettings[] invalid =
        {
            new() { Theme = (AppTheme)999 },
            new() { SelectedGpuId = new string('x', 513) },
            new() { RefreshIntervalSeconds = 0 }, new() { RefreshIntervalSeconds = 11 },
            new() { DockOpacity = double.NaN }, new() { DockOpacity = double.PositiveInfinity },
            new() { DockOpacity = 0.39 }, new() { DockOpacity = 1.01 },
            new() { DockLeft = double.NegativeInfinity }, new() { DockTop = double.NaN },
            new() { DockPosition = (DockPosition)999 }, new() { UpdateRepository = "owner/.." },
            new() { UpdateRepository = "owner/repo?ref=main" }, new() { UpdateRepository = "https://example.com/owner/repo" }
        };
        foreach (AppSettings candidate in invalid)
        {
            Expect<ArgumentException>(() => service.Save(candidate));
            Check(File.ReadAllText(path) == beforeInvalidSave, "validation must preserve current settings before any write");
        }
        Expect<ArgumentNullException>(() => service.Save(null!));
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "failed validation leaves no temporary files");

        const string corrupt = "{ invalid JSON ";
        File.WriteAllText(path, corrupt);
        defaults = service.Load();
        Check(defaults.RefreshIntervalSeconds == 2 && service.LastLoadWarning?.Contains(path, StringComparison.Ordinal) == true, "corrupt JSON returns defaults and identifies preserved file");
        Check(File.ReadAllText(path) == corrupt, "loading corrupt JSON does not destroy it");
        service.Save(defaults);
        Check(File.ReadAllText(path + ".bak") == corrupt && service.LastLoadWarning is null, "explicit subsequent save preserves corrupt source as backup");

        foreach (string invalidJson in new[] { "null", "{\"DockOpacity\":2}", "{\"DockPosition\":999}", "{\"RefreshIntervalSeconds\":-1}", "{\"UpdateRepository\":\"owner/..\"}" })
        {
            File.WriteAllText(path, invalidJson);
            Check(service.Load().RefreshIntervalSeconds == 2 && service.LastLoadWarning is not null, "invalid saved values must not become live preferences");
            Check(File.ReadAllText(path) == invalidJson, "invalid saved values remain available for recovery");
        }
        service.Save(new AppSettings { UpdateRepository = " " });
        Check(service.Load().UpdateRepository == "" && service.LastLoadWarning is null, "unconfigured updater is valid");
    }

    private static void VerifyVersionAndRepositoryParsing()
    {
        Check(UpdateService.ParseVersion("v1.2.3") == new Version(1, 2, 3), "v prefix");
        Check(UpdateService.ParseVersion("V0.0.0") == new Version(0, 0, 0), "uppercase prefix and zero components");
        Check(UpdateService.ParseVersion("12.34.56") == new Version(12, 34, 56), "plain stable version");
        foreach (string tag in new[] { "", "vv1.2.3", "vV1.2.3", "1.2", "1", "1.2.3.4", "v01.2.3", "v1.02.3", "v1.2.03", "v1.2.3-beta.1", "v1.2.3+build", " v1.2.3", "v1.2.3\n", "2147483648.0.0", "-1.2.3", "../v1.2.3" })
            Expect<InvalidOperationException>(() => UpdateService.ParseVersion(tag));
        Expect<InvalidOperationException>(() => UpdateService.ParseVersion(null!));

        Check(UpdateService.ValidateRepository(" owner/Roger ") == "owner/Roger", "short repository normalization");
        Check(UpdateService.ValidateRepository("https://github.com/owner/Roger/") == "owner/Roger", "canonical HTTPS URL normalization");
        Check(UpdateService.ValidateRepository("Owner/.github") == "Owner/.github", "real leading-dot repository names remain valid");
        foreach (string repository in InvalidRepositories())
            Expect<ArgumentException>(() => UpdateService.ValidateRepository(repository));
        Expect<ArgumentException>(() => UpdateService.ValidateRepository(null!));
    }

    private static IEnumerable<string> InvalidRepositories()
    {
        return new[]
        {
            "", "owner", "owner/.", "owner/..", "owner/repo/other", "owner/repo?x=1", "owner/repo#fragment",
            "owner/repo%2f..", "owner/repo\\other", "../repo", "owner name/repo", "owner/repo\nmore",
            "https://github.com.evil.example/owner/repo", "https://github.com@evil.example/owner/repo",
            "https://github.com:443/owner/repo", "http://github.com/owner/repo", "file:///owner/repo",
            "https://example.com/owner/repo", new string('x', 40) + "/repo", "owner/" + new string('x', 101)
        };
    }

    private static async Task VerifyUpdatesAsync()
    {
        string latest = $"v{UpdateService.CurrentVersion.Major + 1}.0.0";
        string current = "v" + UpdateService.CurrentVersion;
        var handler = new ReleaseHandler();
        using var client = new HttpClient(handler);
        using var service = new UpdateService(client);
        handler.Json = JsonSerializer.Serialize(new { tag_name = latest, body = "发布说明", html_url = "https://untrusted.example/download.exe" });
        UpdateResult result = await service.CheckAsync("https://github.com/example/Roger/");
        Check(result.IsNewer && result.Tag == latest && result.Notes == "发布说明", "new stable release detected");
        Check(result.ReleaseUrl == "https://github.com/example/Roger/releases/tag/" + latest, "browser destination must be constructed from validated repo/tag instead of server html_url");
        Check(handler.LastUri?.AbsoluteUri == "https://api.github.com/repos/example/Roger/releases/latest", "only fixed GitHub API endpoint is requested");
        Check(handler.SawUserAgent && handler.SawGitHubAccept, "GitHub request identity and media type");

        handler.Json = JsonSerializer.Serialize(new { tag_name = current });
        result = await service.CheckAsync("example/Roger");
        Check(!result.IsNewer && result.Notes == "暂无更新说明。", "same version is not an update and missing notes are explicit");
        handler.Json = "{\"tag_name\":\"v0.0.0\"}";
        Check(!(await service.CheckAsync("example/Roger")).IsNewer, "older versions must not offer a downgrade");

        int requestCount = handler.Count;
        foreach (string repository in InvalidRepositories())
            await ExpectAsync<ArgumentException>(() => service.CheckAsync(repository));
        Check(handler.Count == requestCount, "invalid repositories must fail before HTTP dispatch");

        foreach (string payload in new[] { "null", "{}", "{\"tag_name\":null}", "{\"tag_name\":\"vv1.2.3\"}", "{\"tag_name\":\"v1.2.3.4\"}", "{\"tag_name\":\"v1.2.3\",\"prerelease\":true}", "{\"tag_name\":\"v1.2.3\",\"draft\":true}" })
        {
            handler.Json = payload;
            await ExpectAsync<InvalidOperationException>(() => service.CheckAsync("example/Roger"));
        }
        handler.Json = "{";
        await ExpectAsync<JsonException>(() => service.CheckAsync("example/Roger"));
        handler.StatusCode = HttpStatusCode.NotFound;
        await ExpectAsync<HttpRequestException>(() => service.CheckAsync("example/Roger"));
        handler.StatusCode = HttpStatusCode.OK;
        handler.Json = JsonSerializer.Serialize(new { tag_name = latest });
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => service.CheckAsync("example/Roger", cancellation.Token));
        }

        service.Dispose();
        await ExpectAsync<ObjectDisposedException>(() => service.CheckAsync("example/Roger"));
        using HttpResponseMessage response = await client.GetAsync("https://api.github.com/repos/example/Roger/releases/latest");
        Check(response.IsSuccessStatusCode, "disposing updater must preserve a caller-owned HttpClient");
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        public string Json { get; set; } = "{}";
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public int Count { get; private set; }
        public Uri? LastUri { get; private set; }
        public bool SawUserAgent { get; private set; }
        public bool SawGitHubAccept { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            LastUri = request.RequestUri;
            SawUserAgent = request.Headers.UserAgent.Any(x => x.Product?.Name == "Roger");
            SawGitHubAccept = request.Headers.Accept.Any(x => x.MediaType == "application/vnd.github+json");
            return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Settings/update smoke expected " + typeof(T).Name);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Settings/update smoke expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Settings/update smoke failed: " + message);
    }
}

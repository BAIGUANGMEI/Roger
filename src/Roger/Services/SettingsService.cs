using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roger.Models;

namespace Roger.Services;

/// <summary>Per-user preferences. Writes use an adjacent temporary file and atomic replacement.</summary>
public sealed class SettingsService
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string StoragePath { get; }
    public string? LastLoadWarning { get; private set; }
    internal bool IsLegacyStartupRegistered => StartupService.IsLegacyStartupRegistered;

    public SettingsService(string? storagePath = null)
    {
        StoragePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roger", "settings.json");
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            LastLoadWarning = null;
            if (!File.Exists(StoragePath))
                return new AppSettings();

            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StoragePath), JsonOptions)
                    ?? throw new JsonException("配置文件为空。");
                Validate(settings);
                return settings;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                LastLoadWarning = $"无法读取配置，已使用默认值。原文件保留在 {StoragePath}。{ex.Message}";
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(StoragePath))!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, json);
                if (File.Exists(StoragePath))
                    File.Replace(temporaryPath, StoragePath, StoragePath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(temporaryPath, StoragePath);
                LastLoadWarning = null;
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void SetLaunchAtLogin(bool enabled) => StartupService.SetEnabled(enabled);

    private static void Validate(AppSettings settings)
    {
        if (settings.SelectedGpuId is { Length: > 512 }) throw new ArgumentException("显卡标识过长。");
        if (!Enum.IsDefined(settings.Theme)) throw new ArgumentException("外观主题无效。");
        if (!Enum.IsDefined(settings.DockPosition))
            throw new ArgumentException("监控条位置无效。");
        if (settings.RefreshIntervalSeconds is < 1 or > 10)
            throw new ArgumentException("刷新间隔必须为 1–10 秒。");
        if (!double.IsFinite(settings.DockOpacity) || settings.DockOpacity is < 0.4 or > 1)
            throw new ArgumentException("透明度必须为 0.4–1。");
        if (!double.IsFinite(settings.DockLeft) || !double.IsFinite(settings.DockTop))
            throw new ArgumentException("监控条坐标无效。");

        settings.UpdateRepository = (settings.UpdateRepository ?? "").Trim();
        if (settings.UpdateRepository.Length > 0)
            settings.UpdateRepository = UpdateService.ValidateRepository(settings.UpdateRepository);
    }
}


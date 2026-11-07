using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Roger.Models;

namespace Roger.Services;

/// <summary>Per-user preferences. Writes use an adjacent temporary file and atomic replacement.</summary>
public sealed class SettingsService
{
    private const string StartupValueName = "Roger";
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string StoragePath { get; }
    public string? LastLoadWarning { get; private set; }

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

    public void SetLaunchAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? throw new IOException("无法访问当前用户的启动项。");
        if (!enabled)
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)
            || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从已发布的 Roger.exe 启用开机启动。");

        key.SetValue(StartupValueName, $"\"{executable}\" --tray", RegistryValueKind.String);
    }

    private static void Validate(AppSettings settings)
    {
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


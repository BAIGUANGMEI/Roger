using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Roger.Services;

/// <summary>Registers an elevated, interactive logon task only after the user enables startup.</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "Roger";

    internal static bool IsLegacyStartupRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(LegacyValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        string taskName = "Roger-Logon-" + sid;
        string? executable = null;
        if (enabled)
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("请以管理员身份启动 Roger 后设置登录启动。");
            executable = GetExecutablePath();
        }

        // Release every COM reference, including child collections, on success and failure.
        var references = new List<object>();
        object Keep(object value) { references.Add(value); return value; }
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows 任务计划程序不可用。");
            dynamic service = Keep(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("无法连接 Windows 任务计划程序。"));
            service.Connect();
            dynamic folder = Keep(service.GetFolder(@"\"));
            if (!enabled)
            {
                try { folder.DeleteTask(taskName, 0); }
                catch (COMException ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
                {
                    // A missing current-user task already satisfies the disabled state.
                }
                RemoveLegacyStartup();
                return;
            }

            dynamic definition = Keep(service.NewTask(0));
            dynamic registration = Keep(definition.RegistrationInfo);
            registration.Author = "Roger";
            registration.Description = "登录当前 Windows 用户时，以管理员权限启动 Roger 到托盘。";
            dynamic principal = Keep(definition.Principal);
            principal.UserId = sid;
            principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN; no password is stored.
            principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST.

            dynamic settings = Keep(definition.Settings);
            settings.Enabled = true;
            settings.AllowDemandStart = true;
            settings.StartWhenAvailable = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S"; // Continuous monitoring must not expire after 72 hours.
            settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW.

            dynamic triggers = Keep(definition.Triggers);
            dynamic trigger = Keep(triggers.Create(9)); // TASK_TRIGGER_LOGON.
            trigger.UserId = sid;
            trigger.Enabled = true;
            dynamic actions = Keep(definition.Actions);
            dynamic action = Keep(actions.Create(0)); // TASK_ACTION_EXEC; no shell or startup script.
            action.Path = executable;
            action.Arguments = "--tray";
            action.WorkingDirectory = Path.GetDirectoryName(executable);

            Keep(folder.RegisterTaskDefinition(taskName, definition, 6, sid, null, 3, null)
                ?? throw new IOException("Windows 未返回已注册的 Roger 登录启动任务。"));
            // Preserve the old entry if registration fails; migrate only after the new task exists.
            RemoveLegacyStartup();
        }
        finally
        {
            for (int i = references.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(references[i])) Marshal.ReleaseComObject(references[i]);
        }
    }

    private static string GetExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从 Roger.exe 启动后设置登录启动。");
        return Path.GetFullPath(path);
    }

    private static void RemoveLegacyStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }
}

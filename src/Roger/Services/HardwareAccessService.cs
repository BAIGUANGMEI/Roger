using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Roger.Services;

public sealed record HardwareAccessStatus(bool IsAdministrator, bool DriverInstalled, string? DriverVersion,
    bool CanOpenDriver, int DriverErrorCode, string Summary);

/// <summary>Diagnoses low-level sensor access and installs only the explicitly selected, signed prerequisite.</summary>
public static class HardwareAccessService
{
    public const string OfficialDriverUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    public const string OfficialDriverSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    public const string OfficialDriverPage = "https://pawnio.eu/";

    public static HardwareAccessStatus GetStatus()
    {
        bool admin = ElevationService.IsAdministrator;
        string? version = ReadDriverVersion();
        using var handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        bool canOpen = !handle.IsInvalid;
        int error = canOpen ? 0 : Marshal.GetLastWin32Error();
        bool installed = version is not null || canOpen || error == 5;
        string summary = !installed
            ? "需要安装 PawnIO 硬件传感器驱动后读取 CPU 温度。"
            : !admin
                ? "PawnIO 已安装；请以管理员模式重新打开 Roger 以读取 CPU 温度。"
                : !canOpen
                    ? $"PawnIO 驱动暂不可访问（Windows 错误 {error}）；安装后可能需要重启电脑。"
                    : "管理员模式与 PawnIO 驱动已就绪。";
        return new HardwareAccessStatus(admin, installed, version, canOpen, error, summary);
    }

    public static string CpuTemperatureUnavailableMessage(HardwareAccessStatus status)
    {
        if (!status.DriverInstalled) return "CPU 温度不可用：请在硬件页启用 CPU 温度，安装官方 PawnIO 传感器驱动。";
        if (!status.IsAdministrator) return "CPU 温度不可用：请在硬件页切换至管理员模式。";
        if (!status.CanOpenDriver) return "CPU 温度不可用：PawnIO 驱动尚未就绪，安装后可能需要重启电脑。";
        return "CPU 温度不可用：当前硬件或固件未返回有效传感器读数。";
    }

    /// <summary>Downloads a pinned official release, checks SHA-256 and Windows Authenticode. Never executes it.</summary>
    public static async Task<string> DownloadVerifiedDriverAsync(CancellationToken token = default)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roger", "Prerequisites", "PawnIO-2.2.0");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "PawnIO_setup.exe");
        if (File.Exists(target))
        {
            try { VerifyInstaller(target); return target; }
            catch (Exception ex) when (ex is InvalidDataException or SecurityException) { }
        }
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Roger-Hardware-Setup/" + UpdateService.CurrentVersion);
            using var response = await http.GetAsync(OfficialDriverUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            const int maximumBytes = 20 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("传感器驱动下载大小异常。");
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                int total = 0;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    total += count;
                    if (total > maximumBytes) throw new InvalidDataException("传感器驱动下载大小异常。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            VerifyInstaller(temporary);
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Invoke only from the user's explicit install action. Windows requests UAC as needed.</summary>
    public static async Task InstallDriverAsync(string verifiedInstallerPath, CancellationToken token = default)
    {
        VerifyInstaller(verifiedInstallerPath);
        token.ThrowIfCancellationRequested();
        using Process process = Process.Start(new ProcessStartInfo(Path.GetFullPath(verifiedInstallerPath), "-install -silent")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("无法启动官方传感器驱动安装程序。");
        // Once installation begins, wait for its outcome rather than interrupt a kernel driver installer.
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"传感器驱动安装未完成（退出代码 {process.ExitCode}）。");
        if (ReadDriverVersion() is null) throw new InvalidOperationException("未检测到 PawnIO 安装信息，请检查 Windows 安装结果后重试。");
    }

    public static void VerifyInstaller(string path)
    {
        using (var stream = File.OpenRead(path))
        {
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, OfficialDriverSha256, StringComparison.Ordinal))
                throw new InvalidDataException("传感器驱动 SHA-256 校验失败，未执行安装。");
        }
        if (!HasValidWindowsSignature(path)) throw new SecurityException("传感器驱动的 Windows 数字签名验证失败，未执行安装。");
    }

    private static string? ReadDriverVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key?.GetValue("DisplayVersion") is string version && Version.TryParse(version, out _)) return version;
            }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return null;
    }

    private static bool HasValidWindowsSignature(string path)
    {
        var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = Path.GetFullPath(path) };
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1,
            FileInfo = filePointer, StateAction = 1, ProviderFlags = 0x1000 | 0x10
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try { return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0; }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData, SipClientData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}

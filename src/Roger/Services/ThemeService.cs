using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Roger.Models;
namespace Roger.Services;
public static class ThemeService
{
    public static bool IsDark { get; private set; } = true;
    public static event EventHandler? Changed;
    public static void Apply(AppTheme theme)
    {
        bool dark = theme != AppTheme.Light;
        if (theme == AppTheme.System)
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = key?.GetValue("AppsUseLightTheme") is not int light || light == 0; }
            catch (System.Security.SecurityException) { dark = true; }
        }
        IsDark = dark;
        var colors = new Dictionary<string, (string Dark, string Light)>
        {
            ["Bg"] = ("#0C111A", "#F3F6FA"), ["Sidebar"] = ("#101722", "#FFFFFF"),
            ["Panel"] = ("#141D2A", "#FFFFFF"), ["SurfaceRaised"] = ("#192434", "#F8FAFD"),
            ["Line"] = ("#263346", "#E1E7EF"), ["TextPrimary"] = ("#EDF3FC", "#18283D"),
            ["Muted"] = ("#9AAAC0", "#60728B"), ["Faint"] = ("#6B809B", "#8291A6"),
            ["Accent"] = ("#65DEB1", "#087D5C"), ["AccentSoft"] = ("#18382F", "#E3F4EC"),
            ["AccentText"] = ("#092B20", "#FFFFFF"), ["Hover"] = ("#202E41", "#EDF2F8"),
            ["InputBg"] = ("#101824", "#FAFCFF"), ["Track"] = ("#28364A", "#E7EDF5"),
            ["GridAlternate"] = ("#162131", "#F6F9FD"), ["HeaderBg"] = ("#1B283A", "#EFF4FA"),
            ["ScrollThumb"] = ("#35465C", "#BFCCD9"), ["ScrollThumbHover"] = ("#5B718D", "#8C9EB5"),
            ["ChromeHover"] = ("#243347", "#E8EEF6"), ["Success"] = ("#65DEB1", "#087D5C"),
            ["Warning"] = ("#E8B969", "#95600C"), ["Danger"] = ("#F47D8F", "#C52C49"),
            ["CpuAccent"] = ("#65DEB1", "#087D5C"), ["GpuAccent"] = ("#B4A0FF", "#7954CB"),
            ["MemoryAccent"] = ("#78B9F8", "#246FBB"), ["NetworkAccent"] = ("#E8B969", "#95600C")
        };
        foreach (var entry in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? entry.Value.Dark : entry.Value.Light));
            brush.Freeze(); Application.Current.Resources[entry.Key] = brush;
        }
        if (Application.Current.MainWindow is Window window) WindowPlacementService.ApplyTheme(window, dark);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

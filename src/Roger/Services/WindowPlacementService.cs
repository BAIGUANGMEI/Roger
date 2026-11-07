using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Roger.Services;

/// <summary>Constrains a custom-chrome window's maximized bounds to its monitor's work area.</summary>
public static class WindowPlacementService
{
    private static readonly ConditionalWeakTable<Window, PlacementHook> Hooks = new();

    /// <summary>Call once after InitializeComponent. Hooks and handlers are removed when the window closes.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (!Hooks.TryGetValue(window, out _))
        {
            // WindowStyle=None alone retains the native resize frame, including a bright
            // non-client band on Windows 11. Own all frame edges while preserving resizing.
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                NonClientFrameEdges = NonClientFrameEdges.None,
                UseAeroCaptionButtons = false
            });
            Hooks.Add(window, new PlacementHook(window));
        }
    }

    /// <summary>Keep the native frame consistent when Roger's theme changes independently of Windows.</summary>
    public static void ApplyTheme(Window window, bool dark)
    {
        Attach(window);
        if (Hooks.TryGetValue(window, out var hook)) hook.SetTheme(dark);
    }

    private sealed class PlacementHook
    {
        private readonly Window _window;
        private HwndSource? _source;
        private bool _dark = true;

        public PlacementHook(Window window)
        {
            _window = window;
            _window.SourceInitialized += OnSourceInitialized;
            _window.Closed += OnClosed;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) Connect(handle);
        }

        private void OnSourceInitialized(object? sender, EventArgs e) => Connect(new WindowInteropHelper(_window).Handle);

        private void Connect(IntPtr handle)
        {
            if (_source is not null) return;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(HandleMessage);
            ApplyNativeTheme(handle);
        }

        public void SetTheme(bool dark)
        {
            _dark = dark;
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero) ApplyNativeTheme(handle);
        }

        private void ApplyNativeTheme(IntPtr handle)
        {
            int dark = _dark ? 1 : 0;
            // Attribute 20 is supported on current Windows 10/11; older builds used 19.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            // Windows 11-specific attributes are safely ignored on Windows 10.
            // Roger paints its own 1px border, so the DWM border must not add a bright edge.
            int noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
            int caption = ResourceColor("Sidebar", _dark ? Color.FromRgb(17, 23, 31) : Color.FromRgb(248, 250, 252));
            int text = ResourceColor("TextPrimary", _dark ? Color.FromRgb(233, 239, 246) : Color.FromRgb(20, 32, 44));
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }

        private int ResourceColor(string key, Color fallback)
        {
            Color color = (_window.TryFindResource(key) as SolidColorBrush)?.Color ?? fallback;
            return color.R | color.G << 8 | color.B << 16;
        }

        private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmGetMinMaxInfo = 0x0024;
            const uint MonitorDefaultToNearest = 0x00000002;
            if (message is 0x031A or 0x031E) ApplyNativeTheme(hwnd);
            if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

            // MINMAXINFO uses physical pixels and a monitor-relative maximum position.
            // No WPF DIP conversion is applied to monitor/work-area rectangles.
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var work = monitorInfo.Work;
            var bounds = monitorInfo.Monitor;
            int width = Math.Max(1, work.Right - work.Left);
            int height = Math.Max(1, work.Bottom - work.Top);
            limits.MaxPosition = new NativePoint(work.Left - bounds.Left, work.Top - bounds.Top);
            limits.MaxSize = new NativePoint(width, height);
            var scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96d;
            limits.MinTrackSize = new NativePoint(
                Math.Min(width, ToPhysicalMinimum(_window.MinWidth, scale)),
                Math.Min(height, ToPhysicalMinimum(_window.MinHeight, scale)));
            // Preserve native maximum tracking limits so normal resizing can span displays.
            Marshal.StructureToPtr(limits, lParam, fDeleteOld: false);
            handled = true;
            return IntPtr.Zero;
        }

        private static int ToPhysicalMinimum(double logical, double scale) =>
            double.IsFinite(logical) ? (int)Math.Clamp(Math.Ceiling(logical * scale), 0, int.MaxValue) : 0;

        private void OnClosed(object? sender, EventArgs e)
        {
            _source?.RemoveHook(HandleMessage);
            _source = null;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnClosed;
            Hooks.Remove(_window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
        public NativePoint(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

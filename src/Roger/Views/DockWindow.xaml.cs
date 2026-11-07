using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Roger.Models;
using Roger.Services;

namespace Roger.Views;

/// <summary>A standalone, per-monitor-DPI-aware overlay. Does not inject into Explorer.</summary>
public partial class DockWindow : Window
{
    private AppSettings _settings;
    private MonitorSnapshot _snapshot = MonitorSnapshot.Empty;
    private HwndSource? _source;
    private bool _positionPending;
    private bool _dragging;
    private bool _pointerGesture;
    private NativePoint _pointerStart;
    private NativeRect _gestureBounds;
    private double _gestureDpiScale;
    private bool _closed;
    private bool _externalMenuOpen;
    private IntPtr _handle;
    private readonly DockTopmostService _topmost;

    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsChanged;

    public DockWindow(AppSettings settings)
    {
        _settings = settings;
        // Subscribe before XAML can create the HWND, and repeat after rendering: a native
        // window's first size-to-content pass can run before it becomes visible.
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { EnsureNativeWindow(); SchedulePosition(); };
        ContentRendered += (_, _) => SchedulePosition();
        IsVisibleChanged += (_, _) => { if (IsVisible) SchedulePosition(); };
        SizeChanged += (_, _) => SchedulePosition();
        InitializeComponent();
        _topmost = new DockTopmostService(this, () => _pointerGesture || _externalMenuOpen || Shell.ContextMenu?.IsOpen == true);
        BuildContextMenu();
        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        Opacity = Math.Clamp(settings.DockOpacity, 0.4, 1);
        CpuGroup.Visibility = VisibleIf(settings.ShowCpu);
        GpuGroup.Visibility = VisibleIf(settings.ShowGpu);
        MemoryGroup.Visibility = VisibleIf(settings.ShowMemory);
        NetworkGroup.Visibility = VisibleIf(settings.ShowNetwork);
        CpuTemperature.Visibility = GpuTemperature.Visibility = VisibleIf(settings.ShowTemperature && !settings.CompactDock);
        EmptyMessage.Visibility = VisibleIf(!settings.ShowCpu && !settings.ShowGpu && !settings.ShowMemory && !settings.ShowNetwork);
        Shell.Padding = settings.CompactDock ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5);
        CpuGroup.MinWidth = GpuGroup.MinWidth = settings.CompactDock ? 38 : 69;
        MemoryGroup.MinWidth = settings.CompactDock ? 38 : 48;
        NetworkGroup.MinWidth = settings.CompactDock ? 94 : 107;
        NetworkUpload.Visibility = VisibleIf(!settings.CompactDock);
        UpdateSnapshot(_snapshot);
        UpdateTopmostMode();
        SchedulePosition();
    }

    public void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        CpuValue.Text = Percent(snapshot.CpuLoad);
        GpuValue.Text = Percent(snapshot.GpuLoad);
        MemoryValue.Text = snapshot.MemoryTotalGb > 0 ? Percent(snapshot.MemoryPercent) : "—";
        CpuTemperature.Text = Temperature(snapshot.CpuTemperature);
        GpuTemperature.Text = Temperature(snapshot.GpuTemperature);
        NetworkDownload.Text = "↓ " + Rate(snapshot.DownloadBytesPerSecond);
        NetworkUpload.Text = "↑ " + Rate(snapshot.UploadBytesPerSecond);
        CpuGroup.ToolTip = $"CPU {Percent(snapshot.CpuLoad)} · 温度 {Temperature(snapshot.CpuTemperature)}";
        GpuGroup.ToolTip = $"GPU {Percent(snapshot.GpuLoad)} · 温度 {Temperature(snapshot.GpuTemperature)}";
        MemoryGroup.ToolTip = $"内存 {snapshot.MemoryUsedGb:0.0} / {snapshot.MemoryTotalGb:0.0} GiB";
        NetworkGroup.ToolTip = $"{snapshot.NetworkName}\n下载 {Rate(snapshot.DownloadBytesPerSecond)} · 上传 {Rate(snapshot.UploadBytesPerSecond)}";
    }

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private static string Percent(double? value) => value.HasValue && double.IsFinite(value.Value) ? $"{Math.Clamp(value.Value, 0, 100):0}%" : "—";
    private static string Temperature(double? value) => value.HasValue && double.IsFinite(value.Value) ? $"{value.Value:0}°C" : "不可用";
    private static string Rate(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return "—";
        return bytes >= 1_000_000 ? $"{bytes / 1_000_000:0.0} MB/s" : $"{bytes / 1_000:0} KB/s";
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "SurfaceRaised");
        menu.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        menu.SetResourceReference(Control.BorderBrushProperty, "Line");
        var open = new MenuItem { Header = "打开 Roger" };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);
        var position = new MenuItem { Header = "监控条位置" };
        AddPosition(position, "任务栏右侧", DockPosition.TaskbarRight);
        AddPosition(position, "任务栏左侧", DockPosition.TaskbarLeft);
        AddPosition(position, "屏幕右上角", DockPosition.TopRight);
        AddPosition(position, "屏幕右下角", DockPosition.BottomRight);
        AddPosition(position, "自由拖动", DockPosition.Free);
        menu.Items.Add(position);
        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = "隐藏监控条" };
        hide.Click += (_, _) =>
        {
            _settings.ShowDock = false;
            Hide();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        menu.Items.Add(hide);
        Shell.ContextMenu = menu;
    }

    private void AddPosition(MenuItem parent, string label, DockPosition value)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) =>
        {
            _settings.DockPosition = value;
            UpdateTopmostMode();
            SchedulePosition();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        parent.Items.Add(item);
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => EnsureNativeWindow();

    private void UpdateTopmostMode() => _topmost.SetEnabled(
        _settings.DockPosition is DockPosition.TaskbarLeft or DockPosition.TaskbarRight or DockPosition.Free);

    internal void SetExternalMenuOpen(bool open)
    {
        _externalMenuOpen = open;
        if (!open && !_closed) _topmost.RequestRestore();
    }

    private void EnsureNativeWindow()
    {
        _handle = new WindowInteropHelper(this).Handle;
        if (_handle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(_handle);
        if (_source != source)
        {
            _source?.RemoveHook(WindowHook);
            _source = source;
            _source?.AddHook(WindowHook);
        }
        // ShowActivated only affects Show(). NOACTIVATE also keeps ordinary clicks from
        // activating the overlay and causing a foreground/taskbar Z-order round trip.
        var style = GetWindowLongPtr(_handle, -20).ToInt64();
        long desired = style | 0x80 | 0x08000000; // TOOLWINDOW | NOACTIVATE.
        if (style != desired) SetWindowLongPtr(_handle, -20, new IntPtr(desired));
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE: deliver the click without activating.
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE; do not use MA_NOACTIVATEANDEAT.
        }
        // Work area, display configuration, and per-monitor DPI changes all affect placement.
        if (message is 0x001A or 0x007E or 0x02E0)
            SchedulePosition();
        return IntPtr.Zero;
    }

    private void SchedulePosition()
    {
        if (_closed || _pointerGesture || _positionPending) return;
        _positionPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _positionPending = false;
            if (!_closed && !_pointerGesture && IsVisible)
            {
                EnsureNativeWindow();
                PositionWindow();
            }
        }));
    }

    private void PositionWindow()
    {
        if (_handle == IntPtr.Zero || ActualWidth < 1 || ActualHeight < 1) return;
        var screen = _settings.DockPosition == DockPosition.Free
            ? Forms.Screen.FromPoint(new System.Drawing.Point(SafeCoordinate(_settings.DockLeft), SafeCoordinate(_settings.DockTop)))
            : Forms.Screen.FromHandle(_handle);
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        var scale = Math.Max(1, GetDpiForWindow(_handle)) / 96d;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var gap = (int)Math.Ceiling(8 * scale);
        int left;
        int top;

        if (_settings.DockPosition == DockPosition.Free)
        {
            left = SafeCoordinate(_settings.DockLeft);
            top = SafeCoordinate(_settings.DockTop);
        }
        else if (_settings.DockPosition == DockPosition.TopRight)
        {
            left = work.Right - width - gap;
            top = work.Top + gap;
        }
        else
        {
            left = work.Right - width - gap;
            top = work.Bottom - height - gap;
            if (_settings.DockPosition is DockPosition.TaskbarLeft or DockPosition.TaskbarRight)
            {
                var taskbar = FindWindow("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var bar)
                    && bar.Right > bounds.Left && bar.Left < bounds.Right
                    && bar.Bottom > bounds.Top && bar.Top < bounds.Bottom
                    && bar.Right - bar.Left > bar.Bottom - bar.Top)
                {
                    top = bar.Top + Math.Max(0, ((bar.Bottom - bar.Top) - height) / 2);
                    // Reserve the system tray, using its actual HWND when Explorer exposes it.
                    var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                    var rightEdge = bar.Right - (int)(240 * scale);
                    if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect)) rightEdge = trayRect.Left;
                    left = _settings.DockPosition == DockPosition.TaskbarRight
                        ? rightEdge - width - gap
                        : bar.Left + (int)(64 * scale);
                }
                else if (_settings.DockPosition == DockPosition.TaskbarLeft)
                    left = work.Left + gap;
            }
        }

        left = Math.Clamp(left, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        top = Math.Clamp(top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        // Native physical-pixel positioning avoids mixed-DPI virtual desktop coordinate drift.
        if (!GetWindowRect(_handle, out var current) || current.Left != left || current.Top != top)
            SetWindowPos(_handle, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010);
        _topmost.RequestRestore();
    }

    private static int SafeCoordinate(double value) => double.IsFinite(value)
        ? (int)Math.Clamp(value, int.MinValue + 1d, int.MaxValue - 1d) : 0;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            EndPointerGesture(cancel: true);
            OpenRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (GetCursorPos(out var cursor) && BeginPointerGesture(cursor.X, cursor.Y)
            && !Mouse.Capture(this, CaptureMode.Element)) EndPointerGesture(cancel: true);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerGesture) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPointerGesture(); return; }
        if (GetCursorPos(out var cursor)) UpdatePointerGesture(cursor.X, cursor.Y);
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerGesture) return;
        EndPointerGesture();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pointerGesture) EndPointerGesture(cancel: true);
    }

    internal bool BeginPointerGesture(int screenX, int screenY)
    {
        if (_closed || _handle == IntPtr.Zero || !GetWindowRect(_handle, out _gestureBounds)) return false;
        _pointerStart = new NativePoint { X = screenX, Y = screenY };
        _gestureDpiScale = Math.Max(1, GetDpiForWindow(_handle)) / 96d;
        _pointerGesture = true;
        _dragging = false;
        return true;
    }

    internal void UpdatePointerGesture(int screenX, int screenY)
    {
        if (!_pointerGesture || _closed) return;
        long dx = (long)screenX - _pointerStart.X, dy = (long)screenY - _pointerStart.Y;
        if (!_dragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * _gestureDpiScale
            && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * _gestureDpiScale) return;
        _dragging = true;
        int left = SafeCoordinate(_gestureBounds.Left + (double)dx), top = SafeCoordinate(_gestureBounds.Top + (double)dy);
        if (GetWindowRect(_handle, out var current) && current.Left == left && current.Top == top) return;
        SetWindowPos(_handle, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
    }

    internal void EndPointerGesture(bool cancel = false)
    {
        if (!_pointerGesture) return;
        bool moved = _dragging;
        _pointerGesture = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (cancel && moved && !_closed)
            SetWindowPos(_handle, IntPtr.Zero, _gestureBounds.Left, _gestureBounds.Top, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
        else if (moved && !_closed && GetWindowRect(_handle, out var after)
            && (after.Left != _gestureBounds.Left || after.Top != _gestureBounds.Top))
        {
            _settings.DockPosition = DockPosition.Free;
            _settings.DockLeft = after.Left;
            _settings.DockTop = after.Top;
            UpdateTopmostMode();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        if (!_closed)
        {
            if (!moved || cancel) SchedulePosition();
            _topmost.RequestRestore();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        EndPointerGesture(cancel: true);
        _topmost.Dispose();
        _source?.RemoveHook(WindowHook);
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);
}


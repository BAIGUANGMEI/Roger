using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Roger.Services;

/// <summary>
/// Keeps a taskbar overlay above a taskbar that Explorer has just raised, without activating it.
/// WPF Topmost alone does not preserve order within the group of topmost windows.
/// </summary>
internal sealed class DockTopmostService : IDisposable
{
    private const uint ForegroundEvent = 0x0003;
    private const uint MenuEndEvent = 0x0005;
    private const uint MenuPopupEndEvent = 0x0007;
    private const uint ReorderEvent = 0x8004;
    private const uint SkipOwnProcess = 0x0002; // Out-of-context (0) callbacks run on this UI thread.
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly Window _window;
    private readonly Func<bool> _isInteractionInProgress;
    private readonly Func<IReadOnlyList<IntPtr>> _findTaskbars;
    private readonly WinEventDelegate _callback;
    private DispatcherOperation? _restoreOperation;
    private readonly DispatcherTimer _watchdog;
    private readonly IntPtr _desktop = GetDesktopWindow();
    private IReadOnlyList<IntPtr> _taskbars = Array.Empty<IntPtr>();
    private IntPtr _systemHook, _reorderHook;
    private IntPtr _lastForeground;
    private bool _foregroundIsShellPopup;
    private bool _enabled, _running, _disposed;

    internal DockTopmostService(Window window, Func<bool> isInteractionInProgress,
        Func<IReadOnlyList<IntPtr>>? findTaskbars = null)
    {
        _window = window;
        _isInteractionInProgress = isInteractionInProgress;
        _findTaskbars = findTaskbars ?? FindTaskbars;
        _callback = OnWinEvent;
        // Windows 11 does not emit a usable foreground event for every taskbar click.
        // This fallback only repairs an actual overlap with a taskbar above our window.
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(500) };
        _watchdog.Tick += OnWatchdog;
        _window.IsVisibleChanged += OnVisibilityChanged;
    }

    internal void SetEnabled(bool enabled)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _enabled = enabled;
        UpdateRegistration();
    }

    internal void RequestRestore()
    {
        if (_running) ScheduleRestore();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRegistration();

    private void UpdateRegistration()
    {
        bool run = _enabled && _window.IsVisible && new WindowInteropHelper(_window).Handle != IntPtr.Zero;
        if (!run) { Stop(); return; }
        if (!_running)
        {
            _running = true;
            _taskbars = _findTaskbars();
            _systemHook = SetWinEventHook(ForegroundEvent, MenuPopupEndEvent, IntPtr.Zero, _callback, 0, 0, SkipOwnProcess);
            _reorderHook = SetWinEventHook(ReorderEvent, ReorderEvent, IntPtr.Zero, _callback, 0, 0, SkipOwnProcess);
            _watchdog.Start();
        }
        ScheduleRestore();
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime)
    {
        if (!_running || _disposed) return;
        if (eventType is ForegroundEvent or MenuEndEvent or MenuPopupEndEvent)
            ScheduleRestore();
        else if (eventType == ReorderEvent && idChild == 0 && (hwnd == _desktop || IsKnownTaskbar(hwnd)))
            ScheduleRestore();
    }

    private bool IsKnownTaskbar(IntPtr hwnd)
    {
        foreach (var bar in _taskbars) if (bar == hwnd) return true;
        return false;
    }

    private void ScheduleRestore()
    {
        if (_restoreOperation is { Status: DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing }) return;
        // Repair confirmed taskbar overlap on the next dispatcher pass. An additional
        // 50 ms timer left a visible covered frame after taskbar activation.
        _restoreOperation = _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            _restoreOperation = null;
            RestoreIfCovered();
        }));
    }

    private void OnWatchdog(object? sender, EventArgs e)
    {
        // Resolve handles again so Explorer restarts and secondary taskbars need no app restart.
        _taskbars = _findTaskbars();
        RestoreIfCovered();
    }

    private void RestoreIfCovered()
    {
        if (!_running || _disposed || !_window.IsVisible || _isInteractionInProgress() || IsShellMenuActive()) return;
        IntPtr dock = new WindowInteropHelper(_window).Handle;
        if (dock == IntPtr.Zero || !GetWindowRect(dock, out var dockRect)) return;
        foreach (IntPtr taskbar in _taskbars)
        {
            if (taskbar == dock || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out var barRect)
                || !Overlaps(dockRect, barRect)) continue;
            bool topmost = (GetWindowLongPtr(dock, -20).ToInt64() & 0x8) != 0;
            if (topmost && !IsAbove(taskbar, dock)) continue;
            // Do not use SWP_NOZORDER: it would discard HWND_TOPMOST, which caused the bug.
            // SWP_NOACTIVATE preserves the taskbar/app receiving the user's click and focus.
            SetWindowPos(dock, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
            return;
        }
    }

    private bool IsShellMenuActive()
    {
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(0, ref gui) && (gui.Flags & (0x4 | 0x8 | 0x10)) != 0) return true;
        IntPtr foreground = GetForegroundWindow();
        if (foreground != _lastForeground)
        {
            _lastForeground = foreground;
            _foregroundIsShellPopup = false;
            if (foreground != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foreground, out uint processId);
                try
                {
                    using var process = Process.GetProcessById(checked((int)processId));
                    _foregroundIsShellPopup = process.ProcessName is "StartMenuExperienceHost" or "ShellExperienceHost" or "SearchHost" or "SearchApp";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return _foregroundIsShellPopup;
    }

    private static bool IsAbove(IntPtr candidate, IntPtr dock)
    {
        IntPtr previous = GetWindow(dock, 3); // GW_HWNDPREV: only windows above Dock in native Z order.
        for (int i = 0; previous != IntPtr.Zero && i < 1024; i++)
        {
            if (previous == candidate) return true;
            IntPtr next = GetWindow(previous, 3);
            if (next == previous) break;
            previous = next;
        }
        return false;
    }

    private static bool Overlaps(NativeRect first, NativeRect second) =>
        first.Left < second.Right && first.Right > second.Left && first.Top < second.Bottom && first.Bottom > second.Top;

    private static IReadOnlyList<IntPtr> FindTaskbars()
    {
        var result = new List<IntPtr>(2);
        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) result.Add(primary);
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            result.Add(secondary);
        return result;
    }

    private void Stop()
    {
        _running = false;
        _restoreOperation?.Abort(); _restoreOperation = null;
        _watchdog.Stop();
        if (_systemHook != IntPtr.Zero) { UnhookWinEvent(_systemHook); _systemHook = IntPtr.Zero; }
        if (_reorderHook != IntPtr.Zero) { UnhookWinEvent(_reorderHook); _reorderHook = IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _window.Dispatcher.VerifyAccess();
        _disposed = true;
        Stop();
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _watchdog.Tick -= OnWatchdog;
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint eventThread, uint eventTime);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size; public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd,
        IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

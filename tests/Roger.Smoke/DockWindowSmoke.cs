using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Roger.Models;
using Roger.Services;
using Roger.Views;

/// <summary>
/// Opt-in interactive-desktop check. Uses transparent, non-activating windows owned by this test;
/// it never sends input to or changes the position/Z order of Explorer or another application.
/// </summary>
public static class DockWindowSmoke
{
    public static Task RunAsync()
    {
        if (!Environment.UserInteractive || GetForegroundWindow() == IntPtr.Zero)
        {
            Console.WriteLine("SKIP dock desktop check: no interactive desktop is available");
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                // Never instantiate Roger.App: its StartupUri would launch the actual main window.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources["TextPrimary"] = Brushes.White;
                app.Resources["Muted"] = Brushes.SlateGray;
                app.Resources["Accent"] = Brushes.MediumAquamarine;
                app.Resources["SurfaceRaised"] = Brushes.Black;
                app.Resources["Line"] = Brushes.SlateGray;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await VerifyAsync(); }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        app.Shutdown();
                        // This check owns Dispatcher.Run rather than Application.Run.
                        // Shut down that loop explicitly so the test task can complete.
                        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }));
                Dispatcher.Run();
                if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Roger dock desktop regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task VerifyAsync()
    {
        // Corner mode disables DockWindow's production shell guard. The injected guard below
        // exercises the same native recovery code against a controlled taskbar substitute.
        var settings = new AppSettings
        {
            DockPosition = DockPosition.TopRight,
            CompactDock = true, ShowCpu = true, ShowGpu = false, ShowMemory = false, ShowNetwork = false
        };
        var dock = new DockWindow(settings) { Opacity = 0, ShowActivated = false };
        var probe = new Window
        {
            Title = "Roger smoke taskbar substitute", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            Opacity = 0, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
            Width = 32, Height = 24, Left = 24, Top = 24
        };
        DockTopmostService? guard = null;
        IntPtr dockHandle = IntPtr.Zero, probeHandle = IntPtr.Zero;
        bool interacting = false;
        IntPtr originalForeground = GetForegroundWindow();
        try
        {
            dock.Show();
            probe.Show();
            dockHandle = new WindowInteropHelper(dock).Handle;
            probeHandle = new WindowInteropHelper(probe).Handle;
            Check(dockHandle != IntPtr.Zero && probeHandle != IntPtr.Zero, "test HWND creation");
            await Task.Delay(150); // Finish the initial size-to-content/placement pass.
            VerifyMouseActivation(dockHandle, originalForeground);
            VerifyDoubleClick(dock, originalForeground);
            await VerifyPointerGesturesAsync(dock, settings, dockHandle, originalForeground);
            Check(GetWindowRect(dockHandle, out var bounds), "dock physical bounds");
            Check(SetWindowPos(probeHandle, HwndTopmost, bounds.Left, bounds.Top, 24, 16,
                SwpNoActivate | SwpNoOwnerZOrder), "position the test-only taskbar substitute");

            guard = new DockTopmostService(dock, () => interacting, () => new[] { probeHandle });
            guard.SetEnabled(true);
            await WaitUntilAsync(() => IsAbove(dockHandle, probeHandle), "initial native topmost restore");
            await VerifyNoRedundantRestoresAsync(guard, dockHandle);

            PromoteProbe(probeHandle);
            Check(IsAbove(probeHandle, dockHandle), "reproduce a newer topmost window covering the dock");
            await WaitUntilAsync(() => IsAbove(dockHandle, probeHandle), "automatic recovery after topmost overlap");
            Check(GetForegroundWindow() == originalForeground, "topmost recovery must not steal foreground focus");

            interacting = true;
            PromoteProbe(probeHandle);
            await Task.Delay(750);
            Check(IsAbove(probeHandle, dockHandle), "postpone restoration during an active dock interaction");
            interacting = false;
            await WaitUntilAsync(() => IsAbove(dockHandle, probeHandle), "restore after the interaction ends");

            dock.Hide();
            SetNotTopmost(dockHandle);
            await Task.Delay(750);
            Check(!IsWindowVisible(dockHandle), "a hidden dock must remain hidden");
            Check(!IsTopmost(dockHandle), "a hidden dock must stop native topmost restoration");

            dock.Show();
            await WaitUntilAsync(() => IsTopmost(dockHandle), "resume topmost recovery after showing the dock");
            await Task.Delay(150); // Drain the visible/position pass before testing disposal.
            guard.Dispose();
            guard = null;
            SetNotTopmost(dockHandle);
            await Task.Delay(750);
            Check(!IsTopmost(dockHandle), "disposed guard must stop restoring native topmost state");
            dock.Close();
            await Task.Delay(100);
            Check(!IsWindow(dockHandle), "closing the dock destroys its HWND");
            Check(GetForegroundWindow() == originalForeground, "desktop check must leave foreground focus unchanged");
            Console.WriteLine("PASS dock click non-activation, thresholded drag/commit/cancel, double-click routing, no redundant reordering, native Z-order recovery, no focus stealing, interaction deferral, hide/show and disposal");
        }
        finally
        {
            guard?.Dispose();
            if (IsWindow(probeHandle)) probe.Close();
            if (IsWindow(dockHandle)) dock.Close();
        }
    }

    private static void VerifyMouseActivation(IntPtr dock, IntPtr originalForeground)
    {
        Check((GetWindowLongPtr(dock, -20).ToInt64() & 0x08000000) != 0,
            "the actual dock HWND has WS_EX_NOACTIVATE, not only ShowActivated=false");
        // These are messages to the invisible HWND owned by this test only. MA_NOACTIVATE
        // preserves the foreground while allowing the original left/right mouse message.
        foreach (int mouseMessage in new[] { 0x0201, 0x0204 }) // WM_LBUTTONDOWN, WM_RBUTTONDOWN.
        {
            IntPtr result = SendMessage(dock, 0x0021, dock, new IntPtr((mouseMessage << 16) | 1)); // HTCLIENT.
            Check(result == new IntPtr(3), "WM_MOUSEACTIVATE returns MA_NOACTIVATE without eating mouse input");
            Check(GetForegroundWindow() == originalForeground, "left/right clicks never activate the dock");
        }
    }

    private static void VerifyDoubleClick(DockWindow dock, IntPtr originalForeground)
    {
        int opens = 0;
        EventHandler onOpen = (_, _) => opens++;
        dock.OpenRequested += onOpen;
        try
        {
            // Raise the same routed event used by XAML, without moving or clicking the real
            // pointer. WPF normally fills the internal ClickCount setter in its input provider.
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = dock };
            PropertyInfo clickCount = typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!;
            clickCount.SetValue(doubleClick, 2);
            dock.RaiseEvent(doubleClick);
            Check(opens == 1 && doubleClick.Handled, "double-click routes exactly one open request");
            Check(GetForegroundWindow() == originalForeground, "dock double-click handler leaves activation to the main-window subscriber");
        }
        finally { dock.OpenRequested -= onOpen; }
    }

    private static async Task VerifyPointerGesturesAsync(DockWindow dock, AppSettings settings, IntPtr dockHandle, IntPtr originalForeground)
    {
        Check(GetWindowRect(dockHandle, out var before), "initial gesture bounds");
        double originalLeft = settings.DockLeft, originalTop = settings.DockTop;
        DockPosition originalPosition = settings.DockPosition;
        int changes = 0, saves = 0;
        EventHandler onSettings = (_, _) => saves++;
        var source = HwndSource.FromHwnd(dockHandle)!;
        HwndSourceHook observe = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0046) changes++; // Observe writes to this test HWND only.
            return IntPtr.Zero;
        };
        dock.SettingsChanged += onSettings;
        source.AddHook(observe);
        try
        {
            int startX = before.Left + 8, startY = before.Top + 8;
            double scale = Math.Max(1, GetDpiForWindow(dockHandle)) / 96d;
            int threshold = (int)Math.Ceiling(SystemParameters.MinimumHorizontalDragDistance * scale);
            int jitter = Math.Max(0, threshold - 1);
            for (int i = 0; i < 10; i++)
            {
                Check(dock.BeginPointerGesture(startX, startY), "single-click gesture begins");
                dock.UpdatePointerGesture(startX + jitter, startY);
                dock.EndPointerGesture();
            }
            await Task.Delay(100); // Also catch deferred placement/Z-order writes after a click.
            Check(changes == 0, "single-click and sub-threshold jitter never reposition or reorder the dock");
            Check(saves == 0 && settings.DockPosition == originalPosition && settings.DockLeft == originalLeft && settings.DockTop == originalTop,
                "single-click does not switch to free placement or save coordinates");
            Check(GetWindowRect(dockHandle, out var afterClick) && SamePosition(before, afterClick), "single-click leaves physical position unchanged");
            Check(GetForegroundWindow() == originalForeground, "single-click keeps the original foreground");

            int targetX = startX - Math.Max(48, threshold + 1), targetY = startY + 24;
            Check(dock.BeginPointerGesture(startX, startY), "real drag begins");
            dock.UpdatePointerGesture(targetX, targetY);
            Check(GetWindowRect(dockHandle, out var moved) && !SamePosition(before, moved), "moving past the threshold repositions the actual HWND");
            Check(saves == 0 && settings.DockPosition == originalPosition, "dragging does not prematurely persist free placement");
            int afterMove = changes;
            dock.UpdatePointerGesture(targetX, targetY);
            Check(changes == afterMove, "a repeated drag point does not write the same native position twice");
            dock.EndPointerGesture();
            Check(saves == 1 && settings.DockPosition == DockPosition.Free && settings.DockLeft == moved.Left && settings.DockTop == moved.Top,
                "release commits the actual final physical coordinates exactly once");
            dock.EndPointerGesture();
            Check(saves == 1, "a second release cannot save the completed drag twice");
            Check(GetForegroundWindow() == originalForeground, "non-activating drag retains the original foreground");

            Check(dock.BeginPointerGesture(moved.Left + 8, moved.Top + 8), "cancellable drag begins");
            dock.UpdatePointerGesture(moved.Left + 8 - Math.Max(48, threshold + 1), moved.Top + 32);
            dock.EndPointerGesture(cancel: true);
            Check(GetWindowRect(dockHandle, out var cancelled) && SamePosition(moved, cancelled), "cancel restores the drag's original native location");
            Check(saves == 1 && settings.DockLeft == moved.Left && settings.DockTop == moved.Top,
                "cancel preserves the previously committed settings without a new save");
        }
        finally
        {
            dock.EndPointerGesture(cancel: true);
            dock.SettingsChanged -= onSettings;
            source.RemoveHook(observe);
            settings.DockPosition = originalPosition; settings.DockLeft = originalLeft; settings.DockTop = originalTop;
            dock.ApplySettings(settings);
        }
        await Task.Delay(150); // Drain the placement pass after restoring the test-only settings.
    }

    private static bool SamePosition(NativeRect first, NativeRect second) => first.Left == second.Left && first.Top == second.Top;

    private static async Task VerifyNoRedundantRestoresAsync(DockTopmostService guard, IntPtr dock)
    {
        var source = HwndSource.FromHwnd(dock)!;
        int changes = 0;
        HwndSourceHook observe = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0046) changes++; // WM_WINDOWPOSCHANGING.
            return IntPtr.Zero;
        };
        source.AddHook(observe);
        try
        {
            for (int i = 0; i < 10; i++) guard.RequestRestore();
            await Task.Delay(650); // Exercise the queued request and the 500 ms watchdog.
            Check(changes == 0, "restoring an already-uncovered dock does not reorder or repaint its HWND");
        }
        finally { source.RemoveHook(observe); }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string label)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Dock desktop regression: " + label);
    }

    private static void PromoteProbe(IntPtr probe) => Check(SetWindowPos(probe, HwndTopmost, 0, 0, 0, 0,
        SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder), "promote the test-only substitute");
    private static void SetNotTopmost(IntPtr window) => Check(SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0,
        SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder), "reset test HWND topmost state");
    private static bool IsTopmost(IntPtr window) => (GetWindowLongPtr(window, -20).ToInt64() & 0x8) != 0;

    private static bool IsAbove(IntPtr higher, IntPtr lower)
    {
        var seen = new HashSet<IntPtr>();
        for (IntPtr current = GetWindow(lower, 3); current != IntPtr.Zero && seen.Count < 2048 && seen.Add(current); current = GetWindow(current, 3))
            if (current == higher) return true;
        return false;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Dock desktop regression: " + label);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
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
        var dock = new DockWindow(new AppSettings
        {
            DockPosition = DockPosition.TopRight,
            CompactDock = true, ShowCpu = true, ShowGpu = false, ShowMemory = false, ShowNetwork = false
        }) { Opacity = 0, ShowActivated = false };
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
            Check(GetWindowRect(dockHandle, out var bounds), "dock physical bounds");
            Check(SetWindowPos(probeHandle, HwndTopmost, bounds.Left, bounds.Top, 24, 16,
                SwpNoActivate | SwpNoOwnerZOrder), "position the test-only taskbar substitute");

            guard = new DockTopmostService(dock, () => interacting, () => new[] { probeHandle });
            guard.SetEnabled(true);
            await WaitUntilAsync(() => IsAbove(dockHandle, probeHandle), "initial native topmost restore");

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
            Console.WriteLine("PASS dock native Z-order recovery, no focus stealing, interaction deferral, hide/show and disposal");
        }
        finally
        {
            guard?.Dispose();
            if (IsWindow(probeHandle)) probe.Close();
            if (IsWindow(dockHandle)) dock.Close();
        }
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
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
}

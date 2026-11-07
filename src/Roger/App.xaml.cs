using System;
using System.Threading;
using System.Windows;
using Roger.Services;

namespace Roger;
public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        // Fixed maintenance action used by the uninstaller; never opens a monitoring window.
        if (Array.IndexOf(e.Args, "--unregister-startup") >= 0)
        {
            if (e.Args.Length != 1) { Shutdown(2); return; }
            try { StartupService.SetEnabled(false); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        try { ElevationService.WaitForPreviousInstance(e.Args); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Roger"); Shutdown(); return; }
        _mutex = new Mutex(true, "Local\\Roger.Desktop.Instance", out bool first);
        if (!first) { MessageBox.Show("Roger 已在运行，请从系统托盘打开。", "Roger"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { MessageBox.Show("操作未完成：" + args.Exception.Message, "Roger"); args.Handled = true; };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}

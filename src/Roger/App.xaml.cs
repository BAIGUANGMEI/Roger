using System;
using System.Threading;
using System.Windows;

namespace Roger;
public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\Roger.Desktop.Instance", out bool first);
        if (!first) { MessageBox.Show("Roger 已在运行，请从系统托盘打开。", "Roger"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { MessageBox.Show("操作未完成：" + args.Exception.Message, "Roger"); args.Handled = true; };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}

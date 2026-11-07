using System;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            await DiskNetworkSmoke.RunAsync();
            await SettingsUpdateSmoke.RunAsync();
            await FileSystemCheckSmoke.RunAsync();
            await HardwareAccessSmoke.RunAsync();
            await OptimizationSmoke.RunAsync();
            await StorageSmoke.RunAsync();
            await HardwareVendorSmoke.RunAsync();
            await AlertSmoke.RunAsync();
            await ProcessSmoke.RunAsync();
            await OptimizationJournalSmoke.RunAsync();
            await HistorySmoke.RunAsync();
            if (Array.IndexOf(args, "--desktop") >= 0) await DockWindowSmoke.RunAsync();
            if (Array.IndexOf(args, "--hardware") >= 0) { await HardwareSmoke.RunAsync(); await StorageSmoke.RunLiveAsync(); }
            Console.WriteLine("All Roger smoke checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

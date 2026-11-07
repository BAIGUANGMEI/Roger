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
            if (Array.IndexOf(args, "--hardware") >= 0) await HardwareSmoke.RunAsync();
            Console.WriteLine("All Roger smoke checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

using System;
using System.Threading.Tasks;
using Roger.Services;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args) => await UpdateInstallEngine.RunAsync(args);
}

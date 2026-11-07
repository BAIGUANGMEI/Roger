using System;
using System.IO;
using System.Threading.Tasks;
using Roger.Services;

public static class HardwareAccessSmoke
{
    public static Task RunAsync()
    {
        // The installer must be rejected before any elevation or process start when its bytes change.
        string path = Path.Combine(Path.GetTempPath(), "Roger-Untrusted-Prerequisite-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(path, "This is not the pinned official driver installer.");
            bool rejected = false;
            try { HardwareAccessService.VerifyInstaller(path); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Hardware access check: changed prerequisite bytes were accepted.");
            foreach (string[] args in new[] { new[] { "--wait-for-exit" }, new[] { "--wait-for-exit", "invalid" }, new[] { "--wait-for-exit", "0" }, new[] { "--wait-for-exit", Environment.ProcessId.ToString() } })
            {
                rejected = false;
                try { ElevationService.WaitForPreviousInstance(args); }
                catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Hardware access check: invalid restart parent accepted.");
            }
            ElevationService.WaitForPreviousInstance(Array.Empty<string>());
            Console.WriteLine("PASS hardware prerequisite rejects tampered installer and invalid elevated restart arguments without executing anything");
            return Task.CompletedTask;
        }
        finally { File.Delete(path); }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

// Single-file installer: the payload (AnimatedLauncher.exe, ThemeStudio.exe and
// the install/uninstall scripts) rides inside as resources. Extract to %TEMP%,
// run Install.ps1, propagate its exit code. /silent and /quiet are accepted for
// package managers (Install.ps1 is already non-interactive); /nostart forwards
// -NoStart so nothing launches afterwards.
internal static class Setup
{
    private static readonly string[] Payload =
    {
        "AnimatedLauncher.exe", "ThemeStudio.exe",
        "Install.ps1", "Install.cmd", "Uninstall.ps1", "Uninstall.cmd"
    };

    private static int Main(string[] args)
    {
        bool noStart = false;
        foreach (string arg in args)
        {
            string a = (arg ?? "").Trim().TrimStart('-', '/').ToLowerInvariant();
            if (a == "silent" || a == "quiet" || a == "s" || a == "q") continue; // accepted; already silent
            if (a == "nostart") { noStart = true; continue; }
            Console.Error.WriteLine("Unknown switch: " + arg + "  (supported: /silent, /quiet, /nostart)");
            return 2;
        }

        string dir = Path.Combine(Path.GetTempPath(),
            "animated-launchers-setup-" + Process.GetCurrentProcess().Id.ToString());
        try
        {
            Directory.CreateDirectory(dir);
            Assembly self = Assembly.GetExecutingAssembly();
            foreach (string name in Payload)
            {
                string resource = "payload/" + name;
                using (Stream stream = self.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        Console.Error.WriteLine("The installer is incomplete — missing " + name + ".");
                        return 1;
                    }
                    using (FileStream file = File.Create(Path.Combine(dir, name)))
                        stream.CopyTo(file);
                }
            }

            string script = Path.Combine(dir, "Install.ps1");
            string arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -NoBuild" + (noStart ? " -NoStart" : "");
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                WorkingDirectory = dir
            };
            using (Process install = Process.Start(info))
            {
                install.WaitForExit();
                return install.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("The installer could not run: " + ex.Message);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

// Turns an unexpected error into a log entry and, for the wallpaper helper, one automatic restart.
internal static class CrashGuard
{
    internal const int ExitCode = 70;
    private const string RestartVariable = "ANIMATED_RESTART_OF";
    private static bool helper;
    private static DateTime? restartedAt;
    private static Action cleanup;
    private static int uiThread, handling, handlingThread;

    // Test switch ANIMATED_FAULT: start, tick or thread, optionally followed by ",always". Empty when unset.
    internal static string Fault { get; private set; }

    // Call once on the thread that will run the message loop, before any window exists and
    // before the single-instance mutex is created.
    internal static void Install(bool helperMode)
    {
        helper = helperMode;
        uiThread = Thread.CurrentThread.ManagedThreadId;
        Fault = Environment.GetEnvironmentVariable("ANIMATED_FAULT") ?? "";
        string parent = Environment.GetEnvironmentVariable(RestartVariable);
        Environment.SetEnvironmentVariable(RestartVariable, null);
        int pid;
        if (helperMode && int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out pid))
        {
            restartedAt = DateTime.UtcNow;
            WaitForExit(pid);
            AppLog.Write("crash", "started again after the helper stopped unexpectedly");
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Report(e.Exception, "message loop"); };
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
            Report(e.ExceptionObject as Exception ??
                new Exception(Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture)), "background thread");
        };
    }

    // What to do on the message-loop thread just before the process ends (best effort).
    internal static void OnExit(Action action) { cleanup = action; }

    // Writes the error to the log and ends the process. The helper starts itself again first,
    // unless it did so less than ten minutes ago. Does not return.
    internal static void Report(Exception error, string where)
    {
        if (Interlocked.Exchange(ref handling, 1) != 0)
        {
            // A second error while the first is being handled: let the first one finish.
            if (Thread.CurrentThread.ManagedThreadId == handlingThread) return;
            Thread.Sleep(Timeout.Infinite);
        }
        handlingThread = Thread.CurrentThread.ManagedThreadId;
        try
        {
            AppLog.Write("crash", "unexpected error in the " + where + ": " + error);
            bool again = false;
            if (helper)
            {
                string note = "not starting again: this is the second stop within ten minutes";
                if (CrashPolicy.MayRestart(restartedAt, DateTime.UtcNow)) again = StartAgain(out note);
                AppLog.Write("crash", again ? "starting the helper again" : note);
            }
            if (cleanup != null && Thread.CurrentThread.ManagedThreadId == uiThread)
            {
                try { cleanup(); } catch (Exception) { }
            }
            AppLog.Flush();
            if (helper && !again) Tell(error);
            AppLog.Flush();
        }
        catch (Exception) { }
        Environment.Exit(ExitCode);
    }

    private static bool StartAgain(out string failure)
    {
        failure = null;
        try
        {
            var info = new ProcessStartInfo(AppSession.HelperExe(), "--artwork") { UseShellExecute = false };
            using (var self = Process.GetCurrentProcess())
                info.EnvironmentVariables[RestartVariable] = self.Id.ToString(CultureInfo.InvariantCulture);
            // A forced fault happens once unless the test asks for it every time.
            if (Fault.IndexOf(",always", StringComparison.Ordinal) < 0) info.EnvironmentVariables.Remove("ANIMATED_FAULT");
            using (Process.Start(info)) { }
            return true;
        }
        catch (Exception error)
        {
            failure = "could not start the helper again: " + error.Message;
            return false;
        }
    }

    private static void Tell(Exception error)
    {
        if (Fault.Length != 0)
        {
            AppLog.Write("crash", "no message shown (forced fault)");
            return;
        }
        string quiet = QuietTime.Check(AppTable.OwnsAnyProcess);
        if (quiet != null)
        {
            AppLog.Write("crash", "no message shown: " + quiet);
            return;
        }
        MessageBox.Show("The Animated Launchers wallpaper helper stopped because of a problem and stays closed for now.\n\n" +
            error.GetBaseException().Message + "\n\nIt starts again when you open an app through its launcher or sign in to Windows.\n\nDetails: " + AppLog.FilePath,
            "Animated Launchers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using (var parent = Process.GetProcessById(pid)) parent.WaitForExit(10000);
        }
        catch (ArgumentException) { }          // Already gone.
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}

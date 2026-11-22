using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// One small text log per app: <app data folder>\Logs\helper.log. Every process of that app's
// launcher (the helper, the launcher, the animation) appends to the same file. The file stays
// under MaxBytes; the one before it is kept as helper.1.log. Logging never throws and shows nothing.
internal static class AppLog
{
    internal const long MaxBytes = 256 * 1024;
    internal const string FileName = "helper.log", OlderName = "helper.1.log";
    private const int MaxEntry = 16000;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(60);
    private static readonly object gate = new object();
    private static string path, older, tag, userProfile, mutexName;
    private static string lastEntry;
    private static DateTime lastWritten;
    private static int repeats;

    // Tests replace the clock.
    internal static Func<DateTime> Clock = delegate { return DateTime.Now; };

    // Null until Start has succeeded.
    internal static string FilePath { get { lock (gate) return path; } }

    // folder: the app's data folder. mode: one word for what this process does.
    internal static void Start(string folder, string mode)
    {
        lock (gate)
        {
            path = null;
            lastEntry = null;
            repeats = 0;
            try
            {
                string logs = Path.Combine(Path.GetFullPath(folder), "Logs");
                Directory.CreateDirectory(logs);
                using (var self = Process.GetCurrentProcess())
                    tag = self.Id.ToString(CultureInfo.InvariantCulture) + " " + mode;
                userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                older = Path.Combine(logs, OlderName);
                string file = Path.Combine(logs, FileName);
                using (var sha = SHA1.Create())
                    mutexName = @"Local\AnimatedLaunchers.Log." + BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(file.ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
                path = file;
            }
            catch (Exception) { path = null; }
        }
    }

    // The same entry again within a minute is counted instead of written.
    internal static void Write(string area, string message)
    {
        lock (gate)
        {
            if (path == null) return;
            try
            {
                DateTime now = Clock();
                string entry = area + ": " + Clean(message);
                if (entry == lastEntry && now >= lastWritten && now - lastWritten < RepeatWindow)
                {
                    repeats++;
                    return;
                }
                string text = Pending(now) + Line(now, entry);
                lastEntry = entry;
                lastWritten = now;
                Append(text);
            }
            catch (Exception) { }
        }
    }

    // Writes the count of entries that were not written out, if any. Call before the process ends.
    internal static void Flush()
    {
        lock (gate)
        {
            if (path == null) return;
            try
            {
                string text = Pending(Clock());
                if (text.Length != 0) Append(text);
            }
            catch (Exception) { }
        }
    }

    private static string Pending(DateTime now)
    {
        if (repeats == 0) return "";
        string text = Line(now, lastEntry + " [repeated " + repeats.ToString(CultureInfo.InvariantCulture) +
            " more time" + (repeats == 1 ? "" : "s") + "]");
        repeats = 0;
        return text;
    }

    private static string Line(DateTime now, string entry)
    {
        return now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + tag + "] " + entry + "\r\n";
    }

    // One entry stays one block: its later lines are indented. The user's profile folder is not written out.
    private static string Clean(string message)
    {
        string text = (message ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        if (text.Length > MaxEntry) text = text.Substring(0, MaxEntry) + " [cut]";
        if (!string.IsNullOrEmpty(userProfile))
        {
            int at;
            while ((at = text.IndexOf(userProfile, StringComparison.OrdinalIgnoreCase)) >= 0)
                text = text.Substring(0, at) + "%USERPROFILE%" + text.Substring(at + userProfile.Length);
        }
        return text.Replace("\n", "\r\n    ");
    }

    private static void Append(string text)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        if (!MakeRoom(bytes.Length)) return;
        // Append-only access makes each write land at the end of the file, whichever process wrote last.
        using (var file = new FileStream(path, FileMode.Append, FileSystemRights.AppendData,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None))
            file.Write(bytes, 0, bytes.Length);
    }

    // Moves a full log aside. False when the log is far over its limit and cannot be moved.
    private static bool MakeRoom(int incoming)
    {
        if (Length() + incoming <= MaxBytes) return true;
        try
        {
            using (var mutex = new Mutex(false, mutexName))
            {
                bool held;
                try { held = mutex.WaitOne(250); }
                catch (AbandonedMutexException) { held = true; }
                if (held)
                {
                    try
                    {
                        if (Length() + incoming > MaxBytes) // Another process may have moved it already.
                        {
                            File.Delete(older);
                            File.Move(path, older);
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Length() + incoming <= MaxBytes * 4;
    }

    private static long Length()
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }
}

// When a helper that stopped unexpectedly may start itself again.
internal static class CrashPolicy
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    // restartedAt: when this process was started as an automatic restart; null for a normal start.
    internal static bool MayRestart(DateTime? restartedAt, DateTime now)
    {
        return restartedAt == null || now < restartedAt.Value || now - restartedAt.Value >= Window;
    }
}

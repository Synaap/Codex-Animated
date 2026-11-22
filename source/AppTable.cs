using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// The supported desktop applications, described in one table instead of build-time
// defines. One compiled binary serves every app: a copy named <App>Animated.exe is
// that app's launcher, and AnimatedLauncher.exe --artwork is the shared wallpaper
// helper for all of them.
internal enum AppKind { Regular, Squirrel, Store }

internal sealed class App
{
    internal readonly string Id, Name, Product, HelperName, WindowClass;
    internal readonly AppKind Kind;
    internal readonly string ExecutableName, InstallFolder, Scheme;
    internal readonly string PackageFamily, Aumid;
    internal readonly string[] StoreImageNames;
    // How Theme Studio sketches the app's window behind a theme: "editor" or "chat".
    internal readonly string Preview;
    // A user-added app: the exact executable path it was picked from. WindowClass
    // is null for these — any visible top-level window of the process matches.
    internal readonly string CustomExe;
    internal bool IsCustom { get { return CustomExe != null; } }
    private volatile string[] directories;

    private App(AppKind kind, string id, string name, string executableName, string installFolder,
        string scheme, string packageFamily, string[] storeImageNames, string preview, string helperName,
        string customExe, string windowClass)
    {
        Kind = kind;
        Id = id;
        Name = name;
        Product = name + " Animated";
        HelperName = helperName ?? (name + "Animated");
        WindowClass = windowClass;
        ExecutableName = executableName;
        InstallFolder = installFolder;
        Scheme = scheme;
        PackageFamily = packageFamily;
        Aumid = packageFamily == null ? null : packageFamily + "!App";
        StoreImageNames = storeImageNames;
        Preview = preview;
        CustomExe = customExe;
    }

    internal static App Regular(string id, string name, string executableName, string installFolder,
        string scheme, string preview = "editor", string helperName = null)
    {
        return new App(AppKind.Regular, id, name, executableName, installFolder, scheme, null, null, preview, helperName,
            null, "Chrome_WidgetWin_1");
    }

    internal static App Squirrel(string id, string name, string executableName, string installFolder,
        string scheme, string preview = "editor", string helperName = null)
    {
        return new App(AppKind.Squirrel, id, name, executableName, installFolder, scheme, null, null, preview, helperName,
            null, "Chrome_WidgetWin_1");
    }

    internal static App Store(string id, string name, string packageFamily, string[] storeImageNames, string preview = "editor")
    {
        return new App(AppKind.Store, id, name, null, null, null, packageFamily, storeImageNames, preview, null,
            null, "Chrome_WidgetWin_1");
    }

    // A user-added app: matched by its exact executable path, any window class.
    internal static App Custom(string id, string name, string exePath, string preview = "editor")
    {
        return new App(AppKind.Regular, id, name, Path.GetFileName(exePath), Path.GetDirectoryName(exePath),
            null, null, null, preview, null, exePath, null);
    }

    // Test switch: a scratch data root instead of LocalApplicationData. Every app's
    // folder then lives under it as <root>\<Product>. ThemeLibrary.DataRoot is the
    // one definition.
    internal string SettingsDirectory { get { return Path.Combine(ThemeLibrary.DataRoot, Product); } }

    internal string[] Directories
    {
        get
        {
            var list = directories;
            if (list == null) directories = list = Probe();
            return list;
        }
    }

    // Whether the desktop app looks installed for this Windows account.
    // Test switch ANIMATED_ASSUME_INSTALLED (comma-separated app ids) answers yes
    // directly, so the shared helper watches a test app under a scratch data root.
    internal bool Installed
    {
        get
        {
            string forced = Environment.GetEnvironmentVariable("ANIMATED_ASSUME_INSTALLED");
            if (!string.IsNullOrEmpty(forced))
                foreach (string id in forced.Split(','))
                    if (string.Equals(id.Trim(), Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsCustom) return File.Exists(CustomExe);
            if (Kind == AppKind.Store)
                return Directory.Exists(Path.Combine(ThemeLibrary.DataRoot, "Packages", PackageFamily));
            return ExecutablePath() != null;
        }
    }

    // Opens the target application. Starting it again lets it bring an existing
    // session forward.
    internal void Launch()
    {
        if (Kind == AppKind.Store) { ActivateStore(); return; }
        string executable = ExecutablePath();
        if (executable == null)
            throw new FileNotFoundException("The " + Name + " desktop app is not installed for this Windows account.");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }

    // The first existing target executable among the candidate roots, or null.
    // The Store package is activated through its AUMID instead.
    internal string ExecutablePath()
    {
        if (IsCustom) return File.Exists(CustomExe) ? CustomExe : null;
        if (Kind == AppKind.Store) return null;
        if (Kind == AppKind.Squirrel)
        {
            // The stub at the root starts the newest version; fall back to the highest app-<version> folder.
            foreach (string directory in Directories)
            {
                string stub = Path.Combine(directory, ExecutableName);
                if (File.Exists(stub)) return stub;
            }
            string best = null;
            Version bestVersion = null;
            string bestText = null;
            foreach (string directory in Directories)
            {
                string[] folders;
                try { folders = Directory.GetDirectories(directory, "app-*"); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string folder in folders)
                {
                    string exe = Path.Combine(folder, ExecutableName);
                    if (!File.Exists(exe)) continue;
                    string text = Path.GetFileName(folder).Substring(4);
                    Version version;
                    bool parsed = Version.TryParse(text, out version);
                    bool better;
                    if (best == null) better = true;
                    else if (parsed && bestVersion != null) better = version > bestVersion;
                    else if (parsed != (bestVersion != null)) better = parsed;
                    else better = string.CompareOrdinal(text, bestText) > 0;
                    if (better) { best = exe; bestVersion = parsed ? version : null; bestText = text; }
                }
            }
            return best;
        }
        foreach (string directory in Directories)
        {
            string path = Path.Combine(directory, ExecutableName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // Whether a process image path belongs to the target desktop app itself.
    internal bool Owns(string imagePath)
    {
        if (IsCustom) return string.Equals(imagePath, CustomExe, StringComparison.OrdinalIgnoreCase);
        if (Kind == AppKind.Store)
        {
            string file = Path.GetFileName(imagePath);
            foreach (string allowed in StoreImageNames)
                if (string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase))
                    return imagePath.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }
        if (!string.Equals(Path.GetFileName(imagePath), ExecutableName, StringComparison.OrdinalIgnoreCase))
            return false;
        string directory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(directory)) return false;
        if (Kind == AppKind.Squirrel)
        {
            // claude.exe in an install root or in an app-<version> folder directly under
            // one, so a folder created by an update while the helper runs is accepted.
            // Other programs also called claude.exe live elsewhere and never match.
            string parent = null;
            if (Path.GetFileName(directory).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                parent = Path.GetDirectoryName(directory);
            foreach (string candidate in Directories)
            {
                if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase)) return true;
                if (parent != null && string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        foreach (string candidate in Directories)
            if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Whether a live process (by id) belongs to the target app.
    internal bool OwnsProcess(uint pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try
        {
            uint length = 32768;
            var image = new StringBuilder((int)length);
            if (!QueryFullProcessImageName(process, 0, image, ref length)) return false;
            if (!Owns(image.ToString())) return false;
            if (Kind == AppKind.Store)
            {
                uint familyLength = 0;
                if (GetPackageFamilyName(process, ref familyLength, null) != 122) return false;
                var family = new StringBuilder((int)familyLength);
                if (GetPackageFamilyName(process, ref familyLength, family) != 0) return false;
                return string.Equals(family.ToString(), PackageFamily, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
        finally { CloseHandle(process); }
    }

    // When the process (by id) was created; false when it cannot be queried.
    internal bool TryGetStartTime(uint pid, out DateTime utc)
    {
        utc = DateTime.MinValue;
        IntPtr process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try
        {
            long created, exited, kernel, user;
            if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) return false;
            utc = DateTime.FromFileTimeUtc(created);
            return true;
        }
        finally { CloseHandle(process); }
    }

    // A visible top-level target window, or zero when the app is not running.
    // Used by the launcher to skip the splash when a session already exists.
    internal IntPtr FindMainWindow()
    {
        IntPtr found = IntPtr.Zero;
        string windowClass = WindowClass;
        EnumWindows(delegate(IntPtr window, IntPtr param) {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
            var cls = new StringBuilder(128);
            GetClassNameW(window, cls, cls.Capacity);
            if (windowClass != null && cls.ToString() != windowClass) return true;
            uint pid;
            if (GetWindowThreadProcessId(window, out pid) == 0) return true;
            if (OwnsProcess(pid))
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void ActivateStore()
    {
        var activator = (IApplicationActivationManager)new ApplicationActivationManager();
        uint pid;
        int hr = activator.ActivateApplication(Aumid, null, 0, out pid);
        if (hr != 0) throw new System.ComponentModel.Win32Exception(hr);
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string aumid,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string aumid,
            IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string aumid,
            IntPtr itemArray, out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder output, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder family);

    private string[] Probe()
    {
        var list = new List<string>();
        if (IsCustom) { Add(list, Path.GetDirectoryName(CustomExe)); return list.ToArray(); }
        if (Kind == AppKind.Store) return list.ToArray();
        if (Kind == AppKind.Squirrel)
        {
            Add(list, Path.Combine(ThemeLibrary.DataRoot, InstallFolder));
            Add(list, RegisteredCommandDirectory());
            return list.ToArray();
        }
        Add(list, Path.Combine(ThemeLibrary.DataRoot, "Programs", InstallFolder));
        foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            string root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root)) Add(list, Path.Combine(root, InstallFolder));
        }
        Add(list, RegisteredCommandDirectory());
        return list.ToArray();
    }

    // The protocol registration records the real install location even when the
    // executable sits outside the usual Programs folders.
    private string RegisteredCommandDirectory()
    {
        if (Kind == AppKind.Store || Scheme == null) return null;
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Classes\" + Scheme + @"\shell\open\command"))
            {
                string command = key == null ? null : key.GetValue(null) as string;
                // Shaped like: "C:\…\Devin.exe" --open-url -- "%1"
                if (string.IsNullOrEmpty(command) || command[0] != '"') return null;
                int end = command.IndexOf('"', 1);
                if (end <= 1) return null;
                string folder = Path.GetDirectoryName(command.Substring(1, end - 1));
                if (Kind == AppKind.Squirrel && folder != null &&
                    Path.GetFileName(folder).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
                    folder = Path.GetDirectoryName(folder);
                return folder;
            }
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException ||
                error is System.Security.SecurityException)) throw;
            return null;
        }
    }

    private static void Add(List<string> list, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { path = Path.GetFullPath(path); }
        catch (ArgumentException) { return; }
        catch (NotSupportedException) { return; }
        catch (System.Security.SecurityException) { return; }
        foreach (string existing in list)
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
        list.Add(path);
    }
}

internal static class AppTable
{
    // The one compiled binary and the names it can be installed under.
    internal const string HelperExeName = "AnimatedLauncher.exe";
    internal const string HelperProcessName = "AnimatedLauncher";
    internal const string RunValueName = "AnimatedLaunchers";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // One helper and one playing animation across all apps.
    internal const string ArtworkMutex = @"Local\AnimatedLaunchers.Artwork";
    internal const string PlayMutex = @"Local\AnimatedLaunchers.Splash.Play";

    internal static readonly App[] All =
    {
        App.Regular("devin", "Devin", "Devin.exe", "Devin", "devin"),
        App.Regular("antigravity", "Antigravity", "Antigravity IDE.exe", "Antigravity IDE", "antigravity"),
        App.Store("codex", "Codex", "OpenAI.Codex_2p2nqsd0c76g0", new[] { "ChatGPT.exe", "Codex.exe" }, "chat"),
        App.Squirrel("claude", "Claude", "claude.exe", "AnthropicClaude", "claude", "chat"),
        App.Regular("vscode", "VS Code", "Code.exe", "Microsoft VS Code", "vscode", "editor", "VSCodeAnimated"),
        App.Regular("cursor", "Cursor", "Cursor.exe", "cursor", "cursor"),
        App.Regular("kiro", "Kiro", "Kiro.exe", "Kiro", "kiro"),
        App.Regular("windsurf", "Windsurf", "Windsurf.exe", "Windsurf", "windsurf"),
        App.Regular("vscodium", "VSCodium", "VSCodium.exe", "VSCodium", "vscodium"),
        App.Regular("obsidian", "Obsidian", "Obsidian.exe", "Obsidian", "obsidian")
    };

    // The built-ins plus the user's own apps from custom-apps.ini.
    internal static App[] WithCustom()
    {
        App[] custom = CustomApps.Current;
        if (custom.Length == 0) return All;
        var list = new App[All.Length + custom.Length];
        All.CopyTo(list, 0);
        custom.CopyTo(list, All.Length);
        return list;
    }

    internal static App ById(string id)
    {
        if (id == null) return null;
        foreach (App app in WithCustom())
            if (string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase)) return app;
        return null;
    }

    // The app a launcher copy stands for, from its file name (DevinAnimated.exe -> devin).
    // The neutral AnimatedLauncher.exe stands for no single app.
    internal static App ByExeName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            fileName = fileName.Substring(0, fileName.Length - 4);
        foreach (App app in WithCustom())
            if (string.Equals(app.HelperName, fileName, StringComparison.OrdinalIgnoreCase)) return app;
        return null;
    }

    // Whether the pid belongs to any of the supported apps.
    internal static bool OwnsAnyProcess(uint pid)
    {
        foreach (App app in WithCustom())
            if (app.OwnsProcess(pid)) return true;
        return false;
    }

    // The shared start-with-Windows entry exists exactly while at least one app is
    // watched. App folders' settings.ini watch keys decide; the helper exe must exist.
    internal static void SyncRunValue(string installFolder)
    {
        string exe = Path.Combine(installFolder, HelperExeName);
        bool wanted = File.Exists(exe);
        if (wanted)
        {
            bool any = false;
            foreach (App app in WithCustom())
                if (AppProfile.Load(app.SettingsDirectory).Watch) { any = true; break; }
            wanted = any;
        }
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (wanted) key.SetValue(RunValueName, "\"" + exe + "\" --artwork");
            else key.DeleteValue(RunValueName, false);
        }
    }
}

// The user's own apps: <SharedRoot>\custom-apps.ini, one line per app
// "id=Name|C:\full\app.exe|preview". An entry keeps its App instance while the
// line is unchanged, so the helper's session dictionary stays stable.
internal static class CustomApps
{
    internal const string FileName = "custom-apps.ini";
    private static readonly object gate = new object();
    private static readonly Dictionary<string, App> byId = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
    private static App[] order = new App[0];

    internal static string FilePath { get { return Path.Combine(ThemeLibrary.SharedRoot, FileName); } }

    internal static App[] Current { get { lock (gate) { Reload(); return order; } } }

    // Parses the file; entries with a bad shape, a bad id or a built-in id are skipped.
    private static void Reload()
    {
        var previous = new Dictionary<string, App>(byId, StringComparer.OrdinalIgnoreCase);
        byId.Clear();
        var next = new List<App>();
        foreach (var pair in ParseEntries(IniFile.Load(FilePath)))
        {
            App app;
            previous.TryGetValue(pair.Id, out app);
            if (app == null || !Same(app, pair)) app = App.Custom(pair.Id, pair.Name, pair.Exe, pair.Preview);
            byId[app.Id] = app;
            next.Add(app);
        }
        order = next.ToArray();
    }

    private static bool Same(App app, Entry entry)
    {
        return string.Equals(app.Name, entry.Name, StringComparison.Ordinal) &&
            string.Equals(app.CustomExe, entry.Exe, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(app.Preview, entry.Preview, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class Entry { internal string Id, Name, Exe, Preview; }

    // The file's valid entries. An id is [a-z0-9][a-z0-9_-]{0,23} and must not be
    // a built-in's; the exe must be rooted and the preview editor or chat.
    internal static List<Entry> ParseEntries(IniFile ini)
    {
        var list = new List<Entry>();
        foreach (string key in ini.Keys)
        {
            string id = (key ?? "").Trim().ToLowerInvariant();
            if (!ValidId(id) ||
                Array.Exists(AppTable.All, a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
            string[] parts = (ini.Get(key) ?? "").Split('|');
            if (parts.Length < 2) continue;
            string name = CleanName(parts[0]), exe = parts[1].Trim();
            string preview = parts.Length > 2 ? parts[2].Trim() : "";
            if (name.Length == 0 || !Path.IsPathRooted(exe)) continue;
            if (!string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new Entry {
                Id = id, Name = name, Exe = exe,
                Preview = preview == "chat" ? "chat" : "editor" });
        }
        return list;
    }

    internal static bool ValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 24) return false;
        foreach (char c in id)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
        return true;
    }

    internal static string CleanName(string raw)
    {
        var chars = new StringBuilder();
        foreach (char c in raw ?? "")
            chars.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == '|' || c == '=' ? ' ' : c);
        string name = string.Join(" ", chars.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        return name.Length > 40 ? name.Substring(0, 40).TrimEnd('.', ' ') : name;
    }

    // A new id from the app name, unique across built-ins and customs.
    private static string NewId(string name)
    {
        var chars = new StringBuilder();
        foreach (char c in (name ?? "").ToLowerInvariant())
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) chars.Append(c);
            else if (chars.Length > 0 && chars[chars.Length - 1] != '-') chars.Append('-');
        string stem = chars.ToString().Trim('-');
        if (stem.Length == 0) stem = "app";
        if (stem.Length > 20) stem = stem.Substring(0, 20).TrimEnd('-');
        for (int i = 0; ; i++)
        {
            string id = i == 0 ? stem : stem + "-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AppTable.ById(id) == null && !byId.ContainsKey(id)) return id;
        }
    }

    // Adds an entry and saves the file. error explains a refusal; returns the app.
    internal static App Add(string name, string exePath, string preview, out string error)
    {
        lock (gate)
        {
            error = null;
            name = CleanName(name);
            exePath = (exePath ?? "").Trim();
            if (name.Length == 0) { error = "the app needs a name"; return null; }
            if (!Path.IsPathRooted(exePath)) { error = "pick the app's .exe file"; return null; }
            if (!string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase))
            { error = "the file is not a program (.exe)"; return null; }
            if (!File.Exists(exePath)) { error = "that file was not found"; return null; }
            Reload();
            foreach (App existing in order)
                if (string.Equals(existing.CustomExe, exePath, StringComparison.OrdinalIgnoreCase))
                { error = "that app is already added"; return null; }
            App app = App.Custom(NewId(name), name, exePath, preview == "chat" ? "chat" : "editor");
            Directory.CreateDirectory(ThemeLibrary.SharedRoot);
            ThemeLibrary.WithLock(ThemeLibrary.SharedRoot, delegate {
                IniFile ini = IniFile.Load(FilePath);
                ini.Set(app.Id, app.Name + "|" + app.CustomExe + "|" + app.Preview);
                ini.Save(FilePath);
            });
            byId[app.Id] = app;
            var next = new List<App>(order) { app };
            order = next.ToArray();
            return app;
        }
    }

    internal static bool Remove(string id)
    {
        lock (gate)
        {
            Reload();
            if (!byId.ContainsKey(id)) return false;
            ThemeLibrary.WithLock(ThemeLibrary.SharedRoot, delegate {
                IniFile ini = IniFile.Load(FilePath);
                ini.Set(id, null);
                ini.Save(FilePath);
            });
            byId.Remove(id);
            order = Array.FindAll(order, a => !string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            return true;
        }
    }
}

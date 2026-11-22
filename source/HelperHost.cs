using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// The single wallpaper helper process. It hosts one AppSession per supported app,
// one tray icon with a submenu per app, and the message loop. Sessions isolate
// their own faults; only a process-level crash goes through CrashGuard.
internal sealed class HelperHost : ApplicationContext
{
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly Dictionary<App, AppSession> sessions = new Dictionary<App, AppSession>();
    private readonly Dictionary<App, string> broken = new Dictionary<App, string>();
    private readonly MarshalWindow marshal = new MarshalWindow();
    private readonly string installFolder;
    private readonly bool installedCopy;
    private FileSystemWatcher customWatcher, restartWatcher;
    private System.Windows.Forms.Timer faultTimer;
    private bool disposed;

    internal static void Run()
    {
        using (var host = new HelperHost())
            Application.Run(host);
    }

    private HelperHost()
    {
        // Bare fault kinds are host-level: they exercise the process crash path in
        // CrashGuard. A "start" fault throws before anything is ready. Per-session
        // faults use ANIMATED_FAULT=<app>:<kind> (AppSession).
        if (HostFaultKind() == "start")
            throw new InvalidOperationException("Forced fault (ANIMATED_FAULT=start).");
        installFolder = Path.GetDirectoryName(Application.ExecutablePath);
        installedCopy = InstallFolderIsOurs(installFolder);
        foreach (App app in AppTable.WithCustom())
        {
            try { sessions[app] = new AppSession(app, marshal); }
            catch (Exception error)
            {
                AppLog.Write("crash", "[" + app.Id + "] session did not start: " + error);
                broken[app] = error.GetBaseException().Message;
            }
        }
        WatchCustomApps();
        marshal.Hotkey += delegate { ToggleAllArtwork(); };
        marshal.NextThemeKey += delegate { NextThemeAll(); };
        BuildMenu();
        tray.Text = "Animated Launchers";
        try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (ArgumentException) { tray.Icon = SystemIcons.Application; }
        catch (FileNotFoundException) { tray.Icon = SystemIcons.Application; }
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { OpenThemeStudio(); };
        tray.Visible = true;
        CrashGuard.OnExit(Cleanup);
        SystemEvents.SessionEnding += OnSessionEnding;
        SyncRun();
        AppLog.Write("helper", "ready: " + sessions.Count + " app session" + (sessions.Count == 1 ? "" : "s") +
            (broken.Count > 0 ? ", " + broken.Count + " failed to start" : ""));

        string kind = HostFaultKind();
        if (kind == "tick")
        {
            faultTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            faultTimer.Tick += delegate {
                faultTimer.Stop();
                throw new InvalidOperationException("Forced fault (ANIMATED_FAULT=tick).");
            };
            faultTimer.Start();
        }
        else if (kind == "thread")
        {
            ThreadPool.QueueUserWorkItem(delegate {
                Thread.Sleep(3000);
                throw new InvalidOperationException("Forced fault (ANIMATED_FAULT=thread).");
            });
        }
    }

    private static string HostFaultKind()
    {
        string fault = CrashGuard.Fault ?? "";
        if (fault.IndexOf(':') >= 0) return "";
        int comma = fault.IndexOf(',');
        return comma < 0 ? fault : fault.Substring(0, comma);
    }

    // Only an installed helper manages the start-with-Windows entry; a build run
    // from the repo never writes the registry.
    private static bool InstallFolderIsOurs(string folder)
    {
        string ours = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "AnimatedLaunchers");
        return string.Equals(folder, ours, StringComparison.OrdinalIgnoreCase);
    }

    // Sessions for apps added through Theme Studio start and stop live, so a
    // custom app is watched without restarting the helper.
    private void WatchCustomApps()
    {
        try
        {
            IntPtr unused = marshal.Handle; // SynchronizingObject needs the handle.
            Directory.CreateDirectory(ThemeLibrary.SharedRoot);
            customWatcher = new FileSystemWatcher(ThemeLibrary.SharedRoot, CustomApps.FileName)
            {
                SynchronizingObject = marshal,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            FileSystemEventHandler changed = delegate { ReconcileCustomApps(); };
            customWatcher.Changed += changed;
            customWatcher.Created += changed;
            customWatcher.Deleted += changed;
            customWatcher.Renamed += delegate { ReconcileCustomApps(); };
            customWatcher.EnableRaisingEvents = true;
            restartWatcher = new FileSystemWatcher(ThemeLibrary.SharedRoot, "restart.stamp")
            {
                SynchronizingObject = marshal,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            FileSystemEventHandler restart = delegate
            {
                AppLog.Write("helper", "restart requested");
                EndSession();
            };
            restartWatcher.Changed += restart;
            restartWatcher.Created += restart;
            restartWatcher.EnableRaisingEvents = true;
        }
        catch (Exception error)
        {
            AppLog.Write("helper", "custom-app watch could not start: " + error.Message);
        }
    }

    private void ReconcileCustomApps()
    {
        if (disposed) return;
        App[] customs = CustomApps.Current;
        bool changed = false;
        var gone = new List<App>();
        foreach (var pair in sessions)
            if (pair.Key.IsCustom && !Array.Exists(customs, c => ReferenceEquals(c, pair.Key))) gone.Add(pair.Key);
        foreach (var pair in broken)
            if (pair.Key.IsCustom && !Array.Exists(customs, c => ReferenceEquals(c, pair.Key)) && !gone.Contains(pair.Key))
                gone.Add(pair.Key);
        foreach (App app in gone)
        {
            AppSession session;
            if (sessions.TryGetValue(app, out session))
            {
                try { session.Dispose(); } catch (Exception) { }
                sessions.Remove(app);
            }
            broken.Remove(app);
            changed = true;
            AppLog.Write("helper", "[" + app.Id + "] session stopped (custom app removed)");
        }
        foreach (App app in customs)
        {
            if (sessions.ContainsKey(app) || broken.ContainsKey(app)) continue;
            try
            {
                sessions[app] = new AppSession(app, marshal);
                changed = true;
                AppLog.Write("helper", "[" + app.Id + "] session started (custom app added)");
            }
            catch (Exception error)
            {
                broken[app] = error.GetBaseException().Message;
                AppLog.Write("crash", "[" + app.Id + "] session did not start: " + error);
            }
        }
        if (changed)
        {
            menu.Items.Clear();
            BuildMenu();
        }
    }

    private void BuildMenu()
    {
        var studio = new ToolStripMenuItem("Theme Studio…");
        studio.Font = new Font(menu.Font, FontStyle.Bold);
        studio.Click += delegate { OpenThemeStudio(); };
        menu.Items.Add(studio);
        menu.Items.Add(new ToolStripSeparator());
        foreach (App app in AppTable.WithCustom())
        {
            App target = app;
            var appItem = new ToolStripMenuItem(app.Name);
            var placeHolder = new ToolStripMenuItem("…");
            appItem.DropDownItems.Add(placeHolder); // keeps the submenu arrow
            appItem.DropDownOpening += delegate { FillAppMenu(target, appItem); };
            menu.Items.Add(appItem);
            DarkMenuRenderer.Style(appItem.DropDown);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { EndSession(); });
        DarkMenuRenderer.Style(menu);
    }

    private void FillAppMenu(App app, ToolStripMenuItem appItem)
    {
        appItem.DropDownItems.Clear();
        AppSession session;
        if (!sessions.TryGetValue(app, out session))
        {
            appItem.DropDownItems.Add(new ToolStripMenuItem("This part did not start") { Enabled = false });
            var retryStart = new ToolStripMenuItem("Try again");
            retryStart.Click += delegate { RetrySession(app); };
            appItem.DropDownItems.Add(retryStart);
            return;
        }
        if (session.Failed)
        {
            appItem.DropDownItems.Add(new ToolStripMenuItem("Artwork stopped after errors") { Enabled = false });
            var retry = new ToolStripMenuItem("Try again");
            retry.Click += delegate { session.TryAgain(); };
            appItem.DropDownItems.Add(retry);
            appItem.DropDownItems.Add(new ToolStripSeparator());
        }
        else if (!session.Watching)
        {
            appItem.DropDownItems.Add(new ToolStripMenuItem("Not watching " + app.Name) { Enabled = false });
            appItem.DropDownItems.Add(new ToolStripSeparator());
        }

        var themes = new ToolStripMenuItem("Theme");
        themes.DropDownItems.Add(new ToolStripMenuItem("No themes yet. Add one in Theme Studio.") { Enabled = false });
        themes.DropDownOpening += delegate { FillThemes(session, themes); };
        DarkMenuRenderer.Style(themes.DropDown);
        appItem.DropDownItems.Add(themes);

        var artwork = new ToolStripMenuItem(session.ArtworkHidden ? "Show artwork" : "Hide artwork");
        artwork.Click += delegate { session.ToggleArtwork(); };
        appItem.DropDownItems.Add(artwork);

        var nextTheme = new ToolStripMenuItem("Next theme");
        nextTheme.Click += delegate { session.RotateNow(); };
        appItem.DropDownItems.Add(nextTheme);

        var moreVisible = new ToolStripMenuItem("More visible");
        moreVisible.Click += delegate { session.NudgeVisibility(5); };
        appItem.DropDownItems.Add(moreVisible);
        var lessVisible = new ToolStripMenuItem("Less visible");
        lessVisible.Click += delegate { session.NudgeVisibility(-5); };
        appItem.DropDownItems.Add(lessVisible);

        var splash = new ToolStripMenuItem("Play animation when " + app.Name + " starts")
        {
            Checked = session.SplashOnStart,
            CheckOnClick = true
        };
        splash.Click += delegate { session.SetSplash(splash.Checked); };
        appItem.DropDownItems.Add(splash);

        // Only meaningful while the theme animates: freeze the gif on its
        // current frame without hiding the artwork.
        if (session.HasAnimation)
        {
            var pause = new ToolStripMenuItem("Pause wallpaper animation")
            {
                Checked = session.GifPaused,
                CheckOnClick = true
            };
            pause.Click += delegate { session.SetGifPause(pause.Checked); };
            appItem.DropDownItems.Add(pause);
        }

        appItem.DropDownItems.Add(new ToolStripSeparator());
        var watch = new ToolStripMenuItem(session.Watching ? "Stop watching " + app.Name : "Watch " + app.Name);
        watch.Click += delegate { session.SetWatch(!session.Watching); SyncRun(); };
        appItem.DropDownItems.Add(watch);
    }

    private void FillThemes(AppSession session, ToolStripMenuItem themes)
    {
        themes.DropDownItems.Clear();
        string[] names = ThemeLibrary.Names();
        if (names.Length == 0)
        {
            themes.DropDownItems.Add(new ToolStripMenuItem("No themes yet. Add one in Theme Studio.") { Enabled = false });
            return;
        }
        foreach (string name in names)
        {
            string chosen = name;
            var item = new ToolStripMenuItem(name)
            {
                // The checked theme is the one showing: the night or light pick while it applies.
                Checked = string.Equals(name, session.ShownTheme, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += delegate { session.ApplyTheme(chosen); };
            themes.DropDownItems.Add(item);
        }
    }

    private void RetrySession(App app)
    {
        if (sessions.ContainsKey(app)) return;
        try
        {
            sessions[app] = new AppSession(app, marshal);
            broken.Remove(app);
            AppLog.Write("helper", "[" + app.Id + "] session started after a retry");
        }
        catch (Exception error)
        {
            broken[app] = error.GetBaseException().Message;
            AppLog.Write("crash", "[" + app.Id + "] session did not start on retry: " + error);
        }
    }

    // The start-with-Windows entry exists only while at least one app is watched.
    private void SyncRun()
    {
        if (!installedCopy) return;
        try { AppTable.SyncRunValue(installFolder); }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException ||
                error is System.Security.SecurityException)) throw;
            AppLog.Write("helper", "could not update the start-with-Windows entry: " + error.Message);
        }
    }

    private void OpenThemeStudio()
    {
        string path = Path.Combine(installFolder, "ThemeStudio.exe");
        if (!File.Exists(path))
        {
            MessageBox.Show("Theme Studio was not found.\n\nExpected: " + path,
                "Animated Launchers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })) { } }
        catch (Win32Exception error)
        {
            MessageBox.Show("Theme Studio could not start.\n\n" + error.Message,
                "Animated Launchers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        AppLog.Write("helper", "Windows is signing out or shutting down; stopping");
        Cleanup();
    }

    private void Cleanup()
    {
        if (disposed) return;
        disposed = true;
        SystemEvents.SessionEnding -= OnSessionEnding;
        if (customWatcher != null) { customWatcher.EnableRaisingEvents = false; customWatcher.Dispose(); }
        if (restartWatcher != null) { restartWatcher.EnableRaisingEvents = false; restartWatcher.Dispose(); }
        foreach (AppSession session in sessions.Values)
        {
            try { session.Dispose(); }
            catch (Exception error) { AppLog.Write("crash", "error while stopping a session: " + error.Message); }
        }
        tray.Visible = false;
        marshal.Dispose();
    }

    private void EndSession()
    {
        Cleanup();
        ExitThread();
    }

    // Win+Alt+H: any visible artwork anywhere hides them all; all hidden shows all.
    private void ToggleAllArtwork()
    {
        bool anyVisible = false;
        foreach (var pair in sessions) if (!pair.Value.ArtworkHidden) { anyVisible = true; break; }
        foreach (var pair in sessions) pair.Value.SetArtwork(!anyVisible);
        AppLog.Write("helper", "hotkey " + (anyVisible ? "hid" : "showed") + " the artwork");
    }

    // Win+Alt+T: every active session steps to its next theme, the same as the
    // tray's Next theme item on each app.
    private void NextThemeAll()
    {
        foreach (var pair in sessions) pair.Value.RotateNow();
        AppLog.Write("helper", "hotkey asked every app for its next theme");
    }

    // The SynchronizingObject for the watchers and the WM_HOTKEY receiver.
    // Win+Alt+H is the global artwork toggle; a registration failure (another app
    // owns it) just disables the hotkey.
    private sealed class MarshalWindow : Control
    {
        private const int HotkeyMessage = 0x0312;
        private const int NoRepeat = 0x4000, ModAlt = 0x0001, ModWin = 0x0008;
        private bool registered, registeredNext;

        internal event EventHandler Hotkey;
        internal event EventHandler NextThemeKey;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { registered = RegisterHotKey(Handle, 1, NoRepeat | ModAlt | ModWin, (int)Keys.H); }
            catch (Exception error)
            {
                if (!(error is Win32Exception || error is System.Runtime.InteropServices.ExternalException)) throw;
                registered = false;
            }
            try { registeredNext = RegisterHotKey(Handle, 2, NoRepeat | ModAlt | ModWin, (int)Keys.T); }
            catch (Exception error)
            {
                if (!(error is Win32Exception || error is System.Runtime.InteropServices.ExternalException)) throw;
                registeredNext = false;
            }
            if (!registered) AppLog.Write("helper", "the Win+Alt+H artwork hotkey is taken; it stays off");
            if (!registeredNext) AppLog.Write("helper", "the Win+Alt+T theme hotkey is taken; it stays off");
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (registered) UnregisterHotKey(Handle, 1);
            if (registeredNext) UnregisterHotKey(Handle, 2);
            registered = registeredNext = false;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == HotkeyMessage)
            {
                EventHandler handler = m.WParam.ToInt32() == 2 ? NextThemeKey : Hotkey;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cleanup();
            if (faultTimer != null) faultTimer.Dispose();
            menu.Dispose();
            tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

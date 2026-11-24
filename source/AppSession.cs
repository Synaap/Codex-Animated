using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// One app's part of the shared wallpaper helper: watches the app's window, draws
// the artwork inside or over it, replays settings changes and decides whether a
// cold start earns the animation. HelperHost owns the message loop and the tray.
// A fault here is logged and parks only this session; the other apps keep running.
internal sealed class AppSession : IDisposable
{
    private const uint ForegroundEvent = 3;
    private const uint MoveSizeStart = 0x000A, MoveSizeEnd = 0x000B;
    private const uint LocationEvent = 0x800B;
    private const int IdleInterval = 350, DragInterval = 16, WaitingInterval = 1500, DriftInterval = 400;
    private const int ReloadDelay = 150, InstallCheckInterval = 60000;
    private const int MaxSessionFaults = 3;
    // At most this many app windows get artwork; a slow rescan picks up windows
    // opened while the app already had the foreground.
    private const int MaxSlots = 8;
    private static readonly int[] ReloadRetryDelays = { 500, 2000, 5000 };
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StampPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ColdStartAge = TimeSpan.FromSeconds(20);

    private readonly App app;
    private readonly WallpaperSettings settings;
    private readonly string settingsDirectory;
    private readonly Control marshal;
    private readonly bool forceFallback = Environment.GetEnvironmentVariable("ANIMATED_FALLBACK") == "1";
    private AppProfile profile;
    private Bitmap raw, source; // source aliases raw unless the look produced a copy.
    // An animated wallpaper: raw gif frames, the per-frame rendered images and
    // the pump that steps them. gif is null for a still wallpaper.
    private ImageCodec.GifArt gif;
    private Bitmap[] gifFrames;
    private int gifIndex;
    private int gifDuration;
    private int gifStarted;
    // Authored delays after the profile's speed multiplier and fps cap; null
    // means use gif.Delays directly.
    private int[] gifEff;
    // True when gif holds a slides\ folder of stills rather than a real gif —
    // its delays come from the profile's slide seconds, not the file.
    private bool gifIsSlides;
    // Slide crossfade: the previous frame, when the fade started and a reusable
    // blend buffer at frame size.
    private Bitmap crossFrom, crossBmp;
    private int crossStart;
    private const int CrossMs = 400;
    // Win+Alt+P: an in-memory freeze on top of the persisted gifPause flag.
    internal bool PauseHotkey;
    // A custom crosshair: one small layered child per slot, moved by a ~30 ms
    // cursor poll; hidden while the cursor is outside the window.
    private readonly System.Windows.Forms.Timer crossTimer = new System.Windows.Forms.Timer();
    private string crossStyle;
    private Color crossColor;
    private string wallpaperStamp;
    // One slot per accepted app window, capped so a window factory cannot flood it.
    private readonly List<WindowSlot> slots = new List<WindowSlot>();
    private bool refreshing;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer installTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer frameTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer driftTimer = new System.Windows.Forms.Timer();
    private double driftPhase;
    private FileSystemWatcher watcher;
    private int reloadFailures;
    private readonly HashSet<IntPtr> loggedWindows = new HashSet<IntPtr>();
    private uint loggedPid;
    private System.Windows.Forms.Timer faultTimer;
    private readonly Native.EventCallback onEvent;
    private IntPtr foregroundHook;
    private uint decidedPid;
    private bool enabled = true, shuttingDown, disposed;
    // Transient hides: the presentation-mode rule and the night-state tracker.
    private bool hiddenByRule, nightKnown, nightActive;
    private bool active, failed;
    private readonly List<DateTime> faultTimes = new List<DateTime>();
    private bool capLogged;
    private DateTime rescanUtc = DateTime.MinValue, stampCheckUtc = DateTime.MinValue;
    private string statusProblem, lastStatusKey;
    private DateTime rotateUtc = DateTime.MaxValue, modeCheckUtc;
    private bool rotatedThisAttach, modeCheckNow;

    internal AppSession(App app, Control marshal)
    {
        this.app = app;
        this.marshal = marshal;
        settingsDirectory = app.SettingsDirectory;
        settings = new WallpaperSettings(LauncherTheme.DefaultWallpaperPath, settingsDirectory);
        profile = AppProfile.Load(settingsDirectory, app.IsCustom);
        raw = settings.LoadBitmap();
        TryLoadGif(profile.Look);
        source = gifFrames != null ? gifFrames[0] : ArtworkLook.Render(raw, profile.Look);
        wallpaperStamp = WallpaperStamp();
        onEvent = WindowChanged;
        timer.Tick += delegate { Guard(Tick, "window refresh"); };
        frameTimer.Tick += delegate { Guard(FrameTick, "wallpaper frame"); };
        crossTimer.Interval = 33;
        crossTimer.Tick += delegate { Guard(CrossTick, "crosshair"); };
        driftTimer.Interval = DriftInterval;
        driftTimer.Tick += delegate { Guard(DriftTick, "artwork drift"); };
        reloadTimer.Interval = ReloadDelay;
        reloadTimer.Tick += delegate { Guard(Reload, "settings reload"); };
        try
        {
            // Runs while the session is parked too, so installing or removing the
            // desktop app wakes or sleeps this part without a helper restart.
            installTimer.Interval = InstallCheckInterval;
            installTimer.Tick += delegate { Guard(CheckInstall, "install check"); };
            installTimer.Start();
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            StartWatching();
            string fault = FaultKind();
            if (fault == "start") throw new InvalidOperationException("Forced fault (ANIMATED_FAULT=" + app.Id + ":start).");
            if (profile.Watch && app.Installed) Activate();
            else Log("helper", "paused (" + (!profile.Watch ? "watch is off" : app.Name + " is not installed") + ")");
            if (forceFallback) Log("helper", "ANIMATED_FALLBACK=1: drawing in a separate window");
            if (fault == "tick")
            {
                // Every tick throws: the session's guard counts three faults and parks it.
                faultTimer = new System.Windows.Forms.Timer { Interval = 3000 };
                faultTimer.Tick += delegate {
                    Guard(delegate { throw new InvalidOperationException("Forced fault (ANIMATED_FAULT=" + app.Id + ":tick)."); },
                        "fault injection");
                };
                faultTimer.Start();
            }
            else if (fault == "thread")
                ThreadPool.QueueUserWorkItem(delegate {
                    Thread.Sleep(3000);
                    throw new InvalidOperationException("Forced fault (ANIMATED_FAULT=" + app.Id + ":thread).");
                });
        }
        catch (Exception error)
        {
            // The host never gets this object, so nothing else could stop its timers,
            // watcher or hooks: a half-built session would keep ticking and could wake
            // up next to the one a "Try again" creates.
            Dispose();
            WriteStatus("parked", "did not start: " + error.GetBaseException().Message);
            throw;
        }
    }

    internal App Target { get { return app; } }
    internal bool Failed { get { return failed; } }
    internal bool Watching { get { return profile.Watch; } }
    internal bool ArtworkShown { get { return enabled && !hiddenByRule; } }
    internal bool ArtworkHidden { get { return !ArtworkShown; } }
    internal bool SplashOnStart { get { return profile.SplashOnStart; } }
    internal string Theme { get { return profile.Theme; } }
    // The pick that should be on screen now: the night or light-mode one while it applies.
    internal string ShownTheme { get { return DesiredTheme(); } }

    // The tray "Try again" item: clear the parked state and wake the session when
    // its app is watched and installed.
    internal void TryAgain()
    {
        faultTimes.Clear();
        failed = false;
        statusProblem = null;
        ReportStatus();
        if (profile.Watch && app.Installed)
        {
            Activate();
            ScheduleReload(ReloadDelay); // pick up what changed while it was parked
        }
        else Log("helper", "still paused (" + (!profile.Watch ? "watch is off" : app.Name + " is not installed") + ")");
    }

    private void Log(string area, string message) { AppLog.Write(area, "[" + app.Id + "] " + message); }

    // The test switch addresses one session as <app id>:<kind>. Bare kinds are the
    // host-level faults in HelperHost, not this session's.
    private string FaultKind()
    {
        string fault = CrashGuard.Fault ?? "";
        int colon = fault.IndexOf(':');
        if (colon < 0 || !string.Equals(fault.Substring(0, colon), app.Id, StringComparison.OrdinalIgnoreCase)) return "";
        string kind = fault.Substring(colon + 1);
        int comma = kind.IndexOf(',');
        return comma < 0 ? kind : kind.Substring(0, comma);
    }

    // Every session callback goes through here. Three faults inside a minute park
    // this app only; a change to its settings folder or the tray's Try again revives it.
    private void Guard(Action action, string where)
    {
        if (shuttingDown) return;
        try
        {
            action();
        }
        catch (Exception error)
        {
            faultTimes.Add(DateTime.UtcNow);
            faultTimes.RemoveAll(delegate(DateTime at) { return DateTime.UtcNow - at > TimeSpan.FromMinutes(1); });
            Log("crash", "error in " + where + " (fault " + faultTimes.Count + " of " + MaxSessionFaults + "): " + error);
            if (faultTimes.Count >= MaxSessionFaults)
            {
                failed = true;
                statusProblem = "kept failing; it stays off until its settings change";
                Park("too many errors; it stays off until its settings change");
            }
        }
    }

    private void Activate()
    {
        if (active || shuttingDown) return;
        active = true;
        timer.Interval = IdleInterval;
        timer.Start();
        // OUTOFCONTEXT (0) delivers these notifications on our own message-loop thread.
        // Only a foreground notification is global. Location events are later limited to
        // the official application's UI thread and the exact window handle.
        foregroundHook = Native.SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero, onEvent, 0, 0, 0);
        Log("helper", "ready (theme " + (string.IsNullOrEmpty(profile.Theme) ? "none" : "\"" + profile.Theme + "\"") +
            ", visibility " + profile.Look.Opacity + "%, animation " + (profile.SplashOnStart ? "on" : "off") + ")");
        Guard(RefreshArtwork, "window refresh");
    }

    // The target, hooks and timers are released; the settings watcher keeps
    // running so toggling watch back on wakes the session again.
    private void Park(string reason)
    {
        if (!active && slots.Count == 0) { Log("helper", "parked: " + reason); ReportStatus(); return; }
        active = false;
        ReleaseTargets();
        timer.Stop();
        reloadTimer.Stop();
        if (foregroundHook != IntPtr.Zero) { Native.UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        Log("helper", "parked: " + reason);
        ReportStatus();
    }

    // What the session would tell Theme Studio, kept in status.ini next to
    // settings.ini. Sticky 'problem' is the last real fault; state is recomputed.
    private void ReportStatus()
    {
        bool live = false;
        foreach (WindowSlot slot in slots) if (slot.HasRenderer) { live = true; break; }
        string state = failed ? "parked" : !active ? "off" : live ? "on" : "waiting";
        WriteStatus(state, statusProblem);
    }

    private void WriteStatus(string state, string problem)
    {
        string key = state + "|" + (problem ?? "") + "|" + slots.Count;
        if (key == lastStatusKey) return;
        lastStatusKey = key;
        string file = Path.Combine(settingsDirectory, "status.ini");
        try
        {
            ThemeLibrary.WithLock(settingsDirectory, delegate {
                IniFile ini = IniFile.Load(file);
                ini.Set("status", state);
                ini.Set("problem", problem ?? "");
                ini.Set("windows", slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                ini.Set("when", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                ini.Save(file);
            });
        }
        catch (Exception) { }
    }

    private void Tick()
    {
        RefreshArtwork();
        MaybePresentation();
        MaybeRotate();
        MaybeAutoTheme();
        MaybeStamp();
    }

    // FileSystemWatcher drops events under load. A slow stamp poll is the floor:
    // a wallpaper change the watcher missed still reloads within a few seconds.
    private void MaybeStamp()
    {
        if (!active || DateTime.UtcNow < stampCheckUtc) return;
        stampCheckUtc = DateTime.UtcNow + StampPollInterval;
        try { if (WallpaperStamp() != wallpaperStamp) ScheduleReload(ReloadDelay); }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException ||
                error is NotSupportedException)) throw;
        }
    }

    // Presentation mode hides every slot at once; a window that itself covers
    // its monitor hides only its own artwork. Both are transient — never
    // written to settings.ini.
    private void MaybePresentation()
    {
        bool changed = false;
        bool presenting = active && profile.HideFullscreen &&
            slots.Count > 0 && QuietTime.State() == QuietTime.PresentationMode;
        if (presenting != hiddenByRule)
        {
            hiddenByRule = presenting;
            Log("window", presenting ? "artwork hidden while presenting" : "artwork back after presenting");
            changed = true;
        }
        foreach (WindowSlot slot in slots)
        {
            bool full = profile.HideFullscreen && QuietTime.IsAppFullScreen(slot.Window);
            if (full == slot.FullscreenHidden) continue;
            slot.FullscreenHidden = full;
            slot.SetTransitions(ShownFor(slot));
            Log("window", full ? "artwork hidden on the full-screen window" : "artwork back on the window");
            changed = true;
        }
        if (changed) Guard(RefreshArtwork, "window refresh");
    }

    // Session and per-slot rules folded together: the tray/hotkey flag, the
    // presentation flag and the slot's own full-screen flag.
    private bool ShownFor(WindowSlot slot) { return ArtworkShown && !slot.FullscreenHidden; }

    private void SetSlotTransitions()
    {
        foreach (WindowSlot slot in slots) slot.SetTransitions(ShownFor(slot));
    }

    private void CheckInstall()
    {
        if (active && (!profile.Watch || !app.Installed))
            Park(!profile.Watch ? "watch is off" : app.Name + " is not installed");
        else if (!active && !failed && profile.Watch && app.Installed)
        {
            Log("helper", app.Name + " is installed again; watching");
            Activate();
        }
    }

    // Any visible artwork anywhere hides them all; all hidden shows them all.
    internal void SetArtwork(bool show)
    {
        if (enabled == show) return;
        enabled = show;
        SetSlotTransitions();
        Guard(RefreshArtwork, "window refresh");
    }

    internal void ToggleArtwork() { SetArtwork(!enabled); }

    // Tray nudge: rewrite opacity= by +-5; the settings watcher repaints it.
    internal void NudgeVisibility(int delta)
    {
        try { ThemeLibrary.NudgeOpacity(settingsDirectory, delta); }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            Log("settings", "could not save the visibility: " + error.Message);
        }
    }

    // Tray "Next theme": rotate the active slot now.
    internal void RotateNow()
    {
        if (!active) return;
        RotateOnce("menu");
    }

    // Rotation: "start" fires once per attach (reset when the window goes away);
    // a minute count fires when due but never while the app's window is the
    // foreground one — that is what "while you are using the app" means here.
    private void MaybeRotate()
    {
        if (!active) return;
        string mode = profile.Rotate;
        if (mode == "start")
        {
            if (slots.Count == 0) { rotatedThisAttach = false; return; }
            if (rotatedThisAttach) return;
            rotatedThisAttach = true;
            RotateOnce("app start");
            return;
        }
        int minutes;
        if (!int.TryParse(mode, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out minutes)) return;
        // Test switch: ANIMATED_ROTATE_FAST counts the minutes as seconds.
        bool fast = Environment.GetEnvironmentVariable("ANIMATED_ROTATE_FAST") != null;
        TimeSpan span = fast ? TimeSpan.FromSeconds(minutes) : TimeSpan.FromMinutes(minutes);
        if (rotateUtc == DateTime.MaxValue) { rotateUtc = DateTime.UtcNow + span; return; }
        if (DateTime.UtcNow < rotateUtc) return;
        if (ForegroundSlot() != null) return;
        rotateUtc = DateTime.UtcNow + span;
        RotateOnce("timer");
    }

    // Which settings slot the desired theme lives in: the night pick inside its
    // window, the Windows-light pick while follow is on, else the base pick.
    private string DesiredSlot()
    {
        if (profile.ThemeNight.Length > 0 && IsNightNow(profile))
            return "themeNight";
        if (profile.FollowWindows && WindowsTheme.IsLight() && profile.ThemeLight.Length > 0)
            return "themeLight";
        return "theme";
    }

    // The theme the slot logic says should be showing right now.
    private string DesiredTheme()
    {
        string slot = DesiredSlot();
        if (slot == "themeNight") return profile.ThemeNight;
        if (slot == "themeLight") return profile.ThemeLight;
        return profile.Theme;
    }

    private static bool IsNightNow(AppProfile cfg)
    {
        return ThemeLibrary.IsNight(cfg.NightStart, cfg.NightUntil, DateTime.Now.Hour);
    }

    // The stored look plus this app's dim extras: extraDim always, nightDim inside
    // the night window. The ini keeps the user's numbers; profile.Look holds the
    // effective copy so render comparisons see a boundary flip.
    private static Look EffectiveLook(Look look, AppProfile cfg)
    {
        int dim = look.Dim + cfg.ExtraDim + (IsNightNow(cfg) ? cfg.NightDim : 0);
        if (dim > 80) dim = 80;
        if (dim == look.Dim) return look;
        var copy = look.Copy();
        copy.Dim = dim;
        return copy;
    }

    private void RotateOnce(string why)
    {
        string next = ThemeLibrary.NextInPool(ThemeLibrary.RotationPool(profile.RotateFavs), DesiredTheme());
        if (next.Length == 0 || string.Equals(next, DesiredTheme(), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            ThemeLibrary.ApplyForMode(next, settingsDirectory, DesiredSlot());
            Log("settings", "theme rotated to \"" + next + "\" (" + why + ")");
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is NotSupportedException ||
                error is System.Security.SecurityException)) throw;
            Log("settings", "could not rotate the theme: " + error.Message);
        }
    }

    // Automatic theme shifts: a Windows-mode or night-window flip changes the
    // desired slot, and a night boundary changes the effective dim. When the
    // desired name drifts from applied= (mode flip, follow turned off after a
    // variant, or an outside edit) the slot-aware apply puts the right theme's
    // files back; a dim-only flip only needs a re-render.
    private void MaybeAutoTheme()
    {
        bool check = modeCheckNow;
        modeCheckNow = false;
        if (DateTime.UtcNow >= modeCheckUtc) { modeCheckUtc = DateTime.UtcNow.AddSeconds(15); check = true; }
        if (!check || !active) return;
        bool night = IsNightNow(profile);
        if (!nightKnown) { nightKnown = true; nightActive = night; }
        else if (night != nightActive)
        {
            nightActive = night;
            Log("settings", night ? "night hours began" : "night hours ended");
            ScheduleReload(ReloadDelay); // re-render the dim change right away
        }
        string desired = DesiredTheme();
        if (desired.Length == 0 || string.Equals(desired, profile.Applied, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            ThemeLibrary.ApplyForMode(desired, settingsDirectory, DesiredSlot());
            Log("settings", "followed the mode to \"" + desired + "\"");
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is NotSupportedException ||
                error is System.Security.SecurityException)) throw;
            Log("settings", "could not follow the Windows mode: " + error.Message);
        }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        modeCheckNow = true;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (marshal.IsHandleCreated && !marshal.IsDisposed)
            marshal.BeginInvoke((MethodInvoker)delegate { Guard(UpdatePump, "power change"); });
    }

    internal void ApplyTheme(string name)
    {
        try { ThemeLibrary.ApplyForMode(name, settingsDirectory, DesiredSlot()); }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is ExternalException || error is NotSupportedException ||
                error is System.Security.SecurityException)) throw;
            Log("settings", "could not apply theme \"" + name + "\": " + error.Message);
            MessageBox.Show("Could not apply the theme.\n\n" + error.Message,
                app.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal void SetSplash(bool value)
    {
        try
        {
            AppProfile fresh = AppProfile.LoadStrict(settingsDirectory, app.IsCustom);
            fresh.SplashOnStart = value;
            fresh.Save();
            profile.SplashOnStart = value;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            Log("settings", "could not save the animation choice: " + error.Message);
            MessageBox.Show("Could not save the setting.\n\n" + error.Message,
                app.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // The tray "Pause animation" toggle: persists gifPause; the settings
    // watcher stops or resumes the pump on reload.
    internal bool HasAnimation { get { return gif != null; } }
    internal bool GifPaused { get { return profile.GifPause; } }
    internal void SetGifPause(bool paused)
    {
        try
        {
            AppProfile fresh = AppProfile.LoadStrict(settingsDirectory, app.IsCustom);
            fresh.GifPause = paused;
            fresh.Save();
            profile.GifPause = paused;
            UpdatePump();
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            Log("settings", "could not save the animation pause choice: " + error.Message);
        }
    }

    // The studio toggle and the tray item both land here: persist the choice; the
    // settings watcher then pauses or wakes the session.
    internal void SetWatch(bool on)
    {
        try
        {
            AppProfile fresh = AppProfile.LoadStrict(settingsDirectory, app.IsCustom);
            fresh.Watch = on;
            fresh.Save();
            profile.Watch = on;
            // Act now too: the settings watcher does the same when its event lands.
            if (on && app.Installed)
            {
                if (failed) { failed = false; faultTimes.Clear(); }
                Activate();
            }
            else Park("watch is off");
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            Log("settings", "could not save the watch choice: " + error.Message);
            MessageBox.Show("Could not save the setting.\n\n" + error.Message,
                app.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void StartWatching()
    {
        IntPtr unused = marshal.Handle; // Creating the handle lets the watcher post events to this thread.
        try
        {
            Directory.CreateDirectory(settingsDirectory);
            watcher = new FileSystemWatcher(settingsDirectory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                SynchronizingObject = marshal
            };
            FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs e) { Guard(delegate { SettingsChanged(e.Name); }, "settings watch"); };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += delegate(object sender, RenamedEventArgs e) { Guard(delegate { SettingsChanged(e.Name); SettingsChanged(e.OldName); }, "settings watch"); };
            watcher.Error += delegate { ScheduleReload(ReloadDelay); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is ArgumentException || error is UnauthorizedAccessException ||
                error is PlatformNotSupportedException)) throw;
            Log("settings", "live reload is off: " + error.Message);
        }
    }

    private void SettingsChanged(string name)
    {
        if (name == null) return;
        if (string.Equals(name, "settings.ini", StringComparison.OrdinalIgnoreCase))
        {
            // Watch may have flipped: the session resumes or parks before anything else.
            AppProfile fresh = null;
            try { fresh = AppProfile.Load(settingsDirectory, app.IsCustom); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (fresh != null && fresh.Watch != profile.Watch)
            {
                profile.Watch = fresh.Watch;
                if (fresh.Watch && app.Installed)
                {
                    if (failed) { failed = false; faultTimes.Clear(); Log("helper", "trying again after being parked"); }
                    Activate();
                }
                else Park("watch is off");
            }
            ScheduleReload(ReloadDelay);
            return;
        }
        if (!active) return;
        if (string.Equals(name, "wallpaper.jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "wallpaper.gif", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "wallpaper.apng", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "slides", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "avatar.jpg", StringComparison.OrdinalIgnoreCase))
            ScheduleReload(ReloadDelay);
    }

    private void ScheduleReload(int delay)
    {
        if (shuttingDown || !active) return;
        reloadTimer.Stop();
        reloadTimer.Interval = delay;
        reloadTimer.Start();
    }

    private void Reload()
    {
        reloadTimer.Stop();
        if (shuttingDown) return;
        try
        {
            ApplyProfile();
            if (reloadFailures > 0)
                Log("settings", "reload succeeded after " + reloadFailures + " failed attempt" + (reloadFailures == 1 ? "" : "s"));
            reloadFailures = 0;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is ExternalException || error is OutOfMemoryException ||
                error is NotSupportedException)) throw;
            // Keep what is on screen; the file may only be busy for a moment.
            Log("settings", "reload failed (attempt " + (reloadFailures + 1) + " of " + (ReloadRetryDelays.Length + 1) + "): " +
                error.GetType().Name + ": " + error.Message);
            if (reloadFailures < ReloadRetryDelays.Length) ScheduleReload(ReloadRetryDelays[reloadFailures++]);
            else
            {
                reloadFailures = 0;
                statusProblem = "settings reload failed; what is on screen stays until the next change";
                Log("settings", "reload gave up; what is on screen stays until the next change");
                ReportStatus();
            }
        }
    }

    private void ApplyProfile()
    {
        AppProfile next = AppProfile.LoadStrict(settingsDirectory, app.IsCustom);
        next.Look = EffectiveLook(next.Look, next);
        string stamp = WallpaperStamp();
        bool rawChanged = stamp != wallpaperStamp;
        // Load, unload or re-render the animation: only a new file needs the
        // full reload; look changes re-render the decoded frames below.
        if (rawChanged || gif == null && gifFrames == null)
            TryLoadGif(next.Look);
        else if (gif != null && !next.Look.SameRender(profile.Look))
            RenderGifFrames(next.Look);
        bool relook = rawChanged || !next.Look.SameRender(profile.Look);
        Bitmap nextRaw = rawChanged ? settings.LoadBitmapStrict() : null;
        Bitmap nextSource = null;
        if (relook)
        {
            try { nextSource = gifFrames != null ? gifFrames[gifIndex] : ArtworkLook.Render(nextRaw ?? raw, next.Look); }
            catch { if (nextRaw != null) nextRaw.Dispose(); throw; }
        }
        Bitmap oldRaw = raw, oldSource = source;
        if (rawChanged) { raw = nextRaw; wallpaperStamp = stamp; }
        if (relook)
        {
            source = nextSource;
            foreach (WindowSlot slot in slots) slot.SetImage(source);
        }
        if (!next.Look.SamePresent(profile.Look))
            foreach (WindowSlot slot in slots) slot.SetLook(next.Look);
        // A new pick or night window takes effect on the next tick, not up to 15 s later.
        if (next.Theme != profile.Theme || next.ThemeLight != profile.ThemeLight || next.ThemeNight != profile.ThemeNight ||
            next.FollowWindows != profile.FollowWindows || next.NightStart != profile.NightStart || next.NightUntil != profile.NightUntil)
            modeCheckNow = true;
        profile.Look = next.Look;
        profile.Text = next.Text;
        profile.Theme = next.Theme;
        profile.SplashOnStart = next.SplashOnStart;
        profile.Speed = next.Speed;
        profile.Style = next.Style;
        profile.SplashOnce = next.SplashOnce;
        profile.Rotate = next.Rotate;
        profile.RotateFavs = next.RotateFavs;
        profile.FollowWindows = next.FollowWindows;
        profile.ThemeLight = next.ThemeLight;
        profile.ThemeNight = next.ThemeNight;
        profile.NightStart = next.NightStart;
        profile.NightUntil = next.NightUntil;
        profile.NightDim = next.NightDim;
        profile.ExtraDim = next.ExtraDim;
        profile.HideFullscreen = next.HideFullscreen;
        profile.Applied = next.Applied;
        profile.GifSpeed = next.GifSpeed;
        profile.GifCap = next.GifCap;
        profile.GifPause = next.GifPause;
        profile.BattSaver = next.BattSaver;
        profile.SlideSec = next.SlideSec;
        profile.PauseBg = next.PauseBg;
        profile.Crosshair = next.Crosshair;
        profile.CrosshairColor = next.CrosshairColor;
        UpdateCrosshair();
        ComputeGifDelays();
        // A gif frame is owned by gifFrames; only free looks' own renders.
        if (relook && oldSource != oldRaw && oldSource != crossBmp && (gifFrames == null || Array.IndexOf(gifFrames, oldSource) < 0))
            oldSource.Dispose();
        if (rawChanged)
        {
            oldRaw.Dispose();
            Log("settings", "wallpaper loaded (" + raw.Width + "x" + raw.Height + ")");
        }
        RefreshArtwork();
    }

    private string WallpaperStamp()
    {
        var info = new FileInfo(Path.Combine(settingsDirectory, "wallpaper.jpg"));
        var animated = new FileInfo(Path.Combine(settingsDirectory, "wallpaper.gif"));
        var apng = new FileInfo(Path.Combine(settingsDirectory, "wallpaper.apng"));
        var slides = new DirectoryInfo(Path.Combine(settingsDirectory, "slides"));
        long slideStamp = 0;
        if (slides.Exists) foreach (FileInfo file in slides.GetFiles()) slideStamp = slideStamp * 31 + file.LastWriteTimeUtc.Ticks + file.Length + file.Name.GetHashCode();
        return (info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "none") +
            "|" + (animated.Exists ? animated.LastWriteTimeUtc.Ticks + ":" + animated.Length : "none") +
            "|" + (apng.Exists ? apng.LastWriteTimeUtc.Ticks + ":" + apng.Length : "none") +
            "|" + slideStamp;
    }

    // Loads wallpaper.gif and renders every frame with the look; unloads when
    // the file is gone or the look needs per-pixel effects it cannot animate.
    private void TryLoadGif(Look look)
    {
        string path = Path.Combine(settingsDirectory, "wallpaper.gif");
        string apngPath = Path.Combine(settingsDirectory, "wallpaper.apng");
        string slides = Path.Combine(settingsDirectory, "slides");
        string reason = null;
        ImageCodec.GifArt art = null;
        gifIsSlides = false;
        // A real gif wins, then an APNG; otherwise a slides\ folder of stills
        // animates the same pump with the profile's slide-seconds delay.
        if (File.Exists(path)) art = ImageCodec.LoadGif(path, out reason);
        else if (File.Exists(apngPath)) art = ImageCodec.LoadApng(apngPath, out reason);
        else if (Directory.Exists(slides)) { art = ImageCodec.LoadSlides(slides, out reason); gifIsSlides = art != null; }
        // A look needing per-pixel alpha still animates: the child folds the
        // mask into the persistent DIB in place each frame, so clear zones,
        // glow and vignette all work on moving wallpapers.
        FreeGif();
        if (art == null)
        {
            if (reason != null) Log("settings", "animated wallpaper stays a still: " + reason);
            return;
        }
        gif = art;
        RenderGifFrames(look);
        gifIndex = 0;
        ComputeGifDelays();
        gifStarted = Environment.TickCount;
        Log("settings", "wallpaper animation loaded (" + gif.Count + " frames, " +
            gif.Frames[0].Width + "x" + gif.Frames[0].Height + ")");
    }

    // Renders each raw frame with the look once; the pump then only pays the
    // cover-scale and present cost per step, not the whole pipeline.
    private void RenderGifFrames(Look look)
    {
        var frames = new Bitmap[gif.Count];
        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                Bitmap rendered = ArtworkLook.Render(gif.Frames[i], look);
                frames[i] = object.ReferenceEquals(rendered, gif.Frames[i]) ? new Bitmap(rendered) : rendered;
            }
        }
        catch { foreach (Bitmap frame in frames) if (frame != null) frame.Dispose(); throw; }
        Bitmap[] old = gifFrames;
        gifFrames = frames;
        // A crossfade in flight still points at an old frame about to die, and so
        // may the presented source.
        crossFrom = null;
        if (source != null && old != null && Array.IndexOf(old, source) >= 0)
            source = frames[Math.Min(gifIndex, frames.Length - 1)];
        if (old != null) foreach (Bitmap frame in old) if (frame != null) frame.Dispose();
    }

    // crossFrom over the next slide, alpha t. The canvas takes the incoming
    // slide's size; slides may differ in size, so the outgoing one is
    // cover-scaled onto it. Reuses crossBmp between steps.
    private Bitmap BlendFrames(Bitmap a, Bitmap b, float t)
    {
        if (crossBmp == null || crossBmp.Size != b.Size)
        {
            if (crossBmp != null) crossBmp.Dispose();
            crossBmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
        }
        using (var g = Graphics.FromImage(crossBmp))
        {
            if (a.Size == b.Size) g.DrawImageUnscaled(a, 0, 0);
            else
            {
                double scale = Math.Max((double)b.Width / a.Width, (double)b.Height / a.Height);
                int sw = (int)Math.Ceiling(b.Width / scale), sh = (int)Math.Ceiling(b.Height / scale);
                var src = new Rectangle((a.Width - sw) / 2, (a.Height - sh) / 2, Math.Min(sw, a.Width), Math.Min(sh, a.Height));
                g.DrawImage(a, new Rectangle(0, 0, b.Width, b.Height), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            var cm = new ColorMatrix { Matrix33 = t };
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(b, new Rectangle(0, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
            }
        }
        return crossBmp;
    }

    private void FreeGif()
    {
        frameTimer.Stop();
        crossFrom = null;
        // `source` must never keep pointing at a frame disposed below.
        if (source != null && source != raw &&
            (source == crossBmp || (gifFrames != null && Array.IndexOf(gifFrames, source) >= 0))) source = raw;
        if (crossBmp != null) { crossBmp.Dispose(); crossBmp = null; }
        gifDuration = 0;
        if (gifFrames != null) { foreach (Bitmap frame in gifFrames) if (frame != null) frame.Dispose(); gifFrames = null; }
        if (gif != null) { gif.Dispose(); gif = null; }
        gifIndex = 0;
    }

    // The animation pump: step to the next frame only while some slot is
    // actually showing artwork, so hidden, minimized or fullscreen apps cost 0%.
    private void FrameTick()
    {
        if (gif == null || gifFrames == null || gifDuration <= 0 || PumpPaused() || !AnyShown()) { frameTimer.Stop(); return; }
        // Timeline pump: presenting through UpdateLayeredWindow costs tens of
        // ms per frame, so stepping exactly one frame per tick makes a short-
        // delay gif run late and judder. Instead jump the index to wherever
        // wall-clock says the animation should be, dropping frames when behind
        // so the loop keeps its authored duration.
        int elapsed = unchecked((int)(Environment.TickCount - gifStarted)) % gifDuration;
        int[] d = gifEff ?? gif.Delays;
        int acc = 0, idx = 0;
        while (idx < d.Length - 1 && acc + d[idx] <= elapsed) { acc += d[idx]; idx++; }
        bool changed = idx != gifIndex;
        bool fadeEnded = false;
        if (changed)
        {
            // Slideshows get a short alpha crossfade instead of a hard cut —
            // the dwell is seconds long, so ~7 cheap blend steps cost nothing.
            if (gifIsSlides) { crossFrom = gifFrames[gifIndex]; crossStart = Environment.TickCount; }
            gifIndex = idx;
        }
        Bitmap frame = gifFrames[gifIndex];
        if (crossFrom != null)
        {
            int t = unchecked((int)(Environment.TickCount - crossStart));
            if (t >= CrossMs) { crossFrom = null; fadeEnded = !changed; }
            else frame = BlendFrames(crossFrom, frame, t / (float)CrossMs);
        }
        if (changed || crossFrom != null || fadeEnded)
        {
            source = frame;
            foreach (WindowSlot slot in slots.ToArray())
                if (ShownFor(slot)) { slot.SetAnimatedImage(source); slot.Present(); }
        }
        frameTimer.Interval = crossFrom != null ? 60 : Math.Max(15, acc + d[gifIndex] - elapsed);
    }

    // Delays with the profile's pacing applied: gifSpeed scales every delay
    // (percent) and gifCap floors each delay at 1000/fps.
    private void ComputeGifDelays()
    {
        if (gif == null) { gifEff = null; gifDuration = 0; return; }
        int[] d = new int[gif.Count];
        int cap = profile.GifCap > 0 ? 1000 / profile.GifCap : 0;
        for (int i = 0; i < d.Length; i++)
        {
            int authored = gifIsSlides ? profile.SlideSec * 1000 : gif.Delays[i];
            int floor = gifIsSlides ? 2000 : 15;
            int e = Math.Max(floor, authored * 100 / Math.Max(25, profile.GifSpeed));
            if (cap > 0 && e < cap) e = cap;
            d[i] = e;
        }
        gifEff = d;
        gifDuration = 0;
        foreach (int e in d) gifDuration += e;
    }

    private bool AnyShown()
    {
        foreach (WindowSlot slot in slots) if (ShownFor(slot)) return true;
        return false;
    }

    // The pump holds on a manual freeze, and on battery power when the app's
    // battery-saver switch is on; either way the wallpaper keeps its last frame.
    private bool PumpPaused()
    {
        return profile.GifPause || PauseHotkey ||
            (profile.BattSaver && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline) ||
            (profile.PauseBg && !AnyFocused());
    }

    // Win+Alt+P: the helper flips this in-memory flag on every session at once.
    internal void SetPauseHotkey(bool on)
    {
        if (PauseHotkey == on) return;
        PauseHotkey = on;
        UpdatePump();
    }

    // True while the foreground window belongs to one of the app's own processes.
    private bool AnyFocused()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint pid;
        Native.GetWindowThreadProcessId(fg, out pid);
        foreach (WindowSlot slot in slots) if (slot.Pid == pid) return true;
        return false;
    }

    // The crosshair poll: only worth ticking while a style is on; the tick
    // itself hides everything the moment no slot is shown.
    private void UpdateCrosshair()
    {
        bool want = active && !shuttingDown && profile.Crosshair != "off" && slots.Count > 0;
        if (want && !crossTimer.Enabled) crossTimer.Start();
        else if (!want && crossTimer.Enabled) { crossTimer.Stop(); HideCrosshairs(); }
    }

    private void CrossTick()
    {
        if (!active || shuttingDown || profile.Crosshair == "off" || !AnyShown()) { HideCrosshairs(); return; }
        Native.Point pt;
        if (!Native.GetCursorPos(out pt)) return;
        string style = profile.Crosshair;
        Color color = CrossColor();
        if (style != crossStyle || color != crossColor)
        {
            crossStyle = style; crossColor = color;
            foreach (WindowSlot slot in slots) slot.ResetCrosshair();
        }
        foreach (WindowSlot slot in slots.ToArray()) slot.TrackCrosshair(pt);
    }

    private void HideCrosshairs()
    {
        foreach (WindowSlot slot in slots) slot.HideCrosshair();
    }

    private Color CrossColor()
    {
        if (profile.CrosshairColor.Length == 7)
        {
            try { return ColorTranslator.FromHtml(profile.CrosshairColor); } catch (Exception) { }
        }
        string accent = profile.Text.Accent ?? "";
        if (accent.Length >= 4)
        {
            try { return ColorTranslator.FromHtml(accent); } catch (Exception) { }
        }
        return Color.FromArgb(255, 46, 230, 120);
    }

    private void UpdatePump()
    {
        bool want = gif != null && gifFrames != null && active && !shuttingDown && !PumpPaused() && AnyShown();
        if (want && !frameTimer.Enabled)
        {
            int[] d = gifEff ?? gif.Delays;
            int before = 0;
            for (int i = 0; i < gifIndex && i < d.Length; i++) before += d[i];
            gifStarted = Environment.TickCount - before;
            frameTimer.Interval = Math.Max(15, d[gifIndex]);
            frameTimer.Start();
        }
        else if (!want && frameTimer.Enabled) frameTimer.Stop();
        bool wantDrift = active && !shuttingDown && profile.Look.Drift > 0 && AnyShown() &&
            !ArtworkLook.NeedsPixelPass(profile.Look);
        if (wantDrift && !driftTimer.Enabled) { driftPhase = 0; driftTimer.Start(); }
        else if (!wantDrift && driftTimer.Enabled) { driftTimer.Stop(); ResetDrift(); }
    }

    // The eased parallax drift: a slow lissajous wobble of the cover crop around
    // the theme's focus. A 400 ms step costs one opaque re-crop, never the
    // per-pixel pass, so clear/glow/vignette looks keep a steady image.
    private void DriftTick()
    {
        if (profile.Look.Drift <= 0 || !AnyShown()) { driftTimer.Stop(); return; }
        driftPhase += 0.5;
        double reach = profile.Look.Drift * 0.22; // drift=100 pans up to +-22 focus points
        var look = profile.Look.Copy();
        look.FocusX = ClampFocus(profile.Look.FocusX + (int)Math.Round(Math.Sin(driftPhase) * reach));
        look.FocusY = ClampFocus(profile.Look.FocusY + (int)Math.Round(Math.Sin(driftPhase * 0.7 + 1.3) * reach));
        foreach (WindowSlot slot in slots.ToArray())
            if (ShownFor(slot)) { slot.SetLook(look); slot.Present(); }
    }

    private void ResetDrift()
    {
        driftPhase = 0;
        foreach (WindowSlot slot in slots.ToArray()) slot.SetLook(profile.Look);
    }

    private static int ClampFocus(int value) { return Math.Max(0, Math.Min(100, value)); }

    private void WindowChanged(IntPtr hook, uint kind, IntPtr window, int objectId, int childId,
        uint threadId, uint time)
    {
        Guard(delegate
        {
            if (kind != ForegroundEvent)
            {
                if (FindSlot(window) == null) return;
                if (kind == MoveSizeStart) timer.Interval = DragInterval;
                else if (kind == MoveSizeEnd) timer.Interval = IdleInterval;
                else if (childId != 0 || (objectId != 0 && objectId != -4)) return;
            }
            RefreshArtwork();
        }, "window event");
    }

    // The acceptance filter every app window has to pass before it gets a slot:
    // visible, unowned, not a tool window, matching class for built-ins, not
    // cloaked, owned by the app's process.
    private bool AcceptWindow(IntPtr window, out uint pid, out uint thread)
    {
        pid = thread = 0;
        if (window == IntPtr.Zero || !Native.IsWindowVisible(window) ||
            Native.GetWindow(window, Native.Owner) != IntPtr.Zero ||
            (Native.Styles(window, Native.ExStyle) & Native.ToolWindow) != 0)
            return false;
        var cls = new StringBuilder(128);
        Native.GetClassName(window, cls, cls.Capacity);
        // A custom app matches any window class; built-ins want Chrome_WidgetWin_1.
        if (app.WindowClass != null && cls.ToString() != app.WindowClass) return false;
        if (IsCloaked(window)) return false;
        // Electron can remove the frame styles when the main window is already fullscreen.
        if ((Native.Styles(window, Native.Style) & (0x40000 | 0x20000 | 0x10000)) == 0)
        {
            Native.Rect bounds;
            if (!Native.GetWindowRect(window, out bounds) ||
                !Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
                    .Contains(Screen.FromHandle(window).Bounds)) return false;
        }
        thread = Native.GetWindowThreadProcessId(window, out pid);
        return thread != 0 && app.OwnsProcess(pid);
    }

    private WindowSlot FindSlot(IntPtr window)
    {
        foreach (WindowSlot slot in slots) if (slot.Window == window) return slot;
        return null;
    }

    // The slot whose window currently has keyboard focus, or null when some
    // other program is in front. Drives the rotation's "app in use" check and
    // the cold-start animation decision.
    private WindowSlot ForegroundSlot()
    {
        IntPtr foreground = Native.GetForegroundWindow();
        return foreground == IntPtr.Zero ? null : FindSlot(foreground);
    }

    private bool AddSlot(IntPtr window)
    {
        uint pid, thread;
        if (FindSlot(window) != null || !AcceptWindow(window, out pid, out thread)) return false;
        if (slots.Count >= MaxSlots)
        {
            if (!capLogged) { capLogged = true; Log("window", "more than " + MaxSlots + " windows; artwork stays on the first " + MaxSlots); }
            return false;
        }
        var slot = new WindowSlot(this, window, pid, thread);
        slots.Add(slot);
        timer.Interval = IdleInterval;
        slot.SetTransitions(ShownFor(slot));
        slot.HookEvents();
        LogAttached(window, pid);
        UpdateCrosshair();
        return true;
    }

    private void ReleaseTargets()
    {
        foreach (WindowSlot slot in slots) slot.Release();
        slots.Clear();
        rotatedThisAttach = false;
        capLogged = false;
        HideCrosshairs();
        UpdateCrosshair();
        timer.Interval = WaitingInterval;
    }

    // Once per window: which window was picked, without its title.
    private void LogAttached(IntPtr window, uint pid)
    {
        if (loggedPid != pid || loggedWindows.Count > 64) { loggedWindows.Clear(); loggedPid = pid; }
        if (!loggedWindows.Add(window)) return;
        Native.Rect r;
        int width = 0, height = 0;
        if (Native.GetWindowRect(window, out r)) { width = r.Right - r.Left; height = r.Bottom - r.Top; }
        Log("window", string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "attached to window 0x{0:X} of process {1} ({2}x{3}, style 0x{4:X}, extended style 0x{5:X})",
            window.ToInt64(), pid, width, height, Native.Styles(window, Native.Style), Native.Styles(window, Native.ExStyle)));
    }

    private void RefreshArtwork()
    {
        // Cross-thread window creation or destruction can dispatch a WinEvent before
        // the original callback returns. Publish one renderer at a time. A parked
        // session (watch off, app gone, too many errors) never touches a window.
        if (shuttingDown || refreshing || !active) return;
        refreshing = true;
        try { UpdateArtwork(); }
        finally { refreshing = false; ReportStatus(); }
    }

    private void UpdateArtwork()
    {
        PresentArtwork();
        if (slots.Count > 0) DecideColdStart();
        UpdatePump();
    }

    // One decision per process id, so a window the app replaces during startup or a
    // second window of the same process never replays the animation. Runs on the
    // app's foreground window only — background windows never trigger a splash.
    private void DecideColdStart()
    {
        WindowSlot front = ForegroundSlot();
        if (front == null || front.Pid == decidedPid) return;
        string process = "process " + front.Pid;
        DateTime started;
        if (!app.TryGetStartTime(front.Pid, out started))
        {
            decidedPid = front.Pid;
            Log("animation", "not played for " + process + ": its start time is not available");
            return;
        }
        if (DateTime.UtcNow - started > ColdStartAge)
        {
            decidedPid = front.Pid;
            Log("animation", "not played for " + process + ": it started more than 20 seconds ago");
            return;
        }
        Mutex playing;
        if (Mutex.TryOpenExisting(AppTable.PlayMutex, out playing))
        {
            // The launcher or an earlier --splash is already playing for this start.
            playing.Dispose();
            decidedPid = front.Pid;
            Log("animation", "not started for " + process + ": one is already playing");
            return;
        }
        decidedPid = front.Pid;
        string skip = !profile.SplashOnStart ? "turned off for " + app.Name : QuietTime.Check(app.OwnsProcess);
        if (skip == null && profile.SplashOnce && SplashMemory.PlayedOrMark(app.SettingsDirectory))
            skip = "it already played since sign-in";
        if (skip != null)
        {
            Log("animation", "not played for " + process + ": " + skip);
            return;
        }
        try
        {
            string helper = HelperExe();
            using (Process.Start(new ProcessStartInfo(helper,
                "--splash " + front.Window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) + " --app " + app.Id)
            { UseShellExecute = false })) { }
            Log("animation", "playing for " + process);
        }
        catch (Win32Exception error) { Log("animation", "could not start for " + process + ": " + error.Message); }
    }

    // The splash child uses the neutral copy when it is installed next to this one.
    internal static string HelperExe()
    {
        string sibling = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), AppTable.HelperExeName);
        return File.Exists(sibling) ? sibling : Application.ExecutablePath;
    }

    private void PresentArtwork()
    {
        // Drop slots whose windows are gone.
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (slots[i].Exists()) continue;
            Log("window", "window 0x" + slots[i].Window.ToInt64().ToString("X") + " is gone");
            slots[i].Release();
            slots.RemoveAt(i);
        }

        // A window the app just brought forward is picked up right away.
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground != IntPtr.Zero && FindSlot(foreground) == null)
        {
            uint pid;
            if (Native.GetWindowThreadProcessId(foreground, out pid) != 0 && app.OwnsProcess(pid))
                AddSlot(foreground);
        }

        // Slow rescan: windows opened while the app already held the foreground,
        // and replacements for windows that closed, land within a few seconds.
        if (slots.Count == 0 || DateTime.UtcNow >= rescanUtc)
        {
            rescanUtc = DateTime.UtcNow + RescanInterval;
            Native.EnumWindows(delegate(IntPtr window, IntPtr param) {
                if (!Native.IsIconic(window)) AddSlot(window);
                return true;
            }, IntPtr.Zero);
        }
        UpdateCrosshair();
        if (slots.Count == 0) { timer.Interval = WaitingInterval; return; }
        // A copy: Present can drop its own slot when the window dies mid-tick.
        foreach (WindowSlot slot in slots.ToArray()) slot.Present();
    }

    private void RemoveSlot(WindowSlot slot)
    {
        slot.Release();
        slots.Remove(slot);
        if (slots.Count == 0) timer.Interval = WaitingInterval;
    }

    private static bool IsCloaked(IntPtr window)
    {
        int value;
        return Native.DwmGetWindowAttribute(window, 14, out value, sizeof(int)) == 0 && value != 0;
    }

    // Everything that follows one app window: its artwork renderer (the child
    // drawn inside the window, or the owned overlay window when a child cannot
    // attach), its WinEvent hooks and the DWM transition record. The session
    // keeps one slot per accepted window.
    private sealed class WindowSlot
    {
        private readonly AppSession session;
        internal readonly IntPtr Window;
        internal readonly uint Pid, Thread;
        private IntPtr locationHook, moveSizeHook;
        private PreviewArtworkChild child;
        private ArtworkWindow artwork;
        private CrosshairChild crosshair;
        private bool childUnavailable, changedTransitions;
        private int previousTransitions;
        internal bool FullscreenHidden;

        internal WindowSlot(AppSession session, IntPtr window, uint pid, uint thread)
        {
            this.session = session;
            Window = window;
            Pid = pid;
            Thread = thread;
        }

        internal bool HasRenderer { get { return child != null || artwork != null; } }

        // The crosshair follows the cursor only while it is inside this window.
        internal void TrackCrosshair(Native.Point pt)
        {
            Native.Rect rect;
            var origin = new Native.Point();
            if (!Native.GetClientRect(Window, out rect) || !Native.ClientToScreen(Window, ref origin))
            { HideCrosshair(); return; }
            int x = pt.X - origin.X, y = pt.Y - origin.Y;
            if (x < 0 || y < 0 || x >= rect.Right || y >= rect.Bottom) { HideCrosshair(); return; }
            if (crosshair == null)
            {
                try { crosshair = new CrosshairChild(Window, session.app.Product); }
                catch { return; }
            }
            crosshair.Track(session.crossStyle, session.crossColor,
                pt.X - CrosshairChild.Box / 2, pt.Y - CrosshairChild.Box / 2,
                x - CrosshairChild.Box / 2, y - CrosshairChild.Box / 2);
        }

        internal void HideCrosshair() { if (crosshair != null) crosshair.Hide(); }

        internal void ResetCrosshair() { if (crosshair != null) { crosshair.Dispose(); crosshair = null; } }

        internal bool Exists()
        {
            uint pid;
            return Native.GetWindowThreadProcessId(Window, out pid) == Thread && pid == Pid;
        }

        internal void HookEvents()
        {
            locationHook = Native.SetWinEventHook(LocationEvent, LocationEvent, IntPtr.Zero, session.onEvent, Pid, Thread, 0);
            moveSizeHook = Native.SetWinEventHook(MoveSizeStart, MoveSizeEnd, IntPtr.Zero, session.onEvent, Pid, Thread, 0);
        }

        internal void SetImage(Bitmap image)
        {
            if (child != null) child.SetImage(image);
            if (artwork != null) artwork.SetImage(image);
        }

        // A gif/APNG/slideshow frame: the child uses its persistent-DIB path so
        // the per-frame cost stays low, including through per-pixel looks.
        internal void SetAnimatedImage(Bitmap image)
        {
            if (child != null) child.SetAnimatedImage(image);
            if (artwork != null) artwork.SetImage(image);
        }

        internal void SetLook(Look look)
        {
            if (child != null) child.SetLook(look);
            if (artwork != null) artwork.SetLook(look);
        }

        internal void SetTransitions(bool disable)
        {
            if (disable == changedTransitions) return;
            uint pid;
            if (Native.GetWindowThreadProcessId(Window, out pid) != Thread || pid != Pid) return;
            if (disable)
            {
                // Some Windows versions expose this per-window policy only to the setter.
                if (Native.DwmGetWindowAttribute(Window, Native.DisableTransitions, out previousTransitions, sizeof(int)) != 0)
                    previousTransitions = 0; // Release to Windows' normal animation policy on exit.
                int on = 1;
                changedTransitions = Native.DwmSetWindowAttribute(Window, Native.DisableTransitions, ref on, sizeof(int)) == 0;
            }
            else
            {
                if (Native.DwmSetWindowAttribute(Window, Native.DisableTransitions, ref previousTransitions, sizeof(int)) == 0)
                    changedTransitions = false;
            }
        }

        // Draw or conceal this window's artwork for the current session state.
        internal void Present()
        {
            // Any presentation, including one after a hidden spell, uses the
            // animation's current frame rather than what was live last time.
            // The child gets the persistent-buffer path that can hold gif
            // cadence; the overlay fallback keeps the still path.
            if (session.gif != null)
            {
                if (child != null) child.SetAnimatedImage(session.source);
                if (artwork != null) artwork.SetImage(session.source);
            }
            bool shown = session.ShownFor(this);
            if (!session.forceFallback && !childUnavailable && (shown || child != null) && TryChild()) return;
            if (!shown)
            {
                if (artwork != null) artwork.Conceal();
                return;
            }
            // Only one renderer is visible per window. The independent owned
            // window is used when Windows cannot attach a layered child.
            if (artwork == null)
            {
                artwork = new ArtworkWindow(session.source);
                artwork.SetLook(session.profile.Look);
                artwork.AttachTo(Window);
            }
            if (!Native.IsWindowVisible(Window) || Native.IsIconic(Window))
            {
                artwork.Conceal();
                return;
            }
            if (IsCloaked(Window))
            {
                artwork.Conceal();
                return;
            }
            Native.Rect rect;
            var point = new Native.Point();
            if (!Native.GetClientRect(Window, out rect) || !Native.ClientToScreen(Window, ref point) ||
                rect.Right <= 0 || rect.Bottom <= 0)
                return; // A transitioning HWND can briefly report no usable dimensions.
            artwork.Place(Window, new Rectangle(point.X, point.Y, rect.Right, rect.Bottom));
        }

        private bool TryChild()
        {
            try
            {
                if (child == null)
                {
                    child = new PreviewArtworkChild(Window, Pid, session.source, session.profile.Look, session.app.Product);
                    session.Log("window", "drawing inside the app window");
                    return true;
                }
                child.SetEnabled(session.ShownFor(this));
                if (child.RefreshSurface()) return true;
            }
            catch (Exception error)
            {
                if (!(error is ExternalException || error is ArgumentException ||
                    error is InvalidOperationException || error is NotSupportedException ||
                    error is EntryPointNotFoundException)) throw;
                session.Log("window", "cannot draw inside the app window (" + error.GetType().Name + ": " + error.Message +
                    "); using a separate window");
            }
            if (child != null) child.Dispose();
            child = null;
            childUnavailable = true;
            if (!Exists())
            {
                session.RemoveSlot(this);
                return true;
            }
            return false;
        }

        internal void Release()
        {
            SetTransitions(false);
            if (moveSizeHook != IntPtr.Zero) Native.UnhookWinEvent(moveSizeHook);
            if (locationHook != IntPtr.Zero) Native.UnhookWinEvent(locationHook);
            moveSizeHook = locationHook = IntPtr.Zero;
            if (child != null) { child.Dispose(); child = null; }
            if (artwork != null) { artwork.Dispose(); artwork = null; }
            if (crosshair != null) { crosshair.Dispose(); crosshair = null; }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shuttingDown = true;
        ReleaseTargets();
        WriteStatus("off", null);
        timer.Dispose();
        installTimer.Dispose();
        if (faultTimer != null) faultTimer.Dispose();
        reloadTimer.Dispose();
        frameTimer.Dispose();
        crossTimer.Dispose();
        driftTimer.Dispose();
        FreeGif();
        if (watcher != null) watcher.Dispose();
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
        if (source != null && source != raw) source.Dispose();
        if (raw != null) raw.Dispose();
    }

    private sealed class ArtworkWindow : Form
    {
        private Bitmap image;
        private Surface source, current, previousSize;
        private Rectangle placed;
        private bool shown, forceComposite;
        private Look look;
        private IntPtr targetOwner;

        internal ArtworkWindow(Bitmap source)
        {
            image = source;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            // UpdateLayeredWindow supplies the bitmap and opacity in one operation.
            // Form.Opacity uses a different Windows layering API and would disable it.
        }

        internal void SetImage(Bitmap replacement)
        {
            image = replacement;
            // Windows retains the last submitted pixels until the next layered update.
            ReleaseSurfaces();
        }

        // Re-composites the same pixels with the new look on the next Place.
        internal void SetLook(Look value)
        {
            if (look != null && look.SamePresent(value)) return;
            // An opacity-only change can re-blend the composed frame; a crop or
            // mask change needs fresh pixels.
            bool keepPixels = look != null && look.FocusX == value.FocusX && look.FocusY == value.FocusY &&
                look.Clear == value.Clear && look.ClearSide == value.ClearSide && look.Glow == value.Glow &&
                look.Vignette == value.Vignette && look.Fit == value.Fit;
            look = value;
            forceComposite = true;
            if (keepPixels) return;
            // Keep showing the last submitted pixels until the next Place recomposes.
            if (previousSize != null) previousSize.Dispose();
            if (current != null) current.Dispose();
            previousSize = current = null;
        }

        internal void AttachTo(IntPtr window)
        {
            // Let Windows raise the pair together, before it presents the first frame
            // of the reactivated window. Clicks and keyboard focus still go to the app.
            targetOwner = window;
            if (IsHandleCreated && Native.GetWindow(Handle, Native.Owner) != window)
                Native.SetOwner(Handle, -8, window); // GWLP_HWNDPARENT on our own top-level window.
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // NOACTIVATE, TRANSPARENT, LAYERED, TOOLWINDOW.
                p.ExStyle |= 0x08000000 | 0x20 | 0x80000 | Native.ToolWindow;
                p.ExStyle &= ~0x40000; // APPWINDOW
                if (targetOwner != IntPtr.Zero) p.Parent = targetOwner;
                return p;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            shown = false;
            placed = Rectangle.Empty;
            // Keep the helper's own show/hide immediate as well.
            int disabled = 1;
            Native.DwmSetWindowAttribute(Handle, Native.DisableTransitions, ref disabled, sizeof(int));
        }

        internal void Place(IntPtr targetWindow, Rectangle rect)
        {
            if (IsDisposed) return;
            IntPtr handle = Handle;
            // Framework handle creation can assign a hidden taskbar owner of its own.
            if (targetOwner == targetWindow && Native.GetWindow(handle, Native.Owner) != targetWindow)
                Native.SetOwner(handle, -8, targetWindow);
            bool topmost = Native.IsTopmost(targetWindow);
            const uint keepBounds = Native.NoActivate | Native.NoOwnerZOrder | Native.NoMove | Native.NoSize;
            if (!topmost && Native.IsTopmost(handle))
                Native.SetWindowPos(handle, Native.NotTopmost, 0, 0, 0, 0, keepBounds);

            if (forceComposite || current == null || rect.Size != placed.Size)
            {
                // Keep the previous image on screen while preparing the new crop.
                // Commit both the pixels and the bounds without an intervening WM_PAINT.
                Surface next = (forceComposite && current != null && current.Size == rect.Size)
                    ? current : previousSize;
                bool masked = ArtworkLook.NeedsPixelPass(look);
                if (next == null || next.Size != rect.Size)
                {
                    try
                    {
                        if (masked)
                            next = MaskedFrame(rect.Size);
                        else
                        {
                            if (source == null) source = new Surface(image.GetHbitmap(), image.Size);
                            next = source.ScaleToCover(rect.Size, look.FocusX, look.FocusY);
                        }
                    }
                    catch (Win32Exception error)
                    {
                        AppLog.Write("window", "could not prepare the picture for the separate window: " + error.Message);
                        return; // Preserve the last committed frame and retry.
                    }
                }
                var destination = new Native.Point { X = rect.X, Y = rect.Y };
                var size = new Native.Size { Width = rect.Width, Height = rect.Height };
                var from = new Native.Point();
                var blend = new Native.Blend { ConstantAlpha = look.Alpha };
                if (masked) blend.AlphaFormat = 1;
                if (!Native.UpdateLayeredWindow(handle, IntPtr.Zero, ref destination, ref size,
                    next.DC, ref from, 0, ref blend, 2))
                {
                    AppLog.Write("window", "Windows refused the picture for the separate window (error " + Marshal.GetLastWin32Error() + ")");
                    if (next != previousSize && next != current) next.Dispose();
                    return;
                }
                if (next != current)
                {
                    if (previousSize != null && previousSize != next) previousSize.Dispose();
                    previousSize = current;
                    current = next;
                }
                placed = rect;
                forceComposite = false;
            }
            IntPtr previous = Native.GetWindow(targetWindow, Native.Previous);
            bool inPlace = previous == handle && Native.IsTopmost(handle) == topmost;
            // Windows may have hidden or restored an owned popup with its owner.
            bool visible = shown && Native.IsWindowVisible(handle);
            if (visible && placed == rect && inPlace) return;
            uint flags = Native.NoActivate | Native.NoOwnerZOrder | Native.NoSize;
            if (!visible) flags |= Native.Show;
            if (rect.Location == placed.Location) flags |= Native.NoMove;
            if (inPlace) flags |= Native.NoZOrder;
            else if (topmost)
            {
                // Use the preceding topmost window as the anchor if another app covers the target.
                if (previous == IntPtr.Zero || previous == handle || !Native.IsTopmost(previous))
                    previous = Native.TopmostPosition;
            }
            else if (previous != IntPtr.Zero && Native.IsTopmost(previous))
                previous = IntPtr.Zero; // Top of the ordinary band.
            if (!Native.SetWindowPos(handle, previous, rect.X, rect.Y, 0, 0, flags)) return;
            shown = true;
            placed = rect;
        }

        internal void Conceal()
        {
            if (IsHandleCreated && Native.IsWindowVisible(Handle))
            {
                Native.ShowWindow(Handle, 0);
                if (Native.IsTopmost(Handle) && Native.GetWindow(Handle, Native.Owner) != targetOwner)
                    Native.SetWindowPos(Handle, Native.NotTopmost, 0, 0, 0, 0,
                        Native.NoMove | Native.NoSize | Native.NoActivate | Native.NoOwnerZOrder);
            }
            shown = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }

        // 32bpp premultiplied frame for the per-pixel path (clear zone, glow,
        // vignette, fitted frames with transparent bars).
        private Surface MaskedFrame(Size viewport)
        {
            using (var frame = new Bitmap(viewport.Width, viewport.Height, PixelFormat.Format32bppArgb))
            {
                RectangleF crop; Rectangle dst; InterpolationMode mode;
                ArtworkLook.FramePlan(image.Size, viewport, look, out crop, out dst, out mode);
                using (var g = Graphics.FromImage(frame))
                {
                    g.InterpolationMode = mode;
                    if (mode == InterpolationMode.NearestNeighbor) g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(image, dst, crop, GraphicsUnit.Pixel);
                }
                byte[] px = ArtworkLook.AlphaPixels(frame, look);
                return new Surface(ArtworkLook.AlphaDib(px, viewport.Width, viewport.Height), viewport);
            }
        }

        private void ReleaseSurfaces()
        {
            if (previousSize != null) previousSize.Dispose();
            if (current != null) current.Dispose();
            if (source != null) source.Dispose();
            source = current = previousSize = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseSurfaces();
            base.Dispose(disposing);
        }
    }

    // A 48x48 layered child window carrying the custom crosshair. Its surface
    // is painted once per (style, colour); movement is a plain SetWindowPos so
    // the 30 Hz cursor poll costs no repaints.
    private sealed class CrosshairChild : NativeWindow, IDisposable
    {
        internal const int Box = 48;
        private string style;
        private Color color;
        private bool shown;

        internal CrosshairChild(IntPtr parent, string product)
        {
            CreateHandle(new CreateParams {
                Parent = parent, Caption = product + " crosshair", Style = 0x40000000,
                ExStyle = 0x80000 | 0x20 | 0x08000000 | 0x4,
                X = 0, Y = 0, Width = 1, Height = 1 });
        }

        // screenX/screenY position the layered surface (ULW is screen space);
        // clientX/clientY position the child inside the app window.
        internal void Track(string style, Color color, int screenX, int screenY, int clientX, int clientY)
        {
            if (Handle == IntPtr.Zero) return;
            if (style != this.style || color != this.color)
            {
                IntPtr dib = ArtworkLook.AlphaDib(CrosshairArt.Pixels(style, color, Box), Box, Box);
                IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
                IntPtr old = IntPtr.Zero;
                var at = new Native.Point { X = screenX, Y = screenY };
                var size = new Native.Size { Width = Box, Height = Box };
                var from = new Native.Point();
                var blend = new Native.Blend { ConstantAlpha = 255, AlphaFormat = 1 };
                bool painted = false;
                try
                {
                    if (dc != IntPtr.Zero) old = Native.SelectObject(dc, dib);
                    if (old != IntPtr.Zero && old != new IntPtr(-1))
                        painted = Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref at, ref size, dc, ref from, 0, ref blend, 2);
                }
                finally
                {
                    if (old != IntPtr.Zero && old != new IntPtr(-1)) Native.SelectObject(dc, old);
                    if (dc != IntPtr.Zero) Native.DeleteDC(dc);
                    if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                }
                if (!painted) return; // retry on the next tick instead of staying blank
                this.style = style; this.color = color;
            }
            Native.SetWindowPos(Handle, IntPtr.Zero, clientX, clientY, 0, 0,
                Native.NoSize | Native.NoActivate | Native.NoZOrder | Native.Show);
            shown = true;
        }

        internal void Hide()
        {
            if (shown && Handle != IntPtr.Zero) Native.ShowWindow(Handle, 0);
            shown = false;
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
    }

    // Premultiplied BGRA pixels for the crosshair styles: a dark under-stroke so
    // the reticle reads on any wallpaper, then the chosen colour on top.
    private static class CrosshairArt
    {
        // Decoded cursor-pack bitmaps, reused across ticks; keyed by file path.
        private static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();

        internal static byte[] Pixels(string style, Color color, int size)
        {
            if (style.StartsWith("img:")) return ImagePixels(style, size);
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float c = size / 2f;
                    using (var darkPen = new Pen(Color.FromArgb(190, 0, 0, 0), 4.5f))
                    using (var darkBrush = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                        Shapes(g, style, c, darkPen, darkBrush);
                    using (var pen = new Pen(color, 2.5f))
                    using (var brush = new SolidBrush(color))
                        Shapes(g, style, c, pen, brush);
                }
                var rect = new Rectangle(0, 0, size, size);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[size * size * 4];
                try { Marshal.Copy(data.Scan0, px, 0, px.Length); }
                finally { bmp.UnlockBits(data); }
                for (int i = 0; i < px.Length; i += 4)
                {
                    int a = px[i + 3];
                    px[i] = (byte)(px[i] * a / 255);
                    px[i + 1] = (byte)(px[i + 1] * a / 255);
                    px[i + 2] = (byte)(px[i + 2] * a / 255);
                }
                return px;
            }
        }

        // A cursor-pack PNG composited into the layered surface: drawn at full
        // box, the pack art already carries its own outline.
        private static byte[] ImagePixels(string style, int size)
        {
            var px = new byte[size * size * 4];
            string path = ThemeLibrary.CursorImage(style);
            if (path == null) return px;
            Bitmap src;
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (!cache.TryGetValue(key, out src))
            {
                try
                {
                    // Copy off the file-bound image so pack PNGs are never held open.
                    using (Image file = Image.FromFile(path)) src = new Bitmap(file);
                    if (cache.Count >= 64) { foreach (Bitmap old in cache.Values) old.Dispose(); cache.Clear(); }
                    cache[key] = src;
                }
                catch { return px; }
            }
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    float scale = Math.Min((size - 4f) / src.Width, (size - 4f) / src.Height);
                    float w = src.Width * scale, hgt = src.Height * scale;
                    g.DrawImage(src, (size - w) / 2f, (size - hgt) / 2f, w, hgt);
                }
                var rect = new Rectangle(0, 0, size, size);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(data.Scan0, px, 0, px.Length); }
                finally { bmp.UnlockBits(data); }
            }
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                px[i] = (byte)(px[i] * a / 255);
                px[i + 1] = (byte)(px[i + 1] * a / 255);
                px[i + 2] = (byte)(px[i + 2] * a / 255);
            }
            return px;
        }

        private static void Shapes(Graphics g, string style, float c, Pen pen, Brush brush)
        {
            switch (style)
            {
                case "dot":
                    g.FillEllipse(brush, c - 3, c - 3, 6, 6);
                    g.DrawEllipse(pen, c - 8, c - 8, 16, 16);
                    break;
                case "cross":
                    g.DrawLine(pen, c - 11, c, c - 4, c);
                    g.DrawLine(pen, c + 4, c, c + 11, c);
                    g.DrawLine(pen, c, c - 11, c, c - 4);
                    g.DrawLine(pen, c, c + 4, c, c + 11);
                    g.FillEllipse(brush, c - 1.5f, c - 1.5f, 3, 3);
                    break;
                case "circle":
                    g.DrawEllipse(pen, c - 8, c - 8, 16, 16);
                    g.FillEllipse(brush, c - 2.5f, c - 2.5f, 5, 5);
                    break;
                case "ring":
                    g.DrawEllipse(pen, c - 12, c - 12, 24, 24);
                    break;
                case "chevron":
                    g.DrawLine(pen, c - 8, c - 5, c, c + 3);
                    g.DrawLine(pen, c + 8, c - 5, c, c + 3);
                    g.FillEllipse(brush, c - 2, c - 7.5f, 4, 4);
                    break;
                case "diamond":
                    g.DrawLine(pen, c, c - 9, c + 9, c);
                    g.DrawLine(pen, c + 9, c, c, c + 9);
                    g.DrawLine(pen, c, c + 9, c - 9, c);
                    g.DrawLine(pen, c - 9, c, c, c - 9);
                    g.FillEllipse(brush, c - 2, c - 2, 4, 4);
                    break;
                case "star":
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        float x1 = c + (float)(Math.Cos(a) * 4), y1 = c + (float)(Math.Sin(a) * 4);
                        float x2 = c + (float)(Math.Cos(a) * 9), y2 = c + (float)(Math.Sin(a) * 9);
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                    g.FillEllipse(brush, c - 1.5f, c - 1.5f, 3, 3);
                    break;
                case "scope":
                    g.DrawEllipse(pen, c - 10, c - 10, 20, 20);
                    g.DrawLine(pen, c - 14, c, c - 10, c);
                    g.DrawLine(pen, c + 10, c, c + 14, c);
                    g.DrawLine(pen, c, c - 14, c, c - 10);
                    g.DrawLine(pen, c, c + 10, c, c + 14);
                    g.FillEllipse(brush, c - 2, c - 2, 4, 4);
                    break;
                case "cat":
                    g.DrawEllipse(pen, c - 10, c - 8, 20, 17);
                    g.DrawLine(pen, c - 8, c - 4, c - 8, c - 13); g.DrawLine(pen, c - 8, c - 13, c - 3, c - 9);
                    g.DrawLine(pen, c + 8, c - 4, c + 8, c - 13); g.DrawLine(pen, c + 8, c - 13, c + 3, c - 9);
                    g.FillEllipse(brush, c - 5, c - 1, 2.5f, 2.5f);
                    g.FillEllipse(brush, c + 2.5f, c - 1, 2.5f, 2.5f);
                    g.DrawLine(pen, c - 1, c + 1.5f, c, c + 2.5f);
                    g.DrawLine(pen, c, c + 2.5f, c + 1, c + 1.5f);
                    g.DrawLine(pen, c - 12, c + 1, c - 8.5f, c + 0.5f);
                    g.DrawLine(pen, c - 12, c + 3.5f, c - 8.5f, c + 3);
                    g.DrawLine(pen, c + 12, c + 1, c + 8.5f, c + 0.5f);
                    g.DrawLine(pen, c + 12, c + 3.5f, c + 8.5f, c + 3);
                    break;
                case "paw":
                    g.DrawEllipse(pen, c - 8, c + 1, 16, 10);
                    g.DrawEllipse(pen, c - 9.5f, c - 7, 5, 5);
                    g.DrawEllipse(pen, c - 2.5f, c - 9, 5, 5);
                    g.DrawEllipse(pen, c + 4.5f, c - 7, 5, 5);
                    break;
                case "heart":
                    g.DrawArc(pen, c - 9, c - 7, 9, 9, 180, 180);
                    g.DrawArc(pen, c, c - 7, 9, 9, 180, 180);
                    g.DrawLine(pen, c - 9, c - 2.5f, c, c + 10);
                    g.DrawLine(pen, c + 9, c - 2.5f, c, c + 10);
                    break;
                case "bow":
                    g.DrawLine(pen, c - 3, c, c - 11, c - 6); g.DrawLine(pen, c - 11, c - 6, c - 11, c + 6); g.DrawLine(pen, c - 11, c + 6, c - 3, c);
                    g.DrawLine(pen, c + 3, c, c + 11, c - 6); g.DrawLine(pen, c + 11, c - 6, c + 11, c + 6); g.DrawLine(pen, c + 11, c + 6, c + 3, c);
                    g.FillEllipse(brush, c - 2.5f, c - 2.5f, 5, 5);
                    break;
                case "ghost":
                    g.DrawArc(pen, c - 9, c - 10, 18, 16, 180, 180);
                    g.DrawLine(pen, c - 9, c - 2, c - 9, c + 6);
                    g.DrawLine(pen, c + 9, c - 2, c + 9, c + 6);
                    g.DrawLine(pen, c - 9, c + 6, c - 6, c + 9); g.DrawLine(pen, c - 6, c + 9, c - 3, c + 6);
                    g.DrawLine(pen, c - 3, c + 6, c, c + 9); g.DrawLine(pen, c, c + 9, c + 3, c + 6);
                    g.DrawLine(pen, c + 3, c + 6, c + 6, c + 9); g.DrawLine(pen, c + 6, c + 9, c + 9, c + 6);
                    g.FillEllipse(brush, c - 5, c - 4, 3, 3);
                    g.FillEllipse(brush, c + 2, c - 4, 3, 3);
                    break;
                case "bunny":
                    g.DrawEllipse(pen, c - 7.5f, c - 14, 5.5f, 13);
                    g.DrawEllipse(pen, c + 2, c - 14, 5.5f, 13);
                    g.DrawEllipse(pen, c - 7, c - 2, 14, 13);
                    g.FillEllipse(brush, c - 1.5f, c + 3, 3, 3);
                    break;
                case "crown":
                    g.DrawLine(pen, c - 10, c + 5, c - 10, c - 4);
                    g.DrawLine(pen, c - 10, c - 4, c - 5, c + 1);
                    g.DrawLine(pen, c - 5, c + 1, c, c - 7);
                    g.DrawLine(pen, c, c - 7, c + 5, c + 1);
                    g.DrawLine(pen, c + 5, c + 1, c + 10, c - 4);
                    g.DrawLine(pen, c + 10, c - 4, c + 10, c + 5);
                    g.DrawLine(pen, c - 10, c + 5, c + 10, c + 5);
                    break;
                case "flower":
                    for (int i = 0; i < 5; i++)
                    {
                        double a = -Math.PI / 2 + i * 2 * Math.PI / 5;
                        float px = c + (float)(Math.Cos(a) * 7), py = c + (float)(Math.Sin(a) * 7);
                        g.DrawEllipse(pen, px - 4, py - 4, 8, 8);
                    }
                    g.FillEllipse(brush, c - 2.5f, c - 2.5f, 5, 5);
                    break;
                case "sparkle":
                    g.DrawPolygon(pen, new PointF[] {
                        new PointF(c, c - 14), new PointF(c + 3, c - 3), new PointF(c + 14, c),
                        new PointF(c + 3, c + 3), new PointF(c, c + 14), new PointF(c - 3, c + 3),
                        new PointF(c - 14, c), new PointF(c - 3, c - 3) });
                    g.FillEllipse(brush, c - 1.5f, c - 1.5f, 3, 3);
                    break;
                case "bolt":
                    g.DrawPolygon(pen, new PointF[] {
                        new PointF(c + 3, c - 14), new PointF(c - 6, c + 2), new PointF(c - 1, c + 2),
                        new PointF(c - 3, c + 14), new PointF(c + 6, c - 2), new PointF(c + 1, c - 2) });
                    break;
                case "moon":
                    g.DrawArc(pen, c - 9, c - 11, 18, 22, 60, 240);
                    g.DrawArc(pen, c - 1, c - 9, 14, 18, 300, 240);
                    break;
                case "skull":
                    g.DrawEllipse(pen, c - 8, c - 10, 16, 15);
                    g.DrawRectangle(pen, c - 5, c + 3, 10, 8);
                    g.DrawLine(pen, c - 2, c + 3, c - 2, c + 11);
                    g.DrawLine(pen, c + 2, c + 3, c + 2, c + 11);
                    g.FillEllipse(brush, c - 5, c - 4, 3.5f, 3.5f);
                    g.FillEllipse(brush, c + 1.5f, c - 4, 3.5f, 3.5f);
                    break;
                case "music":
                    g.DrawLine(pen, c + 5, c - 12, c + 5, c + 6);
                    g.DrawLine(pen, c + 5, c - 12, c + 10, c - 8);
                    g.DrawEllipse(pen, c - 6, c + 2, 11, 8);
                    g.DrawLine(pen, c - 1, c - 8, c - 1, c + 4);
                    g.DrawLine(pen, c - 1, c - 8, c + 5, c - 12);
                    break;
                case "cherry":
                    g.DrawEllipse(pen, c - 10, c + 2, 9, 9);
                    g.DrawEllipse(pen, c + 2, c + 3, 9, 9);
                    g.DrawLine(pen, c - 5, c + 3, c + 4, c - 11);
                    g.DrawLine(pen, c + 7, c + 4, c + 4, c - 11);
                    g.DrawLine(pen, c + 4, c - 11, c + 10, c - 9);
                    break;
                case "mushroom":
                    g.DrawArc(pen, c - 11, c - 11, 22, 16, 180, 180);
                    g.DrawLine(pen, c - 11, c - 3, c + 11, c - 3);
                    g.DrawLine(pen, c - 5, c - 3, c - 4, c + 10);
                    g.DrawLine(pen, c + 5, c - 3, c + 4, c + 10);
                    g.DrawLine(pen, c - 4, c + 10, c + 4, c + 10);
                    g.FillEllipse(brush, c - 5, c - 9, 3, 3);
                    g.FillEllipse(brush, c + 2, c - 8, 3, 3);
                    break;
                case "snowflake":
                    g.DrawLine(pen, c, c - 12, c, c + 12);
                    g.DrawLine(pen, c - 10, c - 6, c + 10, c + 6);
                    g.DrawLine(pen, c - 10, c + 6, c + 10, c - 6);
                    g.DrawLine(pen, c - 2.5f, c - 10, c, c - 12); g.DrawLine(pen, c, c - 12, c + 2.5f, c - 10);
                    g.DrawLine(pen, c - 2.5f, c + 10, c, c + 12); g.DrawLine(pen, c, c + 12, c + 2.5f, c + 10);
                    break;
                default: // a bare small dot
                    g.FillEllipse(brush, c - 3, c - 3, 6, 6);
                    break;
            }
        }


    }

    // Opaque JPEG pixels in a reusable GDI bitmap. Global opacity is applied at composition.
    private sealed class Surface : IDisposable
    {
        internal readonly Size Size;
        internal IntPtr DC { get; private set; }
        private IntPtr bitmap, original;

        internal Surface(IntPtr bitmap, Size size)
        {
            if (bitmap == IntPtr.Zero) throw new Win32Exception();
            this.bitmap = bitmap;
            Size = size;
            DC = Native.CreateCompatibleDC(IntPtr.Zero);
            if (DC != IntPtr.Zero) original = Native.SelectObject(DC, bitmap);
            if (original == IntPtr.Zero || original == new IntPtr(-1))
            {
                var error = new Win32Exception();
                Dispose();
                throw error;
            }
        }

        internal Surface ScaleToCover(Size viewport, int focusX, int focusY)
        {
            var frame = new Surface(Native.CreateCompatibleBitmap(DC, viewport.Width, viewport.Height), viewport);
            RectangleF crop = ArtworkLook.CoverSource(Size, viewport, focusX, focusY);
            int width = Math.Max(1, Math.Min(Size.Width, (int)Math.Round(crop.Width)));
            int height = Math.Max(1, Math.Min(Size.Height, (int)Math.Round(crop.Height)));
            int left = Math.Max(0, Math.Min(Size.Width - width, (int)Math.Round(crop.X)));
            int top = Math.Max(0, Math.Min(Size.Height - height, (int)Math.Round(crop.Y)));
            Native.SetStretchBltMode(frame.DC, 4); // HALFTONE
            Native.SetBrushOrgEx(frame.DC, 0, 0, IntPtr.Zero);
            if (!Native.StretchBlt(frame.DC, 0, 0, viewport.Width, viewport.Height, DC,
                left, top, width, height, 0x00CC0020))
            {
                var error = new Win32Exception();
                frame.Dispose();
                throw error;
            }
            return frame;
        }

        public void Dispose()
        {
            if (original != IntPtr.Zero && original != new IntPtr(-1)) Native.SelectObject(DC, original);
            if (DC != IntPtr.Zero) Native.DeleteDC(DC);
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            DC = bitmap = original = IntPtr.Zero;
        }
    }

    private static class Native
    {
        internal const int DisableTransitions = 3; // DWMWA_TRANSITIONS_FORCEDISABLED
        internal const int Style = -16, ExStyle = -20, ToolWindow = 0x80, Topmost = 8;
        internal const uint Previous = 3, Owner = 4;
        internal const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10, Show = 0x40, NoOwnerZOrder = 0x200;
        internal static readonly IntPtr TopmostPosition = new IntPtr(-1), NotTopmost = new IntPtr(-2);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack=1)] internal struct Blend { internal byte Operation, Flags, ConstantAlpha, AlphaFormat; }
        internal delegate bool EnumCallback(IntPtr window, IntPtr data);
        internal delegate void EventCallback(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
        internal static long Styles(IntPtr window, int which) { return GetWindowLongPtr(window, which).ToInt64(); }
        internal static bool IsTopmost(IntPtr window) { return (Styles(window, ExStyle) & Topmost) != 0; }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError=true)] internal static extern IntPtr SetOwner(IntPtr window, int index, IntPtr owner);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, IntPtr data);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder output, int length);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr previous, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr library, EventCallback callback, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr display, ref Point destination, ref Size size, IntPtr source, ref Point from, uint key, ref Blend blend, uint flags);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern IntPtr SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll", SetLastError=true)] internal static extern IntPtr SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")] internal static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    }
}

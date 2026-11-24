using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("Animated Launchers")]
[assembly: AssemblyProduct("Animated Launchers")]
[assembly: AssemblyDescription("Windows launcher with the startup animation and wallpaper for Devin, Antigravity, Codex and Claude")]
[assembly: AssemblyVersion("3.4.0.0")]
[assembly: AssemblyFileVersion("3.4.0.0")]

internal static class Launcher
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    // none: open the app, then play the animation on top. --artwork: the shared
    // wallpaper helper for every app. --preview [folder [hwnd]]: play with the app's
    // settings or a library theme, then exit; with a window handle, centred on that
    // window's monitor. --splash <hwnd>: the same, started by the helper.
    // --app <id> names the app when the exe file name does not (AnimatedLauncher.exe).
    // A copy named <App>Animated.exe is that app's launcher without --app.
    internal static int Run(string[] args)
    {
        Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        App app = AppTable.ByExeName(Path.GetFileName(Application.ExecutablePath));
        bool artwork = false, preview = false, splash = false;
        string folder = null;
        IntPtr anchor = IntPtr.Zero;
        bool anchorGiven = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--artwork") artwork = true;
            else if (arg == "--preview") preview = true;
            else if (arg == "--splash")
            {
                long handle;
                splash = true;
                if (++i >= args.Length ||
                    !long.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out handle)) return 64;
                anchor = new IntPtr(handle);
                anchorGiven = true;
            }
            else if (arg == "--app")
            {
                if (++i >= args.Length || (app = AppTable.ById(args[i])) == null) return 64;
            }
            else if (preview && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (folder == null)
                {
                    folder = arg;
                    if (!Directory.Exists(folder)) return 64;
                }
                else if (!anchorGiven)
                {
                    long handle;
                    if (!long.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out handle)) return 64;
                    anchor = new IntPtr(handle);
                    anchorGiven = true;
                }
                else return 64;
            }
            else return 64;
        }
        bool launch = !artwork && !preview && !splash;
        if ((artwork ? 1 : 0) + (preview ? 1 : 0) + (splash ? 1 : 0) > 1) return 64;
        if (app == null && !artwork) return 64; // a neutral exe needs --app for app modes.
        AppLog.Write("start", (app != null ? app.Product : "Animated Launchers") + " " +
            typeof(Launcher).Assembly.GetName().Version.ToString(3) + " " +
            (artwork ? "helper" : launch ? "launcher" : splash ? "animation" : "preview"));
        CrashGuard.Install(artwork);
        bool first;
        // One helper and one animation at a time, across all apps.
        using (var mutex = new Mutex(true, artwork ? AppTable.ArtworkMutex : AppTable.PlayMutex, out first))
        {
            if (artwork)
            {
                // Runs even when the shared mutex is already ours: helpers of older
                // versions hold their own per-app mutexes and must still hand over.
                if (!ArtworkUpgrade.TakeOverPreviousHelper(mutex))
                {
                    AppLog.Write("helper", "another helper is already running and did not hand over; exiting");
                    return 0;
                }
                if (!first) AppLog.Write("helper", "took over from a previous helper");
            }
            else if (!first && !launch)
            {
                AppLog.Write("animation", "not played: an animation is already playing");
                return 5;
            }
            int result = 3;
            string failure = null;
            try
            {
                try
                {
                    if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware();
                }
                catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (artwork)
                {
                    HelperHost.Run();
                    AppLog.Write("helper", "stopped");
                    AppLog.Flush();
                    return 0;
                }
                if (launch) return OpenAndPlay(app, first);
                if (splash) SplashMemory.CountPlay(app.SettingsDirectory);
                result = Play(app, folder ?? app.SettingsDirectory, anchor, !splash, out failure);
            }
            catch (Exception error)
            {
                if (artwork)
                {
                    CrashGuard.Report(error, "startup");
                    return CrashGuard.ExitCode;
                }
                failure = error.GetBaseException().Message;
                AppLog.Write("animation", "failed: " + error);
            }
            AppLog.Flush();
            return result;
        }
    }

    private static int OpenAndPlay(App app, bool first)
    {
        bool wasOpen = app.FindMainWindow() != IntPtr.Zero;
        StartHelperIfMissing();
        AppLog.Write("launcher", "opening " + app.Name);
        try
        {
            app.Launch();
        }
        catch (Exception error)
        {
            AppLog.Write("launcher", "Windows could not open " + app.Name + ": " + error.Message);
            MessageBox.Show("Windows could not open " + app.Name + ".\n\n" + error.Message,
                app.Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        AppProfile profile = AppProfile.Load(app.SettingsDirectory);
        string skip = wasOpen ? app.Name + " was already open"
            : !first ? "another animation is already playing"
            : !profile.SplashOnStart ? "turned off for " + app.Name
            : QuietTime.Check(app.OwnsProcess);
        if (skip == null && profile.SplashOnce && SplashMemory.PlayedOrMark(app.SettingsDirectory))
            skip = "it already played since sign-in";
        if (skip != null)
        {
            AppLog.Write("animation", "not played: " + skip);
            return 0;
        }
        SplashMemory.CountPlay(app.SettingsDirectory);
        string failure = null;
        try { Play(app, app.SettingsDirectory, IntPtr.Zero, false, out failure); }
        catch (Exception error)
        {
            failure = error.GetBaseException().Message;
            AppLog.Write("animation", "failed: " + error);
        }
        if (failure != null)
            MessageBox.Show("The startup animation could not play.\n\n" + failure +
                "\n\nCheck that the Microsoft Edge WebView2 Runtime is installed and run the full Windows download again.",
                app.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 0;
    }

    // The shared helper is the AnimatedLauncher.exe copy next to this one when it
    // exists; a lone renamed copy can still run --artwork itself.
    private static void StartHelperIfMissing()
    {
        Mutex running;
        if (Mutex.TryOpenExisting(AppTable.ArtworkMutex, out running))
        {
            running.Dispose();
            return;
        }
        try
        {
            using (var child = Process.Start(new ProcessStartInfo(AppSession.HelperExe(), "--artwork")
            {
                UseShellExecute = false
            })) { }
            AppLog.Write("launcher", "started the wallpaper helper");
        }
        catch (Exception error)
        {
            AppLog.Write("launcher", "could not start the wallpaper helper: " + error.Message);
        }
    }

    // Keep animation dependencies out of the normal activation fallback's JIT path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Play(App app, string folder, IntPtr anchor, bool activate, out string failure)
    {
        // A theme can ship a sound.wav chime; playing is best-effort and never
        // blocks or breaks the animation.
        try
        {
            string chime = Path.Combine(folder, "sound.wav");
            if (File.Exists(chime)) new System.Media.SoundPlayer(chime).Play();
        }
        catch (Exception error) { AppLog.Write("animation", "the chime could not play: " + error.Message); }
        CoreWebView2Environment.SetLoaderDllFolderPath(PortableStartup.BundleDirectory);
        using (var splash = new Splash(app, folder, anchor, activate))
        {
            Application.Run(splash);
            failure = splash.Failure;
            AppLog.Write("animation", splash.Result == 0 ? "played to the end"
                : splash.Failure == null ? "closed early"
                : "failed (result " + splash.Result + "): " + splash.Failure);
            return splash.Result;
        }
    }
}

internal sealed class LauncherTheme
{
    private readonly string root;

    private LauncherTheme(string root) { this.root = root; }

    internal static LauncherTheme Current
    {
        get { return new LauncherTheme(Path.Combine(PortableStartup.BundleDirectory, "web")); }
    }

    internal static string DefaultWallpaperPath { get { return Current.WallpaperPath; } }
    internal string WallpaperPath { get { return Path.Combine(root, "assets", "artwork.jpg"); } }

    // The wallpaper, avatar and text come from the given folder: an app's settings
    // folder or a library theme.
    internal Dictionary<string, byte[]> ReadFiles(string contentFolder, App app)
    {
        var settings = new WallpaperSettings(WallpaperPath, contentFolder);
        byte[] wallpaper = settings.LoadJpeg();
        Size artwork;
        using (var decoded = ImageCodec.DecodeJpeg(wallpaper)) artwork = decoded.Size;
        SplashContent content = SplashContent.Load(contentFolder, app.Name, artwork);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { "index.html", SplashPage.RenderIndex(Read("index.html"), content, app.Name) },
            { "style.css", Read("style.css") },
            { "animation.js", Read("animation.js") },
            { "assets/artwork.jpg", wallpaper },
            { "assets/avatar.jpg", settings.LoadAvatarJpegOr(wallpaper) },
            { "assets/contours.js", Read("assets/contours.js") }
        };
    }

    private byte[] Read(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(root, name));
        if (data.Length == 0) throw new InvalidDataException("The animation file " + name + " is empty.");
        return data;
    }
}

internal sealed class Splash : Form
{
    private const string Origin = "https://animation.invalid/";
    private const string Page = Origin + "index.html";
    private static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        {"index.html", "text/html; charset=utf-8"},
        {"style.css", "text/css; charset=utf-8"},
        {"animation.js", "text/javascript; charset=utf-8"},
        {"assets/artwork.jpg", "image/jpeg"},
        {"assets/avatar.jpg", "image/jpeg"},
        {"assets/contours.js", "text/javascript; charset=utf-8"}
    };

    private static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;
    private readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer escape = new System.Windows.Forms.Timer();
    private readonly WebView2 browser;
    private readonly App app;
    private readonly string contentFolder;
    private readonly bool activate;
    private bool finished;
    internal int Result { get; private set; }
    internal string Failure { get; private set; }

    internal Splash(App app, string contentFolder, IntPtr anchor, bool activate)
    {
        this.app = app;
        this.contentFolder = contentFolder;
        this.activate = activate;
        Result = 4;
        Text = app.Product;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(8, 6, 13);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Screen screen = anchor != IntPtr.Zero && IsWindow(anchor) ? Screen.FromHandle(anchor) : Screen.FromPoint(Cursor.Position);
        Rectangle workArea = screen.WorkingArea;
        float scale;
        using (var graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
        int width = (int)Math.Min(1000 * scale, Math.Min(workArea.Width * 0.86, workArea.Height * 0.84 * 1.5));
        int height = width * 2 / 3;
        Bounds = new Rectangle(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
        browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor, AllowExternalDrop = false };
        Controls.Add(browser);
        KeyDown += SkipOnEscape;
        browser.KeyDown += SkipOnEscape;
        watchdog.Interval = 25000;
        watchdog.Tick += delegate { Fail(2, "The animation did not finish loading or playing within 25 seconds."); };
        // The app keeps keyboard focus while the animation plays, so poll for Escape.
        escape.Interval = 30;
        escape.Tick += delegate { if (EscapePressedInAppOrSplash()) Finish(4); };
        Shown += Start;
    }

    protected override bool ShowWithoutActivation { get { return !activate; } }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    private bool EscapePressedInAppOrSplash()
    {
        if ((GetAsyncKeyState(0x1B) & 0x8000) == 0) return false;
        IntPtr foreground = GetForegroundWindow();
        uint pid;
        if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out pid) == 0) return false;
        return pid == OwnProcessId || app.OwnsProcess(pid);
    }

    private void SkipOnEscape(object sender, KeyEventArgs args)
    {
        if (args.KeyCode != Keys.Escape) return;
        args.SuppressKeyPress = true;
        Finish(4);
    }

    private async void Start(object sender, EventArgs args)
    {
        watchdog.Start();
        escape.Start();
        string stage = "loading animation assets";
        try
        {
            var files = LauncherTheme.Current.ReadFiles(contentFolder, app);
            string profile = Path.Combine(app.SettingsDirectory, "WebView2");
            var options = new CoreWebView2EnvironmentOptions();
            options.AdditionalBrowserArguments = "--disable-background-networking --disable-sync";
            stage = "creating the WebView2 environment";
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            if (finished || IsDisposed) return;
            stage = "initializing the WebView2 control";
            await browser.EnsureCoreWebView2Async(environment);
            if (finished || IsDisposed) return;
            stage = "configuring the local renderer";
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (s, e) => e.Handled = true;
            core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (s, e) => e.Cancel = true;
            core.LaunchingExternalUriScheme += (s, e) => e.Cancel = true;
            core.NavigationStarting += (s, e) => e.Cancel = e.Uri != Page;
            core.FrameNavigationStarting += (s, e) => e.Cancel = true;
            core.NavigationCompleted += (s, e) =>
            {
                if (e.IsSuccess) return;
                Fail(3, "WebView2 could not load the animation page (" + e.WebErrorStatus + ").");
            };
            core.ProcessFailed += (s, e) =>
            {
                Fail(3, "The WebView2 renderer closed unexpectedly (" + e.ProcessFailedKind + ").");
            };
            core.WebMessageReceived += OnMessage;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (s, e) =>
            {
                string uri = e.Request.Uri;
                string name = uri.StartsWith(Origin, StringComparison.Ordinal) ? uri.Substring(Origin.Length) : "";
                byte[] bytes;
                if (e.Request.Method != "GET" || !files.TryGetValue(name, out bytes))
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                    return;
                }
                string headers = "Content-Type: " + ContentTypes[name] +
                    "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK", headers);
            };
            stage = "opening the animation page";
            core.Navigate(Page);
        }
        catch (Exception error)
        {
            Fail(3, "An error occurred while " + stage + ". " + error.GetBaseException().Message);
        }
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != Page) return;
        // WebMessageAsJson also safely ignores non-string messages.
        if (args.WebMessageAsJson == "\"complete\"") Finish(0);
        if (args.WebMessageAsJson == "\"assetError\"")
            Fail(3, "The animation page could not decode an image or initialize its drawing surface.");
    }

    private void Fail(int result, string message)
    {
        if (finished || IsDisposed) return;
        Failure = message;
        Finish(result);
    }

    private void Finish(int result)
    {
        if (finished || IsDisposed) return;
        finished = true;
        Result = result;
        watchdog.Stop();
        escape.Stop();
        // Let the WebView2 callback return before disposing its controller.
        BeginInvoke(new Action(Close));
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        finished = true;
        watchdog.Stop();
        escape.Stop();
        base.OnFormClosing(args);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            watchdog.Dispose();
            escape.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

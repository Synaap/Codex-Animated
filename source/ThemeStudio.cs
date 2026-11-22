using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("Theme Studio")]
[assembly: AssemblyProduct("Theme Studio")]
[assembly: AssemblyDescription("Theme library and look editor for the Animated launchers")]
[assembly: AssemblyVersion("3.0.0.0")]
[assembly: AssemblyFileVersion("3.0.0.0")]

internal static class ThemeStudioProgram
{
    private static readonly string[] bundleFiles = {
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
        "notices/LICENSE.txt", "notices/NOTICE.txt"
    };
    internal static string BundleDirectory { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        bool evalMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANIMATED_STUDIO_EVAL"));
        bool first;
        using (var mutex = new Mutex(true, @"Local\AnimatedLaunchers.ThemeStudio", out first))
        {
            if (!first)
            {
                if (!evalMode)
                {
                    string import = ArgValue(args, "--import");
                    if (import != null) import = Path.GetFullPath(import);
                    uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                    foreach (var other in System.Diagnostics.Process.GetProcessesByName("ThemeStudio"))
                        using (other)
                        {
                            if ((uint)other.Id == self) continue;
                            IntPtr existing = other.MainWindowHandle;
                            if (existing == IntPtr.Zero) continue;
                            if (import != null) SendImport(existing, import);
                            if (IsIconic(existing)) ShowWindow(existing, 9);
                            SetForegroundWindow(existing);
                            break;
                        }
                }
                return 0;
            }
            try
            {
                BundleDirectory = EmbeddedBundle.Prepare(typeof(ThemeStudioProgram).Assembly, ThemeLibrary.SharedRoot,
                    "ThemeStudio.Bundle.", bundleFiles);
                EmbeddedBundle.ResolveWebViewFrom(BundleDirectory);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception ||
                    ex is System.Security.SecurityException)) throw;
                MessageBox.Show("Windows could not prepare the Theme Studio files.\n\n" + ex.Message,
                    "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return Run(args);
        }
    }

    internal static string ArgValue(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
        return null;
    }

    // A second instance's --import <file> lands on the first window's WndProc.
    internal const int ImportCopyCode = 0xA17BE;
    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyData { public IntPtr code; public int length; public IntPtr text; }

    private static void SendImport(IntPtr window, string path)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(path);
        IntPtr text = Marshal.AllocHGlobal(bytes.Length);
        IntPtr box = IntPtr.Zero;
        try
        {
            Marshal.Copy(bytes, 0, text, bytes.Length);
            var data = new CopyData { code = (IntPtr)ImportCopyCode, length = bytes.Length, text = text };
            box = Marshal.AllocHGlobal(Marshal.SizeOf(data));
            Marshal.StructureToPtr(data, box, false);
            SendMessage(window, 0x004A, IntPtr.Zero, box); // WM_COPYDATA
        }
        finally
        {
            Marshal.FreeHGlobal(text);
            if (box != IntPtr.Zero) Marshal.FreeHGlobal(box);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);

    // Keep WebView2 references out of Main until the assembly resolver is installed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new StudioForm(args)) Application.Run(form);
        return 0;
    }

    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
}

internal sealed partial class StudioForm : Form
{
    private const string Origin = "https://studio.invalid/";
    private static App[] Apps { get { return AppTable.WithCustom(); } }
    private static readonly string[] Commands = {
        "state", "saveTheme", "applyTheme", "createTheme", "renameTheme", "deleteTheme", "replaceWallpaper",
        "setAvatar", "clearAvatar", "setApp", "setAutostart", "startHelper", "playSplash",
        "exportTheme", "importTheme", "importAnyData", "createThemeData", "duplicateTheme",
        "openThemesFolder", "backupLibrary", "restoreLibrary",
        "saveLookPreset", "deleteLookPreset", "applyLookPreset",
        "pickAppExe", "probeApp", "addCustomApp", "removeCustomApp",
        "useDesktopWallpaper", "pasteImage", "importFolder",
        "cropAvatar", "createGradient", "editGradient", "setPrefs", "seedStarters", "starterPreview",
        "checkReadability", "copyReport", "openLogFolder", "restartHelpers",
        "checkUpdates", "getUpdate" };

    private readonly WebView2 browser;
    private readonly string[] args;
    private readonly bool evalMode;
    private readonly string devFolder;
    private readonly string evalFile;
    private readonly System.Windows.Forms.Timer evalTimer = new System.Windows.Forms.Timer();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
    private readonly Dictionary<string, byte[]> scaledCache = new Dictionary<string, byte[]>();
    private readonly Dictionary<string, string[]> accentCache = new Dictionary<string, string[]>();
    private readonly Dictionary<string, bool> brokenCache = new Dictionary<string, bool>();
    private string previewKey;
    private Bitmap previewSource;
    private string evalStamp = "";
    private bool closingSaved;

    internal StudioForm(string[] args)
    {
        this.args = args;
        evalFile = Environment.GetEnvironmentVariable("ANIMATED_STUDIO_EVAL");
        evalMode = !string.IsNullOrEmpty(evalFile);
        devFolder = Environment.GetEnvironmentVariable("ANIMATED_STUDIO_DEV");
        Text = "Theme Studio";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(0x0F, 0x10, 0x14);
        StartPosition = FormStartPosition.Manual;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
        PlaceWindow();
        browser = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor, AllowExternalDrop = true };
        Controls.Add(browser);
        Shown += Start;
        evalTimer.Interval = 150;
        evalTimer.Tick += delegate { PollEval(); };
    }

    protected override bool ShowWithoutActivation { get { return evalMode; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1, caption = 0x14100F, text = 0xF4EEEC; // COLORREF is BGR
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        DwmSetWindowAttribute(Handle, 36, ref text, 4);
    }

    private static string StudioIni { get { return Path.Combine(ThemeLibrary.SharedRoot, "studio.ini"); } }

    private static float ScaleAt(Point point)
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(point, 2);
            uint x, y;
            if (GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0) return x / 96f;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1f;
    }

    private void SetSizes(float scale)
    {
        ClientSize = new Size((int)Math.Round(1240 * scale), (int)Math.Round(800 * scale));
        MinimumSize = SizeFromClientSize(new Size((int)Math.Round(1040 * scale), (int)Math.Round(660 * scale)));
    }

    // Client size and minimum size are in CSS pixels, scaled by the DPI of the monitor the window opens on.
    private void PlaceWindow()
    {
        Rectangle saved;
        string value = IniFile.Load(StudioIni).Get("window");
        if (value != null && TryParseRect(value, out saved))
        {
            var center = new Point(saved.Left + saved.Width / 2, saved.Top + saved.Height / 2);
            foreach (Screen screen in Screen.AllScreens)
                if (screen.WorkingArea.Contains(center))
                {
                    SetSizes(ScaleAt(center));
                    Bounds = saved;
                    return;
                }
        }
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        SetSizes(ScaleAt(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)));
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private static bool TryParseRect(string text, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        string[] parts = text.Split(',');
        int[] n = new int[4];
        if (parts.Length != 4) return false;
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
        if (n[2] < 400 || n[3] < 300) return false;
        rect = new Rectangle(n[0], n[1], n[2], n[3]);
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingSaved)
        {
            closingSaved = true;
            try
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                IniFile ini = IniFile.Load(StudioIni);
                ini.Set("window", string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", bounds.X, bounds.Y, bounds.Width, bounds.Height));
                ini.Save(StudioIni);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            evalTimer.Dispose();
            if (previewSource != null) previewSource.Dispose();
        }
        base.Dispose(disposing);
    }

    private async void Start(object sender, EventArgs e)
    {
        try
        {
            CoreWebView2Environment.SetLoaderDllFolderPath(ThemeStudioProgram.BundleDirectory);
            string profile = Path.Combine(ThemeLibrary.SharedRoot, "WebView2");
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-background-networking --disable-sync" };
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            await browser.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = !string.IsNullOrEmpty(devFolder);
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = !string.IsNullOrEmpty(devFolder);
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (s, a) => a.Handled = true;
            core.PermissionRequested += (s, a) => a.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (s, a) => a.Cancel = true;
            core.LaunchingExternalUriScheme += (s, a) => a.Cancel = true;
            core.NavigationStarting += (s, a) => a.Cancel = !a.Uri.StartsWith(Origin + "index.html", StringComparison.Ordinal);
            core.FrameNavigationStarting += (s, a) => a.Cancel = !a.Uri.StartsWith(Origin + "splash/", StringComparison.Ordinal);
            core.WebMessageReceived += OnMessage;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (s, a) => Serve(environment, a);
            string hash = "";
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--open") hash = "#" + args[i + 1].Replace(" ", "%20");
            string import = ThemeStudioProgram.ArgValue(args, "--import");
            if (import != null)
            {
                try { hash = "#theme=" + Uri.EscapeDataString(ThemePack.Import(import)); }
                catch (Exception error)
                {
                    if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                        error is ArgumentException || error is ExternalException || error is NotSupportedException)) throw;
                    MessageBox.Show("The theme file could not be imported.\n\n" + error.GetBaseException().Message,
                        "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            core.Navigate(Origin + "index.html" + hash);
            if (evalMode) evalTimer.Start();
        }
        catch (Exception error)
        {
            MessageBox.Show("Theme Studio could not start its window.\n\n" + error.GetBaseException().Message +
                "\n\nCheck that the Microsoft Edge WebView2 Runtime is installed.", "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // ---- resources ----

    private static Dictionary<string, string> Query(Uri uri)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string query = uri.Query;
        if (query.StartsWith("?", StringComparison.Ordinal)) query = query.Substring(1);
        foreach (string pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq));
            map[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        return map;
    }

    private static int Number(Dictionary<string, string> q, string key, int fallback, int min, int max)
    {
        string text;
        int value;
        if (!q.TryGetValue(key, out text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return fallback;
        return Math.Max(min, Math.Min(max, value));
    }

    private void Serve(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs a)
    {
        string uriText = a.Request.Uri;
        if (!uriText.StartsWith(Origin, StringComparison.Ordinal))
        {
            a.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
            return;
        }
        byte[] body = null;
        string type = "text/plain; charset=utf-8";
        int status = 403;
        try
        {
            if (a.Request.Method == "GET")
            {
                var uri = new Uri(uriText);
                string[] segments = uri.AbsolutePath.TrimStart('/').Split('/');
                for (int i = 0; i < segments.Length; i++) segments[i] = Uri.UnescapeDataString(segments[i]);
                status = 404;
                body = Route(segments, Query(uri), ref type);
                if (body != null) status = 200;
            }
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is ExternalException || error is OutOfMemoryException || error is UriFormatException)) throw;
            body = null;
            status = 404;
        }
        string headers = "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
        a.Response = environment.CreateWebResourceResponse(body == null ? null : new MemoryStream(body, false), status,
            status == 200 ? "OK" : status == 404 ? "Not Found" : "Forbidden", headers);
    }

    private byte[] Route(string[] s, Dictionary<string, string> q, ref string type)
    {
        if (s.Length == 1 && (s[0] == "index.html" || s[0] == "studio.css" || s[0] == "studio.js" || s[0] == "strings.js"))
        {
            type = s[0].EndsWith(".html") ? "text/html; charset=utf-8" : s[0].EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
            return PageFile(s[0]);
        }
        if (s.Length == 2 && s[0] == "img")
        {
            string theme;
            if (!q.TryGetValue("theme", out theme)) return null;
            theme = ThemeLibrary.Exact(theme);
            if (theme == null) return null;
            type = "image/jpeg";
            if (s[1] == "wallpaper") return ScaledWallpaper(theme, Number(q, "w", 160, 16, 2400));
            if (s[1] == "avatar")
            {
                string avatar = Path.Combine(ThemeLibrary.Folder(theme), "avatar.jpg");
                return File.Exists(avatar) ? ImageCodec.ReadBytes(avatar) : null;
            }
            if (s[1] == "look") return LookImage(theme, q);
            return null;
        }
        if (s.Length >= 4 && s[0] == "splash") return SplashFile(s, q, ref type);
        return null;
    }

    private byte[] PageFile(string name)
    {
        if (!string.IsNullOrEmpty(devFolder)) return File.ReadAllBytes(Path.Combine(devFolder, name));
        return Embedded("studio/" + name);
    }

    private static byte[] Embedded(string resource)
    {
        using (Stream stream = typeof(StudioForm).Assembly.GetManifestResourceStream(resource))
        {
            if (stream == null) return null;
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        using (var output = new MemoryStream())
        using (var options = new EncoderParameters(1))
        {
            options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            ImageCodecInfo codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
            bitmap.Save(output, codec, options);
            return output.ToArray();
        }
    }

    private static Bitmap Scale(Bitmap src, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(result))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, width, height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        return result;
    }

    private byte[] ScaledWallpaper(string theme, int width)
    {
        string key = theme + "|" + width + "|" + ThemeLibrary.Stamp(theme);
        byte[] cached;
        if (scaledCache.TryGetValue(key, out cached)) return cached;
        using (Bitmap source = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(ThemeLibrary.Folder(theme), "wallpaper.jpg"))))
        using (Bitmap small = width >= source.Width ? null : Scale(source, width, Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width))))
        {
            cached = EncodeJpeg(small ?? source, 85);
        }
        if (scaledCache.Count > 80) scaledCache.Clear();
        scaledCache[key] = cached;
        return cached;
    }

    private byte[] LookImage(string theme, Dictionary<string, string> q)
    {
        string key = theme + "|" + ThemeLibrary.Stamp(theme);
        if (previewKey != key)
        {
            using (Bitmap source = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(ThemeLibrary.Folder(theme), "wallpaper.jpg"))))
            {
                double zoom = Math.Min(1.0, 960.0 / Math.Max(source.Width, source.Height));
                Bitmap fresh = zoom >= 1.0 ? new Bitmap(source) : Scale(source, Math.Max(1, (int)Math.Round(source.Width * zoom)), Math.Max(1, (int)Math.Round(source.Height * zoom)));
                if (previewSource != null) previewSource.Dispose();
                previewSource = fresh;
                previewKey = key;
            }
        }
        var look = new Look { Blur = Number(q, "blur", 0, 0, 100), Dim = Number(q, "dim", 0, 0, 80),
            Tint = Number(q, "tint", 0, 0, 100), Sat = Number(q, "sat", 100, 0, 150), Fade = Number(q, "fade", 0, 0, 100) };
        Color tint;
        string tintText;
        if (q.TryGetValue("tintColor", out tintText) && SplashText.TryParseColor(tintText, out tint)) look.TintColor = tint;
        Bitmap rendered = ArtworkLook.Render(previewSource, look);
        try { return EncodeJpeg(rendered, 88); }
        finally { if (!object.ReferenceEquals(rendered, previewSource)) rendered.Dispose(); }
    }

    private byte[] SplashFile(string[] s, Dictionary<string, string> q, ref string type)
    {
        string theme = ThemeLibrary.Exact(s[1]);
        if (theme == null) return null;
        string folder = ThemeLibrary.Folder(theme);
        string file = string.Join("/", s, 3, s.Length - 3);
        switch (file)
        {
            case "index.html":
            {
                string id;
                q.TryGetValue("app", out id);
                App app = FindApp(id) ?? Apps[0];
                byte[] wallpaper = ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"));
                Size size;
                using (var memory = new MemoryStream(wallpaper, false))
                using (var image = Image.FromStream(memory, false, false)) size = image.Size;
                SplashContent content = SplashContent.Load(folder, app.Name, size);
                type = "text/html; charset=utf-8";
                return SplashPage.RenderIndex(Embedded("web/index.html"), content, app.Name);
            }
            case "style.css": type = "text/css; charset=utf-8"; return Embedded("web/style.css");
            case "animation.js": type = "text/javascript; charset=utf-8"; return Embedded("web/animation.js");
            case "assets/contours.js": type = "text/javascript; charset=utf-8"; return Embedded("web/assets/contours.js");
            case "assets/artwork.jpg":
                type = "image/jpeg";
                return ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"));
            case "assets/avatar.jpg":
            {
                type = "image/jpeg";
                string avatar = Path.Combine(folder, "avatar.jpg");
                return ImageCodec.ReadBytes(File.Exists(avatar) ? avatar : Path.Combine(folder, "wallpaper.jpg"));
            }
        }
        return null;
    }

    // ---- bridge ----

    private static App FindApp(string id)
    {
        foreach (App app in Apps) if (string.Equals(app.Id, id, StringComparison.Ordinal)) return app;
        return null;
    }

    private static IEnumerable<string> AppRoots()
    {
        foreach (App app in Apps) yield return app.SettingsDirectory;
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs a)
    {
        // The embedded splash frame also posts "complete"; only the studio page itself may command the host.
        if (!a.Source.StartsWith(Origin + "index.html", StringComparison.Ordinal)) return;
        Dictionary<string, object> message = null;
        try { message = json.DeserializeObject(a.WebMessageAsJson) as Dictionary<string, object>; }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        if (message == null) return;
        object cmdValue;
        if (!message.TryGetValue("cmd", out cmdValue) || !(cmdValue is string) || Array.IndexOf(Commands, (string)cmdValue) < 0) return;
        object id;
        message.TryGetValue("id", out id);
        var reply = new Dictionary<string, object>();
        reply["id"] = id;
        try
        {
            reply["data"] = Execute((string)cmdValue, message);
            reply["ok"] = true;
        }
        catch (Exception error)
        {
            reply["ok"] = false;
            reply["error"] = Friendly(error);
        }
        browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(reply));
    }

    private static string Friendly(Exception error)
    {
        if (error is InvalidDataException) return "Couldn't read that image. Use a JPEG, PNG or BMP under 64 megapixels.";
        if (error is ArgumentException || error is IOException || error is FileNotFoundException || error is UnauthorizedAccessException ||
            error is InvalidOperationException || error is Win32Exception || error is ExternalException || error is NotSupportedException)
            return error.Message;
        return "Something went wrong: " + error.Message;
    }

    private static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    private static bool Flag(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v is bool && (bool)v;
    }

    private static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    private static int Int(Dictionary<string, object> d, string key, int fallback)
    {
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return fallback;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch (FormatException) { return fallback; } catch (InvalidCastException) { return fallback; }
    }

    private static double Num(Dictionary<string, object> d, string key, double fallback)
    {
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return fallback;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch (FormatException) { return fallback; } catch (InvalidCastException) { return fallback; }
    }

    private string ExistingTheme(Dictionary<string, object> m)
    {
        string name = ThemeLibrary.Exact(Str(m, "name"));
        if (name == null) throw new FileNotFoundException("That theme no longer exists. Pick another one.");
        return name;
    }

    // A dropped file's bytes land in a temporary file so the file-based paths
    // apply. Its own folder keeps the real name, which names the new theme.
    private const long MaxDropBytes = 40L * 1024 * 1024;
    private string TempDataFile(Dictionary<string, object> m)
    {
        string data = Str(m, "data");
        if (string.IsNullOrEmpty(data)) throw new ArgumentException("No file was sent.");
        byte[] bytes = Convert.FromBase64String(data);
        if (bytes.Length <= 0 || bytes.Length > MaxDropBytes)
            throw new InvalidDataException("That file is too big to drop here. Use the file button instead.");
        string name = Path.GetFileName(Str(m, "fileName") ?? "dropped");
        string folder = Path.Combine(Path.GetTempPath(), "AnimatedLaunchers-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, name.Length == 0 ? "dropped" : name);
        File.WriteAllBytes(temp, bytes);
        return temp;
    }

    private static void DeleteTemp(string path)
    {
        try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)); }
        catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
    }

    private string Handle64() { return Handle.ToInt64().ToString(CultureInfo.InvariantCulture); }

    private static string StudioFolder() { return Path.GetDirectoryName(Application.ExecutablePath); }

    // The window class of a running program's visible top-level window, or null
    // when it has none right now (not running, or no matching window).
    private static string WindowClassOf(string exePath)
    {
        App probe = App.Custom("_probe", "probe", exePath);
        string found = null;
        EnumWindowsProc enumProc = delegate(IntPtr window, IntPtr param) {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
            uint pid;
            if (GetWindowThreadProcessId(window, out pid) == 0 || !probe.OwnsProcess(pid)) return true;
            var cls = new StringBuilder(128);
            GetClassNameW(window, cls, cls.Capacity);
            found = cls.ToString();
            return false;
        };
        EnumWindows(enumProc, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder output, int length);

    private string TestPath(Dictionary<string, object> m)
    {
        // Test-only: the native file dialog cannot be driven without input.
        string path = Str(m, "path");
        return evalMode && !string.IsNullOrEmpty(path) ? path : null;
    }

    private string PickImage(string title)
    {
        using (var dialog = new OpenFileDialog {
            Title = title, RestoreDirectory = true,
            Filter = "Images (*.jpg, *.jpeg, *.png, *.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
        })
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private string PickFolder()
    {
        using (var dialog = new FolderBrowserDialog { Description = "Choose a folder of images", ShowNewFolderButton = false })
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    // The file behind the desktop wallpaper: the SPI path, or the transcoded
    // copy Windows keeps for slideshows. Null when no picture is set.
    private static string DesktopWallpaperPath()
    {
        var buffer = new StringBuilder(260);
        if (SystemParametersInfo(0x0073, buffer.Capacity, buffer, 0))
        {
            string path = buffer.ToString();
            if (path.Length > 0 && File.Exists(path)) return path;
        }
        string transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    // The clipboard picture as jpeg bytes, or null when no image is held.
    private static byte[] ClipboardJpeg()
    {
        Image held = Clipboard.GetImage();
        if (held == null) return null;
        try
        {
            using (held)
            using (var bitmap = new Bitmap(held.Width, held.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bitmap)) g.DrawImageUnscaled(held, 0, 0);
                return ImageCodec.EncodeJpeg(bitmap);
            }
        }
        catch (ExternalException) { return null; }
    }

    // g1|g2|degrees message fields -> a validated spec. error explains a refusal.
    private static bool TryGradient(Dictionary<string, object> m, out GradientSpec spec, out string error)
    {
        spec = new GradientSpec();
        string text = (Str(m, "from") ?? "").Trim() + "|" + (Str(m, "to") ?? "").Trim() + "|" +
            (Str(m, "degrees") ?? "0").Trim();
        if (!GradientSpec.TryParse(text, out spec)) { error = "Pick two colours like #RRGGBB."; return false; }
        error = null;
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, int size, StringBuilder value, uint update);

    private string PickFile(string title, string filter)
    {
        using (var dialog = new OpenFileDialog { Title = title, RestoreDirectory = true, Filter = filter })
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private string PickSave(string title, string fileName, string filter)
    {
        using (var dialog = new SaveFileDialog {
            Title = title, RestoreDirectory = true, Filter = filter, FileName = fileName, OverwritePrompt = true
        })
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    // Pushes a theme's new files to the apps showing it, through the slot it holds
    // there, so a night or light-mode pick keeps its place and applied= stays true.
    private void ReapplyTo(string name)
    {
        foreach (string root in AppRoots())
        {
            string slot = ThemeLibrary.ShowingSlot(root, name);
            if (slot != null) ThemeLibrary.ApplyForMode(name, root, slot);
        }
    }

    private object SaveTheme(Dictionary<string, object> m)
    {
        string name = ExistingTheme(m);
        Dictionary<string, object> l = Obj(m, "look") ?? new Dictionary<string, object>();
        Dictionary<string, object> t = Obj(m, "text") ?? new Dictionary<string, object>();
        var ini = new IniFile();
        foreach (string key in new[] { "opacity", "blur", "dim", "tint", "tintColor", "focusX", "focusY",
            "clear", "clearSide", "glow", "vignette", "sat", "fade", "fit" })
            if (Str(l, key) != null) ini.Set(key, Str(l, key));
        foreach (string key in new[] { "title", "subtitle", "accent", "credit", "style", "font", "frame" })
            if (Str(t, key) != null) ini.Set(key, Str(t, key));
        foreach (string key in new[] { "logs", "lines" })
        {
            object values;
            if (t.TryGetValue(key, out values) && values is System.Collections.IEnumerable && !(values is string))
            {
                var lines = new List<string>();
                foreach (object line in (System.Collections.IEnumerable)values) lines.Add(Convert.ToString(line, CultureInfo.InvariantCulture));
                ini.Set(key, string.Join("|", lines.ToArray()));
            }
        }
        SplashText stored;
        Look clamped = ThemeLibrary.Save(name, Look.Read(ini), SplashText.Read(ini), AppRoots(), out stored);
        return new Dictionary<string, object> { { "look", LookData(clamped) }, { "text", TextData(stored) } };
    }

    private static Dictionary<string, object> LookData(Look look)
    {
        return new Dictionary<string, object> {
            { "opacity", look.Opacity }, { "blur", look.Blur }, { "dim", look.Dim }, { "tint", look.Tint },
            { "tintColor", string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", look.TintColor.R, look.TintColor.G, look.TintColor.B) },
            { "focusX", look.FocusX }, { "focusY", look.FocusY },
            { "clear", look.Clear }, { "clearSide", look.ClearSide }, { "glow", look.Glow },
            { "vignette", look.Vignette }, { "sat", look.Sat }, { "fade", look.Fade }, { "fit", look.Fit }
        };
    }

    private static Dictionary<string, object> TextData(SplashText text)
    {
        return new Dictionary<string, object> {
            { "title", text.Title }, { "subtitle", text.Subtitle }, { "accent", text.Accent }, { "logs", text.Logs },
            { "credit", text.Credit }, { "style", text.Style }, { "font", text.Font }, { "frame", text.Frame },
            { "lines", text.Lines }
        };
    }

    // The shared wallpaper helper, or this app's part of it: the process is up and
    // the app is watched. Pass an app for the per-app answer.
    private bool HelperRunning(App app = null)
    {
        Mutex running;
        if (!Mutex.TryOpenExisting(AppTable.ArtworkMutex, out running)) return false;
        running.Dispose();
        return app == null || AppProfile.Load(app.SettingsDirectory).Watch;
    }

    // The settings slot a manual pick or apply lands in right now: the night
    // pick inside its window, the Windows-light pick while follow is on, else
    // theme=. The session's DesiredSlot mirrors this.
    private static string SlotFor(AppProfile profile)
    {
        if (profile.ThemeNight.Length > 0 &&
            ThemeLibrary.IsNight(profile.NightStart, profile.NightUntil, DateTime.Now.Hour))
            return "themeNight";
        if (profile.FollowWindows && WindowsTheme.IsLight() && profile.ThemeLight.Length > 0)
            return "themeLight";
        return "theme";
    }

    private object AppList()
    {
        var list = new List<object>();
        bool helperExists = File.Exists(Path.Combine(StudioFolder(), AppTable.HelperExeName));
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppTable.RunKey))
        {
            bool runEntry = key != null && key.GetValue(AppTable.RunValueName) != null;
            bool light = WindowsTheme.IsLight();
            foreach (App app in Apps)
            {
                AppProfile profile = AppProfile.Load(app.SettingsDirectory);
                // What the helper last reported about this app's window.
                IniFile status = IniFile.Load(Path.Combine(app.SettingsDirectory, "status.ini"));
                // The theme select shows the slot that is actually on screen: the
                // night pick inside its window, the light pick in follow mode.
                string slot = SlotFor(profile);
                string shown = slot == "themeNight" ? profile.ThemeNight :
                    slot == "themeLight" ? profile.ThemeLight : profile.Theme;
                bool night = ThemeLibrary.IsNight(profile.NightStart, profile.NightUntil, DateTime.Now.Hour);
                var row = new Dictionary<string, object> {
                    { "id", app.Id }, { "name", app.Name }, { "theme", shown }, { "splash", profile.SplashOnStart },
                    { "rotate", profile.Rotate }, { "rotateFavs", profile.RotateFavs },
                    { "followWindows", profile.FollowWindows }, { "themeLight", profile.ThemeLight },
                    { "themeNight", profile.ThemeNight },
                    { "nightStart", profile.NightStart }, { "nightUntil", profile.NightUntil },
                    { "nightDim", profile.NightDim }, { "extraDim", profile.ExtraDim },
                    { "hideFullscreen", profile.HideFullscreen },
                    { "modeLight", light }, { "modeNight", night },
                    { "speed", profile.Speed }, { "style", profile.Style }, { "once", profile.SplashOnce }, { "watch", profile.Watch },
                    { "installed", app.Installed }, { "preview", app.Preview },
                    { "helperExists", helperExists },
                    { "helperRunning", HelperRunning(app) },
                    { "autostart", runEntry && profile.Watch },
                    { "status", status.Get("status") ?? "" },
                    { "problem", status.Get("problem") ?? "" },
                    { "statusWhen", status.Get("when") ?? "" }
                };
                if (app.IsCustom) { row["custom"] = true; row["exe"] = app.CustomExe; }
                list.Add(row);
            }
        }
        return list;
    }

    // Plain-text status report for bug reports; mirrors the Apps page, plus the
    // last problem each session wrote to status.ini.
    private string BuildReport()
    {
        var text = new StringBuilder();
        text.AppendLine("Animated Launchers report");
        text.AppendLine("Theme Studio " + Application.ProductVersion + " - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC");
        text.AppendLine("Windows " + Environment.OSVersion.VersionString);
        text.AppendLine();
        foreach (Dictionary<string, object> row in (List<object>)AppList())
        {
            text.Append(row["name"]).Append(" (").Append(row["id"]).Append(")");
            if ((bool)row["installed"]) text.Append(" installed"); else text.Append(" not installed");
            if (!(bool)row["watch"]) text.Append(", watching off");
            if ((bool)row["helperRunning"]) text.Append(", helper on");
            if ((bool)row["autostart"]) text.Append(", autostart");
            text.Append(", theme: ").Append(row["theme"]);
            string status = (string)row["status"];
            string problem = (string)row["problem"];
            if (status != "") text.Append(", wallpaper: ").Append(status);
            if (problem != "") text.Append(", problem: ").Append(problem);
            text.AppendLine();
        }
        // The tail of each helper log, so a pasted report carries the errors too.
        var logs = new List<string>();
        logs.Add(Path.Combine(ThemeLibrary.SharedRoot, "Logs", "helper.log"));
        foreach (App app in Apps)
            logs.Add(Path.Combine(app.SettingsDirectory, "Logs", "helper.log"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in logs)
        {
            if (!seen.Add(file)) continue;
            string tail;
            try
            {
                if (!File.Exists(file)) continue;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long skip = Math.Max(0, stream.Length - 8192);
                    stream.Position = skip;
                    var bytes = new byte[stream.Length - skip];
                    int read = 0, n;
                    while (read < bytes.Length && (n = stream.Read(bytes, read, bytes.Length - read)) > 0) read += n;
                    // Start after the first line break, so the tail never opens mid-line
                    // (a StreamReader would have buffered past it).
                    int start = 0;
                    if (skip > 0) { int nl = Array.IndexOf(bytes, (byte)'\n', 0, read); start = nl < 0 ? read : nl + 1; }
                    tail = Encoding.UTF8.GetString(bytes, start, read - start);
                }
            }
            catch (Exception) { continue; }
            if (tail.Trim().Length == 0) continue;
            text.AppendLine();
            text.AppendLine("--- " + file.Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%") + " ---");
            text.Append(tail);
        }
        return text.ToString();
    }

    private Dictionary<string, object> State()
    {
        var themes = new List<object>();
        foreach (string name in ThemeLibrary.Names())
        {
            string folder = ThemeLibrary.Folder(name);
            IniFile ini = IniFile.Load(Path.Combine(folder, "theme.ini"));
            string stamp = name + "|" + ThemeLibrary.Stamp(name);
            string[] accents;
            bool broken;
            if (!accentCache.TryGetValue(stamp, out accents))
            {
                var found = new List<string>();
                broken = false;
                try
                {
                    using (Bitmap bitmap = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"))))
                        foreach (Color c in ArtworkLook.SuggestAccents(bitmap, 4))
                            found.Add(string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B));
                }
                catch (Exception error)
                {
                    if (!(error is InvalidDataException || error is IOException || error is UnauthorizedAccessException ||
                        error is ArgumentException || error is ExternalException || error is OutOfMemoryException)) throw;
                    broken = true;
                }
                accents = found.ToArray();
                accentCache[stamp] = accents;
                brokenCache[stamp] = broken;
            }
            brokenCache.TryGetValue(stamp, out broken);
            themes.Add(new Dictionary<string, object> {
                { "name", name }, { "look", LookData(Look.Read(ini)) }, { "text", TextData(SplashText.Read(ini)) },
                { "hasAvatar", File.Exists(Path.Combine(folder, "avatar.jpg")) }, { "stamp", ThemeLibrary.Stamp(name) },
                { "gradient", ini.Get("gradient") ?? "" }, { "created", Directory.GetCreationTimeUtc(folder).ToString("o") },
                { "accents", accents }, { "broken", broken }
            });
        }
        Rectangle area = Screen.FromControl(this).WorkingArea;
        return new Dictionary<string, object> {
            { "themes", themes }, { "apps", AppList() }, { "looks", LookPresets.Names() },
            { "prefs", Prefs() }, { "version", Application.ProductVersion },
            { "screen", new Dictionary<string, object> { { "w", area.Width }, { "h", area.Height } } }
        };
    }

    // Studio-window prefs in studio.ini: favorites is a |-list of theme names,
    // sort is name|favorites|newest. Unknown keys (window position) survive untouched.
    private static Dictionary<string, object> Prefs()
    {
        IniFile ini = IniFile.Load(StudioIni);
        string sort = (ini.Get("sort") ?? "name").Trim().ToLowerInvariant();
        if (sort != "favorites" && sort != "newest") sort = "name";
        var favorites = new List<object>();
        foreach (string part in (ini.Get("favorites") ?? "").Split('|'))
        {
            string exact = ThemeLibrary.Exact(part.Trim());
            if (exact != null && !favorites.Contains(exact)) favorites.Add(exact);
        }
        bool welcome = !string.Equals((ini.Get("welcome") ?? "on").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        return new Dictionary<string, object> { { "favorites", favorites }, { "sort", sort }, { "welcome", welcome }, { "lang", (ini.Get("lang") ?? "").Trim() } };
    }

    // A second instance's --import <file> arrives as WM_COPYDATA; the first
    // instance imports it and tells the page.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x004A) // WM_COPYDATA
        {
            var data = (ThemeStudioProgram.CopyData)Marshal.PtrToStructure(m.LParam, typeof(ThemeStudioProgram.CopyData));
            if (data.code == (IntPtr)ThemeStudioProgram.ImportCopyCode && data.length > 0 &&
                data.length < 8192 && data.length % 2 == 0 && data.text != IntPtr.Zero)
            {
                string path = Marshal.PtrToStringUni(data.text, data.length / 2);
                if (!string.IsNullOrEmpty(path)) BeginInvoke(new Action<string>(ReceiveImport), path);
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void ReceiveImport(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            string imported = ThemePack.Import(path);
            Dictionary<string, object> state = State();
            state["imported"] = imported;
            var push = new Dictionary<string, object> { { "event", "imported" }, { "state", state } };
            if (browser.CoreWebView2 != null)
                browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(push));
            if (ThemeStudioProgram.IsIconic(Handle)) ThemeStudioProgram.ShowWindow(Handle, 9);
            ThemeStudioProgram.SetForegroundWindow(Handle);
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is ExternalException || error is NotSupportedException)) throw;
            MessageBox.Show("The theme file could not be imported.\n\n" + error.GetBaseException().Message,
                "Theme Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- eval hook (tests only, ANIMATED_STUDIO_EVAL=<file>) ----

    private void PollEval()
    {
        FileInfo info = new FileInfo(evalFile);
        if (!info.Exists) return;
        string stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
        if (stamp == evalStamp) return;
        string code;
        try { code = File.ReadAllText(evalFile, Encoding.UTF8); }
        catch (IOException) { return; }
        evalStamp = stamp;
        RunEval(code);
    }

    private async void RunEval(string code)
    {
        string result;
        try
        {
            CoreWebView2 core = browser.CoreWebView2;
            if (code.StartsWith("@capture ", StringComparison.Ordinal))
            {
                string path = code.Substring(9).Trim();
                using (var file = File.Create(path))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                result = json.Serialize(new Dictionary<string, object> { { "ok", true }, { "captured", path } });
            }
            else if (code.StartsWith("@close", StringComparison.Ordinal))
            {
                result = json.Serialize(new Dictionary<string, object> { { "ok", true }, { "closing", true } });
                File.WriteAllText(evalFile + ".out", result, new UTF8Encoding(false));
                BeginInvoke(new Action(Close));
                return;
            }
            else
            {
                string wrapped = "(function(){try{var r=(function(){" + code + "\n})();return JSON.stringify({ok:true,value:r===undefined?null:r});}" +
                    "catch(e){return JSON.stringify({ok:false,error:String((e&&e.stack)||e)});}})()";
                string raw = await core.ExecuteScriptAsync(wrapped);
                result = json.DeserializeObject(raw) as string ?? raw;
            }
        }
        catch (Exception error)
        {
            result = json.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", error.Message } });
        }
        File.WriteAllText(evalFile + ".out", result, new UTF8Encoding(false));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}

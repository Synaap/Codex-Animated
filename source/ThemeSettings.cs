using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// key=value settings file: UTF-8 without BOM, CRLF, keys case-insensitive,
// unknown keys kept on save.
internal sealed class IniFile
{
    private readonly List<string> order = new List<string>();
    private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // A missing or unreadable file reads as empty.
    internal static IniFile Load(string path)
    {
        try { return LoadStrict(path); }
        catch (IOException) { return new IniFile(); }
        catch (UnauthorizedAccessException) { return new IniFile(); }
    }

    // A missing file is empty; a file that exists but cannot be read throws.
    internal static IniFile LoadStrict(string path)
    {
        if (!File.Exists(path)) return new IniFile();
        string text;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var reader = new StreamReader(file, new UTF8Encoding(false), true))
            text = reader.ReadToEnd();
        return Parse(text);
    }

    // The same key=value rules applied to text already in memory.
    internal static IniFile Parse(string text)
    {
        var ini = new IniFile();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';') continue;
            int split = line.IndexOf('=');
            if (split <= 0) continue;
            string key = line.Substring(0, split).Trim();
            if (key.Length == 0) continue;
            ini.Set(key, line.Substring(split + 1).Trim());
        }
        return ini;
    }

    internal string Get(string key)
    {
        string value;
        return values.TryGetValue(key, out value) ? value : null;
    }

    // The keys in file order.
    internal string[] Keys { get { return order.ToArray(); } }

    internal void Set(string key, string value)
    {
        if (value == null)
        {
            if (values.Remove(key)) order.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            return;
        }
        value = value.Replace("\r", "").Replace("\n", "");
        if (!values.ContainsKey(key)) order.Add(key);
        values[key] = value;
    }

    internal string Render()
    {
        var text = new StringBuilder();
        foreach (string key in order) text.Append(key).Append('=').Append(values[key]).Append("\r\n");
        return text.ToString();
    }

    internal void Save(string path)
    {
        ImageCodec.SaveAtomic(path, new UTF8Encoding(false).GetBytes(Render()));
    }
}

internal sealed class Look
{
    internal static readonly Color DefaultTint = Color.FromArgb(0x8C, 0x66, 0xD1);
    internal int Opacity = 15, Blur, Dim, Tint, FocusX = 50, FocusY = 50, Clear, Glow, Vignette;
    internal int Sat = 100, Fade, Drift;
    internal string ClearSide = "", Fit = "cover";
    internal Color TintColor = DefaultTint;

    internal byte Alpha { get { return (byte)Math.Round(255 * Opacity / 100.0, MidpointRounding.AwayFromZero); } }

    internal Look Copy()
    {
        var look = new Look();
        look.Opacity = Opacity; look.Blur = Blur; look.Dim = Dim; look.Tint = Tint;
        look.FocusX = FocusX; look.FocusY = FocusY; look.Clear = Clear; look.Glow = Glow;
        look.Vignette = Vignette; look.Sat = Sat; look.Fade = Fade; look.Drift = Drift;
        look.ClearSide = ClearSide; look.Fit = Fit; look.TintColor = TintColor;
        return look;
    }

    internal bool SameRender(Look other)
    {
        return Blur == other.Blur && Dim == other.Dim && Tint == other.Tint && Sat == other.Sat &&
            Fade == other.Fade && TintColor.ToArgb() == other.TintColor.ToArgb();
    }

    // Fields applied at present time inside the app window, not to the source image.
    internal bool SamePresent(Look other)
    {
        return Opacity == other.Opacity && FocusX == other.FocusX && FocusY == other.FocusY &&
            Clear == other.Clear && Glow == other.Glow && Vignette == other.Vignette &&
            ClearSide == other.ClearSide && Fit == other.Fit && Drift == other.Drift;
    }

    internal static Look Read(IniFile ini)
    {
        var look = new Look();
        look.Opacity = Number(ini, "opacity", 15, 4, 40);
        look.Blur = Number(ini, "blur", 0, 0, 100);
        look.Dim = Number(ini, "dim", 0, 0, 80);
        look.Tint = Number(ini, "tint", 0, 0, 100);
        look.FocusX = Number(ini, "focusX", 50, 0, 100);
        look.FocusY = Number(ini, "focusY", 50, 0, 100);
        look.Clear = Number(ini, "clear", 0, 0, 100);
        look.Glow = Number(ini, "glow", 0, 0, 100);
        look.Vignette = Number(ini, "vignette", 0, 0, 100);
        look.Sat = Number(ini, "sat", 100, 0, 150);
        look.Fade = Number(ini, "fade", 0, 0, 100);
        look.Drift = Number(ini, "drift", 0, 0, 100);
        string side = (ini.Get("clearSide") ?? "").Trim().ToLowerInvariant();
        look.ClearSide = side == "left" || side == "right" || side == "center" ? side : "";
        string fit = (ini.Get("fit") ?? "").Trim().ToLowerInvariant();
        look.Fit = fit == "contain" || fit == "center" || fit == "pixel" ? fit : "cover";
        Color color;
        look.TintColor = SplashText.TryParseColor(ini.Get("tintColor"), out color) ? color : DefaultTint;
        return look;
    }

    internal void Write(IniFile ini)
    {
        ini.Set("opacity", Opacity.ToString(CultureInfo.InvariantCulture));
        ini.Set("blur", Blur.ToString(CultureInfo.InvariantCulture));
        ini.Set("dim", Dim.ToString(CultureInfo.InvariantCulture));
        ini.Set("tint", Tint.ToString(CultureInfo.InvariantCulture));
        ini.Set("tintColor", string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", TintColor.R, TintColor.G, TintColor.B));
        ini.Set("focusX", FocusX.ToString(CultureInfo.InvariantCulture));
        ini.Set("focusY", FocusY.ToString(CultureInfo.InvariantCulture));
        ini.Set("clear", Clear.ToString(CultureInfo.InvariantCulture));
        ini.Set("clearSide", ClearSide ?? "");
        ini.Set("glow", Glow.ToString(CultureInfo.InvariantCulture));
        ini.Set("vignette", Vignette.ToString(CultureInfo.InvariantCulture));
        ini.Set("sat", Sat.ToString(CultureInfo.InvariantCulture));
        ini.Set("fade", Fade.ToString(CultureInfo.InvariantCulture));
        ini.Set("fit", Fit ?? "cover");
        ini.Set("drift", Drift.ToString(CultureInfo.InvariantCulture));
    }

    internal static int Number(IniFile ini, string key, int fallback, int min, int max)
    {
        int value;
        if (!int.TryParse(ini.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return fallback;
        return Math.Max(min, Math.Min(max, value));
    }
}

internal sealed class SplashText
{
    internal string Title = "", Subtitle = "", Accent = "", Credit = "", Style = "", Font = "", Frame = "";
    internal string[] Logs = new string[0], Lines = new string[0];

    // "full" | "short" | "terminal" | "manga" | "minimal"; anything else means the
    // theme does not pick a style and the app's own default applies.
    internal static string NormalizeStyle(string style)
    {
        switch ((style ?? "").Trim().ToLowerInvariant())
        {
            case "full": case "short": case "terminal": case "manga": case "minimal":
                return style.Trim().ToLowerInvariant();
            default: return "";
        }
    }

    // The title font is a name from the whitelist of fonts Windows always has;
    // "" keeps the original SimSun serif look.
    internal static string NormalizeFont(string font)
    {
        switch ((font ?? "").Trim().ToLowerInvariant())
        {
            case "segoe": case "mono": case "serif": case "gothic": case "type": case "impact":
                return font.Trim().ToLowerInvariant();
            default: return "";
        }
    }

    // The avatar frame shape; "" is the corner-notch square the animation shipped with.
    internal static string NormalizeFrame(string frame)
    {
        switch ((frame ?? "").Trim().ToLowerInvariant())
        {
            case "round": case "circle": case "diamond": case "hex":
                return frame.Trim().ToLowerInvariant();
            default: return "";
        }
    }

    private static string[] ReadLines(IniFile ini, string key, int max)
    {
        var lines = new List<string>();
        string joined = ini.Get(key);
        if (!string.IsNullOrEmpty(joined))
            foreach (string part in joined.Split('|'))
            {
                string line = Limit(part, 80);
                if (line.Length > 0 && lines.Count < max) lines.Add(line);
            }
        return lines.ToArray();
    }

    internal static SplashText Read(IniFile ini)
    {
        var text = new SplashText();
        text.Title = Limit(ini.Get("title"), 40);
        text.Subtitle = Limit(ini.Get("subtitle"), 60);
        text.Credit = Limit(ini.Get("credit"), 80);
        text.Style = NormalizeStyle(ini.Get("style"));
        text.Font = NormalizeFont(ini.Get("font"));
        text.Frame = NormalizeFrame(ini.Get("frame"));
        Color color;
        text.Accent = TryParseColor(ini.Get("accent"), out color)
            ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B) : "";
        text.Logs = ReadLines(ini, "logs", 4);
        text.Lines = ReadLines(ini, "lines", 16);
        return text;
    }

    internal void Write(IniFile ini)
    {
        ini.Set("title", Limit(Title, 40));
        ini.Set("subtitle", Limit(Subtitle, 60));
        ini.Set("accent", Accent ?? "");
        ini.Set("logs", JoinLines(Logs, 4));
        ini.Set("lines", JoinLines(Lines, 16));
        ini.Set("credit", Limit(Credit, 80));
        ini.Set("style", NormalizeStyle(Style));
        ini.Set("font", NormalizeFont(Font));
        ini.Set("frame", NormalizeFrame(Frame));
    }

    private static string JoinLines(string[] values, int max)
    {
        var lines = new List<string>();
        if (values != null)
            foreach (string value in values)
            {
                string line = Limit((value ?? "").Replace("|", " "), 80);
                if (line.Length > 0 && lines.Count < max) lines.Add(line);
            }
        return string.Join("|", lines.ToArray());
    }

    // Strict #RRGGBB.
    internal static bool TryParseColor(string text, out Color color)
    {
        color = Color.Empty;
        if (text == null) return false;
        text = text.Trim();
        if (text.Length != 7 || text[0] != '#') return false;
        int rgb;
        for (int i = 1; i < 7; i++)
            if (Uri.IsHexDigit(text[i]) == false) return false;
        if (!int.TryParse(text.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb))
            return false;
        color = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    private static string Limit(string value, int max)
    {
        value = (value ?? "").Trim();
        return value.Length > max ? value.Substring(0, max) : value;
    }
}

// One app's settings.ini.
internal sealed class AppProfile
{
    internal const string FileName = "settings.ini";
    internal readonly string Root;
    internal string Theme = "";
    internal Look Look = new Look();
    internal SplashText Text = new SplashText();
    internal bool SplashOnStart = true;
    internal bool Watch = true;
    internal int Speed = 1;
    // The app's effective animation style ("full" or "short"). A theme's own style
    // replaces it when the theme is applied or saved; this is the per-app default.
    internal string Style = "full";
    // When on, the animation plays only on the first app start after sign-in.
    internal bool SplashOnce = false;
    // Theme variety: "off", "start" or a number of minutes between switches.
    internal string Rotate = "off";
    // When on, rotation picks only from the studio's favorites list.
    internal bool RotateFavs = false;
    // When on, theme= is the dark pick and themeLight= shows in Windows light mode.
    internal bool FollowWindows = false;
    internal string ThemeLight = "";
    // A third slot: themeNight= shows inside the night window nightStart..nightUntil
    // (wraps midnight). Empty = no night theme.
    internal string ThemeNight = "";
    internal int NightStart = 20, NightUntil = 7;
    // Added to the theme's dim at night, and to it all the time (0..80).
    internal int NightDim = 0, ExtraDim = 0;
    // Hide the artwork while the app covers its monitor or Windows reports
    // presentation mode.
    internal bool HideFullscreen = true;
    // The theme whose files are actually in the folder right now. Normally equal to
    // Theme (or ThemeLight/ThemeNight while that mode is active); "" means the folder
    // was not populated through ApplyForMode.
    internal string Applied = "";

    private AppProfile(string root) { Root = root; }

    internal static AppProfile Load(string root) { return Load(root, false); }

    internal static AppProfile Load(string root, bool customApp) { return Parse(root, IniFile.Load(Path.Combine(root, FileName)), customApp); }

    // Throws when the file exists but cannot be read, so a live reload can keep its last state.
    internal static AppProfile LoadStrict(string root) { return LoadStrict(root, false); }

    internal static AppProfile LoadStrict(string root, bool customApp) { return Parse(root, IniFile.LoadStrict(Path.Combine(root, FileName)), customApp); }

    private static AppProfile Parse(string root, IniFile ini, bool customApp)
    {
        var profile = new AppProfile(root);
        profile.Theme = (ini.Get("theme") ?? "").Trim();
        profile.Look = Look.Read(ini);
        profile.Text = SplashText.Read(ini);
        profile.SplashOnStart = customApp ?
            string.Equals((ini.Get("splash") ?? "").Trim(), "on", StringComparison.OrdinalIgnoreCase) :
            !string.Equals((ini.Get("splash") ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        profile.Watch = !string.Equals((ini.Get("watch") ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        profile.Speed = Look.Number(ini, "speed", 1, 1, 3);
        string style = SplashText.NormalizeStyle(ini.Get("style"));
        profile.Style = style.Length > 0 ? style : "full";
        profile.SplashOnce = string.Equals((ini.Get("once") ?? "").Trim(), "on", StringComparison.OrdinalIgnoreCase);
        profile.Rotate = NormalizeRotate(ini.Get("rotate"));
        profile.RotateFavs = string.Equals((ini.Get("rotateFavs") ?? "").Trim(), "on", StringComparison.OrdinalIgnoreCase);
        profile.FollowWindows = string.Equals((ini.Get("followWindows") ?? "").Trim(), "on", StringComparison.OrdinalIgnoreCase);
        profile.ThemeLight = (ini.Get("themeLight") ?? "").Trim();
        profile.ThemeNight = (ini.Get("themeNight") ?? "").Trim();
        profile.NightStart = Look.Number(ini, "nightStart", 20, 0, 23);
        profile.NightUntil = Look.Number(ini, "nightUntil", 7, 0, 23);
        profile.NightDim = Look.Number(ini, "nightDim", 0, 0, 80);
        profile.ExtraDim = Look.Number(ini, "extraDim", 0, 0, 80);
        profile.HideFullscreen = !string.Equals((ini.Get("hideFullscreen") ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase);
        profile.Applied = (ini.Get("applied") ?? "").Trim();
        return profile;
    }

    // "off" | "start" | whole minutes (5..1440) as text.
    internal static string NormalizeRotate(string value)
    {
        string raw = (value ?? "").Trim().ToLowerInvariant();
        if (raw == "start") return "start";
        int minutes;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) &&
            minutes >= 5 && minutes <= 1440)
            return minutes.ToString(CultureInfo.InvariantCulture);
        return "off";
    }

    internal void Save()
    {
        ThemeLibrary.WithLock(Root, delegate {
            string path = Path.Combine(Root, FileName);
            IniFile ini = IniFile.LoadStrict(path);
            ini.Set("theme", Theme ?? "");
            Look.Write(ini);
            Text.Write(ini);
            ini.Set("splash", SplashOnStart ? "on" : "off");
            ini.Set("watch", Watch ? "on" : "off");
            ini.Set("speed", Math.Max(1, Math.Min(3, Speed)).ToString(CultureInfo.InvariantCulture));
            // The effective style lives on the same key the theme's choice uses; the
            // app's resolved value wins over the copied theme text's own write.
            ini.Set("style", SplashText.NormalizeStyle(Style).Length > 0 ? Style : "full");
            ini.Set("once", SplashOnce ? "on" : "off");
            ini.Set("rotate", NormalizeRotate(Rotate));
            ini.Set("rotateFavs", RotateFavs ? "on" : "off");
            ini.Set("followWindows", FollowWindows ? "on" : "off");
            ini.Set("themeLight", ThemeLight ?? "");
            ini.Set("themeNight", ThemeNight ?? "");
            ini.Set("nightStart", Math.Max(0, Math.Min(23, NightStart)).ToString(CultureInfo.InvariantCulture));
            ini.Set("nightUntil", Math.Max(0, Math.Min(23, NightUntil)).ToString(CultureInfo.InvariantCulture));
            ini.Set("nightDim", Math.Max(0, Math.Min(80, NightDim)).ToString(CultureInfo.InvariantCulture));
            ini.Set("extraDim", Math.Max(0, Math.Min(80, ExtraDim)).ToString(CultureInfo.InvariantCulture));
            ini.Set("hideFullscreen", HideFullscreen ? "on" : "off");
            ini.Set("applied", Applied ?? "");
            ini.Save(path);
        });
    }
}

// Whether Windows apps are currently in light mode (Personalize key; absent or
// 0 means dark). Never throws — a registry problem simply means dark.
internal static class WindowsTheme
{
    internal static bool IsLight()
    {
        // Test switch: 1 forces light, 0 forces dark, anything else reads the key.
        string forced = Environment.GetEnvironmentVariable("ANIMATED_LIGHT");
        if (forced == "1") return true;
        if (forced == "0") return false;
        try
        {
            object value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            return value is int && (int)value != 0;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException ||
                error is System.Security.SecurityException)) throw;
            return false;
        }
    }
}

// Remembers which sign-in session already had its animation, for "once" mode.
// The marker is the earliest explorer.exe start time (sign-in opens the shell),
// falling back to boot time. Each app keeps its own signin.stamp file.
internal static class SplashMemory
{
    internal const string StampName = "signin.stamp";
    private static string marker;

    internal static string SignInMarker()
    {
        if (marker != null) return marker;
        DateTime signIn = DateTime.MinValue;
        try
        {
            foreach (Process process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    DateTime started = process.StartTime;
                    if (signIn == DateTime.MinValue || started < signIn) signIn = started;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        if (signIn == DateTime.MinValue)
            signIn = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount);
        marker = signIn.ToString("o", CultureInfo.InvariantCulture);
        return marker;
    }

    // True when the stamp already matches this sign-in; otherwise writes it.
    internal static bool PlayedOrMark(string root)
    {
        string value = SignInMarker();
        string path = Path.Combine(root, StampName);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == value) return true;
            File.WriteAllText(path, value);
        }
        catch (Exception) { }
        return false;
    }

    // plays.stamp counts how many times the animation really started for this app —
    // previews never touch it. Powers the {starts} boot-line variable.
    internal static int Plays(string root)
    {
        try
        {
            string path = Path.Combine(root, "plays.stamp");
            int count;
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out count)
                ? Math.Max(0, count) : 0;
        }
        catch (Exception) { return 0; }
    }

    internal static int CountPlay(string root)
    {
        int count = Plays(root) + 1;
        try { File.WriteAllText(Path.Combine(root, "plays.stamp"), count.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception) { }
        return count;
    }
}

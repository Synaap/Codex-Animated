using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// The text and accent shown by the splash page, and the token replacement that
// puts them into index.html.
internal sealed class SplashContent
{
    internal const string DefaultAccent = "#D980A7";
    // The text the original animation shipped with; used when an app has no theme and no text of its own.
    internal const string UpstreamTitle = "\u98DE\u884C\u96EA\u7ED2";
    internal const string UpstreamSubtitle = "\u5E7D\u7075\u6765\u5230\u2026\u4F60\u8EAB\u8FB9\uFF5E";
    internal const string UpstreamLabel = "RESONANCE / AEMEATH";
    internal static readonly string[] UpstreamLogs = {
        "MAGI SYSTEM \u00B7 CASPER / MELCHIOR / BALTHASAR", "loading renderer surface ........ ok",
        "entry plug depth 21.6 ........... ok", "LCL pressure nominal" };

    internal string Title = "", Subtitle = "", Accent = DefaultAccent, Theme = "", Label = "", Caption = "", Style = "full";
    internal string Font = "", Frame = "";
    internal string[] Logs = { "", "", "", "" };
    internal int Rate = 1;

    // A folder with theme.ini is a library theme; otherwise it is an app folder
    // and settings.ini supplies the text, theme name and speed.
    internal static SplashContent Load(string folder, string appName, Size artwork)
    {
        string themeFile = Path.Combine(folder, "theme.ini");
        bool isTheme = File.Exists(themeFile);
        IniFile ini = IniFile.Load(isTheme ? themeFile : Path.Combine(folder, AppProfile.FileName));
        SplashText text = SplashText.Read(ini);
        var content = new SplashContent();
        string upper = appName.ToUpperInvariant();
        bool original = !isTheme && (ini.Get("theme") ?? "").Trim().Length == 0 &&
            text.Title.Length == 0 && text.Subtitle.Length == 0 && text.Logs.Length == 0;
        content.Title = original ? UpstreamTitle : text.Title.Length > 0 ? text.Title : upper;
        content.Subtitle = original ? UpstreamSubtitle : text.Subtitle;
        content.Accent = text.Accent.Length > 0 ? text.Accent : DefaultAccent;
        content.Theme = isTheme ? Path.GetFileName(folder.TrimEnd('\\', '/')) : (ini.Get("theme") ?? "").Trim();
        content.Rate = isTheme ? 1 : Look.Number(ini, "speed", 1, 1, 3);
        // A library theme carries its own choice ("" = the app's default); an app
        // folder already holds the resolved style under the same key.
        string style = SplashText.NormalizeStyle(ini.Get("style"));
        content.Style = style.Length > 0 ? style : "full";
        content.Caption = upper + (original ? " INITIALIZE / TYPE-0" : " INITIALIZE");
        content.Label = original ? UpstreamLabel : content.Theme.Length > 0 ? content.Theme : appName;
        content.Font = text.Font;
        content.Frame = text.Frame;
        string[] lines = original ? UpstreamLogs : PickLines(text.Logs, text.Lines);
        if (lines.Length == 0)
            lines = new[] {
                appName + " launcher ready",
                content.Theme.Length > 0 ? "theme: " + content.Theme : "custom artwork",
                "artwork " + artwork.Width.ToString(CultureInfo.InvariantCulture) + " \u00D7 " +
                    artwork.Height.ToString(CultureInfo.InvariantCulture),
                "opening " + appName
            };
        int starts = isTheme ? 0 : SplashMemory.Plays(folder);
        for (int i = 0; i < 4; i++)
            content.Logs[i] = i < lines.Length ? "> " + ExpandVars(lines[i], appName, content.Theme, starts) : "";
        return content;
    }

    // Fixed boot lines keep slots 1-3; one random pick from the "lines" pool is
    // slot 4 when the pool exists (a fourth fixed line gives way to it).
    private static string[] PickLines(string[] fixedLines, string[] pool)
    {
        if (pool == null || pool.Length == 0) return fixedLines ?? new string[0];
        int keep = Math.Min(fixedLines == null ? 0 : fixedLines.Length, 3);
        var lines = new List<string>();
        for (int i = 0; i < keep; i++) lines.Add(fixedLines[i]);
        lines.Add(pool[new Random().Next(pool.Length)]);
        return lines.ToArray();
    }

    // {app} {theme} {user} {date} {time} {starts} inside a boot line. Unknown
    // tokens pass through; the expanded line is limited like a hand-typed one.
    internal static string ExpandVars(string line, string appName, string theme, int starts)
    {
        string expanded = Regex.Replace(line ?? "", @"\{(app|theme|user|date|time|starts)\}",
            delegate(Match match) {
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "app": return appName;
                    case "theme": return theme.Length > 0 ? theme : appName;
                    case "user": return Environment.UserName;
                    case "date": return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    case "time": return DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
                    default: return starts.ToString(CultureInfo.InvariantCulture);
                }
            }, RegexOptions.IgnoreCase);
        return expanded.Length > 80 ? expanded.Substring(0, 80) : expanded;
    }
}

internal static class SplashPage
{
    internal static byte[] RenderIndex(byte[] template, SplashContent content, string appName)
    {
        string accent = content.Accent;
        Color color;
        if (!SplashText.TryParseColor(accent, out color)) accent = SplashContent.DefaultAccent;
        var values = new System.Collections.Generic.Dictionary<string, string>
        {
            { "APP", appName.ToUpperInvariant() }, { "TITLE", content.Title }, { "SUBTITLE", content.Subtitle },
            { "THEME", content.Theme }, { "CAPTION", content.Caption }, { "LABEL", content.Label }, { "ACCENT", accent }, { "RATE", Math.Max(1, Math.Min(3, content.Rate)).ToString(CultureInfo.InvariantCulture) }, { "STYLE", SplashText.NormalizeStyle(content.Style).Length > 0 ? content.Style : "full" },
            { "LOG1", content.Logs[0] }, { "LOG2", content.Logs[1] }, { "LOG3", content.Logs[2] }, { "LOG4", content.Logs[3] },
            { "FONT", content.Font }, { "FRAME", content.Frame }
        };
        // One pass, so text that looks like a token is never substituted again.
        string html = Regex.Replace(Encoding.UTF8.GetString(template), @"\{\{([A-Z0-9]+)\}\}", delegate(Match match) {
            string value;
            return values.TryGetValue(match.Groups[1].Value, out value) ? WebUtility.HtmlEncode(value ?? "") : match.Value;
        });
        return Encoding.UTF8.GetBytes(html);
    }
}

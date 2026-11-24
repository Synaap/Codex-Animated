using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

internal static class LookTests
{
    private static int failures, total;

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--apply")
        {
            ThemeLibrary.Apply(args[1], args[2]);
            Console.WriteLine("applied \"" + args[1] + "\" to " + args[2]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--sheets") return Sheets();
        if (args.Length == 4 && args[0] == "--log-append")
        {
            AppLog.Start(args[1], args[2]);
            int count = int.Parse(args[3]);
            for (int i = 0; i < count; i++) AppLog.Write("multi", args[2] + " " + i);
            AppLog.Flush();
            return 0;
        }
        if (args.Length == 4 && args[0] == "--render-splash") return RenderSplash(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--codepoints")
        {
            string value = IniFile.Load(args[1]).Get(args[2]) ?? "";
            var points = new List<string>();
            foreach (char c in value) points.Add("U+" + ((int)c).ToString("X4"));
            Console.WriteLine(args[2] + " = " + value + "  " + string.Join(" ", points.ToArray()));
            return 0;
        }

        IniRoundTrip();
        LookClamps();
        SplashTextCases();
        ProfileRoundTrip();
        RenderIdentity();
        TintCase();
        DimCase();
        BlurResolutionIndependent();
        BlurKeepsBorder();
        BlurCurve();
        CoverSourceCases();
        AccentCases();
        ApplyCase();
        SplashPageCase();
        LibraryOperations();
        NameValidation();
        LogCases();
        QuietCases();
        CrashPolicyCases();
        PackCases();
        AppTableCases();
        CustomAppCases();
        AlphaMaskCases();
        SharperLookCases();
        AnimationStyleCases();
        GradientCases();
        TextZoneCases();
        VarietyCases();
        NightCases();
        SlotReferenceCases();
        DetailCases();
        StarterCases();
        GifCases();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL " + total + " CHECKS PASSED" : failures + " of " + total + " CHECKS FAILED");
        try { if (Directory.Exists(RunRoot)) Directory.Delete(RunRoot, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail)
    {
        total++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
    }

    // One scratch root per run, removed when the run ends.
    private static readonly string RunRoot = Path.Combine(Path.GetTempPath(), "LookTests-" + Guid.NewGuid().ToString("N"));
    private static int tempCount;

    private static string Temp(string name)
    {
        string folder = Path.Combine(RunRoot, name + "-" + (++tempCount).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void IniRoundTrip()
    {
        string folder = Temp("ini");
        string path = Path.Combine(folder, "a.ini");
        var ini = new IniFile();
        ini.Set("Unknown", "kept");
        ini.Set("title", "\u5915\u713C\u3051\u306E\u6E2F");
        ini.Set("note", "one\r\ntwo");
        ini.Save(path);
        byte[] bytes = File.ReadAllBytes(path);
        Check("ini: no BOM", !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), null);
        string text = new UTF8Encoding(false).GetString(bytes);
        Check("ini: CRLF lines, line break in value stays on one line",
            text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length == 3, null);
        IniFile back = IniFile.Load(path);
        Check("ini: unknown key kept and key lookup case-insensitive", back.Get("UNKNOWN") == "kept", null);
        Check("ini: Japanese value survives", back.Get("title") == "\u5915\u713C\u3051\u306E\u6E2F", null);
        Check("ini: line break stripped", back.Get("note") == "onetwo", back.Get("note"));
        Check("ini: missing key is null", back.Get("nope") == null, null);
        File.WriteAllText(path, "# c\r\n; c\r\n\r\n  key = a=b \r\nkey=last\r\nbad line\r\n", new UTF8Encoding(false));
        back = IniFile.Load(path);
        Check("ini: comments skipped, split at first =, last duplicate wins", back.Get("key") == "last", back.Get("key"));
        File.WriteAllText(path, "k = a=b \r\n", new UTF8Encoding(false));
        Check("ini: value keeps later =", IniFile.Load(path).Get("k") == "a=b", null);
        Check("ini: missing file reads empty", IniFile.Load(Path.Combine(folder, "none.ini")).Get("x") == null, null);
    }

    private static void LookClamps()
    {
        var ini = new IniFile();
        ini.Set("opacity", "300");
        ini.Set("blur", "-5");
        ini.Set("dim", "abc");
        ini.Set("tintColor", "red");
        Look look = Look.Read(ini);
        Check("look: opacity=300 clamps to 40", look.Opacity == 40, look.Opacity.ToString());
        Check("look: blur=-5 clamps to 0", look.Blur == 0, look.Blur.ToString());
        Check("look: dim=abc falls back to 0", look.Dim == 0, look.Dim.ToString());
        Check("look: tintColor=red falls back to #8C66D1", look.TintColor.ToArgb() == Color.FromArgb(0x8C, 0x66, 0xD1).ToArgb(), null);
        Check("look: defaults of an empty file", Look.Read(new IniFile()).Opacity == 15 && Look.Read(new IniFile()).FocusX == 50, null);
        Check("look: alpha for opacity 15 is 38", new Look().Alpha == 38, new Look().Alpha.ToString());
        var round = new Look { Opacity = 22, Blur = 7, Dim = 9, Tint = 61, FocusX = 3, FocusY = 97, TintColor = Color.FromArgb(1, 2, 3) };
        var file = new IniFile();
        round.Write(file);
        Look again = Look.Read(file);
        Check("look: write/read round trip", again.Opacity == 22 && again.Blur == 7 && again.Dim == 9 && again.Tint == 61 &&
            again.FocusX == 3 && again.FocusY == 97 && again.TintColor.ToArgb() == round.TintColor.ToArgb() && again.SameRender(round), null);
        var zone = new Look { Clear = 60, ClearSide = "left", Glow = 25, Drift = 33 };
        var zoneFile = new IniFile();
        zone.Write(zoneFile);
        Look zoneBack = Look.Read(zoneFile);
        Check("look: clear/glow/drift round trip", zoneBack.Clear == 60 && zoneBack.ClearSide == "left" && zoneBack.Glow == 25 &&
            zoneBack.Drift == 33 && zoneBack.SamePresent(zone), null);
        var badSide = new IniFile();
        badSide.Set("clearSide", "sideways");
        Check("look: unknown clearSide falls back to none", Look.Read(badSide).ClearSide == "", null);
        Check("look: defaults need no alpha pass", !ArtworkLook.NeedsAlpha(new Look()), null);
        Check("look: SamePresent ignores render fields", new Look { Tint = 9 }.SamePresent(new Look { Tint = 77 }), null);
    }

    private static void AlphaMaskCases()
    {
        var left = new Look { Clear = 100, ClearSide = "left" };
        byte[] columns = ArtworkLook.ClearColumns(1000, left);
        Check("mask: clear=100 left starts fully clear", columns != null && columns[0] == 0, columns != null ? columns[0].ToString() : "null");
        Check("mask: left ramp reaches full by 60%", columns != null && columns[600] == 255 && columns[999] == 255,
            columns != null ? columns[600] + "," + columns[999] : "null");
        Check("mask: left ramp is monotonic", columns != null && columns[300] >= columns[0] && columns[599] >= columns[300], null);
        var right = new Look { Clear = 100, ClearSide = "right" };
        byte[] mirror = ArtworkLook.ClearColumns(1000, right);
        Check("mask: right mirrors left", mirror != null && mirror[999] == 0 && mirror[0] == 255, null);
        var center = new Look { Clear = 100, ClearSide = "center" };
        byte[] band = ArtworkLook.ClearColumns(1000, center);
        Check("mask: center floor in the middle, full at edges",
            band != null && band[500] == 0 && band[0] == 255 && band[999] == 255, null);
        var keep = new Look { Clear = 50, ClearSide = "left" };
        byte[] partial = ArtworkLook.ClearColumns(1000, keep);
        Check("mask: clear=50 keeps half the opacity in the zone", partial != null && partial[0] > 120 && partial[0] < 135,
            partial != null ? partial[0].ToString() : "null");
        Check("mask: no side or no clear means no mask",
            ArtworkLook.ClearColumns(1000, new Look { Clear = 80 }) == null &&
            ArtworkLook.ClearColumns(1000, new Look { ClearSide = "left" }) == null, null);

        using (var frame = new Bitmap(4, 2, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 4; x++)
                    frame.SetPixel(x, y, Color.FromArgb(255, 100, 150, 200));
            byte[] plain = ArtworkLook.AlphaPixels(frame, new Look());
            Check("mask: no clear and no glow leaves pixels alone", plain[3] == 255 && plain[2] == 100, null);
            byte[] masked = ArtworkLook.AlphaPixels(frame, new Look { Clear = 100, ClearSide = "left" });
            // columns: x=0 n=0 -> 0; x=3 n=1 -> 255 (0.60 cutoff at 4px width)
            Check("mask: zone pixel premultiplied to near-black transparent",
                masked[3] == 0 && masked[0] == 0 && masked[1] == 0 && masked[2] == 0, masked[3].ToString());
            Check("mask: far pixel keeps its colour, opaque", masked[15] == 255 && masked[14] == 100 &&
                masked[13] == 150 && masked[12] == 200, masked[15].ToString());
        }
        using (var dark = new Bitmap(2, 1, PixelFormat.Format32bppArgb))
        {
            dark.SetPixel(0, 0, Color.FromArgb(255, 10, 10, 10));
            dark.SetPixel(1, 0, Color.FromArgb(255, 250, 250, 250));
            byte[] glow = ArtworkLook.AlphaPixels(dark, new Look { Glow = 100 });
            Check("mask: glow=100 hides dark pixels, keeps bright ones", glow[3] < 12 && glow[7] > 240, glow[3] + "," + glow[7]);
            Check("mask: glow premultiplies rgb", glow[2] <= glow[3] && glow[6] == (250 * 250 + 127) / 255, glow[2] + "," + glow[6]);
        }
        Check("mask: NeedsAlpha on clear or glow", ArtworkLook.NeedsAlpha(new Look { Clear = 1 }) &&
            ArtworkLook.NeedsAlpha(new Look { Glow = 1 }) && !ArtworkLook.NeedsAlpha(new Look()), null);
        Check("mask: a fitted frame needs alpha for its bars", ArtworkLook.NeedsAlpha(new Look { Fit = "contain" }) &&
            !ArtworkLook.NeedsAlpha(new Look { Fit = "cover" }), null);
        Check("mask: vignette alone needs the pixel pass but not alpha",
            ArtworkLook.NeedsPixelPass(new Look { Vignette = 30 }) && !ArtworkLook.NeedsAlpha(new Look { Vignette = 30 }), null);
    }

    private static void SharperLookCases()
    {
        using (var img = new Bitmap(3, 1, PixelFormat.Format24bppRgb))
        {
            img.SetPixel(0, 0, Color.FromArgb(255, 0, 0));
            img.SetPixel(1, 0, Color.FromArgb(0, 255, 0));
            img.SetPixel(2, 0, Color.FromArgb(0, 0, 255));
            using (var grey = ArtworkLook.Render(img, new Look { Sat = 0 }))
            {
                Color p = grey.GetPixel(1, 0);
                Check("sat: 0 desaturates to luma", Math.Abs(p.R - p.G) < 8 && Math.Abs(p.G - p.B) < 8, p.ToString());
            }
            Check("sat: 100 returns the source unchanged",
                object.ReferenceEquals(ArtworkLook.Render(img, new Look { Sat = 100 }), img), null);
            using (var vivid = ArtworkLook.Render(img, new Look { Sat = 150 }))
            {
                Color p = vivid.GetPixel(0, 0);
                Check("sat: 150 pushes the red channel out", p.R >= 250 && p.G < 60, p.ToString());
            }
            using (var faded = ArtworkLook.Render(img, new Look { Fade = 100 }))
            {
                Color p = faded.GetPixel(0, 0);
                Check("fade: 100 reaches the light shade", Math.Abs(p.R - 245) < 8 && Math.Abs(p.G - 245) < 8 &&
                    Math.Abs(p.B - 245) < 8, p.ToString());
            }
            using (var dimmed = ArtworkLook.Render(img, new Look { Dim = 50 }))
            {
                Color p = dimmed.GetPixel(0, 0);
                Check("dim: 50 keeps the dark shade direction", p.R > p.B || p.B > p.R, p.ToString());
            }
        }
        var image = new Size(1000, 500);
        RectangleF src; Rectangle dst; InterpolationMode mode;
        ArtworkLook.FramePlan(image, new Size(800, 800), new Look(), out src, out dst, out mode);
        Check("fit: cover fills the frame, crops the source",
            dst == new Rectangle(0, 0, 800, 800) && src.Width < image.Width, dst + ";" + src);
        ArtworkLook.FramePlan(image, new Size(800, 800), new Look { Fit = "contain" }, out src, out dst, out mode);
        Check("fit: contain letterboxes the whole image",
            dst == new Rectangle(0, 200, 800, 400) && src.Width == 1000, dst + ";" + src);
        ArtworkLook.FramePlan(image, new Size(800, 800), new Look { Fit = "center" }, out src, out dst, out mode);
        Check("fit: center is 1:1 in the middle", dst == new Rectangle(0, 150, 800, 500) &&
            src == new RectangleF(100, 0, 800, 500), dst + ";" + src);
        ArtworkLook.FramePlan(image, new Size(2600, 2600), new Look { Fit = "pixel" }, out src, out dst, out mode);
        Check("fit: pixel art scales by integers with nearest neighbour",
            dst == new Rectangle(300, 800, 2000, 1000) && mode == InterpolationMode.NearestNeighbor, dst + ";" + mode);
        using (var frame = new Bitmap(101, 101, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < 101; y++) for (int x = 0; x < 101; x++) frame.SetPixel(x, y, Color.FromArgb(255, 200, 200, 200));
            byte[] vig = ArtworkLook.AlphaPixels(frame, new Look { Vignette = 100 });
            int ci = (50 * 101 + 50) * 4, ei = (50 * 101 + 100) * 4;
            Check("vignette: centre keeps full colour", vig[ci + 2] == 200, vig[ci + 2].ToString());
            Check("vignette: edge darkened, alpha kept", vig[ei + 2] < 200 && vig[ei + 3] == 255, vig[ei + 2] + "," + vig[ei + 3]);
        }
        string root = Temp("looks");
        string keep = Environment.GetEnvironmentVariable("ANIMATED_THEMES");
        Environment.SetEnvironmentVariable("ANIMATED_THEMES", Path.Combine(root, "Themes"));
        try
        {
            LookPresets.Save("Test Look", new Look { Clear = 55, ClearSide = "right", Glow = 12, Sat = 140, Fit = "contain" });
            Check("presets: saved and listed", LookPresets.Names().Length == 1 && LookPresets.Names()[0] == "Test Look",
                string.Join(",", LookPresets.Names()));
            Look back = LookPresets.Load("Test Look");
            Check("presets: look round-trips", back.Clear == 55 && back.ClearSide == "right" && back.Glow == 12 &&
                back.Sat == 140 && back.Fit == "contain", back.Clear + " " + back.Fit);
            LookPresets.Delete("Test Look");
            Check("presets: deleted", LookPresets.Names().Length == 0, null);
        }
        finally { Environment.SetEnvironmentVariable("ANIMATED_THEMES", keep); }
    }

    private static void AnimationStyleCases()
    {
        Check("style: normalize", SplashText.NormalizeStyle(" Short ") == "short" && SplashText.NormalizeStyle("full") == "full" &&
            SplashText.NormalizeStyle("bogus") == "" && SplashText.NormalizeStyle(null) == "", null);
        Check("style: skins normalize",
            SplashText.NormalizeStyle(" Terminal ") == "terminal" && SplashText.NormalizeStyle("MANGA") == "manga" &&
            SplashText.NormalizeStyle("minimal") == "minimal" && SplashText.NormalizeStyle("retro") == "", null);
        var textIni = new IniFile();
        new SplashText { Style = "bogus" }.Write(textIni);
        Check("style: write normalizes", textIni.Get("style") == "", textIni.Get("style"));
        new SplashText { Style = "short" }.Write(textIni);
        Check("style: short written", textIni.Get("style") == "short", null);

        string root = Temp("anim-profile");
        AppProfile profile = AppProfile.Load(root);
        Check("style: app defaults", profile.Style == "full" && profile.Speed == 1 && !profile.SplashOnce, null);
        profile.Style = "short"; profile.Speed = 3; profile.SplashOnce = true; profile.Save();
        AppProfile back = AppProfile.Load(root);
        Check("style: app round-trips", back.Style == "short" && back.Speed == 3 && back.SplashOnce &&
            IniFile.Load(Path.Combine(root, "settings.ini")).Get("style") == "short" &&
            IniFile.Load(Path.Combine(root, "settings.ini")).Get("once") == "on", null);
        File.WriteAllText(Path.Combine(root, "settings.ini"), "style=wide\nspeed=9\nonce=yes\n");
        back = AppProfile.Load(root);
        Check("style: invalid values fall back", back.Style == "full" && back.Speed == 3 && !back.SplashOnce,
            back.Style + " " + back.Speed + " " + back.SplashOnce);

        string work = Temp("anim-apply");
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            string theme = Path.Combine(ThemeLibrary.RootOverride, "Carded");
            Directory.CreateDirectory(theme);
            WriteJpeg(Path.Combine(theme, "wallpaper.jpg"), Color.Teal);
            var themeIni = new IniFile();
            themeIni.Set("style", "short");
            themeIni.Save(Path.Combine(theme, "theme.ini"));
            string app = Path.Combine(work, "App");
            Directory.CreateDirectory(app);
            ThemeLibrary.Apply("Carded", app);
            Check("style: apply takes the theme's style", AppProfile.Load(app).Style == "short", null);
            File.WriteAllText(Path.Combine(theme, "theme.ini"), "title=X\n");
            ThemeLibrary.Apply("Carded", app);
            Check("style: a theme without a style keeps the app's", AppProfile.Load(app).Style == "short", null);
            AppProfile p = AppProfile.Load(app); p.Style = "full"; p.Save();
            var saved = new IniFile();
            new SplashText { Style = "short", Title = "T" }.Write(saved);
            SplashText stored;
            ThemeLibrary.Save("Carded", new Look(), SplashText.Read(saved), new[] { app }, out stored);
            Check("style: saving a styled theme reaches the app", AppProfile.Load(app).Style == "short", null);
        }
        finally { ThemeLibrary.RootOverride = null; }

        string splashFolder = Temp("anim-splash");
        var ini = new IniFile();
        ini.Set("style", "short");
        ini.Set("speed", "3");
        ini.Save(Path.Combine(splashFolder, "settings.ini"));
        SplashContent content = SplashContent.Load(splashFolder, "Devin", new Size(800, 600));
        Check("style: splash content reads style and speed", content.Style == "short" && content.Rate == 3,
            content.Style + " " + content.Rate);
        string html = Encoding.UTF8.GetString(SplashPage.RenderIndex(
            Encoding.UTF8.GetBytes("<main data-rate=\"{{RATE}}\" data-style=\"{{STYLE}}\"></main>"), content, "Devin"));
        Check("style: tokens rendered", html.Contains("data-rate=\"3\"") && html.Contains("data-style=\"short\""), html);
        var skinIni = new IniFile();
        skinIni.Set("style", "terminal");
        skinIni.Save(Path.Combine(splashFolder, "settings.ini"));
        content = SplashContent.Load(splashFolder, "Devin", new Size(800, 600));
        html = Encoding.UTF8.GetString(SplashPage.RenderIndex(
            Encoding.UTF8.GetBytes("<main data-style=\"{{STYLE}}\"></main>"), content, "Devin"));
        Check("style: skin token passes through", html.Contains("data-style=\"terminal\""), html);

        string stampDir = Temp("anim-stamp");
        Check("memory: first sign-in is unmarked", !SplashMemory.PlayedOrMark(stampDir), null);
        Check("memory: same sign-in is marked", SplashMemory.PlayedOrMark(stampDir), null);
        File.WriteAllText(Path.Combine(stampDir, SplashMemory.StampName), "some older session");
        Check("memory: a different session marker plays again", !SplashMemory.PlayedOrMark(stampDir), null);
    }

    private static void GradientCases()
    {
        // ---- spec parse/format ----
        GradientSpec spec;
        Check("gradient: well-formed spec parses", GradientSpec.TryParse("#FF0000| #0000ff | 90", out spec) &&
            spec.From.R == 255 && spec.To.B == 255 && spec.Degrees == 90, spec.Format());
        Check("gradient: degrees wrap into 0..359", GradientSpec.TryParse("#111111|#222222|450", out spec) &&
            spec.Degrees == 90 && GradientSpec.TryParse("#111111|#222222|-90", out spec) && spec.Degrees == 270,
            spec.Degrees.ToString());
        Check("gradient: two parts rejected", !GradientSpec.TryParse("#111111|#222222", out spec), null);
        Check("gradient: bad colour rejected", !GradientSpec.TryParse("red|#222222|90", out spec) &&
            !GradientSpec.TryParse("#111111|#222|90", out spec), null);
        Check("gradient: bad angle rejected", !GradientSpec.TryParse("#111111|#222222|tilt", out spec), null);
        Check("gradient: empty rejected", !GradientSpec.TryParse("", out spec) && !GradientSpec.TryParse(null, out spec), null);
        GradientSpec.TryParse("#102030|#A0B0C0|123", out spec);
        GradientSpec again;
        Check("gradient: format/parse round trip", GradientSpec.TryParse(spec.Format(), out again) &&
            again.Format() == spec.Format(), spec.Format());

        // ---- rendered bitmap ----
        GradientSpec.TryParse("#FF0000|#0000FF|0", out spec);
        byte[] jpeg = spec.RenderJpeg();
        using (var rendered = ImageCodec.DecodeJpeg(jpeg))
        {
            Check("gradient: renders a 1600x900 bitmap", rendered.Width == 1600 && rendered.Height == 900,
                rendered.Width + "x" + rendered.Height);
            Color left = rendered.GetPixel(20, 450), right = rendered.GetPixel(1580, 450);
            Check("gradient: angle 0 goes red on the left", left.R > 180 && left.B < 90,
                left.R + "," + left.G + "," + left.B);
            Check("gradient: angle 0 goes blue on the right", right.B > 180 && right.R < 90,
                right.R + "," + right.G + "," + right.B);
        }
        GradientSpec.TryParse("#FF0000|#0000FF|90", out spec);
        using (var rendered = ImageCodec.DecodeJpeg(spec.RenderJpeg()))
        {
            Color top = rendered.GetPixel(800, 20), bottom = rendered.GetPixel(800, 880);
            Check("gradient: angle 90 goes red on top", top.R > 180 && top.B < 90, top.R + "," + top.B);
            Check("gradient: angle 90 goes blue on the bottom", bottom.B > 180 && bottom.R < 90, bottom.R + "," + bottom.B);
        }
        GradientSpec.TryParse("#FF0000|#0000FF|180", out spec);
        using (var rendered = ImageCodec.DecodeJpeg(spec.RenderJpeg()))
        {
            Color left = rendered.GetPixel(40, 450), right = rendered.GetPixel(1560, 450), edge = rendered.GetPixel(0, 450);
            Check("gradient: angle 180 runs right to left", right.R > 180 && left.B > 180, right.R + "/" + left.B);
            Check("gradient: angle 180 leaves no start-colour line on the far edge", edge.B > 180 && edge.R < 90,
                edge.R + "," + edge.G + "," + edge.B);
        }
        GradientSpec.TryParse("#FF0000|#0000FF|270", out spec);
        using (var rendered = ImageCodec.DecodeJpeg(spec.RenderJpeg()))
        {
            Color edge = rendered.GetPixel(800, 0);
            Check("gradient: angle 270 leaves no start-colour line on the top edge", edge.B > 180 && edge.R < 90,
                edge.R + "," + edge.G + "," + edge.B);
        }

        // ---- theme creation from jpeg bytes + metadata ----
        string work = Temp("grad-themes");
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            GradientSpec.TryParse("#3C9C8F|#8C66D1|135", out spec);
            string name = ThemeLibrary.CreateFromJpeg(spec.RenderJpeg(), "Gradient");
            string folder = ThemeLibrary.Folder(name);
            Check("byte theme: created with wallpaper + ini", name == "Gradient" &&
                File.Exists(Path.Combine(folder, "wallpaper.jpg")) &&
                File.Exists(Path.Combine(folder, "theme.ini")), name);
            Check("byte theme: same base name dedupes",
                ThemeLibrary.CreateFromJpeg(spec.RenderJpeg(), "Gradient") == "Gradient 2", null);
            ThemeLibrary.SetGradient(name, spec);
            Check("byte theme: gradient metadata round trip", ThemeLibrary.GradientOf(name) == spec.Format(),
                ThemeLibrary.GradientOf(name));
            // Saving look/text through the normal writer keeps the gradient key.
            var file = IniFile.Load(Path.Combine(folder, "theme.ini"));
            file.Set("title", "STILL A GRADIENT");
            file.Save(Path.Combine(folder, "theme.ini"));
            Check("byte theme: ini save preserves gradient key", ThemeLibrary.GradientOf(name) == spec.Format(), null);
            using (var wallpaper = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"))))
            {
                Color[] accents = ArtworkLook.SuggestAccents(wallpaper, 3);
                Check("byte theme: gradient accents suggested", accents.Length > 0, accents.Length.ToString());
            }
            Check("byte theme: broken theme rejected", ThemeLibrary.GradientOf("does not exist") == "", null);
        }
        finally { ThemeLibrary.RootOverride = null; }

        // ---- jpeg encode/decode ----
        using (var source = new Bitmap(64, 64))
        {
            using (var g = Graphics.FromImage(source)) g.Clear(Color.FromArgb(10, 200, 30));
            byte[] encoded = ImageCodec.EncodeJpeg(source);
            using (var decoded = ImageCodec.DecodeJpeg(encoded))
                Check("jpeg: encode/decode keeps size and colour", decoded.Width == 64 &&
                    Math.Abs(decoded.GetPixel(32, 32).G - 200) < 30, null);
        }
    }

    private static void TextZoneCases()
    {
        // The lower-left band is measured; top-right does not count.
        using (var img = new Bitmap(10, 10, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(img)) g.Clear(Color.Black);
            Check("text zone: all-dark image reads 0", ArtworkLook.TextZoneLuma(img) == 0,
                ArtworkLook.TextZoneLuma(img).ToString());
            for (int y = 0; y < 4; y++) for (int x = 8; x < 10; x++) img.SetPixel(x, y, Color.White);
            Check("text zone: bright top-right ignored", ArtworkLook.TextZoneLuma(img) == 0,
                ArtworkLook.TextZoneLuma(img).ToString());
            for (int y = 6; y < 10; y++) for (int x = 0; x < 5; x++) img.SetPixel(x, y, Color.White);
            int luma = ArtworkLook.TextZoneLuma(img);
            Check("text zone: bright lower-left counted", luma > 120, luma.ToString());
        }
        using (var img = new Bitmap(1, 1, PixelFormat.Format24bppRgb))
        {
            img.SetPixel(0, 0, Color.White);
            Check("text zone: 1x1 white reads ~255", ArtworkLook.TextZoneLuma(img) > 250,
                ArtworkLook.TextZoneLuma(img).ToString());
        }
    }

    private static void VarietyCases()
    {
        // rotate= parsing: off|start|whole minutes 5..1440.
        Check("rotate: parsing", AppProfile.NormalizeRotate("off") == "off" &&
            AppProfile.NormalizeRotate(" start ") == "start" && AppProfile.NormalizeRotate("60") == "60" &&
            AppProfile.NormalizeRotate("4") == "off" && AppProfile.NormalizeRotate("1441") == "off" &&
            AppProfile.NormalizeRotate("bogus") == "off" && AppProfile.NormalizeRotate(null) == "off", null);

        // Round-trip of the new app keys through settings.ini.
        string prof = Temp("variety-profile");
        var seed = new IniFile();
        seed.Set("rotate", "start"); seed.Set("rotateFavs", "on"); seed.Set("followWindows", "on");
        seed.Set("themeLight", "Sunny"); seed.Set("applied", "Sunny");
        seed.Save(Path.Combine(prof, "settings.ini"));
        AppProfile back = AppProfile.Load(prof);
        Check("crosshair: style ids and img: packs normalize, junk falls to off",
            AppProfile.NormalizeCrosshair(" star ") == "star" &&
            AppProfile.NormalizeCrosshair("img:kitty/pointer") == "img:kitty/pointer" &&
            AppProfile.NormalizeCrosshair("img:kitty/sub/x") == "off" &&
            AppProfile.NormalizeCrosshair("IMG:Kitty/pointer") == "img:kitty/pointer" &&
            AppProfile.NormalizeCrosshair("img:../etc") == "off" &&
            AppProfile.NormalizeCrosshair("nope") == "off", null);
        // A profile loaded without the custom-app flag must not switch a custom app's splash on.
        string custDir = Temp("custom-splash");
        var custIni = new IniFile(); custIni.Set("theme", "X");
        custIni.Save(Path.Combine(custDir, "settings.ini"));
        AppProfile custLoad = AppProfile.Load(custDir);
        custLoad.Theme = "Y";
        custLoad.Save();
        Check("profile: save without a splash key keeps a custom app's default off",
            !AppProfile.Load(custDir, true).SplashOnStart && IniFile.Load(Path.Combine(custDir, "settings.ini")).Get("splash") == null, null);
        custLoad.SplashOnStart = false;
        custLoad.Save();
        Check("profile: a changed splash is written", IniFile.Load(Path.Combine(custDir, "settings.ini")).Get("splash") == "off", null);
        // Hostile APNG chunk lengths (int overflow) are rejected, not thrown.
        byte[] evil = new byte[40];
        Array.Copy(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0x7F, 0xFF, 0xFF, 0xFF, (byte)'a', (byte)'c', (byte)'T', (byte)'L' }, evil, 16);
        bool evilOk;
        try { evilOk = !ImageCodec.IsApng(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0x7F, 0xFF, 0xFF, 0xFF, (byte)'x', (byte)'x', (byte)'x', (byte)'x', 0, 0, 0, 0 }); }
        catch (Exception) { evilOk = false; }
        Check("apng: an overflowing chunk length is not an APNG", evilOk, null);
        Check("cursor: reserved device names are renamed", ThemeLibrary.CursorClean("CON") == "con-x" && ThemeLibrary.CursorClean("") == "pointer", ThemeLibrary.CursorClean("CON"));
        Check("profile: variety keys round-trip", back.Rotate == "start" && back.RotateFavs &&
            back.FollowWindows && back.ThemeLight == "Sunny" && back.Applied == "Sunny",
            back.Rotate + " " + back.Applied);
        back.Save();
        Check("profile: save keeps variety keys", AppProfile.Load(prof).ThemeLight == "Sunny" &&
            AppProfile.Load(prof).Applied == "Sunny" && AppProfile.Load(prof).RotateFavs, null);

        // ApplyForMode slot semantics against a scratch library.
        string work = Temp("variety-apply");
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            foreach (string name in new[] { "Dark One", "Light Two" })
            {
                string folder = Path.Combine(ThemeLibrary.RootOverride, name);
                Directory.CreateDirectory(folder);
                WriteJpeg(Path.Combine(folder, "wallpaper.jpg"), name == "Dark One" ? Color.Black : Color.White);
            }
            string app = Path.Combine(work, "App");
            Directory.CreateDirectory(app);

            // Base slot: the pick always lands in theme=.
            ThemeLibrary.ApplyForMode("Dark One", app, "theme");
            AppProfile p = AppProfile.Load(app);
            Check("apply: follow-off puts the pick in theme=", p.Theme == "Dark One" &&
                p.ThemeLight == "" && p.Applied == "Dark One", p.Theme + "/" + p.Applied);

            // Light slot: the pick lands in themeLight=, theme= keeps the dark pick.
            p.FollowWindows = true; p.Save();
            ThemeLibrary.ApplyForMode("Light Two", app, "themeLight");
            p = AppProfile.Load(app);
            Check("apply: light mode fills themeLight=, keeps theme=", p.ThemeLight == "Light Two" &&
                p.Theme == "Dark One" && p.Applied == "Light Two", p.Theme + "/" + p.ThemeLight);

            // Back to the base slot: the pick lands in theme= again.
            ThemeLibrary.ApplyForMode("Dark One", app, "theme");
            p = AppProfile.Load(app);
            Check("apply: dark mode fills theme=, keeps themeLight=", p.Theme == "Dark One" &&
                p.ThemeLight == "Light Two" && p.Applied == "Dark One", p.Theme + "/" + p.Applied);

            // Picking the same name as the dark pick in the light slot keeps theme= sane.
            ThemeLibrary.ApplyForMode("Dark One", app, "themeLight");
            p = AppProfile.Load(app);
            Check("apply: light pick equal to dark pick stays consistent", p.Theme == "Dark One" &&
                p.ThemeLight == "Dark One" && p.Applied == "Dark One", p.ThemeLight);

            // Night slot: the pick lands in themeNight=, theme= keeps the dark pick.
            ThemeLibrary.ApplyForMode("Light Two", app, "themeNight");
            p = AppProfile.Load(app);
            Check("apply: night slot fills themeNight=, keeps theme=", p.ThemeNight == "Light Two" &&
                p.Theme == "Dark One" && p.Applied == "Light Two", p.Theme + "/" + p.ThemeNight);
        }
        finally { ThemeLibrary.RootOverride = null; }

        // Pool + next-in-pool under a scratch data root (favorites live in studio.ini).
        string keepData = Environment.GetEnvironmentVariable("ANIMATED_DATA");
        string data = Temp("variety-data");
        Environment.SetEnvironmentVariable("ANIMATED_DATA", data);
        try
        {
            string themes = Path.Combine(data, "Animated Launchers", "Themes");
            foreach (string name in new[] { "Alpha", "Beta", "Gamma" })
            {
                Directory.CreateDirectory(Path.Combine(themes, name));
                WriteJpeg(Path.Combine(themes, name, "wallpaper.jpg"), Color.Gray);
            }
            Check("pool: all themes without the favs flag",
                string.Join(",", ThemeLibrary.RotationPool(false)) == "Alpha,Beta,Gamma",
                string.Join(",", ThemeLibrary.RotationPool(false)));
            var prefs = new IniFile();
            prefs.Set("favorites", "Gamma|Alpha|Missing");
            prefs.Save(Path.Combine(data, "Animated Launchers", "studio.ini"));
            Check("pool: favorites only, in library order, missing dropped",
                string.Join(",", ThemeLibrary.RotationPool(true)) == "Alpha,Gamma",
                string.Join(",", ThemeLibrary.RotationPool(true)));
            Check("next: wraps and skips the missing",
                ThemeLibrary.NextInPool(new[] { "Alpha", "Beta", "Gamma" }, "Gamma") == "Alpha" &&
                ThemeLibrary.NextInPool(new[] { "Alpha", "Beta" }, "Gamma") == "Alpha" &&
                ThemeLibrary.NextInPool(new string[0], "x") == "", null);

            // The tray's visibility nudge rewrites opacity= clamped to 4..40.
            string appDir = Path.Combine(data, "Nudge App");
            Directory.CreateDirectory(appDir);
            var lookIni = new IniFile();
            lookIni.Set("opacity", "38");
            lookIni.Save(Path.Combine(appDir, "settings.ini"));
            ThemeLibrary.NudgeOpacity(appDir, 5);
            ThemeLibrary.NudgeOpacity(appDir, -100);
            Check("nudge: opacity steps and clamps", IniFile.Load(Path.Combine(appDir, "settings.ini")).Get("opacity") == "4",
                IniFile.Load(Path.Combine(appDir, "settings.ini")).Get("opacity"));
            ThemeLibrary.NudgeOpacity(appDir, -5);
            Check("nudge: clamped at the floor", IniFile.Load(Path.Combine(appDir, "settings.ini")).Get("opacity") == "4", null);

            // Cursor packs resolve only real PNGs inside the pack folders.
            string packDir = Path.Combine(data, "Animated Launchers", "Cursors", "kitty");
            Directory.CreateDirectory(packDir);
            File.WriteAllBytes(Path.Combine(packDir, "pointer.png"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(packDir, "skip.txt"), new byte[] { 1 });
            string resolved = ThemeLibrary.CursorImage("img:kitty/pointer") ?? "";
            Check("crosshair: CursorImage resolves real pack files only",
                resolved.EndsWith("pointer.png") && ThemeLibrary.CursorImage("img:kitty/skip") == null &&
                ThemeLibrary.CursorImage("img:kitty/missing") == null && ThemeLibrary.CursorImage("star") == null &&
                ThemeLibrary.CursorPacks()["kitty"][0] == "pointer", resolved);

            // A generated .cur holds a type-2 dir, the hotspot, then the PNG blob.
            byte[] fakePng;
            using (var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); fakePng = ms.ToArray(); }
            byte[] cur = CursorScheme.PngToCurBytes(fakePng, 6, 7, 32, 32);
            bool curOk = cur.Length == 22 + fakePng.Length &&
                cur[2] == 2 && cur[3] == 0 &&                       // type 2
                cur[10] == 6 && cur[11] == 0 && cur[12] == 7 && cur[13] == 0 &&  // hotspot 6,7
                cur[8] == 0 && cur[9] == 0 &&                        // palette/reserved
                cur[22] == 0x89 && cur[23] == 0x50;                  // PNG signature \x89P
            Check("cursor: .cur packing keeps dir, hotspot and PNG payload", curOk, null);
        }
        finally { Environment.SetEnvironmentVariable("ANIMATED_DATA", keepData); }
    }

    private static void NightCases()
    {
        // The night window wraps midnight; start == until means no window.
        Check("night: window wraps midnight", ThemeLibrary.IsNight(20, 7, 21) &&
            ThemeLibrary.IsNight(20, 7, 6) && !ThemeLibrary.IsNight(20, 7, 12) &&
            !ThemeLibrary.IsNight(20, 7, 7) && ThemeLibrary.IsNight(20, 7, 20), null);
        Check("night: day window and empty window", ThemeLibrary.IsNight(7, 20, 10) &&
            !ThemeLibrary.IsNight(7, 20, 21) && !ThemeLibrary.IsNight(9, 9, 9), null);

        // The new app keys round-trip through settings.ini.
        string prof = Temp("night-profile");
        var seed = new IniFile();
        seed.Set("themeNight", "Midnight"); seed.Set("nightStart", "22"); seed.Set("nightUntil", "5");
        seed.Set("nightDim", "30"); seed.Set("extraDim", "10"); seed.Set("hideFullscreen", "off");
        seed.Save(Path.Combine(prof, "settings.ini"));
        AppProfile back = AppProfile.Load(prof);
        Check("profile: night keys round-trip", back.ThemeNight == "Midnight" && back.NightStart == 22 &&
            back.NightUntil == 5 && back.NightDim == 30 && back.ExtraDim == 10 && !back.HideFullscreen,
            back.ThemeNight + " " + back.NightStart + "-" + back.NightUntil);
        back.Save();
        AppProfile again = AppProfile.Load(prof);
        Check("profile: save keeps night keys", again.ThemeNight == "Midnight" && again.NightDim == 30 &&
            !again.HideFullscreen, null);
        string empty = Temp("night-empty");
        AppProfile fresh = AppProfile.Load(empty);
        Check("profile: night defaults sane", fresh.NightStart == 20 &&
            fresh.NightUntil == 7 && fresh.HideFullscreen, null);
        // Out-of-range hours clamp to 0..23.
        var bad = new IniFile();
        bad.Set("nightStart", "30"); bad.Set("nightUntil", "-4");
        bad.Save(Path.Combine(prof, "settings.ini"));
        AppProfile clamped = AppProfile.Load(prof);
        Check("profile: night hours clamp", clamped.NightStart == 23 && clamped.NightUntil == 0,
            clamped.NightStart + "-" + clamped.NightUntil);
    }

    private static void DetailCases()
    {
        Check("font: whitelist normalize",
            SplashText.NormalizeFont(" Segoe ") == "segoe" && SplashText.NormalizeFont("MONO") == "mono" &&
            SplashText.NormalizeFont("papyrus") == "" && SplashText.NormalizeFont(null) == "" &&
            SplashText.NormalizeFont("serif;font-family:x") == "", null);
        Check("frame: whitelist normalize",
            SplashText.NormalizeFrame("Circle") == "circle" && SplashText.NormalizeFrame("hex") == "hex" &&
            SplashText.NormalizeFrame("oval") == "" && SplashText.NormalizeFrame(null) == "", null);

        var ini = new IniFile();
        ini.Set("font", "mono"); ini.Set("frame", "circle");
        ini.Set("lines", "p1|p2|p3|" + new string('y', 90));
        SplashText text = SplashText.Read(ini);
        Check("text: font/frame/lines read", text.Font == "mono" && text.Frame == "circle" && text.Lines.Length == 4 &&
            text.Lines[3].Length == 80, text.Font + "/" + text.Frame + "/" + text.Lines.Length);
        var written = new IniFile();
        new SplashText { Font = "bogus", Frame = "hex", Lines = new[] { "a|b", " c " } }.Write(written);
        Check("text: write normalizes font and joins pool", written.Get("font") == "" && written.Get("frame") == "hex" &&
            written.Get("lines") == "a b|c", written.Get("font") + "/" + written.Get("frame") + "/" + written.Get("lines"));

        Check("vars: all tokens expand", SplashContent.ExpandVars("{app}/{theme}/{user}/{starts}", "Devin", "HxH", 12) ==
            "Devin/HxH/" + Environment.UserName + "/12", SplashContent.ExpandVars("{app}/{theme}/{user}/{starts}", "Devin", "HxH", 12));
        Check("vars: date and time are real", System.Text.RegularExpressions.Regex.IsMatch(
            SplashContent.ExpandVars("{date} {time}", "Devin", "", 0), @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$"), null);
        Check("vars: theme empty falls back to app, unknown passes through",
            SplashContent.ExpandVars("{theme}{bogus}", "Devin", "", 0) == "Devin{bogus}", null);
        Check("vars: expanded line limited to 80",
            SplashContent.ExpandVars(new string('x', 78) + "{app}", "Devin", "", 0).Length == 80, null);

        string root = Temp("detail-plays");
        Check("plays: zero for a fresh folder", SplashMemory.Plays(root) == 0, null);
        Check("plays: count bumps and persists",
            SplashMemory.CountPlay(root) == 1 && SplashMemory.CountPlay(root) == 2 && SplashMemory.Plays(root) == 2, null);
        File.WriteAllText(Path.Combine(root, "plays.stamp"), "junk");
        Check("plays: unreadable stamp reads 0", SplashMemory.Plays(root) == 0, null);

        // Load a folder as an app profile: pool pick lands in slot 4, fixed keep 1-3.
        var app = new IniFile();
        app.Set("theme", "HxH"); // not original
        app.Set("logs", "one|two|three|four");
        app.Set("lines", "alpha|beta");
        app.Set("font", "impact"); app.Set("frame", "diamond");
        app.Save(Path.Combine(root, "settings.ini"));
        SplashContent content = SplashContent.Load(root, "Devin", new Size(800, 600));
        Check("pool: pick takes slot 4, fixed lines keep 1-3",
            content.Logs[0] == "> one" && content.Logs[1] == "> two" && content.Logs[2] == "> three" &&
            (content.Logs[3] == "> alpha" || content.Logs[3] == "> beta"), string.Join(",", content.Logs));
        Check("content: font/frame flow to render", content.Font == "impact" && content.Frame == "diamond",
            content.Font + "/" + content.Frame);

        File.WriteAllText(Path.Combine(root, "plays.stamp"), "41");
        app.Set("logs", "start number {starts}");
        app.Set("lines", "");
        app.Save(Path.Combine(root, "settings.ini"));
        content = SplashContent.Load(root, "Devin", new Size(800, 600));
        Check("vars: {starts} reads plays.stamp", content.Logs[0] == "> start number 41", content.Logs[0]);

        byte[] template = Encoding.UTF8.GetBytes("<main data-font=\"{{FONT}}\" data-frame=\"{{FRAME}}\">x</main>");
        string html = Encoding.UTF8.GetString(SplashPage.RenderIndex(template, content, "Devin"));
        Check("render: font/frame tokens land in markup", html.Contains("data-font=\"impact\"") && html.Contains("data-frame=\"diamond\""), html);
    }

    private static void SplashTextCases()
    {
        var ini = new IniFile();
        ini.Set("logs", "a|b|c|d|e|f");
        ini.Set("accent", "#b49cff");
        Check("text: more than 4 log lines keeps 4", SplashText.Read(ini).Logs.Length == 4, null);
        Check("text: accent normalized to upper case", SplashText.Read(ini).Accent == "#B49CFF", SplashText.Read(ini).Accent);
        ini.Set("accent", "javascript:1");
        Check("text: invalid accent reads as empty", SplashText.Read(ini).Accent == "", null);
        ini.Set("title", new string('x', 60));
        Check("text: title limited to 40", SplashText.Read(ini).Title.Length == 40, null);
        var written = new IniFile();
        new SplashText { Title = "T", Subtitle = "S", Accent = "#112233", Logs = new[] { "one", "two" } }.Write(written);
        Check("text: logs joined with pipe", written.Get("logs") == "one|two", written.Get("logs"));
    }

    private static void GifCases()
    {
        // Tiny embedded gifs: three solid frames with 100/200/50 ms delays, and
        // a one-frame gif that must stay a still.
        const string threeB64 =
            "R0lGODlhKAAeAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAKAAeAAAIOgABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzAxBgQAIfkEARQAAQAsAAAAACgAHgCBAP8AAAAAAAAAAAAACDoAAQgcSLCgwYMIEypcyLChw4cQI0qcSLGixYsYM2rcyLGjx48gQ4ocSbKkyZMoU6pcybKly5cwMQYEACH5BAEFAAEALAAAAAAoAB4AgQAA/wAAAAAAAAAAAAg6AAEIHEiwoMGDCBMqXMiwocOHECNKnEixosWLGDNq3Mixo8ePIEOKHEmypMmTKFOqXMmypcuXMDEGBAA7";
        const string oneB64 =
            "R0lGODdhKAAeAIEAAAkJCQAAAAAAAAAAACwAAAAAKAAeAEAIOgABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzAxBgQAOw==";
        byte[] three = Convert.FromBase64String(threeB64);
        byte[] one = Convert.FromBase64String(oneB64);
        Check("gif: signature detected", ImageCodec.IsGif(three) && !ImageCodec.IsGif(new byte[] { 1, 2, 3 }), null);
        string reason;
        using (ImageCodec.GifArt art = ImageCodec.LoadGif(three, out reason))
        {
            Check("gif: three frames decode", art != null && art.Count == 3, reason ?? "null art");
            Check("gif: delays read and floored at 50 ms",
                art != null && art.Delays[0] == 100 && art.Delays[1] == 200 && art.Delays[2] == 50,
                art == null ? "null" : string.Join(",", art.Delays));
            Check("gif: frame 2 pixels are the green frame",
                art != null && art.Frames[1].GetPixel(10, 10).G > 200 && art.Frames[1].GetPixel(10, 10).R < 60,
                art == null ? "null" : art.Frames[1].GetPixel(10, 10).ToString());
        }
        using (ImageCodec.GifArt art = ImageCodec.LoadGif(one, out reason))
            Check("gif: a single frame stays a still", art == null && reason != null, reason ?? "loaded");
        using (ImageCodec.GifArt art = ImageCodec.LoadGif(three, 40L * 30 * 2, out reason))
            Check("gif: over the pixel budget falls back to still", art == null && reason == "it is too large to animate", reason ?? "loaded");
        using (ImageCodec.GifArt art = ImageCodec.LoadGif(ImageCodec.EncodeJpeg(new Bitmap(4, 4)), out reason))
            Check("gif: a jpeg is not a gif", art == null && reason == "it is not a GIF", reason ?? "loaded");

        byte[] jpeg = ImageCodec.GifToJpeg(three);
        using (Bitmap first = ImageCodec.DecodeJpeg(jpeg))
            Check("gif: GifToJpeg writes the red first frame",
                first.Width == 40 && first.GetPixel(10, 10).R > 180 && first.GetPixel(10, 10).G < 80,
                first.Width + " " + first.GetPixel(10, 10));

        string work = Temp("giflib");
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            string name = ThemeLibrary.CreateFromJpeg(jpeg, "Gif theme", three);
            string folder = ThemeLibrary.Folder(name);
            Check("gif: theme holds wallpaper.gif and wallpaper.jpg",
                File.Exists(Path.Combine(folder, "wallpaper.gif")) && File.Exists(Path.Combine(folder, "wallpaper.jpg")), null);

            string app = Path.Combine(work, "Devin Animated");
            Directory.CreateDirectory(app);
            ThemeLibrary.Apply(name, app);
            Check("gif: apply copies wallpaper.gif", File.Exists(Path.Combine(app, "wallpaper.gif")), null);

            string still = ThemeLibrary.CreateFromJpeg(jpeg, "Still theme");
            ThemeLibrary.Apply(still, app);
            Check("gif: applying a still theme clears the app's gif", !File.Exists(Path.Combine(app, "wallpaper.gif")), null);

            // A slides\ folder of stills plays through the same machinery.
            string slidesTheme = ThemeLibrary.CreateFromJpeg(jpeg, "Slides theme");
            string slidesDir = Path.Combine(ThemeLibrary.Folder(slidesTheme), "slides");
            Directory.CreateDirectory(slidesDir);
            File.WriteAllBytes(Path.Combine(slidesDir, "a.jpg"), jpeg);
            File.WriteAllBytes(Path.Combine(slidesDir, "b.jpg"), jpeg);
            File.WriteAllText(Path.Combine(slidesDir, "note.txt"), "not an image");
            using (ImageCodec.GifArt art = ImageCodec.LoadSlides(slidesDir, out reason))
                Check("slides: two stills load as frames", art != null && art.Count == 2, reason ?? "null art");
            ThemeLibrary.Apply(slidesTheme, app);
            Check("slides: apply copies the slides folder",
                Directory.Exists(Path.Combine(app, "slides")) &&
                File.Exists(Path.Combine(app, "slides", "a.jpg")), null);
            ThemeLibrary.Apply(still, app);
            Check("slides: applying a still theme clears the slides",
                !Directory.Exists(Path.Combine(app, "slides")), null);
            string oneDir = Path.Combine(work, "oneslide");
            Directory.CreateDirectory(oneDir);
            File.WriteAllBytes(Path.Combine(oneDir, "a.jpg"), jpeg);
            using (ImageCodec.GifArt art = ImageCodec.LoadSlides(oneDir, out reason))
                Check("slides: one picture stays a still", art == null && reason != null, reason ?? "loaded");

            // APNG: rebuild two solid-colour PNGs' chunks into one animated file.
            byte[] pngA = PngBytes(255, 0, 0), pngB = PngBytes(0, 0, 255);
            byte[] apng = BuildApng(pngA, pngB);
            Check("apng: signature detected", ImageCodec.IsApng(apng) && !ImageCodec.IsApng(pngA), null);
            using (ImageCodec.GifArt art = ImageCodec.LoadApng(apng, out reason))
            {
                Check("apng: two frames decode", art != null && art.Count == 2, reason ?? "null art");
                Check("apng: frames are red then blue",
                    art != null && art.Frames[0].GetPixel(4, 4).R > 200 && art.Frames[1].GetPixel(4, 4).B > 200,
                    art == null ? "null" : art.Frames[0].GetPixel(4, 4) + "/" + art.Frames[1].GetPixel(4, 4));
                Check("apng: delay from fcTL", art != null && art.Delays[0] == 200 && art.Delays[1] == 200,
                    art == null ? "null" : string.Join(",", art.Delays));
            }
            string apngTheme = ThemeLibrary.CreateFromJpeg(jpeg, "Apng theme", null, apng);
            Check("apng: theme keeps wallpaper.apng",
                File.Exists(Path.Combine(ThemeLibrary.Folder(apngTheme), "wallpaper.apng")), null);

            ThemeLibrary.SaveWallpaper(name, jpeg);
            Check("gif: a static wallpaper clears the theme's gif", !File.Exists(Path.Combine(folder, "wallpaper.gif")), null);
        }
        finally { ThemeLibrary.RootOverride = null; }
    }

    // A solid-colour PNG saved by GDI+, for building the test APNG.
    private static byte[] PngBytes(byte r, byte g, byte b)
    {
        using (var bmp = new Bitmap(8, 8, PixelFormat.Format32bppArgb))
        {
            using (var g2 = Graphics.FromImage(bmp)) g2.Clear(Color.FromArgb(r, g, b));
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }
    }

    // Walks a PNG's chunks; returns each entry's (type, data) so the test can
    // repack IHDR/IDAT pieces into an APNG.
    private static List<KeyValuePair<string, byte[]>> PngChunks(byte[] png)
    {
        var list = new List<KeyValuePair<string, byte[]>>();
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            byte[] data = new byte[len];
            Buffer.BlockCopy(png, pos + 8, data, 0, len);
            list.Add(new KeyValuePair<string, byte[]>(type, data));
            pos += 12 + len;
            if (type == "IEND") break;
        }
        return list;
    }

    private static void WriteBe(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void ApngChunk(Stream s, string type, byte[] data)
    {
        WriteBe(s, data.Length);
        byte[] name = Encoding.ASCII.GetBytes(type);
        s.Write(name, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = 0xFFFFFFFFu;
        foreach (byte x in name) crc = ((crc >> 8) & 0xFFFFFFu) ^ CrcTab[(crc ^ x) & 0xFF];
        foreach (byte x in data) crc = ((crc >> 8) & 0xFFFFFFu) ^ CrcTab[(crc ^ x) & 0xFF];
        WriteBe(s, (int)(crc ^ 0xFFFFFFFFu));
    }

    private static uint[] crcT;
    private static uint[] CrcTab
    {
        get
        {
            if (crcT == null)
            {
                crcT = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    crcT[n] = c;
                }
            }
            return crcT;
        }
    }

    // Two full-canvas frames at 200 ms each: pngA through IDAT, pngB through
    // fdAT with a sequence number prefix.
    private static byte[] BuildApng(byte[] pngA, byte[] pngB)
    {
        byte[] ihdr = PngChunks(pngA).Find(p => p.Key == "IHDR").Value;
        byte[] idatA = PngChunks(pngA).Find(p => p.Key == "IDAT").Value;
        byte[] idatB = PngChunks(pngB).Find(p => p.Key == "IDAT").Value;
        using (var ms = new MemoryStream())
        {
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            ApngChunk(ms, "IHDR", ihdr);
            ApngChunk(ms, "acTL", new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 });
            ApngChunk(ms, "fcTL", Fctl(0, 8, 8));
            ApngChunk(ms, "IDAT", idatA);
            ApngChunk(ms, "fcTL", Fctl(1, 8, 8));
            byte[] fdat = new byte[idatB.Length + 4];
            WriteBe(new MemoryStream(fdat, 0, 4, true), 2);
            Buffer.BlockCopy(idatB, 0, fdat, 4, idatB.Length);
            ApngChunk(ms, "fdAT", fdat);
            ApngChunk(ms, "IEND", new byte[0]);
            return ms.ToArray();
        }
    }

    private static byte[] Fctl(int seq, int w, int h)
    {
        var ms = new MemoryStream();
        WriteBe(ms, seq); WriteBe(ms, w); WriteBe(ms, h);
        WriteBe(ms, 0); WriteBe(ms, 0);                    // x, y offsets
        ms.WriteByte(0); ms.WriteByte(2);                  // delay_num = 2
        ms.WriteByte(0); ms.WriteByte(10);                 // delay_den = 10 -> 200 ms
        ms.WriteByte(0); ms.WriteByte(0);                  // dispose none, blend source
        return ms.ToArray();
    }

    private static void StarterCases()
    {
        string work = Temp("starters");
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            string[] made = ThemeLibrary.SeedStarters();
            Check("starters: six created", made.Length == 6, made.Length.ToString());
            Check("starters: all names present", Array.IndexOf(made, "Aurora fields") >= 0 &&
                Array.IndexOf(made, "Paper dusk") >= 0 && Array.IndexOf(made, "Night garden") >= 0 &&
                Array.IndexOf(made, "Sakura rain") >= 0 && Array.IndexOf(made, "Deep sea") >= 0 &&
                Array.IndexOf(made, "Sunset") >= 0, string.Join(",", made));
            foreach (string name in made)
            {
                string iniPath = Path.Combine(ThemeLibrary.Folder(name), "theme.ini");
                Check("starters: " + name + " wallpaper decodes",
                    File.Exists(Path.Combine(ThemeLibrary.Folder(name), "wallpaper.jpg")) &&
                    ImageCodec.DecodeJpeg(File.ReadAllBytes(Path.Combine(ThemeLibrary.Folder(name), "wallpaper.jpg"))) != null, name);
                Check("starters: " + name + " credit set",
                    IniFile.Load(iniPath).Get("credit") == "Generated by Animated Launchers",
                    IniFile.Load(iniPath).Get("credit"));
            }
            Check("starters: Deep sea is a gradient theme",
                IniFile.Load(Path.Combine(ThemeLibrary.Folder("Deep sea"), "theme.ini")).Get("gradient") == "#062E4F|#0A7C8C|160",
                IniFile.Load(Path.Combine(ThemeLibrary.Folder("Deep sea"), "theme.ini")).Get("gradient"));
            Check("starters: procedural themes carry no gradient key",
                IniFile.Load(Path.Combine(ThemeLibrary.Folder("Aurora fields"), "theme.ini")).Get("gradient") == null, null);

            // Idempotent: a second seed creates nothing.
            string[] again = ThemeLibrary.SeedStarters();
            Check("starters: reseeding creates nothing", again.Length == 0, again.Length.ToString());

            // No-overwrite: a user theme named like a starter is untouched.
            string user = Path.Combine(ThemeLibrary.Folder("Sunset"), "theme.ini");
            File.AppendAllText(user, "mine=yes\n");
            ThemeLibrary.SeedStarters();
            Check("starters: user theme not overwritten", IniFile.Load(user).Get("mine") == "yes", null);

            // Thumbnails: six entries, each a decodable jpeg.
            var thumbs = ThemeLibrary.StarterThumbs();
            Check("starters: six thumbs", thumbs.Count == 6, thumbs.Count.ToString());
            bool allDecode = thumbs.Count == 6;
            foreach (var pair in thumbs) allDecode = allDecode && ImageCodec.DecodeJpeg(pair.Value) != null;
            Check("starters: thumbs decode", allDecode, null);
        }
        finally { ThemeLibrary.RootOverride = null; }
    }

    private static void ProfileRoundTrip()
    {
        string root = Temp("profile");
        string path = Path.Combine(root, "settings.ini");
        var seed = new IniFile();
        seed.Set("futureKey", "42");
        seed.Save(path);
        AppProfile profile = AppProfile.Load(root);
        profile.SplashOnStart = false;
        profile.Speed = 2;
        profile.Theme = "T";
        profile.Look.Blur = 33;
        profile.Save();
        AppProfile back = AppProfile.Load(root);
        Check("profile: splash=off and speed=2 kept", !back.SplashOnStart && back.Speed == 2, null);
        Check("profile: theme and look kept", back.Theme == "T" && back.Look.Blur == 33, null);
        Check("profile: unknown key kept", IniFile.Load(path).Get("futureKey") == "42", null);
        Check("profile: file documents every known key",
            IniFile.Load(path).Get("focusX") == "50" && IniFile.Load(path).Get("logs") == "" && IniFile.Load(path).Get("splash") == "off" &&
            IniFile.Load(path).Get("watch") == "on", null);
        Check("profile: missing file gives defaults", AppProfile.Load(Temp("empty")).SplashOnStart && AppProfile.Load(Temp("empty2")).Speed == 1 &&
            AppProfile.Load(Temp("empty3")).Watch, null);
        string watchRoot = Temp("watch");
        File.WriteAllText(Path.Combine(watchRoot, "settings.ini"), "watch=off\nsplash=off\n");
        AppProfile watchOff = AppProfile.Load(watchRoot);
        Check("profile: watch=off reads off, other keys still read", !watchOff.Watch && !watchOff.SplashOnStart, null);
        watchOff.Watch = true;
        watchOff.Save();
        Check("profile: watch=on written", IniFile.Load(Path.Combine(watchRoot, "settings.ini")).Get("watch") == "on", null);
    }

    private static void AppTableCases()
    {
        Check("apps: ten are described", AppTable.All.Length == 10, AppTable.All.Length.ToString());
        var ids = new List<string>();
        var helpers = new List<string>();
        foreach (App app in AppTable.All) { ids.Add(app.Id); helpers.Add(app.HelperName); }
        ids.Sort(); helpers.Sort();
        Check("apps: ids cover the four agents, the three editors and the three untested ones",
            string.Join(",", ids.ToArray()) == "antigravity,claude,codex,cursor,devin,kiro,obsidian,vscode,vscodium,windsurf",
            string.Join(",", ids.ToArray()));
        Check("dispatch: DevinAnimated.exe -> devin", AppTable.ByExeName("DevinAnimated.exe").Id == "devin", null);
        Check("dispatch: VSCodeAnimated.exe -> vscode",
            AppTable.ByExeName("VSCodeAnimated.exe") != null && AppTable.ByExeName("VSCodeAnimated.exe").Id == "vscode", null);
        Check("dispatch: every app has a unique helper name", new HashSet<string>(helpers, StringComparer.OrdinalIgnoreCase).Count == 10, null);
        Check("dispatch: bare name and case both work",
            AppTable.ByExeName("claudeanimated").Id == "claude" && AppTable.ByExeName("CodexAnimated").Id == "codex", null);
        Check("dispatch: the neutral exe is no app", AppTable.ByExeName("AnimatedLauncher.exe") == null, null);
        Check("dispatch: ThemeStudio and unknown names are no app",
            AppTable.ByExeName("ThemeStudio.exe") == null && AppTable.ByExeName("notepad.exe") == null, null);
        Check("lookup: ById matches id case-insensitively",
            AppTable.ById("CLAUDE") == AppTable.ById("claude") && AppTable.ById("bogus") == null, null);
        Check("mutexes: one helper and one animation across apps",
            AppTable.ArtworkMutex == @"Local\AnimatedLaunchers.Artwork" && AppTable.PlayMutex == @"Local\AnimatedLaunchers.Splash.Play", null);
        Check("run value: shared entry name", AppTable.RunValueName == "AnimatedLaunchers", null);
        Check("exe name: the one binary", AppTable.HelperExeName == "AnimatedLauncher.exe", null);
    }

    private static void CustomAppCases()
    {
        var ini = new IniFile();
        ini.Set("myapp", "My App|C:\\Tools\\MyApp.exe|chat");
        ini.Set("note", " Notes |D:\\Apps\\notes.exe|bogus-preview");
        ini.Set("devin", "Clash|C:\\x\\y.exe|editor");            // built-in id refused
        ini.Set("Bad Id", "X|C:\\x\\z.exe|editor");               // spaces are not a valid id
        ini.Set("rel", "X|bin\\app.exe|editor");                  // must be rooted
        ini.Set("dir", "X|C:\\x|editor");
        ini.Set("short", "X|C:\\only.exe");
        ini.Set("empty", "");
        var entries = CustomApps.ParseEntries(ini);
        Check("custom: valid rows parse, invalid rows drop", entries.Count == 3,
            entries.Count + ": " + string.Join(",", entries.ConvertAll(e => e.Id).ToArray()));
        Check("custom: built-in and bad ids are refused",
            !entries.Exists(e => e.Id == "devin" || e.Id == "bad id"), null);
        Check("custom: unrooted and non-exe paths are refused",
            !entries.Exists(e => e.Id == "rel" || e.Id == "dir"), null);
        Check("custom: preview normalizes to chat or editor",
            entries.Find(e => e.Id == "myapp").Preview == "chat" && entries.Find(e => e.Id == "note").Preview == "editor", null);
        Check("custom: id lowercased, name trimmed",
            entries.Find(e => e.Id == "note").Name == "Notes", entries.Find(e => e.Id == "note").Name);
        Check("custom: a missing preview part defaults to editor",
            entries.Find(e => e.Id == "short").Preview == "editor", null);
        Check("custom: ids accept digits, dash and underscore, reject the rest",
            CustomApps.ValidId("a1_-") && CustomApps.ValidId("app") &&
            !CustomApps.ValidId("") && !CustomApps.ValidId("has space") && !CustomApps.ValidId("Upper") &&
            !CustomApps.ValidId(new string('x', 25)), null);
        Check("custom: name cleaning strips pipes, equals and file-hostile chars",
            CustomApps.CleanName(" A|b=c:d? ") == "A b c d", CustomApps.CleanName(" A|b=c:d? "));

        string keep = Environment.GetEnvironmentVariable("ANIMATED_DATA");
        string root = Temp("custom-apps");
        Environment.SetEnvironmentVariable("ANIMATED_DATA", root);
        try
        {
            Check("custom: no file under a scratch root", CustomApps.Current.Length == 0, null);
            string file = CustomApps.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "zed=Zed|" + Path.Combine(root, "zed.exe") + "|chat\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "zed.exe"), "not really a program");
            Check("custom: the file is picked up", CustomApps.Current.Length == 1 &&
                CustomApps.Current[0].Id == "zed" && CustomApps.Current[0].IsCustom, null);
            App zed = CustomApps.Current[0];
            Check("custom: any window class matches", zed.WindowClass == null, null);
            Check("custom: only its exact exe is owned",
                zed.Owns(Path.Combine(root, "zed.exe")) && !zed.Owns(Path.Combine(root, "other.exe")), null);
            Check("custom: installed follows the exe file", zed.Installed, null);
            Check("custom: product folder is stable",
                zed.SettingsDirectory == Path.Combine(root, "Zed Animated"), zed.SettingsDirectory);
            Check("custom: merged list keeps built-ins first",
                AppTable.WithCustom().Length == AppTable.All.Length + 1 && AppTable.ById("zed") == zed, null);

            string error;
            App added = CustomApps.Add("Second App", Path.Combine(root, "second.exe"), "editor", out error);
            Check("custom: Add refuses a missing exe", added == null && error != null, error);
            File.WriteAllText(Path.Combine(root, "second.exe"), "x");
            added = CustomApps.Add("Second App", Path.Combine(root, "second.exe"), "chat", out error);
            Check("custom: Add writes the row and merges",
                added != null && added.Id == "second-app" && AppTable.ById("second-app") != null &&
                CustomApps.Current.Length == 2, added == null ? error : added.Id);
            Check("custom: the ini row round-trips",
                IniFile.Load(file).Get("second-app").EndsWith("second.exe|chat", StringComparison.OrdinalIgnoreCase),
                IniFile.Load(file).Get("second-app"));
            App again = CustomApps.Add("Copy", Path.Combine(root, "second.exe"), "editor", out error);
            Check("custom: the same exe cannot be added twice", again == null && error != null, error);
            Check("custom: Remove drops the row",
                CustomApps.Remove("second-app") && CustomApps.Current.Length == 1 && AppTable.ById("second-app") == null, null);
            Check("custom: removing twice is false", !CustomApps.Remove("second-app"), null);
            // A changed row keeps its id but rebuilds the App, so sessions reconcile.
            File.WriteAllText(Path.Combine(root, "zed2.exe"), "x");
            File.WriteAllText(file, "zed=Zed|" + Path.Combine(root, "zed2.exe") + "|editor\n", Encoding.UTF8);
            App[] now = CustomApps.Current;
            Check("custom: an edited row gives a new App instance",
                now.Length == 1 && now[0].Id == "zed" && !ReferenceEquals(now[0], zed), null);
        }
        finally { Environment.SetEnvironmentVariable("ANIMATED_DATA", keep); }
    }

    private static Bitmap Solid(int w, int h, Color color)
    {
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(color);
        return bitmap;
    }

    private static void RenderIdentity()
    {
        using (var image = Solid(40, 30, Color.Orange))
        {
            Bitmap result = ArtworkLook.Render(image, new Look());
            Check("render: blur 0, tint 0, dim 0 returns the same object", object.ReferenceEquals(result, image), null);
            Bitmap tiny = ArtworkLook.Render(image, new Look { Blur = 1 });
            Check("render: negligible blur returns the same object", object.ReferenceEquals(tiny, image), null);
        }
    }

    private static Bitmap RandomImage(int w, int h, int seed)
    {
        var random = new Random(seed);
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        return bitmap;
    }

    private static void TintCase()
    {
        using (var image = RandomImage(48, 48, 7))
        {
            var look = new Look { Tint = 100, TintColor = Color.FromArgb(0x8C, 0x66, 0xD1) };
            using (Bitmap result = ArtworkLook.Render(image, look))
            {
                int worst = 0;
                for (int y = 0; y < 48; y++)
                    for (int x = 0; x < 48; x++)
                    {
                        Color s = image.GetPixel(x, y), o = result.GetPixel(x, y);
                        double luma = 0.299 * s.R + 0.587 * s.G + 0.114 * s.B;
                        worst = Math.Max(worst, Math.Abs(o.R - (int)Math.Round(luma * 140 / 255)));
                        worst = Math.Max(worst, Math.Abs(o.G - (int)Math.Round(luma * 102 / 255)));
                        worst = Math.Max(worst, Math.Abs(o.B - (int)Math.Round(luma * 209 / 255)));
                    }
                Check("tint: tint=100 equals luma * tint colour within 2 levels", worst <= 2, "worst " + worst);
            }
        }
    }

    private static double MeanLuma(Bitmap bitmap)
    {
        double sum = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color c = bitmap.GetPixel(x, y);
                sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            }
        return sum / (bitmap.Width * bitmap.Height);
    }

    private static void DimCase()
    {
        using (var image = RandomImage(32, 32, 11))
        {
            double previous = MeanLuma(image);
            bool decreasing = true;
            string trail = previous.ToString("0.0");
            foreach (int dim in new[] { 20, 40, 80 })
                using (Bitmap result = ArtworkLook.Render(image, new Look { Dim = dim }))
                {
                    double now = MeanLuma(result);
                    trail += " > " + now.ToString("0.0");
                    if (!(now < previous)) decreasing = false;
                    previous = now;
                }
            Check("dim: mean luminance strictly decreases across 0, 20, 40, 80", decreasing, trail);
        }
        using (var white = Solid(8, 8, Color.White))
        using (Bitmap result = ArtworkLook.Render(white, new Look { Dim = 80 }))
        {
            Color c = result.GetPixel(4, 4);
            Check("dim: dim=80 on white is (57, 67, 74) within 2",
                Math.Abs(c.R - 57) <= 2 && Math.Abs(c.G - 67) <= 2 && Math.Abs(c.B - 74) <= 2, c.R + "," + c.G + "," + c.B);
        }
    }

    private static Bitmap Scaled(Bitmap src, int w, int h)
    {
        var result = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(result))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        }
        return result;
    }

    private static double MeanDifference(Bitmap a, Bitmap b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
            }
        return sum / (3.0 * a.Width * a.Height);
    }

    private static void BlurResolutionIndependent()
    {
        var random = new Random(5);
        using (var big = new Bitmap(2000, 1250, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(Color.FromArgb(40, 60, 90));
                for (int i = 0; i < 700; i++)
                    using (var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))))
                        g.FillRectangle(brush, random.Next(2000), random.Next(1250), 6 + random.Next(70), 6 + random.Next(70));
                using (var pen = new Pen(Color.White, 1.5f))
                    for (int i = 0; i < 400; i++)
                        g.DrawLine(pen, random.Next(2000), random.Next(1250), random.Next(2000), random.Next(1250));
            }
            using (Bitmap small = Scaled(big, 800, 500))
            {
                var look = new Look { Blur = 40 };
                using (Bitmap blurBig = ArtworkLook.Render(big, look))
                using (Bitmap blurSmall = ArtworkLook.Render(small, look))
                using (Bitmap a = Scaled(blurBig, 400, 250))
                using (Bitmap b = Scaled(blurSmall, 400, 250))
                using (Bitmap plain = Scaled(big, 400, 250))
                {
                    double between = MeanDifference(a, b), versusPlain = MeanDifference(a, plain);
                    Console.WriteLine("     measured: 2000px vs 800px blur difference " + between.ToString("0.00") +
                        ", blurred vs unblurred " + versusPlain.ToString("0.00"));
                    Check("blur: same look at 2000x1250 and 800x500 within 6 per channel", between < 6, between.ToString("0.00"));
                    Check("blur: blur=40 differs from the unblurred image by more than 6", versusPlain > 6, versusPlain.ToString("0.00"));
                }
            }
        }
    }

    private static Bitmap Detailed(int w, int h, int seed)
    {
        var random = new Random(seed);
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(40, 60, 90));
            for (int i = 0; i < 350; i++)
                using (var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))))
                    g.FillRectangle(brush, random.Next(w), random.Next(h), 4 + random.Next(w / 25), 4 + random.Next(w / 25));
            using (var pen = new Pen(Color.White, 1f))
                for (int i = 0; i < 250; i++)
                    g.DrawLine(pen, random.Next(w), random.Next(h), random.Next(w), random.Next(h));
        }
        return bitmap;
    }

    private static void BlurCurve()
    {
        using (Bitmap image = Detailed(960, 600, 9))
        {
            double previous = 0;
            bool growing = true;
            string trail = "";
            double first = 0;
            foreach (int blur in new[] { 6, 12, 20, 35, 60 })
                using (Bitmap result = ArtworkLook.Render(image, new Look { Blur = blur }))
                {
                    double difference = MeanDifference(result, image);
                    if (blur == 6) first = difference;
                    if (!(difference > previous)) growing = false;
                    previous = difference;
                    trail += blur + ":" + difference.ToString("0.00") + " ";
                }
            Console.WriteLine("     measured: mean difference by blur " + trail);
            Check("blur curve: blur=6 already differs from the unblurred image by more than 0.3", first > 0.3, first.ToString("0.00"));
            Check("blur curve: difference grows strictly across 6, 12, 20, 35, 60", growing, trail);
        }
    }

    private static void BlurKeepsBorder()
    {
        using (var white = Solid(300, 200, Color.White))
        using (Bitmap result = ArtworkLook.Render(white, new Look { Blur = 60 }))
        {
            int lowest = 255;
            foreach (Point p in new[] { new Point(0, 0), new Point(299, 0), new Point(0, 199), new Point(299, 199) })
            {
                Color c = result.GetPixel(p.X, p.Y);
                lowest = Math.Min(lowest, Math.Min(c.R, Math.Min(c.G, c.B)));
            }
            Check("blur: border of a white image stays at least 250", lowest >= 250, "lowest " + lowest);
        }
    }

    private static void CoverSourceCases()
    {
        bool ok = true;
        string detail = "";
        foreach (Size image in new[] { new Size(2000, 1000), new Size(1000, 2000) })
        {
            var viewport = new Size(800, 600);
            RectangleF mid = ArtworkLook.CoverSource(image, viewport, 50, 50);
            RectangleF lo = ArtworkLook.CoverSource(image, viewport, 0, 0);
            RectangleF hi = ArtworkLook.CoverSource(image, viewport, 100, 100);
            float overflowX = image.Width - mid.Width, overflowY = image.Height - mid.Height;
            ok &= Math.Abs(lo.X) < 0.01f && Math.Abs(lo.Y) < 0.01f;
            ok &= Math.Abs(hi.X - overflowX) < 0.01f && Math.Abs(hi.Y - overflowY) < 0.01f;
            ok &= Math.Abs(mid.X - overflowX / 2) < 0.01f && Math.Abs(mid.Y - overflowY / 2) < 0.01f;
            foreach (RectangleF r in new[] { lo, mid, hi })
                ok &= r.Left >= -0.01f && r.Top >= -0.01f && r.Right <= image.Width + 0.01f && r.Bottom <= image.Height + 0.01f;
            detail += image + " ";
        }
        Check("cover: focus 0/50/100 on a wide and a tall image, never outside the image", ok, detail);
    }

    private static double HueOf(Color c) { return c.GetHue(); }

    private static void AccentCases()
    {
        using (var red = Solid(120, 80, Color.FromArgb(220, 30, 30)))
        {
            Color[] accents = ArtworkLook.SuggestAccents(red, 3);
            double hue = accents.Length == 0 ? -1 : HueOf(accents[0]);
            Check("accents: mostly red image gives red first", accents.Length > 0 && (hue <= 15 || hue >= 345), "hue " + hue.ToString("0"));
            bool legible = true;
            foreach (Color c in accents) legible &= c.GetBrightness() >= 0.619 && c.GetBrightness() <= 0.801;
            Check("accents: lightness within [0.62, 0.80]", legible, null);
        }
        using (var gray = Solid(120, 80, Color.FromArgb(120, 120, 120)))
            Check("accents: grayscale image gives none", ArtworkLook.SuggestAccents(gray, 3).Length == 0, null);
        using (var split = new Bitmap(120, 80, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(split))
            {
                g.FillRectangle(Brushes.Crimson, 0, 0, 60, 80);
                g.FillRectangle(Brushes.RoyalBlue, 60, 0, 60, 80);
            }
            Color[] accents = ArtworkLook.SuggestAccents(split, 3);
            bool apart = accents.Length >= 2;
            if (apart)
            {
                double d = Math.Abs(HueOf(accents[0]) - HueOf(accents[1]));
                apart = Math.Min(d, 360 - d) > 60;
            }
            bool legible = true;
            foreach (Color c in accents) legible &= c.GetBrightness() >= 0.619 && c.GetBrightness() <= 0.801;
            Check("accents: half red, half blue gives two more than 60 degrees apart", apart && legible, accents.Length + " accents");
        }
    }

    private static void WriteJpeg(string path, Color color)
    {
        using (var image = Solid(20, 20, color)) image.Save(path, ImageFormat.Jpeg);
    }

    private static void ApplyCase()
    {
        string work = Temp("apply");
        string library = Path.Combine(work, "Themes");
        string app = Path.Combine(work, "App");
        Directory.CreateDirectory(app);
        ThemeLibrary.RootOverride = library;
        try
        {
            string withAvatar = Path.Combine(library, "With Avatar");
            string without = Path.Combine(library, "Without");
            Directory.CreateDirectory(withAvatar);
            Directory.CreateDirectory(without);
            WriteJpeg(Path.Combine(withAvatar, "wallpaper.jpg"), Color.Red);
            WriteJpeg(Path.Combine(withAvatar, "avatar.jpg"), Color.Green);
            WriteJpeg(Path.Combine(without, "wallpaper.jpg"), Color.Blue);
            var themeIni = new IniFile();
            themeIni.Set("opacity", "22");
            themeIni.Set("blur", "30");
            themeIni.Set("title", "WITH AVATAR");
            themeIni.Set("accent", "#112233");
            themeIni.Set("logs", "x|y");
            themeIni.Set("themeOnlyKey", "ignored");
            themeIni.Save(Path.Combine(withAvatar, "theme.ini"));

            var seed = new IniFile();
            seed.Set("splash", "off");
            seed.Set("speed", "2");
            seed.Set("keepMe", "yes");
            seed.Set("opacity", "5");
            seed.Save(Path.Combine(app, "settings.ini"));
            WriteJpeg(Path.Combine(app, "avatar.jpg"), Color.Yellow);

            Check("library: names list folders with a wallpaper", string.Join("|", ThemeLibrary.Names()) == "With Avatar|Without", string.Join("|", ThemeLibrary.Names()));
            ThemeLibrary.Apply("With Avatar", app);
            Color wall;
            using (var b = ImageCodec.DecodeJpeg(File.ReadAllBytes(Path.Combine(app, "wallpaper.jpg")))) wall = b.GetPixel(5, 5);
            Check("apply: wallpaper copied", wall.R > 200 && wall.G < 60, wall.ToString());
            Color avatar;
            using (var b = ImageCodec.DecodeJpeg(File.ReadAllBytes(Path.Combine(app, "avatar.jpg")))) avatar = b.GetPixel(5, 5);
            Check("apply: avatar copied", avatar.G > 100 && avatar.R < 60, avatar.ToString());
            AppProfile profile = AppProfile.Load(app);
            Check("apply: look and text replaced, theme set",
                profile.Look.Opacity == 22 && profile.Look.Blur == 30 && profile.Text.Title == "WITH AVATAR" &&
                profile.Text.Accent == "#112233" && profile.Text.Logs.Length == 2 && profile.Theme == "With Avatar", null);
            Check("apply: splash, speed and unknown keys kept", !profile.SplashOnStart && profile.Speed == 2 &&
                IniFile.Load(Path.Combine(app, "settings.ini")).Get("keepMe") == "yes", null);
            Check("apply: theme-only unknown keys are not copied", IniFile.Load(Path.Combine(app, "settings.ini")).Get("themeOnlyKey") == null, null);
            ThemeLibrary.Apply("Without", app);
            Check("apply: avatar removed when the theme has none", !File.Exists(Path.Combine(app, "avatar.jpg")), null);
            profile = AppProfile.Load(app);
            Check("apply: theme without theme.ini resets look and text to defaults",
                profile.Look.Opacity == 15 && profile.Look.Blur == 0 && profile.Text.Title == "" && profile.Theme == "Without", null);
            bool rejected = false;
            try { ThemeLibrary.Apply("..\\x", app); } catch (ArgumentException) { rejected = true; }
            Check("apply: names with path separators are rejected", rejected, null);
            bool missing = false;
            try { ThemeLibrary.Apply("Nope", app); } catch (FileNotFoundException) { missing = true; }
            Check("apply: unknown theme throws FileNotFoundException", missing, null);
        }
        finally { ThemeLibrary.RootOverride = null; }
    }

    private static string CodePoints(string value)
    {
        var points = new List<string>();
        foreach (char c in value) points.Add("U+" + ((int)c).ToString("X4"));
        return value.Length + ": " + string.Join(" ", points.ToArray());
    }

    private static void SplashPageCase()
    {
        string folder = Temp("splash");
        var ini = new IniFile();
        ini.Set("title", "<b>\"x\"&");
        ini.Set("accent", "javascript:1");
        ini.Set("logs", "first|second");
        ini.Set("speed", "2");
        ini.Save(Path.Combine(folder, "settings.ini"));
        SplashContent content = SplashContent.Load(folder, "Devin", new Size(800, 600));
        byte[] template = Encoding.UTF8.GetBytes(
            "<main data-rate=\"{{RATE}}\" data-theme=\"{{THEME}}\" style=\"--accent:{{ACCENT}}\">{{APP}}|{{TITLE}}|{{SUBTITLE}}|" +
            "<div>{{LOG1}}</div><div>{{LOG2}}</div><div>{{LOG3}}</div><div>{{LOG4}}</div></main>");
        string html = Encoding.UTF8.GetString(SplashPage.RenderIndex(template, content, "Devin"));
        Check("splash: title is HTML-encoded", html.Contains("&lt;b&gt;&quot;x&quot;&amp;") && !html.Contains("<b>"), html);
        Check("splash: invalid accent becomes #D980A7", html.Contains("--accent:#D980A7"), null);
        Check("splash: two log lines give two filled and two empty tokens",
            html.Contains("<div>&gt; first</div><div>&gt; second</div><div></div><div></div>"), html);
        Check("splash: no token remains", !html.Contains("{{"), null);
        Check("splash: speed=2 from settings.ini", html.Contains("data-rate=\"2\""), null);
        Check("splash: caption and label tokens for an app with its own text", content.Caption == "DEVIN INITIALIZE" && content.Label == "Devin", content.Caption);
        SplashContent plain = SplashContent.Load(Temp("splash-empty"), "Devin", new Size(888, 500));
        string[] expectedLogs = {
            "> MAGI SYSTEM \u00B7 CASPER / MELCHIOR / BALTHASAR", "> loading renderer surface ........ ok",
            "> entry plug depth 21.6 ........... ok", "> LCL pressure nominal" };
        bool logsOk = true;
        for (int i = 0; i < 4; i++) logsOk &= plain.Logs[i] == expectedLogs[i];
        Check("splash: a folder without settings gets the original project's text (title, subtitle, four lines, label, caption)",
            plain.Title == "\u98DE\u884C\u96EA\u7ED2" && plain.Subtitle == "\u5E7D\u7075\u6765\u5230\u2026\u4F60\u8EAB\u8FB9\uFF5E" && logsOk &&
            plain.Label == "RESONANCE / AEMEATH" && plain.Caption == "DEVIN INITIALIZE / TYPE-0", CodePoints(plain.Title) + " | " + CodePoints(plain.Subtitle));
        Console.WriteLine("     title code points: " + CodePoints(plain.Title));
        Console.WriteLine("     subtitle code points: " + CodePoints(plain.Subtitle));
        Console.WriteLine("     label: " + CodePoints(plain.Label));
        Console.WriteLine("     caption: " + CodePoints(plain.Caption));
        for (int i = 0; i < 4; i++) Console.WriteLine("     log " + (i + 1) + ": " + CodePoints(plain.Logs[i]));
        string themeFolder = Temp("splash-theme");
        var themeIni = new IniFile();
        themeIni.Set("title", "SAMPLE");
        themeIni.Set("subtitle", "Quiet harbor");
        themeIni.Save(Path.Combine(themeFolder, "theme.ini"));
        SplashContent themed = SplashContent.Load(themeFolder, "Devin", new Size(800, 600));
        Check("splash: a theme folder renders the theme's text with the plain caption and its name as the label",
            themed.Title == "SAMPLE" && themed.Subtitle == "Quiet harbor" && themed.Caption == "DEVIN INITIALIZE" && themed.Label == Path.GetFileName(themeFolder) && themed.Logs[0] == "> Devin launcher ready", themed.Label);
        var themedApp = new IniFile();
        themedApp.Set("theme", "Some theme");
        string themedAppFolder = Temp("splash-themed-app");
        themedApp.Save(Path.Combine(themedAppFolder, "settings.ini"));
        SplashContent keep = SplashContent.Load(themedAppFolder, "Devin", new Size(800, 600));
        Check("splash: an app with a theme set does not get the original text", keep.Title == "DEVIN" && keep.Caption == "DEVIN INITIALIZE" && keep.Label == "Some theme", keep.Title);
    }

    private static int RenderSplash(string folder, string app, string output)
    {
        string repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
        string web = Path.Combine(repo, "web");
        Directory.CreateDirectory(Path.Combine(output, "assets"));
        File.Copy(Path.Combine(web, "style.css"), Path.Combine(output, "style.css"), true);
        File.Copy(Path.Combine(web, "animation.js"), Path.Combine(output, "animation.js"), true);
        File.Copy(Path.Combine(web, "assets", "contours.js"), Path.Combine(output, "assets", "contours.js"), true);
        string wallpaper = Path.Combine(folder, "wallpaper.jpg");
        if (!File.Exists(wallpaper)) wallpaper = Path.Combine(web, "assets", "artwork.jpg");
        string avatar = Path.Combine(folder, "avatar.jpg");
        if (!File.Exists(avatar)) avatar = wallpaper;
        File.Copy(wallpaper, Path.Combine(output, "assets", "artwork.jpg"), true);
        File.Copy(avatar, Path.Combine(output, "assets", "avatar.jpg"), true);
        Size size;
        using (Bitmap decoded = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(wallpaper))) size = decoded.Size;
        SplashContent content = SplashContent.Load(folder, app, size);
        File.WriteAllBytes(Path.Combine(output, "index.html"),
            SplashPage.RenderIndex(File.ReadAllBytes(Path.Combine(web, "index.html")), content, app));
        Console.WriteLine("rendered " + folder + " -> " + output);
        return 0;
    }

    // Rename, delete and edits follow every pick slot: night and light-mode picks
    // are renamed and cleared with the theme, a favorite keeps its star, and an
    // edit reaches only the apps showing the theme, through the slot it holds there.
    private static void SlotReferenceCases()
    {
        string keepData = Environment.GetEnvironmentVariable("ANIMATED_DATA");
        string data = Temp("slots");
        Environment.SetEnvironmentVariable("ANIMATED_DATA", data);
        ThemeLibrary.RootOverride = Path.Combine(data, "Themes");
        try
        {
            foreach (string name in new[] { "Day", "Dusk", "Moon" })
            {
                string folder = ThemeLibrary.Folder(name);
                Directory.CreateDirectory(folder);
                WriteJpeg(Path.Combine(folder, "wallpaper.jpg"), name == "Moon" ? Color.Navy : Color.Orange);
                File.WriteAllText(Path.Combine(folder, "theme.ini"), "opacity=15\r\n");
            }
            string app = Path.Combine(data, "App Animated");
            Directory.CreateDirectory(app);
            ThemeLibrary.ApplyForMode("Day", app, "theme");
            ThemeLibrary.ApplyForMode("Moon", app, "themeNight");
            AppProfile p = AppProfile.Load(app);
            Check("slots: the night pick shows while theme= keeps the base pick",
                p.Theme == "Day" && p.ThemeNight == "Moon" && p.Applied == "Moon", p.Theme + "/" + p.ThemeNight + "/" + p.Applied);
            p.ThemeLight = "Dusk";
            p.Save();
            Check("slots: ShowingSlot names only the slot whose files are showing",
                ThemeLibrary.ShowingSlot(app, "Moon") == "themeNight" && ThemeLibrary.ShowingSlot(app, "Day") == null &&
                ThemeLibrary.ShowingSlot(app, "Dusk") == null, null);

            string before = File.ReadAllText(Path.Combine(app, "settings.ini"));
            SplashText stored;
            ThemeLibrary.Save("Day", new Look { Opacity = 30 }, new SplashText { Title = "DAY" }, new[] { app }, out stored);
            Check("slots: editing the base pick at night leaves the app alone",
                File.ReadAllText(Path.Combine(app, "settings.ini")) == before, null);
            ThemeLibrary.Save("Moon", new Look { Opacity = 22 }, new SplashText { Title = "MOON" }, new[] { app }, out stored);
            AppProfile edited = AppProfile.Load(app);
            Check("slots: editing the showing night pick reaches the app and keeps theme=",
                edited.Look.Opacity == 22 && edited.Text.Title == "MOON" && edited.Theme == "Day" && edited.ThemeNight == "Moon", null);

            Directory.CreateDirectory(ThemeLibrary.SharedRoot);
            File.WriteAllText(Path.Combine(ThemeLibrary.SharedRoot, "studio.ini"), "favorites=Moon|Day\r\n");
            ThemeLibrary.Rename("Moon", "Full Moon", new[] { app });
            AppProfile renamed = AppProfile.Load(app);
            Check("slots: rename follows the night pick and applied=",
                renamed.ThemeNight == "Full Moon" && renamed.Applied == "Full Moon" && renamed.Theme == "Day", null);
            string favorites = string.Join("|", ThemeLibrary.Favorites());
            Check("slots: a renamed favorite keeps its star", favorites == "Full Moon|Day", favorites);
            ThemeLibrary.Rename("Dusk", "Evening", new[] { app });
            Check("slots: rename follows the light-mode pick", AppProfile.Load(app).ThemeLight == "Evening", null);

            ThemeLibrary.ClearReferences("Full Moon", new[] { app });
            ThemeLibrary.ClearReferences("Evening", new[] { app });
            AppProfile cleared = AppProfile.Load(app);
            Check("slots: deleting clears the night and light picks and keeps theme=",
                cleared.ThemeNight == "" && cleared.ThemeLight == "" && cleared.Theme == "Day", null);
            Check("slots: a window that is not there is never full screen", !QuietTime.IsAppFullScreen(IntPtr.Zero), null);
        }
        finally
        {
            ThemeLibrary.RootOverride = null;
            Environment.SetEnvironmentVariable("ANIMATED_DATA", keepData);
        }
    }

    private static void LibraryOperations()
    {
        string work = Temp("ops");
        // Rename also rewrites studio.ini's favorites, so the shared root is scratch too.
        string keepData = Environment.GetEnvironmentVariable("ANIMATED_DATA");
        Environment.SetEnvironmentVariable("ANIMATED_DATA", work);
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            string source = Path.Combine(work, "My Wall.png");
            using (var image = Solid(60, 40, Color.FromArgb(200, 40, 40))) image.Save(source, ImageFormat.Png);
            string first = ThemeLibrary.Create(source);
            string second = ThemeLibrary.Create(source);
            Check("create: two creates from the same file name give X and X 2", first == "My Wall" && second == "My Wall 2", first + " | " + second);
            IniFile created = IniFile.Load(Path.Combine(ThemeLibrary.Folder(first), "theme.ini"));
            Check("create: defaults (opacity 15, title upper case, red-ish accent)",
                created.Get("opacity") == "15" && created.Get("blur") == "0" && created.Get("title") == "MY WALL" && (created.Get("accent") ?? "").StartsWith("#"), created.Get("accent"));
            Check("create: wallpaper.jpg is a decodable JPEG", ImageCodec.DecodeJpeg(File.ReadAllBytes(Path.Combine(ThemeLibrary.Folder(first), "wallpaper.jpg"))).Width == 60, null);
            bool bad = false;
            string junk = Path.Combine(work, "junk.png");
            File.WriteAllText(junk, "not an image");
            try { ThemeLibrary.Create(junk); } catch (InvalidDataException) { bad = true; }
            Check("create: an unreadable image is refused and leaves no folder", bad && ThemeLibrary.Names().Length == 2, null);

            string onTheme = Path.Combine(work, "AppOn"), other = Path.Combine(work, "AppOther");
            Directory.CreateDirectory(onTheme);
            Directory.CreateDirectory(other);
            WriteJpeg(Path.Combine(onTheme, "wallpaper.jpg"), Color.Blue);
            WriteJpeg(Path.Combine(other, "wallpaper.jpg"), Color.Blue);
            string imageBefore = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(onTheme, "wallpaper.jpg")));
            AppProfile a = AppProfile.Load(onTheme); a.Theme = first; a.SplashOnStart = false; a.Save();
            AppProfile b = AppProfile.Load(other); b.Theme = second; b.Save();
            string otherBefore = File.ReadAllText(Path.Combine(other, "settings.ini"));
            var look = new Look { Opacity = 33, Blur = 21, Tint = 40 };
            var text = new SplashText { Title = "Edited", Accent = "#101010", Logs = new[] { "a", "b" } };
            SplashText stored;
            ThemeLibrary.Save(first, look, text, new[] { onTheme, other }, out stored);
            AppProfile reread = AppProfile.Load(onTheme);
            Check("save: theme.ini written and the app on that theme follows", IniFile.Load(Path.Combine(ThemeLibrary.Folder(first), "theme.ini")).Get("blur") == "21" &&
                reread.Look.Opacity == 33 && reread.Look.Blur == 21 && reread.Text.Title == "Edited" && !reread.SplashOnStart, null);
            Check("save: an app on another theme is not touched", File.ReadAllText(Path.Combine(other, "settings.ini")) == otherBefore, null);
            Check("save: the app's images are not touched", Convert.ToBase64String(File.ReadAllBytes(Path.Combine(onTheme, "wallpaper.jpg"))) == imageBefore, null);
            Color shown; SplashText.TryParseColor(stored.Accent, out shown);
            Check("save: a dark accent is stored in legible form", ArtworkLook.Legible(shown).ToArgb() == shown.ToArgb() && shown.GetBrightness() >= 0.619, stored.Accent);

            string renamed = ThemeLibrary.Rename(first, "Fresh Name", new[] { onTheme, other });
            Check("rename: folder moved and theme= follows only the app on it",
                renamed == "Fresh Name" && Directory.Exists(ThemeLibrary.Folder("Fresh Name")) && !Directory.Exists(ThemeLibrary.Folder(first)) &&
                AppProfile.Load(onTheme).Theme == "Fresh Name" && AppProfile.Load(other).Theme == second, null);
            string caseOnly = ThemeLibrary.Rename("Fresh Name", "fresh name", new[] { onTheme });
            bool exactCase = false;
            foreach (string n in ThemeLibrary.Names()) if (n == "fresh name") exactCase = true;
            Check("rename: a letter-case-only change works", caseOnly == "fresh name" && exactCase && AppProfile.Load(onTheme).Theme == "fresh name", null);
            bool clash = false;
            try { ThemeLibrary.Rename("fresh name", second, new string[0]); } catch (IOException) { clash = true; }
            Check("rename: refuses an existing name", clash, null);

            string settingsBefore = File.ReadAllText(Path.Combine(onTheme, "settings.ini"));
            ThemeLibrary.ClearReferences("fresh name", new[] { onTheme, other });
            string settingsAfter = File.ReadAllText(Path.Combine(onTheme, "settings.ini"));
            Check("clear references: theme= emptied, nothing else changed",
                AppProfile.Load(onTheme).Theme == "" && settingsAfter == settingsBefore.Replace("theme=fresh name", "theme=") && AppProfile.Load(other).Theme == second, null);
        }
        finally
        {
            ThemeLibrary.RootOverride = null;
            Environment.SetEnvironmentVariable("ANIMATED_DATA", keepData);
        }
    }

    private static void NameValidation()
    {
        string[] bad = { "", "   ", new string('x', 61), "a/b", "a:b", "what?", "CON", "nul", "Com1", "con.txt", "ends.", "tab	here" };
        bool allRejected = true;
        string leaked = "";
        foreach (string name in bad)
        {
            try { ThemeLibrary.ValidateName(name); allRejected = false; leaked += "[" + name + "]"; }
            catch (ArgumentException) { }
        }
        Check("names: empty, too long, invalid characters, device names and trailing dot are rejected", allRejected, leaked);
        Check("names: trimmed and ordinary names pass", ThemeLibrary.ValidateName("  Sunset harbor ") == "Sunset harbor" && ThemeLibrary.ValidateName(new string('y', 60)).Length == 60, null);
    }

    private const string LineFormat = @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[\d+ [a-z]+\] ";

    private static string[] LogLines(string file)
    {
        if (!File.Exists(file)) return new string[0];
        string text = new UTF8Encoding(false).GetString(File.ReadAllBytes(file));
        return text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string LogFile(string folder) { return Path.Combine(folder, "Logs", "helper.log"); }

    private static void RunLogProcesses(string folder, int count)
    {
        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var running = new List<Process>();
        foreach (string label in new[] { "a", "b", "c", "d" })
            running.Add(Process.Start(new ProcessStartInfo(exe, "--log-append \"" + folder + "\" " + label + " " + count) { UseShellExecute = false, CreateNoWindow = true }));
        foreach (Process process in running) { process.WaitForExit(120000); process.Dispose(); }
    }

    private static void LogCases()
    {
        Func<DateTime> realClock = AppLog.Clock;
        try
        {
            string folder = Temp("log1");
            AppLog.Start(folder, "test");
            AppLog.Write("window", "hello");
            byte[] bytes = File.ReadAllBytes(LogFile(folder));
            string text = new UTF8Encoding(false).GetString(bytes);
            Check("log: one write is one line in the expected format", Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[\d+ test\] window: hello\r\n$"), text);
            Check("log: the file has no BOM", !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), null);

            folder = Temp("log2");
            AppLog.Start(folder, "test");
            AppLog.Write("crash", "a\r\nb\nc");
            Check("log: later lines of an entry are indented", File.ReadAllText(LogFile(folder)).EndsWith("crash: a\r\n    b\r\n    c\r\n"), null);

            folder = Temp("log3");
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0);
            AppLog.Clock = delegate { return now; };
            AppLog.Start(folder, "test");
            for (int i = 0; i < 5; i++) AppLog.Write("window", "same");
            AppLog.Write("window", "other");
            string[] lines = LogLines(LogFile(folder));
            Check("log: repeats are counted (entry, entry with count, other entry)",
                lines.Length == 3 && lines[0].EndsWith("window: same") && lines[1].EndsWith("window: same [repeated 4 more times]") && lines[2].EndsWith("window: other"), string.Join(" | ", lines));

            folder = Temp("log4");
            now = new DateTime(2026, 1, 1, 12, 0, 0);
            AppLog.Start(folder, "test");
            AppLog.Write("window", "same");
            now = now.AddSeconds(30);
            AppLog.Write("window", "same");
            now = now.AddSeconds(31);
            AppLog.Write("window", "same");
            lines = LogLines(LogFile(folder));
            Check("log: the repeat window is one minute", lines.Length == 3 && lines[1].EndsWith("[repeated 1 more time]") && lines[2].EndsWith("window: same"), string.Join(" | ", lines));

            folder = Temp("log5");
            AppLog.Start(folder, "test");
            AppLog.Write("window", "x");
            AppLog.Write("window", "x");
            AppLog.Flush();
            int afterFirst = LogLines(LogFile(folder)).Length;
            AppLog.Flush();
            Check("log: Flush writes a pending count once", afterFirst == 2 && LogLines(LogFile(folder)).Length == 2, afterFirst.ToString());

            AppLog.Clock = realClock;
            folder = Temp("log6");
            AppLog.Start(folder, "test");
            string first = null, last = null;
            for (int i = 0; i < 6000; i++)
            {
                last = i.ToString("D5") + new string('x', 95);
                if (first == null) first = last;
                AppLog.Write("size", last);
            }
            string current = LogFile(folder), older = Path.Combine(folder, "Logs", "helper.1.log");
            string[] currentLines = LogLines(current), olderLines = LogLines(older);
            bool firstGone = true;
            foreach (string line in currentLines) if (line.EndsWith(first)) firstGone = false;
            foreach (string line in olderLines) if (line.EndsWith(first)) firstGone = false;
            Check("log: size limit keeps helper.log and helper.1.log under 256 KB, newest entry last, oldest gone",
                new FileInfo(current).Length <= AppLog.MaxBytes && File.Exists(older) && new FileInfo(older).Length <= AppLog.MaxBytes &&
                currentLines[currentLines.Length - 1].EndsWith(last) && firstGone,
                new FileInfo(current).Length + " / " + new FileInfo(older).Length);

            folder = Temp("log7");
            AppLog.Start(folder, "test");
            AppLog.Write("long", new string('y', 20000));
            string longLine = LogLines(LogFile(folder))[0];
            Check("log: a 20,000 character entry is cut", longLine.EndsWith(" [cut]") && longLine.Length < 16100, longLine.Length.ToString());

            folder = Temp("log8");
            AppLog.Start(folder, "test");
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AppLog.Write("path", profile + "\\AppData\\x");
            string stored = File.ReadAllText(LogFile(folder));
            Check("log: the user's profile folder is replaced", stored.Contains("%USERPROFILE%\\AppData\\x") && stored.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0, null);

            string blocker = Path.Combine(Temp("log9"), "file");
            File.WriteAllText(blocker, "x");
            bool threw = false;
            try
            {
                AppLog.Start(Path.Combine(blocker, "sub"), "test");
                AppLog.Write("a", "b");
                AppLog.Flush();
            }
            catch (Exception) { threw = true; }
            Check("log: a folder that cannot be created leaves no path and never throws", !threw && AppLog.FilePath == null, null);

            folder = Temp("log10");
            RunLogProcesses(folder, 300);
            lines = LogLines(LogFile(folder));
            var seen = new HashSet<string>();
            bool formatOk = true, duplicate = false;
            var entry = new Regex(@"\[\d+ ([abcd])\] multi: ([abcd]) (\d+)$");
            foreach (string line in lines)
            {
                Match m = entry.Match(line);
                if (!Regex.IsMatch(line, LineFormat) || !m.Success || m.Groups[1].Value != m.Groups[2].Value) { formatOk = false; continue; }
                if (!seen.Add(m.Groups[2].Value + " " + m.Groups[3].Value)) duplicate = true;
            }
            Check("log: four processes writing at once lose and tear nothing (1200 entries, each once)", lines.Length == 1200 && seen.Count == 1200 && formatOk && !duplicate,
                lines.Length + " lines, " + seen.Count + " distinct");

            folder = Temp("log11");
            RunLogProcesses(folder, 4000);
            string[] now2 = LogLines(LogFile(folder)), old2 = LogLines(Path.Combine(folder, "Logs", "helper.1.log"));
            bool allMatch = true, anyDuplicate = false;
            var all = new HashSet<string>();
            foreach (string line in now2.Concat2(old2))
            {
                Match m = entry.Match(line);
                if (!Regex.IsMatch(line, LineFormat) || !m.Success || m.Groups[1].Value != m.Groups[2].Value) { allMatch = false; continue; }
                if (!all.Add(m.Groups[2].Value + " " + m.Groups[3].Value)) anyDuplicate = true;
            }
            long size = new FileInfo(LogFile(folder)).Length;
            Check("log: four processes with rotation keep every line whole, nothing twice, helper.log near its limit",
                allMatch && !anyDuplicate && File.Exists(Path.Combine(folder, "Logs", "helper.1.log")) && size <= AppLog.MaxBytes + 64 * 1024,
                now2.Length + " + " + old2.Length + " lines, helper.log " + size + " bytes");
        }
        finally { AppLog.Clock = realClock; }
    }

    private static IEnumerable<string> Concat2(this string[] a, string[] b)
    {
        foreach (string x in a) yield return x;
        foreach (string x in b) yield return x;
    }

    private static void QuietCases()
    {
        bool nothing = true;
        foreach (int state in new[] { 0, 1, 5, 6, 7 })
            foreach (bool full in new[] { false, true })
                foreach (bool app in new[] { false, true })
                    if (QuietTime.Reason(state, full, app) != null) nothing = false;
        Check("quiet: states 0, 1, 5, 6 and 7 never keep the animation away", nothing, null);
        bool always = true;
        foreach (int state in new[] { 3, 4 })
            foreach (bool full in new[] { false, true })
                foreach (bool app in new[] { false, true })
                    if (QuietTime.Reason(state, full, app) == null) always = false;
        Check("quiet: states 3 and 4 always do", always, null);
        Check("quiet: state 2 only counts for a full-screen window of another program",
            QuietTime.Reason(2, true, false) != null && QuietTime.Reason(2, true, true) == null &&
            QuietTime.Reason(2, false, false) == null && QuietTime.Reason(2, false, true) == null, null);
        Check("quiet: the three reason texts",
            QuietTime.Reason(3, false, false) == "Windows reports a full-screen game in front" &&
            QuietTime.Reason(4, false, false) == "Windows reports presentation mode" &&
            QuietTime.Reason(2, true, false) == "a full-screen window of another program is in front", null);
    }

    private static void CrashPolicyCases()
    {
        DateTime now = new DateTime(2026, 1, 1, 12, 0, 0);
        Check("crash: a normal start may restart", CrashPolicy.MayRestart(null, now), null);
        Check("crash: a restart 1 second ago may not", !CrashPolicy.MayRestart(now.AddSeconds(-1), now), null);
        Check("crash: a restart 9 min 59 s ago may not", !CrashPolicy.MayRestart(now.AddSeconds(-599), now), null);
        Check("crash: a restart exactly 10 minutes ago may", CrashPolicy.MayRestart(now.AddMinutes(-10), now), null);
        Check("crash: a restart 3 days ago may", CrashPolicy.MayRestart(now.AddDays(-3), now), null);
        Check("crash: a restart time 5 minutes in the future may", CrashPolicy.MayRestart(now.AddMinutes(5), now), null);
    }

    private static void WriteZip(string path, Dictionary<string, byte[]> entries)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var pair in entries)
            {
                var entry = zip.CreateEntry(pair.Key);
                using (var writer = new BinaryWriter(entry.Open())) writer.Write(pair.Value);
            }
    }

    // A zip entry of <size> zero bytes: tiny in the archive, huge once uncompressed.
    private static void WriteBigZipEntry(string path, string name, long size)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(name);
            using (var s = entry.Open())
            {
                var zeros = new byte[1024 * 1024];
                for (long left = size; left > 0; left -= zeros.Length)
                    s.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
            }
        }
    }

    private static bool SameBytes(string a, string b)
    {
        return File.Exists(a) && File.Exists(b) &&
            Convert.ToBase64String(File.ReadAllBytes(a)) == Convert.ToBase64String(File.ReadAllBytes(b));
    }

    private static void PackCases()
    {
        string work = Temp("pack");
        ThemeLibrary.RootOverride = Path.Combine(work, "Themes");
        try
        {
            // A theme with a wallpaper, an avatar and a credit line.
            string folder = ThemeLibrary.Folder("Harbor");
            Directory.CreateDirectory(folder);
            WriteJpeg(Path.Combine(folder, "wallpaper.jpg"), Color.Teal);
            WriteJpeg(Path.Combine(folder, "avatar.jpg"), Color.Orange);
            var themeIni = new IniFile();
            themeIni.Set("opacity", "27");
            themeIni.Set("title", "HARBOR");
            themeIni.Set("credit", "art by Somebody, https://example.invalid/x");
            themeIni.Set("themeOnlyKey", "travels");
            themeIni.Save(Path.Combine(folder, "theme.ini"));
            string simpler = ThemeLibrary.Folder("Plain");
            Directory.CreateDirectory(simpler);
            WriteJpeg(Path.Combine(simpler, "wallpaper.jpg"), Color.Gray);

            string pack = Path.Combine(work, "harbor.altheme");
            ThemePack.Export("Harbor", pack);
            Check("export: the file is a zip", File.ReadAllBytes(pack).Length > 4 &&
                File.ReadAllBytes(pack)[0] == 'P' && File.ReadAllBytes(pack)[1] == 'K', null);
            var inside = new List<string>();
            using (var zip = ZipFile.OpenRead(pack))
                foreach (ZipArchiveEntry e in zip.Entries) inside.Add(e.FullName);
            inside.Sort();
            Check("export: manifest, theme.ini, wallpaper and avatar only",
                string.Join("|", inside.ToArray()) == "avatar.jpg|manifest.ini|theme.ini|wallpaper.jpg", string.Join("|", inside.ToArray()));

            byte[] wallBefore = File.ReadAllBytes(Path.Combine(folder, "wallpaper.jpg"));
            byte[] avatarBefore = File.ReadAllBytes(Path.Combine(folder, "avatar.jpg"));
            byte[] iniBefore = File.ReadAllBytes(Path.Combine(folder, "theme.ini"));
            Directory.Delete(folder, true);

            string imported = ThemePack.Import(pack);
            string newFolder = ThemeLibrary.Folder(imported);
            Check("import: a theme file restores under its manifest name", imported == "Harbor", imported);
            Check("import: wallpaper.jpg is byte-identical",
                Convert.ToBase64String(File.ReadAllBytes(Path.Combine(newFolder, "wallpaper.jpg"))) == Convert.ToBase64String(wallBefore), null);
            Check("import: avatar.jpg is byte-identical",
                Convert.ToBase64String(File.ReadAllBytes(Path.Combine(newFolder, "avatar.jpg"))) == Convert.ToBase64String(avatarBefore), null);
            Check("import: theme.ini is byte-identical, credit and unknown keys survive",
                Convert.ToBase64String(File.ReadAllBytes(Path.Combine(newFolder, "theme.ini"))) == Convert.ToBase64String(iniBefore) &&
                SplashText.Read(IniFile.Load(Path.Combine(newFolder, "theme.ini"))).Credit == "art by Somebody, https://example.invalid/x", null);
            Check("import: importing the same file again dedupes the name", ThemePack.Import(pack) == "Harbor 2", null);

            // The name comes from the manifest, not the file name.
            string renamedPack = Path.Combine(work, "something-else.altheme");
            File.Copy(pack, renamedPack);
            string manifestImport = ThemePack.Import(renamedPack);
            Check("import: the manifest name wins over the file name", manifestImport.StartsWith("Harbor"), manifestImport);

            bool refused = false;
            string notZip = Path.Combine(work, "notes.altheme");
            File.WriteAllText(notZip, "definitely not a zip");
            try { ThemePack.Import(notZip); } catch (InvalidDataException) { refused = true; }
            Check("import: a file that is not a zip is refused", refused && ThemeLibrary.Names().Length == 4, null);

            refused = false;
            string noWall = Path.Combine(work, "nowall.altheme");
            WriteZip(noWall, new Dictionary<string, byte[]> { { "theme.ini", iniBefore } });
            try { ThemePack.Import(noWall); } catch (InvalidDataException) { refused = true; }
            Check("import: a zip without wallpaper.jpg is refused", refused && !Directory.Exists(ThemeLibrary.Folder("nowall")), null);

            refused = false;
            string damaged = Path.Combine(work, "damaged.altheme");
            var damagedWall = new byte[200];
            new Random(3).NextBytes(damagedWall);
            WriteZip(damaged, new Dictionary<string, byte[]> { { "wallpaper.jpg", damagedWall } });
            try { ThemePack.Import(damaged); } catch (InvalidDataException) { refused = true; }
            Check("import: an image that cannot be decoded is refused and leaves no folder",
                refused && ThemeLibrary.Names().Length == 4, null);

            refused = false;
            string huge = Path.Combine(work, "huge.altheme");
            WriteBigZipEntry(huge, "wallpaper.jpg", 129L * 1024 * 1024);
            try { ThemePack.Import(huge); } catch (InvalidDataException) { refused = true; }
            Check("import: an entry over 128 MiB is refused before it is read", refused, null);

            string hostile = Path.Combine(work, "hostile.altheme");
            WriteZip(hostile, new Dictionary<string, byte[]> {
                { "wallpaper.jpg", wallBefore },
                { "../evil.txt", new byte[] { 65 } },
                { "sub/evil.exe", new byte[] { 66 } },
                { "wallpaper.jpg.exe", new byte[] { 67 } },
                { "manifest.ini", new UTF8Encoding(false).GetBytes("name=Hostile Take\r\n") }
            });
            string hostileName = ThemePack.Import(hostile);
            string[] landed = Directory.GetFiles(ThemeLibrary.Folder(hostileName));
            Check("import: only the known files land, hostile entries are ignored",
                hostileName == "Hostile Take" && landed.Length == 1 && Path.GetFileName(landed[0]) == "wallpaper.jpg" &&
                !File.Exists(Path.Combine(work, "evil.txt")) && !File.Exists(Path.Combine(ThemeLibrary.Root, "..", "evil.txt")),
                hostileName + " -> " + landed.Length + " file(s)");

            string dup = ThemeLibrary.Duplicate("Harbor");
            string dup2 = ThemeLibrary.Duplicate("Harbor");
            Check("duplicate: X becomes X copy then X copy 2", dup == "Harbor copy" && dup2 == "Harbor copy 2", dup + " | " + dup2);
            Check("duplicate: files are identical",
                SameBytes(Path.Combine(ThemeLibrary.Folder(dup), "wallpaper.jpg"), Path.Combine(ThemeLibrary.Folder("Harbor"), "wallpaper.jpg")) &&
                SameBytes(Path.Combine(ThemeLibrary.Folder(dup), "theme.ini"), Path.Combine(ThemeLibrary.Folder("Harbor"), "theme.ini")), null);

            int before = ThemeLibrary.Names().Length;
            string backup = Path.Combine(work, "library.albackup");
            ThemePack.Backup(backup);
            foreach (string name in ThemeLibrary.Names()) Directory.Delete(ThemeLibrary.Folder(name), true);
            int restored = ThemePack.Restore(backup);
            Check("backup: restores every theme", restored == before && ThemeLibrary.Names().Length == before,
                restored + " of " + before);
            Check("backup: restored files are byte-identical",
                Convert.ToBase64String(File.ReadAllBytes(Path.Combine(ThemeLibrary.Folder("Harbor"), "wallpaper.jpg"))) == Convert.ToBase64String(wallBefore) &&
                SplashText.Read(IniFile.Load(Path.Combine(ThemeLibrary.Folder("Harbor"), "theme.ini"))).Credit.StartsWith("art by"), null);
        }
        finally { ThemeLibrary.RootOverride = null; }
    }

    private static int Sheets()
    {
        string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
        foreach (string name in ThemeLibrary.Names())
        {
            string folder = ThemeLibrary.Folder(name);
            Look look = Look.Read(IniFile.Load(Path.Combine(folder, "theme.ini")));
            using (Bitmap wallpaper = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg"))))
            using (Bitmap styled = ArtworkLook.Render(wallpaper, look))
            using (var sheet = new Bitmap(1280, 400, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(0x1E, 0x1E, 0x1E));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                RectangleF crop = ArtworkLook.CoverSource(styled.Size, new Size(640, 400), look.FocusX, look.FocusY);
                g.DrawImage(styled, new Rectangle(0, 0, 640, 400), crop, GraphicsUnit.Pixel);
                using (var attrs = new ImageAttributes())
                {
                    var matrix = new ColorMatrix();
                    matrix.Matrix33 = look.Alpha / 255f;
                    attrs.SetColorMatrix(matrix);
                    g.DrawImage(styled, new Rectangle(640, 0, 640, 400), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attrs);
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var font = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(0xD0, 0xD0, 0xD0)))
                {
                    string[] lines = {
                        name + "  (opacity " + look.Opacity + "%, blur " + look.Blur + ", dim " + look.Dim + ", tint " + look.Tint + ")",
                        "PS C:\\project> git status",
                        "On branch main. Your branch is up to date with origin/main.",
                        "  modified:   source/ArtworkLook.cs",
                        "  modified:   source/ThemeSettings.cs",
                        "The quick brown fox jumps over the lazy dog 0123456789"
                    };
                    for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], font, brush, 660, 24 + i * 22);
                }
                string file = Path.Combine(output, "sheet-" + name + ".png");
                sheet.Save(file, ImageFormat.Png);
                Console.WriteLine(file);
            }
        }
        return 0;
    }
}

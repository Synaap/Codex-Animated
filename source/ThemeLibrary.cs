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

internal static class ThemeLibrary
{
    internal static string RootOverride = null;

    // Test switch: a scratch root instead of LocalApplicationData for every app and
    // shared folder (settings.ini, wallpaper.jpg, logs, the library, studio.ini).
    internal static string DataRoot
    {
        get
        {
            string forced = Environment.GetEnvironmentVariable("ANIMATED_DATA");
            if (!string.IsNullOrEmpty(forced)) return forced;
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    internal static string SharedRoot
    {
        get { return Path.Combine(DataRoot, "Animated Launchers"); }
    }

    // Cursor packs: Cursors\<pack>\<pointer>.png — image-based mouse pointers,
    // seeded by the installer and grown by the studio's upload command.
    internal static string CursorsRoot
    {
        get { return Path.Combine(SharedRoot, "Cursors"); }
    }

    private static readonly System.Text.RegularExpressions.Regex CursorId =
        new System.Text.RegularExpressions.Regex("^[a-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static bool ValidCursorId(string id) { return id != null && CursorId.IsMatch(id); }

    // "<clean id>": lowercases, strips everything outside [a-z0-9_-].
    internal static string CursorClean(string raw)
    {
        var chars = new StringBuilder();
        foreach (char ch in (raw ?? "").ToLowerInvariant())
            chars.Append((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' ? ch : '-');
        string id = chars.ToString().Trim('-');
        while (id.Contains("--")) id = id.Replace("--", "-");
        if (id.Length == 0) return "pointer";
        // Windows reserved device names (con.png would hit the console).
        if (System.Text.RegularExpressions.Regex.IsMatch(id, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$")) id += "-x";
        return id;
    }

    // pack -> sorted pointer ids (file stems) for every pack on disk.
    internal static SortedDictionary<string, List<string>> CursorPacks()
    {
        var packs = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string dir in Directory.GetDirectories(CursorsRoot))
            {
                string pack = Path.GetFileName(dir);
                if (!ValidCursorId(pack)) continue;
                var files = new List<string>();
                foreach (string png in Directory.GetFiles(dir, "*.png"))
                {
                    string stem = Path.GetFileNameWithoutExtension(png);
                    if (ValidCursorId(stem)) files.Add(stem);
                }
                if (files.Count > 0) { files.Sort(StringComparer.Ordinal); packs[pack] = files; }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return packs;
    }

    // The PNG path behind an "img:<pack>/<file>" style, or null when invalid/absent.
    internal static string CursorImage(string style)
    {
        if (string.IsNullOrEmpty(style) || !style.StartsWith("img:")) return null;
        string[] parts = style.Substring(4).Split('/');
        if (parts.Length != 2 || !ValidCursorId(parts[0]) || !ValidCursorId(parts[1])) return null;
        string path = Path.Combine(CursorsRoot, parts[0], parts[1] + ".png");
        return File.Exists(path) ? path : null;
    }

    internal static string Root
    {
        get
        {
            if (RootOverride != null) return RootOverride;
            // Test switch: redirect the whole theme library to a scratch folder.
            string forced = Environment.GetEnvironmentVariable("ANIMATED_THEMES");
            if (!string.IsNullOrEmpty(forced)) return forced;
            return Path.Combine(SharedRoot, "Themes");
        }
    }

    internal static string[] Names()
    {
        var names = new List<string>();
        try
        {
            foreach (string directory in Directory.GetDirectories(Root))
                if (File.Exists(Path.Combine(directory, "wallpaper.jpg"))) names.Add(Path.GetFileName(directory));
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    internal static string Folder(string name) { return Path.Combine(Root, name); }

    // Copies the theme's images into the app folder, then replaces the app's look
    // and splash text with the theme's. Images first, settings.ini last.
    internal static void Apply(string name, string appRoot)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The theme name is not valid.");
        string folder = Folder(name);
        string wallpaper = Path.Combine(folder, "wallpaper.jpg");
        if (!File.Exists(wallpaper)) throw new FileNotFoundException("The theme \"" + name + "\" has no wallpaper.", wallpaper);
        WithLock(appRoot, delegate {
            ImageCodec.SaveAtomic(Path.Combine(appRoot, "wallpaper.jpg"), ImageCodec.ReadBytes(wallpaper));
            // An animated wallpaper travels with the theme; a still theme
            // removes any gif the app folder was left with.
            string gif = Path.Combine(folder, "wallpaper.gif");
            string appGif = Path.Combine(appRoot, "wallpaper.gif");
            if (File.Exists(gif)) ImageCodec.SaveAtomic(appGif, ImageCodec.ReadBytes(gif));
            else File.Delete(appGif);
            string apng = Path.Combine(folder, "wallpaper.apng");
            string appApng = Path.Combine(appRoot, "wallpaper.apng");
            if (File.Exists(apng)) ImageCodec.SaveAtomic(appApng, ImageCodec.ReadBytes(apng));
            else File.Delete(appApng);
            // A slides\ folder of stills travels with the theme the same way;
            // a still theme removes any slides the app folder was left with.
            string slides = Path.Combine(folder, "slides");
            string appSlides = Path.Combine(appRoot, "slides");
            if (Directory.Exists(appSlides)) Directory.Delete(appSlides, true);
            if (Directory.Exists(slides)) CopyFolder(slides, appSlides);
            // An optional startup chime travels with the theme the same way.
            string chime = Path.Combine(folder, "sound.wav");
            string appChime = Path.Combine(appRoot, "sound.wav");
            if (File.Exists(chime)) ImageCodec.SaveAtomic(appChime, ImageCodec.ReadBytes(chime));
            else File.Delete(appChime);
            string avatar = Path.Combine(folder, "avatar.jpg");
            string appAvatar = Path.Combine(appRoot, "avatar.jpg");
            if (File.Exists(avatar)) ImageCodec.SaveAtomic(appAvatar, ImageCodec.ReadBytes(avatar));
            else File.Delete(appAvatar);
            IniFile theme = IniFile.Load(Path.Combine(folder, "theme.ini"));
            AppProfile profile = AppProfile.Load(appRoot);
            profile.Look = Look.Read(theme);
            profile.Text = SplashText.Read(theme);
            // A theme that picks an animation style changes the app's effective
            // style; a theme without one leaves the app's own default alone.
            if (profile.Text.Style.Length > 0) profile.Style = profile.Text.Style;
            profile.Theme = name;
            profile.Save();
        });
    }

    // Removes a theme from an app: the copied files go, every slot that
    // referenced the theme clears, and the artwork switches off — removing a
    // theme means no wallpaper at all, not the bundled fallback.
    internal static void Clear(string name, string appRoot)
    {
        WithLock(appRoot, delegate {
            File.Delete(Path.Combine(appRoot, "wallpaper.jpg"));
            File.Delete(Path.Combine(appRoot, "wallpaper.gif"));
            File.Delete(Path.Combine(appRoot, "wallpaper.apng"));
            File.Delete(Path.Combine(appRoot, "avatar.jpg"));
            File.Delete(Path.Combine(appRoot, "sound.wav"));
            string slides = Path.Combine(appRoot, "slides");
            if (Directory.Exists(slides)) Directory.Delete(slides, true);
            AppProfile profile = AppProfile.Load(appRoot);
            if (string.Equals(profile.Theme, name, StringComparison.OrdinalIgnoreCase)) profile.Theme = "";
            if (string.Equals(profile.Applied, name, StringComparison.OrdinalIgnoreCase)) profile.Applied = "";
            if (string.Equals(profile.ThemeLight, name, StringComparison.OrdinalIgnoreCase)) profile.ThemeLight = "";
            if (string.Equals(profile.ThemeNight, name, StringComparison.OrdinalIgnoreCase)) profile.ThemeNight = "";
            profile.Watch = false;
            profile.Save();
        });
    }

    // True inside the night window nightStart..nightUntil; wraps midnight.
    // start == until means "no night window".
    internal static bool IsNight(int start, int until, int hour)
    {
        if (start == until) return false;
        if (start < until) return hour >= start && hour < until;
        return hour >= start || hour < until;
    }

    // The one slot-aware write path used by the session, the tray and the studio.
    // slot is "theme" (the default pick), "themeLight" (the Windows-light pick) or
    // "themeNight" (the night-window pick); variant slots keep theme= as the user's
    // base pick. applied= always records which theme's files are actually in the
    // folder, so the mode watcher can tell a variant apart from the dark pick.
    internal static void ApplyForMode(string name, string appRoot, string slot)
    {
        WithLock(appRoot, delegate {
            AppProfile before = AppProfile.Load(appRoot);
            Apply(name, appRoot);
            AppProfile after = AppProfile.Load(appRoot);
            after.Applied = name;
            if (slot == "themeLight")
            {
                after.ThemeLight = name;
                if (!string.Equals(name, before.Theme, StringComparison.OrdinalIgnoreCase))
                    after.Theme = before.Theme;
            }
            else if (slot == "themeNight")
            {
                after.ThemeNight = name;
                if (!string.Equals(name, before.Theme, StringComparison.OrdinalIgnoreCase))
                    after.Theme = before.Theme;
            }
            after.Save();
        });
    }

    private static readonly string[] ReservedNames = {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // The trimmed name, or an ArgumentException saying what to change.
    internal static string ValidateName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a name.");
        if (name.Length > 60) throw new ArgumentException("Use 60 characters or fewer.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A name can't contain \\ / : * ? \" < > |.");
        if (name.EndsWith(".", StringComparison.Ordinal)) throw new ArgumentException("A name can't end with a dot.");
        string stem = name.Split('.')[0].TrimEnd();
        if (Array.IndexOf(ReservedNames, stem.ToUpperInvariant()) >= 0)
            throw new ArgumentException("\"" + stem + "\" is reserved by Windows. Use another name.");
        return name;
    }

    // The library folder name that equals the given text exactly, or null.
    internal static string Exact(string name)
    {
        if (name == null) return null;
        foreach (string existing in Names()) if (string.Equals(existing, name, StringComparison.Ordinal)) return existing;
        return null;
    }

    private static bool SameName(string a, string b)
    {
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // True when any of the app's picks (theme, themeLight, themeNight) or the files
    // it shows (applied) name the theme.
    internal static bool ReferencesTheme(string root, string name)
    {
        AppProfile p = AppProfile.Load(root);
        return SameName(p.Theme, name) || SameName(p.ThemeLight, name) || SameName(p.ThemeNight, name) || SameName(p.Applied, name);
    }

    // The slot ("theme", "themeNight", "themeLight") whose theme's files are in the
    // app folder right now when that theme is `name`, else null. Edits go only to
    // apps that show the theme, through the slot it holds there: editing the base
    // pick at night must not paste it over the night pick. applied= says what is
    // showing; folders filled before applied= existed fall back to theme=.
    internal static string ShowingSlot(string root, string name)
    {
        AppProfile p = AppProfile.Load(root);
        string shown = p.Applied.Length > 0 ? p.Applied : p.Theme;
        if (!SameName(shown, name)) return null;
        if (SameName(p.Theme, name)) return "theme";
        if (SameName(p.ThemeNight, name)) return "themeNight";
        if (SameName(p.ThemeLight, name)) return "themeLight";
        return null;
    }

    // A copy of the theme under a free name ("X copy", then "X copy 2"). Apps stay on the original.
    internal static string Duplicate(string name)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The theme \"" + name + "\" no longer exists.");
        string baseName = exact + " copy", target = baseName;
        for (int n = 2; Directory.Exists(Folder(target)); n++)
        {
            string suffix = " " + n.ToString(CultureInfo.InvariantCulture);
            target = (baseName.Length + suffix.Length > 60 ? baseName.Substring(0, 60 - suffix.Length).TrimEnd() : baseName) + suffix;
        }
        CopyFolder(Folder(exact), Folder(target));
        return target;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(source))
            CopyFolder(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    // Writes theme.ini (clamped, accent made legible) and copies look and text into every app on this theme.
    internal static Look Save(string name, Look look, SplashText text, IEnumerable<string> appRoots, out SplashText stored)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The theme \"" + name + "\" no longer exists.");
        string path = Path.Combine(Folder(exact), "theme.ini");
        Look clamped = null;
        SplashText normalized = null;
        WithLock(Folder(exact), delegate {
            IniFile ini = IniFile.LoadStrict(path);
            look.Write(ini);
            clamped = Look.Read(ini);
            clamped.Write(ini);
            text.Write(ini);
            normalized = SplashText.Read(ini);
            Color accent;
            if (SplashText.TryParseColor(normalized.Accent, out accent))
            {
                Color legible = ArtworkLook.Legible(accent);
                normalized.Accent = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", legible.R, legible.G, legible.B);
            }
            normalized.Write(ini);
            ini.Save(path);
        });
        foreach (string root in appRoots)
        {
            if (ShowingSlot(root, exact) == null) continue;
            AppProfile profile = AppProfile.LoadStrict(root);
            profile.Look = clamped;
            profile.Text = normalized;
            if (normalized.Style.Length > 0) profile.Style = normalized.Style;
            profile.Save();
        }
        stored = normalized;
        return clamped;
    }

    // Renames the folder (a case-only change goes through a temporary name) and fixes theme= in apps.
    internal static string Rename(string name, string newName, IEnumerable<string> appRoots)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The theme \"" + name + "\" no longer exists.");
        newName = ValidateName(newName);
        if (string.Equals(exact, newName, StringComparison.Ordinal)) return exact;
        bool caseOnly = string.Equals(exact, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Directory.Exists(Folder(newName)))
            throw new IOException("A theme called \"" + newName + "\" already exists.");
        if (caseOnly)
        {
            string temporary = Folder(exact) + ".rename-" + Guid.NewGuid().ToString("N");
            Directory.Move(Folder(exact), temporary);
            Directory.Move(temporary, Folder(newName));
        }
        else Directory.Move(Folder(exact), Folder(newName));
        foreach (string root in appRoots)
        {
            if (!ReferencesTheme(root, exact)) continue;
            AppProfile profile = AppProfile.LoadStrict(root);
            if (SameName(profile.Theme, exact)) profile.Theme = newName;
            if (SameName(profile.ThemeLight, exact)) profile.ThemeLight = newName;
            if (SameName(profile.ThemeNight, exact)) profile.ThemeNight = newName;
            if (SameName(profile.Applied, exact)) profile.Applied = newName;
            profile.Save();
        }
        RenameFavorite(exact, newName);
        return newName;
    }

    // A renamed favorite keeps its star (and its place in favorites-only rotation):
    // the studio.ini list is matched by exact name, so it follows the rename.
    private static void RenameFavorite(string oldName, string newName)
    {
        string path = Path.Combine(SharedRoot, "studio.ini");
        if (!File.Exists(path)) return;
        WithLock(SharedRoot, delegate {
            IniFile ini = IniFile.Load(path);
            string raw = ini.Get("favorites");
            if (string.IsNullOrEmpty(raw)) return;
            string[] parts = raw.Split('|');
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
                if (string.Equals(parts[i].Trim(), oldName, StringComparison.Ordinal)) { parts[i] = newName; changed = true; }
            if (!changed) return;
            ini.Set("favorites", string.Join("|", parts));
            ini.Save(path);
        });
    }

    // Imports a picture as a new theme named after the file; returns the folder name.
    internal static string Create(string imagePath)
    {
        byte[] bytes = ImageCodec.ReadBytes(imagePath);
        if (ImageCodec.IsGif(bytes))
            return CreateFromJpeg(ImageCodec.GifToJpeg(bytes),
                Path.GetFileNameWithoutExtension(imagePath), bytes, null);
        if (ImageCodec.IsApng(bytes))
            return CreateFromJpeg(ImageCodec.ApngToJpeg(bytes),
                Path.GetFileNameWithoutExtension(imagePath), null, bytes);
        return CreateFromJpeg(ImageCodec.ToJpeg(bytes),
            Path.GetFileNameWithoutExtension(imagePath));
    }

    // The same create for jpeg bytes already in memory (paste, a rendered gradient).
    // baseName names the theme; " 2", " 3" dedupes apply as usual.
    internal static string CreateFromJpeg(byte[] jpeg, string baseName)
    {
        return CreateFromJpeg(jpeg, baseName, null, null);
    }

    internal static string CreateFromJpeg(byte[] jpeg, string baseName, byte[] gif)
    {
        return CreateFromJpeg(jpeg, baseName, gif, null);
    }

    internal static string CreateFromJpeg(byte[] jpeg, string baseName, byte[] gif, byte[] apng)
    {
        Color suggestion = Color.Empty;
        using (Bitmap decoded = ImageCodec.DecodeJpeg(jpeg))
        {
            Color[] accents = ArtworkLook.SuggestAccents(decoded, 1);
            if (accents.Length > 0) suggestion = accents[0];
        }
        baseName = CleanName(baseName);
        string name = baseName;
        for (int n = 2; Directory.Exists(Folder(name)); n++)
        {
            string suffix = " " + n.ToString(CultureInfo.InvariantCulture);
            name = (baseName.Length + suffix.Length > 60 ? baseName.Substring(0, 60 - suffix.Length).TrimEnd() : baseName) + suffix;
        }
        string folder = Folder(name);
        try
        {
            Directory.CreateDirectory(folder);
            ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.jpg"), jpeg);
            if (gif != null) ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.gif"), gif);
            if (apng != null) ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.apng"), apng);
            var ini = new IniFile();
            new Look().Write(ini);
            var text = new SplashText();
            string title = name.ToUpperInvariant();
            text.Title = title.Length > 40 ? title.Substring(0, 40) : title;
            Color accent = suggestion.IsEmpty ? Color.FromArgb(0xD9, 0x80, 0xA7) : ArtworkLook.Legible(suggestion);
            text.Accent = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", accent.R, accent.G, accent.B);
            text.Write(ini);
            ini.Save(Path.Combine(folder, "theme.ini"));
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return name;
    }

    private static string CleanName(string raw)
    {
        var chars = new StringBuilder();
        foreach (char c in raw ?? "")
            chars.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c);
        string name = string.Join(" ", chars.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        if (name.Length > 60) name = name.Substring(0, 60).TrimEnd('.', ' ');
        try { return ValidateName(name); }
        catch (ArgumentException) { return "New theme"; }
    }

    // "gradient=#RRGGBB|#RRGGBB|degrees" records that the wallpaper was generated,
    // so the studio can offer "Edit gradient" instead of treating it as a photo.
    internal static string GradientOf(string name)
    {
        string exact = Exact(name);
        if (exact == null) return "";
        return IniFile.Load(Path.Combine(Folder(exact), "theme.ini")).Get("gradient") ?? "";
    }

    internal static void SetGradient(string name, GradientSpec spec)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The theme \"" + name + "\" no longer exists.");
        string path = Path.Combine(Folder(exact), "theme.ini");
        WithLock(Folder(exact), delegate {
            IniFile ini = IniFile.LoadStrict(path);
            ini.Set("gradient", spec.Format());
            ini.Save(path);
        });
    }

    // Empties every pick (theme=, themeLight=, themeNight=) that named the theme;
    // the apps' own copies keep working and a cleared variant falls back to theme=.
    internal static void ClearReferences(string name, IEnumerable<string> appRoots)
    {
        foreach (string root in appRoots)
        {
            if (!ReferencesTheme(root, name)) continue;
            AppProfile profile = AppProfile.LoadStrict(root);
            if (SameName(profile.Theme, name)) profile.Theme = "";
            if (SameName(profile.ThemeLight, name)) profile.ThemeLight = "";
            if (SameName(profile.ThemeNight, name)) profile.ThemeNight = "";
            profile.Save();
        }
    }

    // The studio's favorites list, pipe-joined in studio.ini under the shared root.
    internal static string[] Favorites()
    {
        string raw = IniFile.Load(Path.Combine(SharedRoot, "studio.ini")).Get("favorites") ?? "";
        var names = new List<string>();
        foreach (string part in raw.Split('|'))
        {
            string name = part.Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names.ToArray();
    }

    // The themes rotation may pick: the whole library, or only the starred ones
    // (a favorite whose folder disappeared simply drops out of the pool).
    internal static string[] RotationPool(bool favoritesOnly)
    {
        string[] names = Names();
        if (!favoritesOnly) return names;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Favorites()) keep.Add(name);
        var pool = new List<string>();
        foreach (string name in names) if (keep.Contains(name)) pool.Add(name);
        return pool.ToArray();
    }

    // The tray's More/Less visible: rewrite opacity= by the delta, clamped; the
    // session's settings watcher repaints it.
    internal static void NudgeOpacity(string appRoot, int delta)
    {
        WithLock(appRoot, delegate {
            string path = Path.Combine(appRoot, AppProfile.FileName);
            IniFile ini = IniFile.LoadStrict(path);
            int opacity = Look.Number(ini, "opacity", 15, 4, 40);
            int next = Math.Max(4, Math.Min(40, opacity + delta));
            if (next == opacity) return;
            ini.Set("opacity", next.ToString(CultureInfo.InvariantCulture));
            ini.Save(path);
        });
    }

    // The theme after `current` in the pool, wrap-around; the first entry when the
    // current one is not in the pool (and "" when the pool is empty).
    internal static string NextInPool(string[] pool, string current)
    {
        if (pool == null || pool.Length == 0) return "";
        for (int i = 0; i < pool.Length; i++)
            if (string.Equals(pool[i], current, StringComparison.OrdinalIgnoreCase))
                return pool[(i + 1) % pool.Length];
        return pool[0];
    }

    // The starter pack: license-clean wallpapers rendered in-process plus two pure
    // gradient recipes. One table drives seeding and the welcome card's previews.
    private struct Starter { internal string Name; internal Func<byte[]> Jpeg; internal string Gradient; }

    private static Starter[] Starters()
    {
        return new[]
        {
            new Starter { Name = "Aurora fields", Jpeg = StarterArt.Aurora },
            new Starter { Name = "Paper dusk", Jpeg = StarterArt.PaperDusk },
            new Starter { Name = "Night garden", Jpeg = StarterArt.NightGarden },
            new Starter { Name = "Sakura rain", Jpeg = StarterArt.SakuraRain },
            new Starter { Name = "Deep sea", Gradient = "#062E4F|#0A7C8C|160" },
            new Starter { Name = "Sunset", Gradient = "#4A1D6B|#E8556E|200" },
        };
    }

    // Creates only names not already in the library (never overwrites a user
    // theme) and returns the names it created.
    internal static string[] SeedStarters()
    {
        var created = new List<string>();
        WithLock(Root, delegate {
            Directory.CreateDirectory(Root);
            foreach (Starter starter in Starters())
            {
                if (Exact(starter.Name) != null) continue;
                byte[] jpeg;
                GradientSpec gradient = new GradientSpec();
                bool hasGradient = starter.Gradient != null && GradientSpec.TryParse(starter.Gradient, out gradient);
                jpeg = hasGradient ? gradient.RenderJpeg() : starter.Jpeg();
                if (jpeg == null) continue;
                string made = CreateFromJpeg(jpeg, starter.Name);
                if (hasGradient) SetGradient(made, gradient);
                string path = Path.Combine(Folder(made), "theme.ini");
                IniFile ini = IniFile.LoadStrict(path);
                ini.Set("credit", "Generated by Animated Launchers");
                ini.Save(path);
                created.Add(made);
            }
        });
        return created.ToArray();
    }

    // 400px-wide thumbnails for the welcome card: (name, jpeg) for every starter.
    // Already-present names still preview — the card explains seeding skips them.
    internal static List<KeyValuePair<string, byte[]>> StarterThumbs()
    {
        var thumbs = new List<KeyValuePair<string, byte[]>>();
        foreach (Starter starter in Starters())
        {
            byte[] jpeg;
            GradientSpec gradient = new GradientSpec();
            if (starter.Gradient != null && GradientSpec.TryParse(starter.Gradient, out gradient)) jpeg = gradient.RenderJpeg();
            else jpeg = starter.Jpeg();
            if (jpeg == null) continue;
            using (Bitmap decoded = ImageCodec.DecodeJpeg(jpeg))
            using (var thumb = new Bitmap(decoded, new Size(400, 225)))
            {
                thumbs.Add(new KeyValuePair<string, byte[]>(starter.Name, ImageCodec.EncodeJpeg(thumb)));
            }
        }
        return thumbs;
    }

    // Wallpaper and avatar write times and sizes combined; changes whenever either image does.
    internal static string Stamp(string name)
    {
        long value = 17;
        foreach (string file in new[] { "wallpaper.jpg", "wallpaper.gif", "wallpaper.apng", "avatar.jpg", "sound.wav" })
        {
            var info = new FileInfo(Path.Combine(Folder(name), file));
            value = value * 31 + (info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0);
        }
        string slides = Path.Combine(Folder(name), "slides");
        if (Directory.Exists(slides))
            foreach (FileInfo file in new DirectoryInfo(slides).GetFiles())
                value = value * 31 + (file.LastWriteTimeUtc.Ticks ^ file.Length);
        return (value & long.MaxValue).ToString("x", CultureInfo.InvariantCulture);
    }

    // Stores image bytes as a theme's wallpaper. An animated GIF keeps its file
    // and gets a first-frame wallpaper.jpg for still paths; any other image
    // becomes the JPEG and clears a gif left over from an earlier theme.
    internal static void SaveWallpaper(string name, byte[] bytes)
    {
        string folder = Folder(name);
        WithLock(folder, delegate {
            string gif = Path.Combine(folder, "wallpaper.gif");
            string apng = Path.Combine(folder, "wallpaper.apng");
            if (ImageCodec.IsGif(bytes))
            {
                ImageCodec.SaveAtomic(gif, bytes);
                File.Delete(apng);
                ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.jpg"), ImageCodec.GifToJpeg(bytes));
            }
            else if (ImageCodec.IsApng(bytes))
            {
                ImageCodec.SaveAtomic(apng, bytes);
                File.Delete(gif);
                ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.jpg"), ImageCodec.ApngToJpeg(bytes));
            }
            else
            {
                ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.jpg"), ImageCodec.ToJpeg(bytes));
                File.Delete(gif);
                File.Delete(apng);
            }
        });
    }

    // The ordered slide filenames of a theme's slides\ folder (same extension
    // filter and ordinal sort as ImageCodec.LoadSlides). Empty when none.
    internal static List<string> SlideFiles(string folder)
    {
        var list = new List<string>();
        string dir = Path.Combine(folder, "slides");
        if (Directory.Exists(dir))
            foreach (string path in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(path);
                if (string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase))
                    list.Add(Path.GetFileName(path));
            }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // Stores a validated WAV as the theme's startup chime (or removes it).
    internal static void SetChime(string name, byte[] wav)
    {
        string folder = Folder(ValidateName(name));
        bool wave = wav != null && wav.Length > 12 &&
            wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F' &&
            wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E';
        if (!wave) throw new InvalidDataException("Pick a .wav sound file.");
        WithLock(folder, delegate { ImageCodec.SaveAtomic(Path.Combine(folder, "sound.wav"), wav); });
    }

    internal static void ClearChime(string name)
    {
        string folder = Folder(ValidateName(name));
        WithLock(folder, delegate { File.Delete(Path.Combine(folder, "sound.wav")); });
    }

    // Three tiny built-in chimes generated as 16-bit mono PCM WAVs, so a theme
    // can get a boot sound without hunting for audio files.
    internal static byte[] ChimeWav(string kind)
    {
        const int rate = 22050;
        short[] pcm;
        switch (kind)
        {
            case "blip":
            {
                int n = rate / 3;
                pcm = new short[n];
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double f = t < 0.12 ? 523.25 : 783.99;
                    double env = t < 0.02 ? t / 0.02 : Math.Exp(-(t - 0.02) * 14);
                    pcm[i] = (short)(Math.Sin(2 * Math.PI * f * t) * env * 16000);
                }
                break;
            }
            case "whoosh":
            {
                int n = rate / 2;
                pcm = new short[n];
                var rnd = new Random(7);
                double lp = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / (n - 1);
                    double env = Math.Sin(Math.PI * t);
                    lp += 0.08 * ((rnd.NextDouble() * 2 - 1) - lp);
                    pcm[i] = (short)(lp * env * env * 20000);
                }
                break;
            }
            default:
            {
                int n = (int)(rate * 0.9);
                pcm = new short[n];
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Exp(-t * 5);
                    double v = Math.Sin(2 * Math.PI * 880 * t) + 0.4 * Math.Sin(2 * Math.PI * 1320 * t) + 0.2 * Math.Sin(2 * Math.PI * 1760 * t);
                    pcm[i] = (short)(v * env * 9000);
                }
                break;
            }
        }
        var ms = new MemoryStream(44 + pcm.Length * 2);
        var w = new BinaryWriter(ms);
        w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + pcm.Length * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(pcm.Length * 2);
        foreach (short s in pcm) w.Write(s);
        return ms.ToArray();
    }

    // Serializes everything that writes into one settings or theme folder, in every process.
    internal static void WithLock(string folder, Action action)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\').ToUpperInvariant();
        string id;
        using (var sha = SHA1.Create())
            id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(full))).Replace("-", "").Substring(0, 16);
        using (var mutex = new Mutex(false, @"Local\AnimatedLaunchers.Files." + id))
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new IOException("Another settings update is still in progress. Try again.");
            }
            catch (AbandonedMutexException) { } // The previous process exited while it held the lock.
            try { action(); } finally { mutex.ReleaseMutex(); }
        }
    }
}

// Named look bundles saved by the user in Theme Studio, stored as plain ini
// files next to the theme library so a backup of Themes\ can ignore them.
internal static class LookPresets
{
    private static string Root
    {
        get
        {
            // A sibling of the theme library, so ANIMATED_THEMES redirects both.
            string themes = ThemeLibrary.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.Combine(Path.GetDirectoryName(themes) ?? themes, "Looks");
        }
    }

    internal static string[] Names()
    {
        if (!Directory.Exists(Root)) return new string[0];
        var names = new List<string>();
        foreach (string folder in Directory.GetDirectories(Root))
        {
            string name = Path.GetFileName(folder);
            if (name.Length > 0 && name[0] != '.') names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    internal static string Exact(string name)
    {
        if (name == null) return null;
        foreach (string existing in Names()) if (string.Equals(existing, name, StringComparison.Ordinal)) return existing;
        return null;
    }

    internal static Look Load(string name)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The look \"" + name + "\" no longer exists.");
        return Look.Read(IniFile.Load(Path.Combine(Root, exact, "look.ini")));
    }

    internal static void Save(string name, Look look)
    {
        string folder = Path.Combine(Root, ThemeLibrary.ValidateName(name));
        Directory.CreateDirectory(folder);
        var ini = new IniFile();
        look.Write(ini);
        ini.Save(Path.Combine(folder, "look.ini"));
    }

    internal static void Delete(string name)
    {
        string exact = Exact(name);
        if (exact == null) throw new FileNotFoundException("The look \"" + name + "\" no longer exists.");
        Directory.Delete(Path.Combine(Root, exact), true);
    }
}

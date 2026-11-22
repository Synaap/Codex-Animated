using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

// Theme files (.altheme) and library backups (.albackup) are plain zip archives.
// A theme file holds manifest.ini, theme.ini, wallpaper.jpg and optionally
// avatar.jpg at the root. A backup holds manifest.ini plus Themes\<name>\ with
// the same three files. Entries are read into memory only; nothing else in the
// archive is touched and no entry name ever reaches the file system.
internal static class ThemePack
{
    internal const string Extension = ".altheme";
    internal const string BackupExtension = ".albackup";
    private const long MaxPackBytes = 128L * 1024 * 1024;
    private const long MaxBackupBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxTextBytes = 1024 * 1024;

    // ---- export -------------------------------------------------------------

    // Writes one theme as a single file. Entries go in a fixed order so two runs
    // over the same theme differ only in compressed bytes.
    internal static void Export(string name, string target)
    {
        string folder = ThemeLibrary.Folder(ThemeLibrary.Exact(name) ?? name);
        if (!File.Exists(Path.Combine(folder, "wallpaper.jpg")))
            throw new FileNotFoundException("The theme \"" + name + "\" has no wallpaper.");
        using (var zip = Create(target))
        {
            var manifest = new IniFile();
            manifest.Set("kind", "theme");
            manifest.Set("format", "1");
            manifest.Set("name", name);
            WriteEntry(zip, "manifest.ini", IniBytes(manifest));
            string ini = Path.Combine(folder, "theme.ini");
            if (File.Exists(ini)) WriteEntry(zip, "theme.ini", ImageCodec.ReadBytes(ini));
            WriteEntry(zip, "wallpaper.jpg", ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg")));
            string gif = Path.Combine(folder, "wallpaper.gif");
            if (File.Exists(gif)) WriteEntry(zip, "wallpaper.gif", ImageCodec.ReadBytes(gif));
            string apng = Path.Combine(folder, "wallpaper.apng");
            if (File.Exists(apng)) WriteEntry(zip, "wallpaper.apng", ImageCodec.ReadBytes(apng));
            string slides = Path.Combine(folder, "slides");
            if (Directory.Exists(slides))
                foreach (string slide in Directory.GetFiles(slides))
                    WriteEntry(zip, "slides/" + Path.GetFileName(slide), ImageCodec.ReadBytes(slide));
            string avatar = Path.Combine(folder, "avatar.jpg");
            if (File.Exists(avatar)) WriteEntry(zip, "avatar.jpg", ImageCodec.ReadBytes(avatar));
            string chime = Path.Combine(folder, "sound.wav");
            if (File.Exists(chime)) WriteEntry(zip, "sound.wav", ImageCodec.ReadBytes(chime));
        }
    }

    // The whole library as one file.
    internal static void Backup(string target)
    {
        string[] names = ThemeLibrary.Names();
        using (var zip = Create(target))
        {
            var manifest = new IniFile();
            manifest.Set("kind", "backup");
            manifest.Set("format", "1");
            manifest.Set("count", names.Length.ToString(CultureInfo.InvariantCulture));
            WriteEntry(zip, "manifest.ini", IniBytes(manifest));
            foreach (string name in names)
            {
                string folder = ThemeLibrary.Folder(name);
                string ini = Path.Combine(folder, "theme.ini");
                if (File.Exists(ini)) WriteEntry(zip, "Themes/" + name + "/theme.ini", ImageCodec.ReadBytes(ini));
                WriteEntry(zip, "Themes/" + name + "/wallpaper.jpg", ImageCodec.ReadBytes(Path.Combine(folder, "wallpaper.jpg")));
                string gif = Path.Combine(folder, "wallpaper.gif");
                if (File.Exists(gif)) WriteEntry(zip, "Themes/" + name + "/wallpaper.gif", ImageCodec.ReadBytes(gif));
                string apng = Path.Combine(folder, "wallpaper.apng");
                if (File.Exists(apng)) WriteEntry(zip, "Themes/" + name + "/wallpaper.apng", ImageCodec.ReadBytes(apng));
                string slides = Path.Combine(folder, "slides");
                if (Directory.Exists(slides))
                    foreach (string slide in Directory.GetFiles(slides))
                        WriteEntry(zip, "Themes/" + name + "/slides/" + Path.GetFileName(slide), ImageCodec.ReadBytes(slide));
                string avatar = Path.Combine(folder, "avatar.jpg");
                if (File.Exists(avatar)) WriteEntry(zip, "Themes/" + name + "/avatar.jpg", ImageCodec.ReadBytes(avatar));
                string chime = Path.Combine(folder, "sound.wav");
                if (File.Exists(chime)) WriteEntry(zip, "Themes/" + name + "/sound.wav", ImageCodec.ReadBytes(chime));
            }
        }
    }

    // ---- import -------------------------------------------------------------

    // Imports a theme file and returns the new theme's name. Only the four known
    // entries are looked at; everything else is ignored.
    internal static string Import(string source)
    {
        Dictionary<string, byte[]> entries = ReadKnown(source, "");
        if (entries.Count == 0)
            throw new InvalidDataException("That file is not a theme file. A theme file ends in " + Extension + ".");
        return ImportEntries(entries, SuggestedName(source, entries));
    }

    // Restores a backup file: every Themes\<name>\ group is imported like a theme
    // file. Returns the number of themes restored.
    internal static int Restore(string source)
    {
        int restored = 0;
        string prefix = "Themes/";
        const string marker = "/wallpaper.jpg";
        using (ZipArchive zip = OpenReadChecked(source, MaxBackupBytes))
        {
            var names = new List<string>();
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                    !entry.FullName.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
                string themeName = entry.FullName.Substring(prefix.Length, entry.FullName.Length - prefix.Length - marker.Length);
                if (themeName.Length > 0 && themeName.IndexOf('/') < 0 && !names.Contains(themeName)) names.Add(themeName);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                Dictionary<string, byte[]> entries = ReadKnown(zip, prefix + name + "/");
                if (entries.ContainsKey("wallpaper.jpg"))
                    try { ImportEntries(entries, name); restored++; }
                    catch (InvalidDataException) { }
            }
        }
        return restored;
    }

    // True when the archive is a library backup (has Themes\ entries) rather
    // than a single theme file.
    internal static bool IsBackup(string source)
    {
        using (ZipArchive zip = OpenReadChecked(source, MaxBackupBytes))
            foreach (ZipArchiveEntry entry in zip.Entries)
                if (entry.FullName.StartsWith("Themes/", StringComparison.Ordinal)) return true;
        return false;
    }

    // Creates the theme folder from a known-entry map; returns the stored name.
    private static string ImportEntries(Dictionary<string, byte[]> entries, string wanted)
    {
        byte[] wallpaper = Required(entries, "wallpaper.jpg", "The theme file has no wallpaper.");
        wallpaper = WallpaperBytes(wallpaper);
        byte[] gif = null;
        if (entries.ContainsKey("wallpaper.gif") && ImageCodec.IsGif(entries["wallpaper.gif"]))
            gif = entries["wallpaper.gif"];
        byte[] apng = null;
        if (entries.ContainsKey("wallpaper.apng") && ImageCodec.IsApng(entries["wallpaper.apng"]))
            apng = entries["wallpaper.apng"];
        byte[] avatar = null;
        if (entries.ContainsKey("avatar.jpg")) avatar = WallpaperBytes(entries["avatar.jpg"]);
        byte[] chime = null;
        if (entries.ContainsKey("sound.wav") && IsWave(entries["sound.wav"])) chime = entries["sound.wav"];
        byte[] themeIni = null;
        if (entries.ContainsKey("theme.ini"))
        {
            themeIni = entries["theme.ini"];
            // Valid UTF-8 text that parses; the original bytes are kept verbatim.
            IniFile.Parse(new UTF8Encoding(false).GetString(themeIni));
        }
        string name = UniqueName(wanted);
        string folder = ThemeLibrary.Folder(name);
        try
        {
            Directory.CreateDirectory(folder);
            if (themeIni != null) ImageCodec.SaveAtomic(Path.Combine(folder, "theme.ini"), themeIni);
            ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.jpg"), wallpaper);
            if (gif != null) ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.gif"), gif);
            if (apng != null) ImageCodec.SaveAtomic(Path.Combine(folder, "wallpaper.apng"), apng);
            if (avatar != null) ImageCodec.SaveAtomic(Path.Combine(folder, "avatar.jpg"), avatar);
            if (chime != null) ImageCodec.SaveAtomic(Path.Combine(folder, "sound.wav"), chime);
            // Slides import normalized to JPEG the same way the wallpaper does.
            int slideIndex = 0;
            foreach (var pair in entries)
            {
                if (!pair.Key.StartsWith("slides/", StringComparison.OrdinalIgnoreCase)) continue;
                string slideDir = Path.Combine(folder, "slides");
                Directory.CreateDirectory(slideDir);
                string slideBase = CleanName(Path.GetFileNameWithoutExtension(pair.Key.Substring(7)));
                string target = Path.Combine(slideDir, slideBase + ".jpg");
                if (File.Exists(target))
                    target = Path.Combine(slideDir, slideBase + "-" + (++slideIndex).ToString(CultureInfo.InvariantCulture) + ".jpg");
                ImageCodec.SaveAtomic(target, WallpaperBytes(pair.Value));
            }
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return name;
    }

    // A WAV starts with a RIFF/WAVE header; anything else is not a chime.
    private static bool IsWave(byte[] bytes)
    {
        return bytes != null && bytes.Length > 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E';
    }

    // Valid JPEG bytes are stored untouched so an export/import round trip keeps
    // the file identical; PNG and BMP are normalized to JPEG.
    private static byte[] WallpaperBytes(byte[] bytes)
    {
        try { using (ImageCodec.DecodeJpeg(bytes)) { } return bytes; }
        catch (InvalidDataException) { return ImageCodec.ToJpeg(bytes); }
    }

    // ---- zip plumbing --------------------------------------------------------

    private static ZipArchive Create(string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target)));
        var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        try { return new ZipArchive(stream, ZipArchiveMode.Create); }
        catch { stream.Dispose(); throw; }
    }

    private static ZipArchive OpenReadChecked(string source) { return OpenReadChecked(source, MaxPackBytes); }

    private static ZipArchive OpenReadChecked(string source, long maxBytes)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("The file was not found.", source);
        if (info.Length <= 0 || info.Length > maxBytes)
            throw new InvalidDataException("The theme file must be smaller than 128 MiB.");
        try { return ZipFile.OpenRead(source); }
        catch (InvalidDataException) { throw new InvalidDataException("That file is not a theme file."); }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var writer = new BinaryWriter(entry.Open()))
            writer.Write(bytes);
    }

    private static byte[] IniBytes(IniFile ini)
    {
        return new UTF8Encoding(false).GetBytes(ini.Render());
    }

    // Reads the four known entries of one theme group (prefix "" for a theme
    // file, "Themes/<name>/" inside a backup). Entry sizes are checked before
    // the bytes are read.
    private static Dictionary<string, byte[]> ReadKnown(string source, string prefix)
    {
        using (ZipArchive zip = OpenReadChecked(source)) return ReadKnown(zip, prefix);
    }

    private static readonly Dictionary<string, bool> Known =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) {
            { "manifest.ini", true }, { "theme.ini", true }, { "wallpaper.jpg", true }, { "avatar.jpg", true },
            { "wallpaper.gif", true }, { "wallpaper.apng", true }, { "sound.wav", true } };

    private static Dictionary<string, byte[]> ReadKnown(ZipArchive zip, string prefix)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string shortName = entry.FullName.Substring(prefix.Length);
            if (shortName.Length == 0 || !(Known.ContainsKey(shortName) ||
                (shortName.StartsWith("slides/", StringComparison.OrdinalIgnoreCase) && shortName.Length > 7 &&
                 shortName.IndexOf('/', 7) < 0 && shortName.IndexOf('\\', 7) < 0))) continue;
            long limit = shortName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? MaxTextBytes : MaxPackBytes;
            if (entry.Length > limit)
                throw new InvalidDataException("\"" + shortName + "\" inside the file is too big.");
            using (var stream = entry.Open())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                entries[shortName] = memory.ToArray();
            }
        }
        return entries;
    }

    private static byte[] Required(Dictionary<string, byte[]> entries, string name, string message)
    {
        byte[] bytes;
        if (!entries.TryGetValue(name, out bytes) || bytes.Length == 0)
            throw new InvalidDataException(message);
        return bytes;
    }

    private static string SuggestedName(string source, Dictionary<string, byte[]> entries)
    {
        byte[] manifest;
        if (entries.TryGetValue("manifest.ini", out manifest))
        {
            string name = IniFile.Parse(new UTF8Encoding(false).GetString(manifest)).Get("name");
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return Path.GetFileNameWithoutExtension(source);
    }

    // Validates and cleans the wanted name, then finds a free "Name" / "Name 2".
    private static string UniqueName(string wanted)
    {
        string baseName = CleanName(wanted);
        string name = baseName;
        for (int n = 2; Directory.Exists(ThemeLibrary.Folder(name)); n++)
        {
            string suffix = " " + n.ToString(CultureInfo.InvariantCulture);
            name = (baseName.Length + suffix.Length > 60 ? baseName.Substring(0, 60 - suffix.Length).TrimEnd() : baseName) + suffix;
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
        try { return ThemeLibrary.ValidateName(name); }
        catch (ArgumentException) { return "Imported theme"; }
    }
}

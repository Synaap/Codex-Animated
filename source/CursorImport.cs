using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

// Imports a downloaded cursor pack — a .zip archive or a plain folder holding
// .cur/.ani/PNG/XCursor files — into Cursors\<pack>. File names are mapped
// onto the pack's role stems (Windows pack names like "normal-select" and
// Linux/X11 names like "left_ptr" both recognised); an install.inf with
// explicit "HKCU,Control Panel\Cursors,<Role>,..." lines supplies the mapping
// when names are opaque. .cur entries yield PNG previews and hotspots;
// .ani applies as-is (first frame decoded for the preview); XCursor files are
// decoded to PNG. Everything is size-bounded like ThemePack.
internal static class CursorImport
{
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private const int MaxFiles = 800;

    // Role stem -> recognised source stems (checked after the stem itself).
    private static readonly string[][] Aliases = {
        new[] { "pointer", "arrow", "default", "left_ptr", "top_left_arrow", "normal-select", "normal_select", "select" },
        new[] { "ibeam", "text", "xterm", "text-select", "text_select" },
        new[] { "hand", "link", "hand2", "pointing_hand", "alias", "link-select", "link_select", "openhand" },
        new[] { "help", "question_arrow", "left_ptr_help", "whats_this", "help-select", "help_select" },
        new[] { "wait", "watch", "busy" },
        new[] { "working", "progress", "left_ptr_watch", "half-busy", "working-in-background", "work", "appstarting" },
        new[] { "plus", "crosshair", "cross", "tcross", "cross_reverse", "precision-select", "precision_select", "cell" },
        new[] { "no", "not-allowed", "crossed_circle", "forbidden", "no-drop", "circle", "unavailable", "dnd-no-drop", "unavailable-1" },
        new[] { "pen", "pencil", "draft", "handwriting", "nwpen" },
        new[] { "move", "fleur", "size_all", "all-scroll", "all-resize", "sizeall", "grabbing", "closedhand", "dnd-move" },
        new[] { "nesw", "nesw-resize", "size_bdiag", "bd_double_arrow", "dgn2", "diagonal-resize-2", "diagonal_resize_2" },
        new[] { "ns", "down", "ns-resize", "size_ver", "sb_v_double_arrow", "v_double_arrow", "vertical-resize", "row-resize" },
        new[] { "nwse", "diag", "nwse-resize", "size_fdiag", "fd_double_arrow", "dgn1", "diagonal-resize-1", "diagonal_resize_1" },
        new[] { "we", "ew-resize", "size_hor", "sb_h_double_arrow", "h_double_arrow", "col-resize", "split_h", "horizontal-resize" },
        new[] { "up", "up-arrow", "up_arrow", "sb_up_arrow", "alt-select", "alternate", "alt_select" },
        new[] { "person", "person-select", "person_select" },
        new[] { "pin", "location-select", "location_select" }
    };

    // Windows role value names as they appear in install.inf "Control Panel\Cursors" lines.
    private static readonly Dictionary<string, string> InfRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Arrow", "pointer" }, { "IBeam", "ibeam" }, { "Hand", "hand" }, { "Help", "help" },
        { "Wait", "wait" }, { "AppStarting", "working" }, { "Crosshair", "plus" }, { "No", "no" },
        { "NWPen", "pen" }, { "SizeAll", "move" }, { "SizeNESW", "nesw" }, { "SizeNS", "ns" },
        { "SizeNWSE", "nwse" }, { "SizeWE", "we" }, { "UpArrow", "up" }, { "Person", "person" }, { "Pin", "pin" }
    };

    private sealed class Entry
    {
        public string Name;   // leaf file name
        public byte[] Data;
    }

    // Imports path (a .zip file or a folder) as a new pack; returns the pack id.
    public static string Import(string path)
    {
        var entries = new List<Entry>();
        var roleByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string packName;
        if (Directory.Exists(path))
        {
            packName = new DirectoryInfo(path).Name;
            var files = new List<string>(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
            if (files.Count > MaxFiles) throw new ArgumentException("Too many files in that folder.");
            foreach (string f in files)
            {
                if (new FileInfo(f).Length > MaxFileBytes) continue;
                entries.Add(new Entry { Name = Path.GetFileName(f), Data = File.ReadAllBytes(f) });
                if (Path.GetFileName(f).Equals("install.inf", StringComparison.OrdinalIgnoreCase))
                    ParseInf(File.ReadAllText(f), roleByFile);
            }
        }
        else if (File.Exists(path))
        {
            packName = Path.GetFileNameWithoutExtension(path);
            using (var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > MaxFiles) throw new ArgumentException("Too many files in that archive.");
                long total = 0;
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    total += e.Length;
                    if (total > MaxTotalBytes) throw new ArgumentException("That archive is too large.");
                    string leaf = Path.GetFileName(e.FullName.Replace('/', '\\'));
                    if (leaf.Length == 0) continue;
                    // Read now, while the archive is open, bounded by MaxFileBytes.
                    using (var s = e.Open())
                    {
                        var bounded = new MemoryStream();
                        var buf = new byte[81920]; long left = MaxFileBytes;
                        int n;
                        while (left > 0 && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                        { bounded.Write(buf, 0, n); left -= n; }
                        entries.Add(new Entry { Name = leaf, Data = bounded.ToArray() });
                    }
                }
                // A .inf inside the zip supplies the role mapping.
                foreach (var e in zip.Entries)
                    if (Path.GetFileName(e.FullName).Equals("install.inf", StringComparison.OrdinalIgnoreCase))
                        using (var r = new StreamReader(e.Open())) ParseInf(r.ReadToEnd(), roleByFile);
            }
        }
        else throw new FileNotFoundException("Nothing to import at " + path + ".");

        packName = ThemeLibrary.CursorClean(packName.Replace('.', ' ').Replace('_', ' ').Replace(' ', '-'));
        if (packName.Length == 0) packName = "imported";
        string dir = UniqueDir(packName);
        Directory.CreateDirectory(dir);

        var hotspots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int written = 0;
        foreach (var ent in entries)
        {
            string stem = StemFor(ent.Name);
            string infStem;
            if (stem == null && roleByFile.TryGetValue(ent.Name, out infStem)) stem = infStem;
            if (stem == null || done.Contains(stem)) continue;
            byte[] data = ent.Data;
            if (data == null || data.Length < 8 || data.Length > MaxFileBytes) continue;
            string ext = Path.GetExtension(ent.Name).ToLowerInvariant();
            int[] spot = null;
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                if (!SavePng(data, Path.Combine(dir, stem + ".png"))) continue;
            }
            else if (ext == ".cur" || LooksCur(data))
            {
                File.WriteAllBytes(Path.Combine(dir, stem + ".cur"), data);
                spot = CurPreview(data, Path.Combine(dir, stem + ".png"));
            }
            else if (ext == ".ani" || LooksAni(data))
            {
                File.WriteAllBytes(Path.Combine(dir, stem + ".ani"), data);
                spot = AniPreview(data, Path.Combine(dir, stem + ".png"));
            }
            else if (LooksXCursor(data))
            {
                spot = XCursorToPng(data, Path.Combine(dir, stem + ".png"));
                if (spot == null) continue;
            }
            else continue;
            if (spot != null) { hotspots[stem] = spot[0]; hotspots[stem + "!y"] = spot[1]; }
            done.Add(stem); written++;
        }
        if (written == 0) { try { Directory.Delete(dir, true); } catch { } throw new ArgumentException("No cursor files found in there."); }
        using (var w = new StreamWriter(Path.Combine(dir, "hotspots.ini"), false))
            foreach (var kv in hotspots) if (!kv.Key.EndsWith("!y")) w.WriteLine("{0}={1},{2}", kv.Key, kv.Value, hotspots[kv.Key + "!y"]);
        File.WriteAllText(Path.Combine(dir, "license.txt"), "Imported from " + Path.GetFileName(path) + " by the user - see its license.");
        return Path.GetFileName(dir);
    }

    private static string UniqueDir(string slug)
    {
        string root = ThemeLibrary.CursorsRoot, dir = Path.Combine(root, slug);
        int n = 2;
        while (Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0) { dir = Path.Combine(root, slug + "-" + n); n++; }
        return dir;
    }

    private static string StemFor(string file)
    {
        string stem = ThemeLibrary.CursorClean(Path.GetFileNameWithoutExtension(file));
        foreach (var role in Aliases)
            if (role[0] == stem) return stem;              // already canonical
        foreach (var role in Aliases)
            for (int i = 1; i < role.Length; i++)
                if (role[i] == stem) return role[0];
        return null;
    }

    private static void ParseInf(string text, Dictionary<string, string> roleByFile)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) || line.IndexOf("Cursors", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            string role = parts[2].Trim().Trim('"'), file = parts[parts.Length - 1].Trim().Trim('"');
            string stem;
            if (!InfRoles.TryGetValue(role, out stem)) continue;
            int slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
            if (slash >= 0) file = file.Substring(slash + 1);
            if (file.EndsWith("\"", StringComparison.Ordinal)) file = file.TrimEnd('"');
            file = Path.GetFileName(file);
            if (file.Length > 0) roleByFile[file] = stem;
        }
    }

    private static bool LooksCur(byte[] d) { return d.Length > 6 && d[0] == 0 && d[1] == 0 && d[2] == 2 && d[3] == 0; }
    private static bool LooksAni(byte[] d) { return d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'A' && d[9] == 'C' && d[10] == 'O' && d[11] == 'N'; }
    private static bool LooksXCursor(byte[] d) { return d.Length > 16 && d[0] == 'X' && d[1] == 'c' && d[2] == 'u' && d[3] == 'r'; }

    // Saves raw bytes as a PNG ≤256 px when it decodes as an image.
    private static bool SavePng(byte[] data, string dest)
    {
        try
        {
            using (var ms = new MemoryStream(data, false))
            using (var src = Image.FromStream(ms, false, false))
            {
                double fit = Math.Min(1.0, Math.Min(256.0 / src.Width, 256.0 / src.Height));
                if (fit < 1.0)
                    using (var small = new Bitmap(src, Math.Max(1, (int)(src.Width * fit)), Math.Max(1, (int)(src.Height * fit))))
                        small.Save(dest, ImageFormat.Png);
                else src.Save(dest, ImageFormat.Png);
            }
            return true;
        }
        catch { return false; }
    }

    // Largest entry of a .cur -> PNG preview; returns its hotspot or null.
    private static int[] CurPreview(byte[] d, string dest)
    {
        if (!LooksCur(d) || d.Length < 6) return null;
        int n = d[4] | (d[5] << 8), best = -1, bestW = -1;
        for (int i = 0; i < n && 22 + i * 16 <= d.Length; i++)
        {
            int o = 6 + i * 16;
            int w = d[o] == 0 ? 256 : d[o];
            if (w > bestW) { bestW = w; best = i; }
        }
        if (best < 0) return null;
        int e = 6 + best * 16;
        int xh = d[e + 4] | (d[e + 5] << 8), yh = d[e + 6] | (d[e + 7] << 8);
        int size = BitConverter.ToInt32(d, e + 8), off = BitConverter.ToInt32(d, e + 12);
        if (off < 0 || size < 8 || off + size > d.Length) return new[] { xh, yh };
        if (d[off] == 0x89 && d[off + 1] == 'P' && d[off + 2] == 'N' && d[off + 3] == 'G')
        {
            var png = new byte[size];
            Array.Copy(d, off, png, 0, size);
            SavePng(png, dest);
        }
        else
        {
            // BMP payload: rebuild an .ico from the entry and let GDI+ decode it.
            try
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(new byte[] { 0, 0, 1, 0 }); w.Write((short)1);
                    w.Write(d, e, 12); w.Write(size); w.Write(22);
                    w.Write(d, off, size);
                    ms.Position = 0;
                    using (var ico = new Icon(ms))
                    using (var bmp = ico.ToBitmap()) bmp.Save(dest, ImageFormat.Png);
                }
            }
            catch { }
        }
        return new[] { xh, yh };
    }

    // First "fram"/"icon" member of an .ani RIFF -> preview + hotspot.
    private static int[] AniPreview(byte[] d, string dest)
    {
        for (int i = 8; i + 8 <= d.Length; i++)
            if (d[i] == 'i' && d[i + 1] == 'c' && d[i + 2] == 'o' && d[i + 3] == 'n')
            {
                int size = BitConverter.ToInt32(d, i + 4);
                if (size > 6 && i + 8 + size <= d.Length)
                {
                    var cur = new byte[size];
                    Array.Copy(d, i + 8, cur, 0, size);
                    if (LooksCur(cur)) return CurPreview(cur, dest);
                }
            }
        return new[] { 0, 0 };
    }

    // Biggest image of an XCursor file -> PNG; returns its hotspot or null.
    private static int[] XCursorToPng(byte[] d, string dest)
    {
        if (!LooksXCursor(d) || d.Length < 16) return null;
        int n = BitConverter.ToInt32(d, 12), best = -1, bw = 0;
        for (int i = 0; i < n && 28 + i * 12 <= d.Length; i++)
        {
            int o = 16 + i * 12;
            if (BitConverter.ToInt32(d, o) != unchecked((int)0xfffd0002)) continue;
            int pos = BitConverter.ToInt32(d, o + 8);
            if (pos + 36 > d.Length) continue;
            int w = BitConverter.ToInt32(d, pos + 16);
            if (w > bw && w <= 1024 && BitConverter.ToInt32(d, pos) >= 36 + w * BitConverter.ToInt32(d, pos + 20) * 4
                && pos + 36 + (long)w * BitConverter.ToInt32(d, pos + 20) * 4 <= d.Length)
            { bw = w; best = pos; }
        }
        if (best < 0) return null;
        int h = BitConverter.ToInt32(d, best + 20), xh = BitConverter.ToInt32(d, best + 24), yh = BitConverter.ToInt32(d, best + 28);
        var bmp = new Bitmap(bw, h, PixelFormat.Format32bppPArgb);   // XCursor pixels are premultiplied
        var bits = bmp.LockBits(new Rectangle(0, 0, bw, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
        Marshal.Copy(d, best + 36, bits.Scan0, bw * h * 4);   // XCursor stores BGRA, same layout as Format32bppArgb
        bmp.UnlockBits(bits);
        bmp.Save(dest, ImageFormat.Png);
        bmp.Dispose();
        return new[] { xh, yh };
    }
}

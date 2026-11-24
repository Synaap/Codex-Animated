using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Turns a cursor pack (Cursors\<pack>\*.png) into a real Windows cursor scheme:
// each PNG is repacked as a .cur file (ICO container type 2, PNG payload — the
// same encoding Vista+ uses for .ico), the role values under
// HKCU\Control Panel\Cursors point at the generated files, and
// SystemParametersInfo(SPI_SETCURSORS) reloads the pointers live so no
// sign-out is needed. The previous scheme is saved once into backup.ini so
// "Windows default" can restore it.
internal static class CursorScheme
{
    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(int action, int param, IntPtr vparam, int init);
    private const int SpiSetCursors = 0x0057;
    private const int SpifUpdateIniFile = 0x01;
    private const int SpifSendChange = 0x02;
    private const string KeyPath = @"Control Panel\Cursors";

    // Windows cursor roles -> accepted pack file stems.
    private static readonly string[][] Roles = {
        new[] { "Arrow", "pointer", "arrow" },
        new[] { "IBeam", "ibeam", "text" },
        new[] { "Hand", "hand", "link" },
        new[] { "Help", "help" },
        new[] { "Wait", "wait", "busy", "ring" },
        new[] { "AppStarting", "working", "appstarting" },
        new[] { "Crosshair", "plus", "crosshair", "cross" },
        new[] { "No", "no", "unavailable" },
        new[] { "NWPen", "pen", "nwpen" },
        new[] { "SizeAll", "move", "sizeall" },
        new[] { "SizeNESW", "nesw" },
        new[] { "SizeNS", "ns", "down" },
        new[] { "SizeNWSE", "nwse", "diag" },
        new[] { "SizeWE", "we" },
        new[] { "UpArrow", "up" },
        new[] { "Person", "person" },
        new[] { "Pin", "pin" }
    };

    // stem -> role, or null when the pack file has no Windows role.
    private static string RoleFor(string stem)
    {
        foreach (string[] role in Roles)
            for (int i = 1; i < role.Length; i++)
                if (role[i] == stem) return role[0];
        return null;
    }

    // Apply a pack as the Windows cursor scheme. Throws on a missing pack or
    // when no file maps onto a role.
    internal static void Apply(string pack)
    {
        string dir = Path.Combine(ThemeLibrary.CursorsRoot, ThemeLibrary.CursorClean(pack));
        if (!Directory.Exists(dir)) throw new FileNotFoundException("That pointer pack no longer exists.");
        var map = new Dictionary<string, string>();
        var hotspots = Hotspots(dir);
        string gen = Path.Combine(dir, "generated");
        Directory.CreateDirectory(gen);
        // Native .cur/.ani win over a same-stem .png, so run them first.
        var files = new List<string>(Directory.GetFiles(dir));
        files.Sort((a, b) =>
        {
            string ea = Path.GetExtension(a).ToLowerInvariant(), eb = Path.GetExtension(b).ToLowerInvariant();
            int ra = ea == ".png" ? 1 : 0, rb = eb == ".png" ? 1 : 0;
            return ra != rb ? ra - rb : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        });
        foreach (string file in files)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(file);
            string role = RoleFor(stem);
            if (role == null || map.ContainsKey(role)) continue;
            // Native cursor files apply as-is (animated .ani included);
            // PNGs are repacked into generated .cur files.
            if (ext == ".cur" || ext == ".ani") { map[role] = file; continue; }
            if (ext != ".png") continue;
            int hx, hy;
            if (!hotspots.TryGetValue(stem, out hx)) { hx = role == "Arrow" ? 1 : -1; hy = hx; }
            else hy = hotspots[stem + "!y"];
            string cur = Path.Combine(gen, stem + ".cur");
            PngToCur(file, cur, hx, hy);
            map[role] = cur;
        }
        if (map.Count == 0) throw new ArgumentException("None of this pack's files match a pointer role (pointer, ibeam, no, move…).");
        SaveBackup();
        // Every role is written: roles this pack lacks fall back to the saved
        // scheme, so no pointer from a previously applied pack lingers.
        var saved = LoadBackup();
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            key.SetValue("", "Animated Launchers " + Path.GetFileName(dir));
            foreach (string[] role in Roles)
            {
                string value;
                if (!map.TryGetValue(role[0], out value)) saved.TryGetValue(role[0], out value);
                SetRole(key, role[0], value ?? "");
            }
        }
        SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);
    }

    private static void SetRole(RegistryKey key, string role, string value)
    {
        key.SetValue(role, value, value.IndexOf('%') >= 0 ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    // role -> the value saved before the first Apply ("" when none was saved).
    private static Dictionary<string, string> LoadBackup()
    {
        var saved = new Dictionary<string, string>();
        foreach (string[] role in Roles) saved[role[0]] = "";
        string backup = Path.Combine(ThemeLibrary.CursorsRoot, "backup.ini");
        if (!File.Exists(backup)) return saved;
        IniFile ini = IniFile.Load(backup);
        foreach (string[] role in Roles)
        {
            string v = ini.Get("role:" + role[0]);
            if (v != null) saved[role[0]] = v;
        }
        return saved;
    }

    // Restore the scheme that was live before the first Apply; falls back to
    // the plain Windows defaults (empty values) when no backup exists.
    internal static void Reset()
    {
        var saved = LoadBackup();
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            key.SetValue("", "Windows Default");
            foreach (var kv in saved) SetRole(key, kv.Key, kv.Value);
        }
        SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);
    }

    // Snapshot the current role values once; an existing backup is kept (it is
    // the pre-Animated-Launchers scheme, not the last applied pack).
    private static void SaveBackup()
    {
        string backup = Path.Combine(ThemeLibrary.CursorsRoot, "backup.ini");
        if (File.Exists(backup)) return;
        var ini = new IniFile();
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            if (key != null)
                foreach (string[] role in Roles)
                {
                    object v = key.GetValue(role[0], "", RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string text = v == null ? "" : v.ToString();
                    // Never snapshot one of our own packs as "the original".
                    if (text.StartsWith(ThemeLibrary.CursorsRoot, StringComparison.OrdinalIgnoreCase)) text = "";
                    ini.Set("role:" + role[0], text);
                }
        try { ini.Save(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, int> Hotspots(string dir)
    {
        var spots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string ini = Path.Combine(dir, "hotspots.ini");
        if (File.Exists(ini))
            foreach (string line in File.ReadAllLines(ini))
            {
                int eq = line.IndexOf('=');
                if (eq < 1) continue;
                string[] xy = line.Substring(eq + 1).Split(',');
                int x, y;
                if (xy.Length == 2 && int.TryParse(xy[0], out x) && int.TryParse(xy[1], out y))
                {
                    spots[line.Substring(0, eq).Trim()] = x;
                    spots[line.Substring(0, eq).Trim() + "!y"] = y;
                }
            }
        return spots;
    }

    // A .cur file is an icon directory with type 2 and the hotspot where .ico
    // keeps the colour planes; PNG payloads work the same as .ico PNG entries.
    internal static byte[] PngToCurBytes(byte[] png, int hx, int hy, int w, int h)
    {
        var ms = new MemoryStream();
        var wr = new BinaryWriter(ms);
        wr.Write((short)0);          // reserved
        wr.Write((short)2);          // 2 = cursor
        wr.Write((short)1);          // count
        wr.Write((byte)(w >= 256 ? 0 : w));
        wr.Write((byte)(h >= 256 ? 0 : h));
        wr.Write((byte)0);           // colours in palette
        wr.Write((byte)0);           // reserved
        wr.Write((short)(hx < 0 ? w / 2 : hx));  // hotspot X
        wr.Write((short)(hy < 0 ? h / 2 : hy));  // hotspot Y
        wr.Write(png.Length);        // data size
        wr.Write(22);                // data offset
        wr.Write(png, 0, png.Length);
        wr.Flush();
        return ms.ToArray();
    }

    internal static void PngToCur(string pngPath, string curPath, int hx, int hy)
    {
        byte[] png = File.ReadAllBytes(pngPath);
        int w, h;
        using (var bmp = new Bitmap(new MemoryStream(png)))
        {
            w = bmp.Width; h = bmp.Height;
            const int Max = 128;
            if (w > Max || h > Max)
            {
                // Oversized art makes unusable cursors: fit it in Max x Max.
                double scale = Math.Min((double)Max / w, (double)Max / h);
                int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
                using (var small = new Bitmap(nw, nh, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(bmp, 0, 0, nw, nh);
                    }
                    if (hx >= 0) { hx = (int)(hx * scale); hy = (int)(hy * scale); }
                    var ms = new MemoryStream();
                    small.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                    w = nw; h = nh;
                }
            }
        }
        string tmp = curPath + ".tmp";
        File.WriteAllBytes(tmp, PngToCurBytes(png, hx, hy, w, h));
        if (File.Exists(curPath)) File.Delete(curPath);
        File.Move(tmp, curPath);
    }
}

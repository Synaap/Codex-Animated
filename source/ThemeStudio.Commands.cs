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

// The bridge commands the studio page posts through chrome.webview. The window,
// the page serving and the helpers they call are in ThemeStudio.cs.
internal sealed partial class StudioForm
{
    private object Execute(string cmd, Dictionary<string, object> m)
    {
        switch (cmd)
        {
            case "state": return State();
            case "saveTheme": return SaveTheme(m);
            case "applyTheme":
            {
                string name = ExistingTheme(m);
                string target = Str(m, "app");
                if (target == "all")
                    foreach (App app in Apps)
                        ThemeLibrary.ApplyForMode(name, app.SettingsDirectory,
                            SlotFor(AppProfile.Load(app.SettingsDirectory, app.IsCustom)));
                else
                {
                    App app = FindApp(target);
                    if (app == null) throw new ArgumentException("Unknown app.");
                    ThemeLibrary.ApplyForMode(name, app.SettingsDirectory,
                        SlotFor(AppProfile.Load(app.SettingsDirectory, app.IsCustom)));
                }
                return AppList();
            }
            case "createTheme":
            {
                string path = TestPath(m) ?? PickImage("Choose a wallpaper image");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                string created = ThemeLibrary.Create(path);
                Dictionary<string, object> state = State();
                state["created"] = created;
                return state;
            }
            case "renameTheme":
            {
                string name = ExistingTheme(m);
                string renamed = ThemeLibrary.Rename(name, Str(m, "newName"), AppRoots());
                Dictionary<string, object> state = State();
                state["renamed"] = renamed;
                return state;
            }
            case "deleteTheme":
            {
                string name = ExistingTheme(m);
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(ThemeLibrary.Folder(name),
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                ThemeLibrary.ClearReferences(name, AppRoots());
                return State();
            }
            case "replaceWallpaper":
            {
                string name = ExistingTheme(m);
                string path = TestPath(m) ?? PickImage("Choose a wallpaper image");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                byte[] bytes = ImageCodec.ReadBytes(path);
                if (!ImageCodec.IsGif(bytes)) using (ImageCodec.DecodeJpeg(ImageCodec.ToJpeg(bytes))) { }
                ThemeLibrary.SaveWallpaper(name, bytes);
                ReapplyTo(name);
                return State();
            }
            case "setAvatar":
            {
                string name = ExistingTheme(m);
                string path = TestPath(m) ?? PickImage("Choose an avatar image");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                byte[] jpeg = ImageCodec.ToJpeg(path);
                using (ImageCodec.DecodeJpeg(jpeg)) { }
                string target = Path.Combine(ThemeLibrary.Folder(name), "avatar.jpg");
                ThemeLibrary.WithLock(ThemeLibrary.Folder(name), delegate { ImageCodec.SaveAtomic(target, jpeg); });
                ReapplyTo(name);
                return State();
            }
            case "clearAvatar":
            {
                string name = ExistingTheme(m);
                string target = Path.Combine(ThemeLibrary.Folder(name), "avatar.jpg");
                ThemeLibrary.WithLock(ThemeLibrary.Folder(name), delegate { File.Delete(target); });
                ReapplyTo(name);
                return State();
            }
            case "setApp":
            {
                App app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                AppProfile profile = AppProfile.LoadStrict(app.SettingsDirectory, app.IsCustom);
                if (m.ContainsKey("splash")) profile.SplashOnStart = Flag(m, "splash");
                if (m.ContainsKey("speed")) profile.Speed = Math.Max(1, Math.Min(3, Int(m, "speed", 1)));
                if (m.ContainsKey("once")) profile.SplashOnce = Flag(m, "once");
                if (m.ContainsKey("style"))
                {
                    string style = SplashText.NormalizeStyle(Str(m, "style"));
                    profile.Style = style.Length > 0 ? style : "full";
                }
                if (m.ContainsKey("rotate")) profile.Rotate = AppProfile.NormalizeRotate(Str(m, "rotate"));
                if (m.ContainsKey("rotateFavs")) profile.RotateFavs = Flag(m, "rotateFavs");
                if (m.ContainsKey("followWindows")) profile.FollowWindows = Flag(m, "followWindows");
                if (m.ContainsKey("themeLight"))
                {
                    string pick = Str(m, "themeLight") ?? "";
                    if (pick.Length > 0 && ThemeLibrary.Exact(pick) == null)
                        throw new ArgumentException("Unknown theme \"" + pick + "\".");
                    profile.ThemeLight = pick;
                }
                if (m.ContainsKey("themeNight"))
                {
                    string pick = Str(m, "themeNight") ?? "";
                    if (pick.Length > 0 && ThemeLibrary.Exact(pick) == null)
                        throw new ArgumentException("Unknown theme \"" + pick + "\".");
                    profile.ThemeNight = pick;
                }
                if (m.ContainsKey("nightStart")) profile.NightStart = Math.Max(0, Math.Min(23, Int(m, "nightStart", 20)));
                if (m.ContainsKey("nightUntil")) profile.NightUntil = Math.Max(0, Math.Min(23, Int(m, "nightUntil", 7)));
                if (m.ContainsKey("nightDim")) profile.NightDim = Math.Max(0, Math.Min(80, Int(m, "nightDim", 0)));
                if (m.ContainsKey("extraDim")) profile.ExtraDim = Math.Max(0, Math.Min(80, Int(m, "extraDim", 0)));
                if (m.ContainsKey("hideFullscreen")) profile.HideFullscreen = Flag(m, "hideFullscreen");
                profile.Save();
                return AppList();
            }
            case "setSplashAll":
            {
                bool on = Flag(m, "on");
                foreach (App app in Apps)
                {
                    AppProfile p = AppProfile.Load(app.SettingsDirectory, app.IsCustom);
                    if (p.SplashOnStart == on) continue;
                    p.SplashOnStart = on;
                    p.Save();
                }
                return AppList();
            }
            case "setAutostart":
            {
                // One shared start-with-Windows entry plus this app's watch flag:
                // the helper starts at sign-in and this app's part stays awake.
                App app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                if (evalMode && Flag(m, "dryRun"))
                    return new Dictionary<string, object> { { "app", app.Id }, { "watch", Flag(m, "on") } };
                AppProfile profile = AppProfile.LoadStrict(app.SettingsDirectory, app.IsCustom);
                profile.Watch = Flag(m, "on");
                profile.Save();
                AppTable.SyncRunValue(StudioFolder());
                return AppList();
            }
            case "startHelper":
            {
                App app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                string exe = Path.Combine(StudioFolder(), AppTable.HelperExeName);
                if (!File.Exists(exe)) throw new FileNotFoundException(AppTable.HelperExeName + " is not next to Theme Studio, so the helper can't be started.");
                using (Process.Start(new ProcessStartInfo(exe, "--artwork") { UseShellExecute = false })) { }
                for (int i = 0; i < 15 && !HelperRunning(); i++) Thread.Sleep(100);
                return AppList();
            }
            case "playSplash":
            {
                string name = ExistingTheme(m);
                App app = FindApp(Str(m, "app"));
                if (app == null) throw new ArgumentException("Unknown app.");
                string exe = Path.Combine(StudioFolder(), AppTable.HelperExeName);
                string arguments = "--preview \"" + ThemeLibrary.Folder(name) + "\" " + Handle64() + " --app " + app.Id;
                if (evalMode && Flag(m, "dryRun"))
                    return new Dictionary<string, object> { { "exe", exe }, { "args", arguments }, { "hwnd", Handle64() } };
                if (!File.Exists(exe)) throw new FileNotFoundException(AppTable.HelperExeName + " is not next to Theme Studio, so the animation can't be played.");
                using (Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false })) { }
                return new Dictionary<string, object>();
            }
            case "exportTheme":
            {
                string name = ExistingTheme(m);
                string target = TestPath(m) ?? PickSave("Export " + name, name + ThemePack.Extension,
                    "Theme file (*" + ThemePack.Extension + ")|*" + ThemePack.Extension);
                if (target == null) return new Dictionary<string, object> { { "cancelled", true } };
                ThemePack.Export(name, target);
                return new Dictionary<string, object> { { "file", target } };
            }
            case "importTheme":
            {
                string path = TestPath(m) ?? PickFile("Import a theme file",
                    "Theme file (*" + ThemePack.Extension + ")|*" + ThemePack.Extension + "|Zip (*.zip)|*.zip");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                string imported = ThemePack.Import(path);
                Dictionary<string, object> state = State();
                state["imported"] = imported;
                return state;
            }
            case "importAnyData":
            {
                string temp = TempDataFile(m);
                try
                {
                    object marker;
                    if (ThemePack.IsBackup(temp)) marker = ThemePack.Restore(temp); else marker = ThemePack.Import(temp);
                    Dictionary<string, object> state = State();
                    state[marker is int ? "restored" : "imported"] = marker;
                    return state;
                }
                finally { DeleteTemp(temp); }
            }
            case "createThemeData":
            {
                string temp = TempDataFile(m);
                try
                {
                    string created = ThemeLibrary.Create(temp);
                    Dictionary<string, object> state = State();
                    state["created"] = created;
                    return state;
                }
                finally { DeleteTemp(temp); }
            }
            case "duplicateTheme":
            {
                string name = ExistingTheme(m);
                string created = ThemeLibrary.Duplicate(name);
                Dictionary<string, object> state = State();
                state["created"] = created;
                return state;
            }
            case "openThemesFolder":
            {
                Directory.CreateDirectory(ThemeLibrary.Root);
                if (evalMode && Flag(m, "dryRun"))
                    return new Dictionary<string, object> { { "folder", ThemeLibrary.Root } };
                using (Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ThemeLibrary.Root + "\"") { UseShellExecute = false })) { }
                return new Dictionary<string, object>();
            }
            case "backupLibrary":
            {
                string target = TestPath(m) ?? PickSave("Back up all themes", "themes" + ThemePack.BackupExtension,
                    "Theme library backup (*" + ThemePack.BackupExtension + ")|*" + ThemePack.BackupExtension);
                if (target == null) return new Dictionary<string, object> { { "cancelled", true } };
                ThemePack.Backup(target);
                return new Dictionary<string, object> { { "file", target } };
            }
            case "restoreLibrary":
            {
                string path = TestPath(m) ?? PickFile("Restore a theme library backup",
                    "Theme library backup (*" + ThemePack.BackupExtension + ")|*" + ThemePack.BackupExtension + "|Zip (*.zip)|*.zip");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                int restored = ThemePack.Restore(path);
                Dictionary<string, object> state = State();
                state["restored"] = restored;
                return state;
            }
            case "saveLookPreset":
            {
                Dictionary<string, object> l = Obj(m, "look") ?? new Dictionary<string, object>();
                var ini = new IniFile();
                foreach (string key in new[] { "opacity", "blur", "dim", "tint", "tintColor", "focusX", "focusY",
                    "clear", "clearSide", "glow", "vignette", "sat", "fade", "fit" })
                    if (Str(l, key) != null) ini.Set(key, Str(l, key));
                LookPresets.Save(Str(m, "name"), Look.Read(ini));
                return State();
            }
            case "deleteLookPreset":
                LookPresets.Delete(Str(m, "name"));
                return State();
            case "applyLookPreset":
                return new Dictionary<string, object> { { "look", LookData(LookPresets.Load(Str(m, "name"))) } };
            case "pickAppExe":
            {
                string path = TestPath(m) ?? PickFile("Choose the app's program file",
                    "Programs (*.exe)|*.exe");
                if (path == null) return new Dictionary<string, object> { { "cancelled", true } };
                return new Dictionary<string, object> { { "path", path }, { "name", Path.GetFileNameWithoutExtension(path) } };
            }
            case "probeApp":
            {
                // What a picked exe looks like to the wallpaper machinery: whether it
                // exists, is already covered, is running, and its window's class.
                string path = Str(m, "path");
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("No program was given.");
                var report = new Dictionary<string, object> {
                    { "path", path }, { "exists", File.Exists(path) } };
                if (!File.Exists(path)) return report;
                bool covered = false;
                foreach (App a in AppTable.WithCustom()) if (a.Owns(path)) { covered = true; break; }
                report["covered"] = covered;
                string cls = WindowClassOf(path);
                if (cls != null) { report["running"] = true; report["windowClass"] = cls; }
                else report["running"] = false;
                return report;
            }
            case "addCustomApp":
            {
                string error;
                App app = CustomApps.Add(Str(m, "name"), Str(m, "path"), Str(m, "preview"), out error);
                if (app == null) return new Dictionary<string, object> { { "error", error } };
                Dictionary<string, object> state = State();
                state["added"] = app.Id;
                return state;
            }
            case "removeCustomApp":
            {
                App app = FindApp(Str(m, "app"));
                if (app == null || !app.IsCustom) throw new ArgumentException("Unknown custom app.");
                CustomApps.Remove(app.Id);
                return AppList();
            }
            case "useDesktopWallpaper":
            {
                string path = TestPath(m) ?? DesktopWallpaperPath();
                if (path == null) return new Dictionary<string, object> { { "none", true } };
                string created = ThemeLibrary.Create(path);
                Dictionary<string, object> state = State();
                state["created"] = created;
                return state;
            }
            case "pasteImage":
            {
                byte[] jpeg = ClipboardJpeg();
                if (jpeg == null) return new Dictionary<string, object> { { "none", true } };
                string created = ThemeLibrary.CreateFromJpeg(jpeg, "Pasted image");
                Dictionary<string, object> state = State();
                state["created"] = created;
                return state;
            }
            case "importFolder":
            {
                string folder = TestPath(m) ?? PickFolder();
                if (folder == null) return new Dictionary<string, object> { { "cancelled", true } };
                var created = new List<string>();
                foreach (string pattern in new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" })
                    foreach (string file in Directory.GetFiles(folder, pattern))
                    {
                        if (created.Count >= 64) break;
                        try { created.Add(ThemeLibrary.Create(file)); }
                        catch (InvalidDataException) { }
                        catch (IOException) { }
                        catch (ArgumentException) { }
                        catch (ExternalException) { }
                        catch (OutOfMemoryException) { }
                    }
                Dictionary<string, object> state = State();
                state["created"] = created.Count > 0 ? created[0] : null;
                state["count"] = created.Count;
                return state;
            }
            case "cropAvatar":
            {
                string name = ExistingTheme(m);
                using (Bitmap wallpaper = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(
                    Path.Combine(ThemeLibrary.Folder(name), "wallpaper.jpg"))))
                {
                    // The page sends fractions of the source image (fx/fy/fsize, 0..1):
                    // the preview it cropped against can be scaled down. Integer
                    // x/y/size in source pixels stay valid for direct calls.
                    int x, y, size;
                    if (m.ContainsKey("fsize"))
                    {
                        size = (int)Math.Round(Num(m, "fsize", 0) * Math.Min(wallpaper.Width, wallpaper.Height));
                        x = (int)Math.Round(Num(m, "fx", 0) * wallpaper.Width);
                        y = (int)Math.Round(Num(m, "fy", 0) * wallpaper.Height);
                    }
                    else
                    {
                        size = Int(m, "size", 0);
                        x = Int(m, "x", 0);
                        y = Int(m, "y", 0);
                    }
                    size = Math.Max(8, Math.Min(size, Math.Min(wallpaper.Width, wallpaper.Height)));
                    x = Math.Max(0, Math.Min(x, wallpaper.Width - size));
                    y = Math.Max(0, Math.Min(y, wallpaper.Height - size));
                    int outSize = Math.Min(512, size);
                    using (var square = new Bitmap(outSize, outSize, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(square))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(wallpaper, new Rectangle(0, 0, outSize, outSize),
                                new Rectangle(x, y, size, size), GraphicsUnit.Pixel);
                        }
                        string target = Path.Combine(ThemeLibrary.Folder(name), "avatar.jpg");
                        byte[] jpeg = ImageCodec.EncodeJpeg(square);
                        ThemeLibrary.WithLock(ThemeLibrary.Folder(name), delegate { ImageCodec.SaveAtomic(target, jpeg); });
                    }
                }
                ReapplyTo(name);
                return State();
            }
            case "createGradient":
            {
                GradientSpec spec;
                string error;
                if (!TryGradient(m, out spec, out error)) throw new ArgumentException(error);
                string created = ThemeLibrary.CreateFromJpeg(spec.RenderJpeg(), "Gradient");
                ThemeLibrary.SetGradient(created, spec);
                Dictionary<string, object> state = State();
                state["created"] = created;
                return state;
            }
            case "setPrefs":
            {
                ThemeLibrary.WithLock(ThemeLibrary.SharedRoot, delegate {
                    IniFile ini = IniFile.Load(StudioIni);
                    string sort = Str(m, "sort");
                    if (sort != null)
                    {
                        sort = sort.Trim().ToLowerInvariant();
                        ini.Set("sort", sort != "favorites" && sort != "newest" ? "name" : sort);
                    }
                    string favoritesRaw = Str(m, "favorites");
                    if (favoritesRaw != null)
                    {
                        var favorites = new List<string>();
                        foreach (string part in favoritesRaw.Split('|'))
                        {
                            string exact = ThemeLibrary.Exact(part.Trim());
                            if (exact != null && !favorites.Contains(exact)) favorites.Add(exact);
                        }
                        ini.Set("favorites", string.Join("|", favorites));
                    }
                    string welcome = Str(m, "welcome");
                    if (welcome != null) ini.Set("welcome", welcome.Trim().ToLowerInvariant() == "off" ? "off" : "on");
                    string langPref = Str(m, "lang");
                    if (langPref != null) ini.Set("lang", langPref.Trim().ToLowerInvariant());
                    ini.Save(StudioIni);
                });
                return Prefs();
            }
            case "seedStarters":
            {
                string[] made = ThemeLibrary.SeedStarters();
                Dictionary<string, object> state = State();
                state["seeded"] = made;
                return state;
            }
            case "starterPreview":
            {
                var thumbs = new List<object>();
                foreach (var pair in ThemeLibrary.StarterThumbs())
                    thumbs.Add(new Dictionary<string, object> {
                        { "name", pair.Key }, { "jpeg", Convert.ToBase64String(pair.Value) } });
                return new Dictionary<string, object> { { "starters", thumbs } };
            }
            case "editGradient":
            {
                string name = ExistingTheme(m);
                GradientSpec spec;
                string error;
                if (!TryGradient(m, out spec, out error)) throw new ArgumentException(error);
                byte[] jpeg = spec.RenderJpeg();
                string target = Path.Combine(ThemeLibrary.Folder(name), "wallpaper.jpg");
                ThemeLibrary.WithLock(ThemeLibrary.Folder(name), delegate {
                    ImageCodec.SaveAtomic(target, jpeg);
                    File.Delete(Path.Combine(ThemeLibrary.Folder(name), "wallpaper.gif"));
                    string ini = Path.Combine(ThemeLibrary.Folder(name), "theme.ini");
                    var file = IniFile.LoadStrict(ini);
                    file.Set("gradient", spec.Format());
                    file.Save(ini);
                });
                ReapplyTo(name);
                return State();
            }
            case "checkReadability":
            {
                string name = ExistingTheme(m);
                using (Bitmap source = ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(Path.Combine(ThemeLibrary.Folder(name), "wallpaper.jpg"))))
                using (Bitmap small = Scale(source, 160, Math.Max(1, (int)Math.Round(source.Height * 160.0 / source.Width))))
                {
                    var look = new Look { Blur = Int(m, "blur", 0), Dim = Int(m, "dim", 0),
                        Tint = Int(m, "tint", 0), Sat = Int(m, "sat", 100), Fade = Int(m, "fade", 0) };
                    Color tint;
                    string tintText = Str(m, "tintColor");
                    if (tintText != null && SplashText.TryParseColor(tintText, out tint)) look.TintColor = tint;
                    using (Bitmap rendered = ArtworkLook.Render(small, look))
                    {
                        int luma = ArtworkLook.TextZoneLuma(rendered);
                        string side = (Str(m, "clearSide") ?? "").ToLowerInvariant();
                        bool cleared = Int(m, "clear", 0) > 0 && (side == "left" || side == "center");
                        return new Dictionary<string, object> { { "luma", luma }, { "bright", luma > 150 && !cleared } };
                    }
                }
            }
            case "copyReport":
            {
                string report = BuildReport();
                if (!(evalMode && Flag(m, "dryRun"))) Clipboard.SetText(report);
                return new Dictionary<string, object> { { "copied", true }, { "text", report } };
            }
            case "openLogFolder":
            {
                App app = FindApp(Str(m, "app"));
                string dir = Path.Combine(app == null ? ThemeLibrary.SharedRoot : app.SettingsDirectory, "Logs");
                if (evalMode && Flag(m, "dryRun")) return new Dictionary<string, object> { { "path", dir } };
                Directory.CreateDirectory(dir);
                using (Process.Start("explorer.exe", "\"" + dir + "\"")) { }
                return new Dictionary<string, object> { { "opened", true }, { "path", dir } };
            }
            case "restartHelpers":
            {
                string exe = Path.Combine(StudioFolder(), AppTable.HelperExeName);
                if (!File.Exists(exe)) throw new FileNotFoundException(AppTable.HelperExeName + " is not next to Theme Studio, so the helper can't be restarted.");
                if (HelperRunning())
                {
                    ThemeLibrary.WithLock(ThemeLibrary.SharedRoot, delegate {
                        File.WriteAllText(Path.Combine(ThemeLibrary.SharedRoot, "restart.stamp"), DateTime.UtcNow.ToString("o"));
                    });
                    for (int i = 0; i < 50 && HelperRunning(); i++) Thread.Sleep(100);
                    if (HelperRunning()) throw new InvalidOperationException("The helper did not stop within five seconds.");
                }
                using (Process.Start(new ProcessStartInfo(exe, "--artwork") { UseShellExecute = false })) { }
                for (int i = 0; i < 50 && !HelperRunning(); i++) Thread.Sleep(100);
                return AppList();
            }
            case "checkUpdates":
            {
                string url = Str(m, "url");
                if (url == null || !(evalMode || url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)))
                    url = "https://api.github.com/repos/Synaap/Codex-Animated/releases/latest";
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string json;
                var request = WebRequest.Create(url);
                var http = request as HttpWebRequest;
                if (http != null) { http.UserAgent = "AnimatedLaunchers/" + Application.ProductVersion; http.Timeout = 10000; }
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    json = reader.ReadToEnd();
                var parsed = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                string tag = Convert.ToString(parsed["tag_name"], CultureInfo.InvariantCulture) ?? "";
                string page = Convert.ToString(parsed["html_url"], CultureInfo.InvariantCulture) ?? "";
                string zip = "", zipName = "", sha = "";
                var downloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                object assets;
                if (parsed.TryGetValue("assets", out assets))
                {
                    var rows = assets as object[];
                    if (rows != null)
                        foreach (object row in rows)
                        {
                            var asset = row as Dictionary<string, object>;
                            if (asset == null) continue;
                            string name = Convert.ToString(asset["name"], CultureInfo.InvariantCulture) ?? "";
                            string download = Convert.ToString(asset["browser_download_url"], CultureInfo.InvariantCulture) ?? "";
                            downloads[name] = download;
                            if (zip == "" && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip = download; zipName = name; }
                        }
                }
                // Package.ps1 publishes <zip>.sha256 next to the zip; getUpdate checks it.
                if (zipName != "") downloads.TryGetValue(zipName + ".sha256", out sha);
                string current = Application.ProductVersion;
                Version latest, mine;
                bool newer = Version.TryParse(tag.TrimStart('v', 'V'), out latest) && Version.TryParse(current, out mine) && latest > mine;
                return new Dictionary<string, object> {
                    { "version", tag.TrimStart('v', 'V') }, { "current", current },
                    { "url", page }, { "zip", zip }, { "sha", sha ?? "" }, { "updateAvailable", newer }
                };
            }
            case "getUpdate":
            {
                string url = Str(m, "url") ?? "";
                if (!url.StartsWith("https://github.com/Synaap/Codex-Animated/releases", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only the Animated Launchers release page can be opened.");
                string zip = Str(m, "zip") ?? "";
                if (zip == "" || !zip.StartsWith("https://github.com/Synaap/Codex-Animated/releases/download/", StringComparison.OrdinalIgnoreCase))
                {
                    // No asset attached to the release — just open the page.
                    using (Process.Start(url)) { }
                    return new Dictionary<string, object> { { "opened", true } };
                }
                string ver = Str(m, "version") ?? "update";
                foreach (char c in Path.GetInvalidFileNameChars()) ver = ver.Replace(c, '-');
                string dir = Path.Combine(Path.GetTempPath(), "animated-launchers-update-" + ver);
                string zipFile = dir + ".zip";
                if (evalMode && Flag(m, "dryRun"))
                    return new Dictionary<string, object> { { "dryRun", true }, { "zip", zip }, { "target", dir } };
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var get = (HttpWebRequest)WebRequest.Create(zip);
                get.UserAgent = "AnimatedLaunchers/" + Application.ProductVersion;
                get.Timeout = 60000;
                byte[] bytes;
                using (var response = get.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
                if (bytes.Length < 2 * 1024 * 1024 || bytes.Length > 64 * 1024 * 1024)
                    throw new InvalidDataException("The update download looks wrong (" + bytes.Length + " bytes), so it was not installed.");
                // The installer runs with the script policy bypassed, so a release that
                // publishes its checksum must match it byte for byte.
                string sha = Str(m, "sha") ?? "";
                if (sha.Length > 0)
                {
                    if (!sha.StartsWith("https://github.com/Synaap/Codex-Animated/releases/download/", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("The checksum has to come from the same release.");
                    var hashGet = (HttpWebRequest)WebRequest.Create(sha);
                    hashGet.UserAgent = get.UserAgent;
                    hashGet.Timeout = 20000;
                    string published;
                    using (var response = hashGet.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        published = reader.ReadToEnd();
                    string expected = published.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0].ToLowerInvariant();
                    string actual;
                    using (var hasher = SHA256.Create())
                        actual = BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    if (expected.Length != 64 || expected != actual)
                        throw new InvalidDataException("The update download does not match its published checksum, so it was not installed.");
                }
                ImageCodec.SaveAtomic(zipFile, bytes);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                ZipFile.ExtractToDirectory(zipFile, dir);
                string installer = Path.Combine(dir, "Install.ps1");
                if (!File.Exists(installer)) throw new FileNotFoundException("The update zip has no Install.ps1, so nothing was started.");
                // The installer itself stops Theme Studio and the helpers.
                using (Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + installer + "\" -NoBuild") { UseShellExecute = false })) { }
                return new Dictionary<string, object> { { "started", installer } };
            }
        }
        throw new ArgumentException("Unknown command.");
    }
}

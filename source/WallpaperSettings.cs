using System;
using System.IO;
using System.Drawing;

// Loads the app's wallpaper and avatar. Everything that changes them lives in
// ThemeLibrary and Theme Studio.
internal sealed class WallpaperSettings
{
    private readonly string bundledPath;
    private readonly string settingsDirectory;
    private readonly string customPath;
    private readonly string avatarPath;

    internal WallpaperSettings(string bundledPath, string settingsDirectory)
    {
        this.bundledPath = bundledPath;
        this.settingsDirectory = settingsDirectory;
        customPath = Path.Combine(settingsDirectory, "wallpaper.jpg");
        avatarPath = Path.Combine(settingsDirectory, "avatar.jpg");
    }

    internal bool HasCustomWallpaper { get { return File.Exists(customPath); } }

    internal bool HasCustomAvatar { get { return File.Exists(avatarPath); } }

    // The radar avatar in the splash; falls back to the wallpaper when unset.
    internal byte[] LoadAvatarJpegOr(byte[] fallback)
    {
        if (HasCustomAvatar)
        {
            try
            {
                byte[] saved = null;
                ThemeLibrary.WithLock(settingsDirectory, delegate { saved = ImageCodec.ReadBytes(avatarPath); });
                using (ImageCodec.DecodeJpeg(saved)) { }
                return saved;
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return fallback;
    }

    internal Bitmap LoadBitmap() { return LoadSelected(ImageCodec.DecodeJpeg); }

    // Throws instead of falling back to the bundled artwork when the saved wallpaper cannot be read.
    internal Bitmap LoadBitmapStrict()
    {
        if (!HasCustomWallpaper) return ImageCodec.DecodeJpeg(ImageCodec.ReadBytes(bundledPath));
        byte[] saved = null;
        ThemeLibrary.WithLock(settingsDirectory, delegate { saved = ImageCodec.ReadBytes(customPath); });
        return ImageCodec.DecodeJpeg(saved);
    }

    internal byte[] LoadJpeg()
    {
        return LoadSelected(delegate(byte[] bytes) {
            // Check the actual pixels before handing a saved wallpaper to WebView2.
            using (ImageCodec.DecodeJpeg(bytes)) { }
            return bytes;
        });
    }

    private T LoadSelected<T>(Func<byte[], T> decode)
    {
        if (HasCustomWallpaper)
        {
            try
            {
                byte[] saved = null;
                ThemeLibrary.WithLock(settingsDirectory, delegate { saved = ImageCodec.ReadBytes(customPath); });
                return decode(saved);
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return decode(ImageCodec.ReadBytes(bundledPath));
    }
}

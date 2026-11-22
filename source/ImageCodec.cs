using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// Reading, validating and writing the wallpaper and avatar files. Callers that
// write into a settings folder hold ThemeLibrary.WithLock for that folder.
internal static class ImageCodec
{
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const long MaxPixels = 64L * 1024 * 1024;

    internal static byte[] ReadBytes(string path)
    {
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var reader = new BinaryReader(file))
        {
            if (file.Length <= 0 || file.Length > MaxFileBytes)
                throw new InvalidDataException("Choose an image under 128 MiB.");
            return reader.ReadBytes((int)file.Length);
        }
    }

    internal static Bitmap DecodeJpeg(byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes, false)) return Decode(stream, true);
    }

    // Any supported picture (JPEG, PNG, BMP) normalized to an upright JPEG.
    internal static byte[] ToJpeg(string path) { return ToJpeg(ReadBytes(path)); }

    // A bitmap (drawn, cropped or generated) encoded as a quality-94 JPEG.
    internal static byte[] EncodeJpeg(Bitmap image)
    {
        using (var output = new MemoryStream())
        using (var options = new EncoderParameters(1))
        {
            options.Param[0] = new EncoderParameter(Encoder.Quality, 94L);
            var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
            image.Save(output, codec, options);
            return output.ToArray();
        }
    }

    internal static byte[] ToJpeg(byte[] bytes)
    {
        using (var input = new MemoryStream(bytes, false))
        using (var normalized = Decode(input, false))
        using (var output = new MemoryStream())
        using (var options = new EncoderParameters(1))
        {
            options.Param[0] = new EncoderParameter(Encoder.Quality, 94L);
            var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
            normalized.Save(output, codec, options);
            return output.ToArray();
        }
    }

    internal static bool IsGif(byte[] bytes)
    {
        return bytes != null && bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' &&
            bytes[3] == '8' && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a';
    }

    // A decoded animated GIF: every frame already normalized to opaque 24bpp.
    internal sealed class GifArt : IDisposable
    {
        internal Bitmap[] Frames;
        internal int[] Delays;
        internal int Count { get { return Frames.Length; } }
        public void Dispose() { if (Frames != null) foreach (Bitmap frame in Frames) if (frame != null) frame.Dispose(); }
    }

    // Decoded pixels stay under this budget so a hostile or huge animation
    // cannot fill memory; bigger animations fall back to a still.
    internal const int MaxGifPixels = 48 * 1000 * 1000;
    private const int MaxGifFrames = 600;

    // Decodes an animated GIF. Returns null with a reason when the file is not a
    // multi-frame GIF or would be too big to animate; throws on unreadable data.
    internal static GifArt LoadGif(string path, out string reason)
    {
        return LoadGif(ImageCodec.ReadBytes(path), out reason);
    }

    internal static GifArt LoadGif(byte[] bytes, out string reason)
    {
        return LoadGif(bytes, MaxGifPixels, out reason);
    }

    internal static GifArt LoadGif(byte[] bytes, long maxPixels, out string reason)
    {
        reason = null;
        if (!IsGif(bytes)) { reason = "it is not a GIF"; return null; }
        using (var image = Image.FromStream(new MemoryStream(bytes, false), true, false))
        {
            int count = image.GetFrameCount(FrameDimension.Time);
            if (count < 2) { reason = "it has only one frame"; return null; }
            if (count > MaxGifFrames) { reason = "it has too many frames"; return null; }
            long pixels = (long)image.Width * image.Height * count;
            if (pixels > maxPixels) { reason = "it is too large to animate"; return null; }
            int[] delays = ReadDelays(image, count);
            var art = new GifArt { Frames = new Bitmap[count], Delays = delays };
            try
            {
                for (int i = 0; i < count; i++)
                {
                    image.SelectActiveFrame(FrameDimension.Time, i);
                    var frame = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
                    try
                    {
                        using (var canvas = Graphics.FromImage(frame))
                        {
                            canvas.Clear(Color.FromArgb(8, 20, 29));
                            canvas.DrawImage(image, new Rectangle(0, 0, frame.Width, frame.Height),
                                0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                        }
                        art.Frames[i] = frame;
                    }
                    catch { frame.Dispose(); throw; }
                }
                return art;
            }
            catch (Exception error) { art.Dispose(); throw BadImage(error); }
        }
    }

    // FrameDelay values arrive in hundredths of a second; clamp to a range that
    // is both respectful of the file and safe for the CPU-bound present path.
    private static int[] ReadDelays(Image image, int count)
    {
        var delays = new int[count];
        int[] raw = null;
        const int frameDelayTag = 0x5100;
        if (Array.IndexOf(image.PropertyIdList, frameDelayTag) >= 0)
        {
            byte[] value = image.GetPropertyItem(frameDelayTag).Value;
            if (value != null && value.Length >= 4)
            {
                raw = new int[value.Length / 4];
                for (int i = 0; i < raw.Length; i++) raw[i] = BitConverter.ToInt32(value, i * 4) * 10;
            }
        }
        for (int i = 0; i < count; i++)
        {
            int delay = raw != null && i < raw.Length ? raw[i] : 100;
            delays[i] = Math.Max(50, Math.Min(5000, delay));
        }
        return delays;
    }

    // A folder of stills (the theme's slides\ directory) as a GifArt: sorted
    // by filename, each decoded through the normal image path. Delays are a
    // placeholder — the session substitutes the profile's slide seconds.
    // Unreadable or oversized files are skipped; fewer than two good frames
    // fails the load.
    internal static GifArt LoadSlides(string dir, out string reason)
    {
        reason = null;
        var names = new List<string>();
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase))
                    names.Add(file);
            }
        }
        catch { reason = "the folder is unreadable"; return null; }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        if (names.Count < 2) { reason = "it needs at least two pictures"; return null; }
        if (names.Count > MaxGifFrames) { reason = "it has too many slides"; return null; }
        var art = new GifArt { Frames = new Bitmap[names.Count], Delays = new int[names.Count] };
        int kept = 0;
        long pixels = 0;
        try
        {
            foreach (string file in names)
            {
                Bitmap frame = null;
                try { frame = Decode(new MemoryStream(ReadBytes(file), false), false); }
                catch { if (frame != null) frame.Dispose(); continue; }
                pixels += (long)frame.Width * frame.Height;
                if (pixels > MaxGifPixels) { frame.Dispose(); art.Frames[kept] = null; reason = "the slides are too large to animate"; break; }
                art.Frames[kept] = frame;
                art.Delays[kept] = 1000;
                kept++;
            }
            if (kept < 2) { reason = reason ?? "fewer than two pictures could be read"; art.Dispose(); return null; }
            if (kept < names.Count)
            {
                var frames = new Bitmap[kept]; var delays = new int[kept];
                Array.Copy(art.Frames, frames, kept); Array.Copy(art.Delays, delays, kept);
                art.Frames = frames; art.Delays = delays;
            }
            return art;
        }
        catch { art.Dispose(); throw; }
    }

    // ---- APNG ---------------------------------------------------------------

    private static readonly byte[] PngSig = { 137, 80, 78, 71, 13, 10, 26, 10 };

    // True for a PNG that carries an acTL animation-control chunk before its
    // image data.
    internal static bool IsApng(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 16 ||
            bytes[0] != 137 || bytes[1] != 'P' || bytes[2] != 'N' || bytes[3] != 'G') return false;
        int pos = 8;
        while (pos + 12 <= bytes.Length)
        {
            int length = Int32Be(bytes, pos);
            string type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (type == "acTL") return true;
            if (type == "IDAT" || type == "IEND" || length < 0 || pos + 12 + length > bytes.Length) return false;
            pos += 12 + length;
        }
        return false;
    }

    // Decodes an animated PNG into the same GifArt shape the GIF path produces:
    // frames composited to opaque 24bpp on the same dark clear, delays clamped
    // to 50..5000 ms. Partial frames honour the blend and dispose ops.
    internal static GifArt LoadApng(string path, out string reason)
    {
        return LoadApng(ImageCodec.ReadBytes(path), MaxGifPixels, out reason);
    }

    internal static GifArt LoadApng(byte[] bytes, out string reason)
    {
        return LoadApng(bytes, MaxGifPixels, out reason);
    }

    internal static GifArt LoadApng(byte[] bytes, long maxPixels, out string reason)
    {
        reason = null;
        if (!IsApng(bytes)) { reason = "it is not an APNG"; return null; }
        try
        {
            int pos = 8;
            byte[] ihdr = null, plte = null, trns = null;
            int canvasW = 0, canvasH = 0;
            var frames = new List<byte[]>(); // each: 26-byte fcTL + the frame's image data
            var pending = new List<byte[]>();
            byte[] fc = null;
            while (pos + 12 <= bytes.Length)
            {
                int length = Int32Be(bytes, pos);
                string type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
                if (length < 0 || pos + 12 + length > bytes.Length) break;
                byte[] data = new byte[length];
                Buffer.BlockCopy(bytes, pos + 8, data, 0, length);
                if (type == "IHDR")
                {
                    ihdr = data;
                    canvasW = Int32Be(data, 0); canvasH = Int32Be(data, 4);
                    if (data[12] != 0) { reason = "interlaced APNGs are not supported"; return null; }
                }
                else if (type == "PLTE") plte = data;
                else if (type == "tRNS") trns = data;
                else if (type == "fcTL") { FlushApng(frames, ref fc, pending); fc = data; }
                else if (type == "IDAT" || type == "fdAT")
                {
                    if (fc != null)
                    {
                        if (type == "fdAT" && length > 4) // strip the sequence number
                        {
                            byte[] body = new byte[length - 4];
                            Buffer.BlockCopy(data, 4, body, 0, length - 4);
                            data = body;
                        }
                        if (data.Length > 0) pending.Add(data);
                    }
                }
                else if (type == "IEND") break;
                pos += 12 + length;
            }
            FlushApng(frames, ref fc, pending);
            if (ihdr == null || canvasW <= 0 || canvasH <= 0) { reason = "it is not a PNG"; return null; }
            if (frames.Count < 2) { reason = "it has only one frame"; return null; }
            if (frames.Count > MaxGifFrames) { reason = "it has too many frames"; return null; }
            if ((long)canvasW * canvasH * frames.Count > maxPixels) { reason = "it is too large to animate"; return null; }

            var art = new GifArt { Frames = new Bitmap[frames.Count], Delays = new int[frames.Count] };
            try
            {
                using (var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(canvas))
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        byte[] ctl = frames[i];
                        int w = Int32Be(ctl, 4), h = Int32Be(ctl, 8);
                        int x = Int32Be(ctl, 12), y = Int32Be(ctl, 16);
                        int num = UInt16Be(ctl, 20), den = UInt16Be(ctl, 22);
                        int dispose = ctl[24], blend = ctl[25];
                        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > canvasW || y + h > canvasH)
                            { reason = "a frame does not fit the canvas"; art.Dispose(); return null; }
                        Bitmap snapshot = dispose == 2 ? (Bitmap)canvas.Clone() : null;
                        using (Bitmap patch = Decode(new MemoryStream(ApngPng(ihdr, plte, trns, ctl), false), false))
                        {
                            g.CompositingMode = blend == 0 ? CompositingMode.SourceCopy : CompositingMode.SourceOver;
                            g.DrawImage(patch, new Rectangle(x, y, w, h), 0, 0, patch.Width, patch.Height, GraphicsUnit.Pixel);
                        }
                        var frame = new Bitmap(canvasW, canvasH, PixelFormat.Format24bppRgb);
                        using (var fg = Graphics.FromImage(frame))
                        {
                            fg.Clear(Color.FromArgb(8, 20, 29));
                            fg.DrawImage(canvas, 0, 0, canvasW, canvasH);
                        }
                        art.Frames[i] = frame;
                        int delay = num * 1000 / Math.Max(1, den == 0 ? 100 : den);
                        art.Delays[i] = Math.Max(50, Math.Min(5000, delay));
                        if (dispose == 1) using (var cg = Graphics.FromImage(canvas))
                            { cg.CompositingMode = CompositingMode.SourceCopy; cg.FillRectangle(Brushes.Transparent, x, y, w, h); }
                        else if (dispose == 2 && snapshot != null)
                            using (var cg = Graphics.FromImage(canvas))
                            { cg.CompositingMode = CompositingMode.SourceCopy; cg.DrawImage(snapshot, 0, 0, canvasW, canvasH); }
                        if (snapshot != null) snapshot.Dispose();
                    }
                }
                return art;
            }
            catch { art.Dispose(); throw; }
        }
        catch (Exception error) { throw BadImage(error); }
    }

    private static void FlushApng(List<byte[]> frames, ref byte[] fc, List<byte[]> pending)
    {
        if (fc == null) return;
        if (fc.Length < 26 || pending.Count == 0) { fc = null; pending.Clear(); return; }
        using (var data = new MemoryStream())
        {
            data.Write(fc, 0, 26);
            foreach (byte[] blob in pending) data.Write(blob, 0, blob.Length);
            frames.Add(data.ToArray());
        }
        fc = null; pending.Clear();
    }

    // Rebuilds a standalone PNG for one frame: the IHDR patched to the frame's
    // patch rectangle (never interlaced), its compressed data as a single IDAT.
    // The frame blob is the 26-byte fcTL followed by the image data.
    private static byte[] ApngPng(byte[] ihdr, byte[] plte, byte[] trns, byte[] frame)
    {
        byte[] patched = (byte[])ihdr.Clone();
        Buffer.BlockCopy(frame, 4, patched, 0, 4);   // frame width
        Buffer.BlockCopy(frame, 8, patched, 4, 4);   // frame height
        patched[12] = 0;                             // never interlaced
        using (var png = new MemoryStream())
        {
            png.Write(PngSig, 0, 8);
            WritePngChunk(png, "IHDR", patched);
            if (plte != null) WritePngChunk(png, "PLTE", plte);
            if (trns != null) WritePngChunk(png, "tRNS", trns);
            WritePngChunk(png, "IDAT", frame, 26, frame.Length - 26);
            WritePngChunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }
    }

    private static void WritePngChunk(Stream png, string type, byte[] data)
    {
        WritePngChunk(png, type, data, 0, data.Length);
    }

    private static void WritePngChunk(Stream png, string type, byte[] data, int offset, int count)
    {
        byte[] length = new byte[4];
        WriteInt32Be(length, 0, count);
        png.Write(length, 0, 4);
        byte[] name = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write(name, 0, 4);
        png.Write(data, offset, count);
        byte[] crc = new byte[4];
        WriteInt32Be(crc, 0, (int)PngCrc(name, data, offset, count));
        png.Write(crc, 0, 4);
    }

    private static uint[] crcTable;
    private static uint PngCrc(byte[] type, byte[] data, int offset, int count)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                crcTable[n] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        for (int i = offset; i < offset + count; i++) crc = crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static int Int32Be(byte[] bytes, int pos)
    {
        return (bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3];
    }

    private static int UInt16Be(byte[] bytes, int pos)
    {
        return (bytes[pos] << 8) | bytes[pos + 1];
    }

    private static void WriteInt32Be(byte[] bytes, int pos, int value)
    {
        bytes[pos] = (byte)(value >> 24); bytes[pos + 1] = (byte)(value >> 16);
        bytes[pos + 2] = (byte)(value >> 8); bytes[pos + 3] = (byte)value;
    }

    // First composited frame of an APNG as a JPEG — the still fallback written
    // next to wallpaper.apng; a plain PNG falls back to its own decode.
    internal static byte[] ApngToJpeg(byte[] bytes)
    {
        string reason;
        using (GifArt art = LoadApng(bytes, out reason))
        using (Bitmap first = art != null ? art.Frames[0] : Decode(new MemoryStream(bytes, false), false))
            return EncodeJpeg(first);
    }

    // First frame of an animated GIF as a JPEG: the still fallback written next
    // to wallpaper.gif so non-animated paths always have a picture.
    internal static byte[] GifToJpeg(byte[] bytes)
    {
        string reason;
        using (GifArt art = LoadGif(bytes, out reason))
        using (Bitmap first = art != null ? art.Frames[0] : Decode(new MemoryStream(bytes, false), false))
            return EncodeJpeg(first);
    }

    internal static void SaveAtomic(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            // Same-directory rename with replace works whether the target exists or not.
            if (!MoveFileEx(temporary, target, 0x1 | 0x8))
                throw new IOException("Windows could not save the file.", new Win32Exception());
        }
        finally { File.Delete(temporary); }
    }

    private static Bitmap Decode(MemoryStream stream, bool jpegOnly)
    {
        var signature = new byte[8];
        int read = stream.Read(signature, 0, signature.Length);
        stream.Position = 0;
        bool jpeg = read >= 3 && signature[0] == 0xFF && signature[1] == 0xD8 && signature[2] == 0xFF;
        bool png = read == 8 && BitConverter.ToUInt64(signature, 0) == 0x0A1A0A0D474E5089UL;
        bool bmp = read >= 2 && signature[0] == 'B' && signature[1] == 'M';
        if (!jpeg && (jpegOnly || !(png || bmp)))
            throw new InvalidDataException("Choose a JPEG, PNG, or BMP image.");
        try
        {
            using (var image = Image.FromStream(stream, true, false))
            {
                if ((long)image.Width * image.Height > MaxPixels)
                    throw new InvalidDataException("Choose an image under 64 megapixels.");
                ApplyOrientation(image);
                var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (var canvas = Graphics.FromImage(bitmap))
                    {
                        canvas.Clear(Color.FromArgb(8, 20, 29));
                        canvas.DrawImage(image, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                            0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                    }
                    return bitmap;
                }
                catch { bitmap.Dispose(); throw; }
            }
        }
        catch (ArgumentException e) { throw BadImage(e); }
        // GDI+ also returns OutOfMemory for invalid or unsupported image data.
        catch (OutOfMemoryException e) { throw BadImage(e); }
        catch (ExternalException e) { throw BadImage(e); }
    }

    private static InvalidDataException BadImage(Exception cause)
    {
        return new InvalidDataException("Windows could not read this image. Choose another JPEG, PNG, or BMP.", cause);
    }

    private static void ApplyOrientation(Image image)
    {
        const int orientationTag = 0x112;
        if (Array.IndexOf(image.PropertyIdList, orientationTag) < 0) return;
        byte[] value = image.GetPropertyItem(orientationTag).Value;
        if (value.Length < 2) return;
        // This two-byte EXIF value can be stored in either byte order.
        switch (value[0] == 0 ? value[1] : value[0])
        {
            case 2: image.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
            case 3: image.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 4: image.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
            case 5: image.RotateFlip(RotateFlipType.Rotate90FlipX); break;
            case 6: image.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 7: image.RotateFlip(RotateFlipType.Rotate270FlipX); break;
            case 8: image.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string oldPath, string newPath, uint flags);
}

using System;
using System.ComponentModel;
using System.Drawing;
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

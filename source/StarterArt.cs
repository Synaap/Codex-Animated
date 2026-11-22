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

// The "gradient" theme key: wallpaper.jpg is generated from two colours and an
// angle instead of a picked image. Stored as "#RRGGBB|#RRGGBB|degrees".
internal struct GradientSpec
{
    internal Color From, To;
    internal int Degrees;

    internal static bool TryParse(string text, out GradientSpec spec)
    {
        spec = new GradientSpec();
        if (string.IsNullOrEmpty(text)) return false;
        string[] parts = text.Split('|');
        if (parts.Length != 3) return false;
        Color from, to;
        int degrees;
        if (!SplashText.TryParseColor(parts[0].Trim(), out from)) return false;
        if (!SplashText.TryParseColor(parts[1].Trim(), out to)) return false;
        if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out degrees)) return false;
        spec.From = from;
        spec.To = to;
        spec.Degrees = ((degrees % 360) + 360) % 360;
        return true;
    }

    internal string Format()
    {
        return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}|#{3:X2}{4:X2}{5:X2}|{6}",
            From.R, From.G, From.B, To.R, To.G, To.B, Degrees);
    }

    // A 1600x900 jpeg of the gradient, ready for ThemeLibrary.CreateFromJpeg.
    internal byte[] RenderJpeg()
    {
        const int width = 1600, height = 900;
        using (var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, width, height), From, To, Degrees))
                {
                    // The default tiling repeats the gradient, which leaves a one-pixel
                    // line of the start colour along the far edge at 180 and 270 degrees.
                    brush.WrapMode = WrapMode.TileFlipXY;
                    g.FillRectangle(brush, 0, 0, width, height);
                }
            }
            return ImageCodec.EncodeJpeg(bitmap);
        }
    }
}

// The starter pack: wallpapers rendered procedurally, so every starter ships
// license-clean by construction (no downloaded assets). Deterministic seeds keep
// the images stable between machines.
internal static class StarterArt
{
    internal static byte[] Aurora()
    {
        var bmp = new Bitmap(1600, 900, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var rand = new Random(1107);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            using (var sky = new LinearGradientBrush(new Rectangle(0, 0, 1600, 900),
                Color.FromArgb(10, 14, 38), Color.FromArgb(18, 60, 70), 90f))
                g.FillRectangle(sky, 0, 0, 1600, 900);
            for (int i = 0; i < 240; i++)
            {
                int a = 40 + rand.Next(160);
                using (var star = new SolidBrush(Color.FromArgb(a, 235, 240, 255)))
                    g.FillEllipse(star, rand.Next(1600), rand.Next(520), rand.Next(2) + 1, rand.Next(2) + 1);
            }
            for (int band = 0; band < 3; band++)
            {
                double phase = band * 2.1, baseY = 130 + band * 90;
                for (int x = -40; x < 1700; x += 14)
                {
                    double sway = Math.Sin(x * .004 + phase) * 90 + Math.Sin(x * .011 + phase * 1.7) * 40;
                    int top = (int)(baseY + sway);
                    int len = 260 + (int)(Math.Sin(x * .007 + phase) * 120);
                    Color c = Color.FromArgb(9 + (band == 1 ? 8 : 0), 90 + rand.Next(40), 200 + rand.Next(55), 120 + rand.Next(60));
                    using (var pen = new Pen(c, 9))
                        g.DrawLine(pen, x, top, x + (float)Math.Sin(x * .005 + phase) * 26, top + len);
                }
            }
            using (var hills = new SolidBrush(Color.FromArgb(235, 6, 10, 24)))
            {
                var pts = new List<PointF> { new PointF(0, 900), new PointF(0, 780) };
                for (int x = 0; x <= 1600; x += 80)
                    pts.Add(new PointF(x, 780 - (float)(Math.Sin(x * .006 + 1) * 46 + Math.Sin(x * .017) * 22)));
                pts.Add(new PointF(1600, 780)); pts.Add(new PointF(1600, 900));
                g.FillPolygon(hills, pts.ToArray());
            }
        }
        return ImageCodec.EncodeJpeg(bmp);
    }

    internal static byte[] PaperDusk()
    {
        var bmp = new Bitmap(1600, 900, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var rand = new Random(421);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            using (var sky = new LinearGradientBrush(new Rectangle(0, 0, 1600, 900),
                Color.FromArgb(64, 26, 66), Color.FromArgb(238, 132, 96), 90f))
                g.FillRectangle(sky, 0, 0, 1600, 900);
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(1120, 170, 190, 190);
            using (var sun = new PathGradientBrush(path))
            {
                sun.CenterColor = Color.FromArgb(245, 255, 224, 170);
                sun.SurroundColors = new[] { Color.FromArgb(0, 255, 224, 170) };
                g.FillEllipse(sun, 1120, 170, 190, 190);
            }
            using (var disc = new SolidBrush(Color.FromArgb(255, 252, 214)))
                g.FillEllipse(disc, 1160, 210, 110, 110);
            int[] bases = { 620, 700, 790 };
            int[] shades = { 120, 190, 245 };
            for (int layer = 0; layer < 3; layer++)
            {
                using (var ridge = new SolidBrush(Color.FromArgb(shades[layer], 26 + layer * 8, 18 + layer * 6, 46 + layer * 10)))
                {
                    var pts = new List<PointF> { new PointF(0, 900), new PointF(0, bases[layer]) };
                    for (int x = 0; x <= 1600; x += 70)
                        pts.Add(new PointF(x, bases[layer] - (float)(Math.Sin(x * (.004 + layer * .002) + layer * 2.4) * (60 - layer * 14) + Math.Sin(x * .013 + layer) * (26 - layer * 6))));
                    pts.Add(new PointF(1600, bases[layer])); pts.Add(new PointF(1600, 900));
                    g.FillPolygon(ridge, pts.ToArray());
                }
            }
            for (int i = 0; i < 26; i++)
            {
                using (var bird = new Pen(Color.FromArgb(200, 30, 20, 40), 2f))
                {
                    int bx = 300 + rand.Next(900), by = 220 + rand.Next(240), s = 5 + rand.Next(7);
                    g.DrawArc(bird, bx - s, by - s / 2, s, s, 200, 140);
                    g.DrawArc(bird, bx, by - s / 2, s, s, 200, 140);
                }
            }
        }
        return ImageCodec.EncodeJpeg(bmp);
    }

    internal static byte[] NightGarden()
    {
        var bmp = new Bitmap(1600, 900, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var rand = new Random(7331);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            using (var sky = new LinearGradientBrush(new Rectangle(0, 0, 1600, 900),
                Color.FromArgb(16, 10, 38), Color.FromArgb(40, 22, 66), 90f))
                g.FillRectangle(sky, 0, 0, 1600, 900);
            Color[] tints = { Color.FromArgb(180, 150, 255), Color.FromArgb(255, 150, 200), Color.FromArgb(140, 210, 235) };
            for (int i = 0; i < 90; i++)
            {
                Color tint = tints[rand.Next(tints.Length)];
                int r = 14 + rand.Next(70), x = rand.Next(1600), y = rand.Next(900);
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddEllipse(x - r, y - r, r * 2, r * 2);
                using (var glow = new PathGradientBrush(path))
                {
                    glow.CenterColor = Color.FromArgb(10 + rand.Next(16), tint.R, tint.G, tint.B);
                    glow.SurroundColors = new[] { Color.FromArgb(0, tint) };
                    g.FillEllipse(glow, x - r, y - r, r * 2, r * 2);
                }
            }
            using (var floor = new SolidBrush(Color.FromArgb(220, 12, 8, 28)))
                g.FillRectangle(floor, 0, 840, 1600, 60);
            for (int i = 0; i < 140; i++)
            {
                int a = 60 + rand.Next(190);
                using (var fly = new SolidBrush(Color.FromArgb(a, 255, 226, 140)))
                    g.FillEllipse(fly, rand.Next(1600), 500 + rand.Next(380), rand.Next(3) + 1, rand.Next(3) + 1);
            }
        }
        return ImageCodec.EncodeJpeg(bmp);
    }

    internal static byte[] SakuraRain()
    {
        var bmp = new Bitmap(1600, 900, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var rand = new Random(9173);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            using (var sky = new LinearGradientBrush(new Rectangle(0, 0, 1600, 900),
                Color.FromArgb(58, 66, 120), Color.FromArgb(233, 176, 196), 35f))
                g.FillRectangle(sky, 0, 0, 1600, 900);
            for (int i = 0; i < 190; i++)
            {
                float x = rand.Next(1700) - 60, y = rand.Next(950) - 30;
                float size = 4 + rand.Next(16), tilt = (float)rand.NextDouble() * 6.28f;
                int a = 60 + rand.Next(150);
                using (var petal = new SolidBrush(Color.FromArgb(a, 255, 205 + rand.Next(40), 215 + rand.Next(30))))
                {
                    var state = g.Save();
                    g.TranslateTransform(x, y);
                    g.RotateTransform(tilt * 57.3f);
                    g.FillEllipse(petal, -size / 2, -size / 4, size, size / 2);
                    g.Restore(state);
                }
            }
            using (var branch = new Pen(Color.FromArgb(235, 38, 26, 44), 13))
            {
                var pts = new List<PointF> { new PointF(-20, 120), new PointF(260, 170), new PointF(520, 150), new PointF(760, 190) };
                g.DrawCurve(branch, pts.ToArray(), .4f);
            }
            for (int i = 0; i < 60; i++)
            {
                float cx = 40 + rand.Next(700), cy = 90 + rand.Next(130), r = 8 + rand.Next(22);
                using (var blossom = new SolidBrush(Color.FromArgb(120 + rand.Next(120), 250, 190 + rand.Next(50), 210 + rand.Next(30))))
                    g.FillEllipse(blossom, cx - r, cy - r, r * 2, r * 2);
            }
        }
        return ImageCodec.EncodeJpeg(bmp);
    }
}

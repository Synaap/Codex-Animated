using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// The controller verifies the official app install, process and top-level HWND before attaching.
internal sealed class PreviewArtworkChild : NativeWindow, IDisposable
{
    private readonly IntPtr parent;
    private readonly uint parentPid;
    private readonly uint parentThread;
    private readonly IntPtr dpiContext;
    private Bitmap image;
    private Look look;
    private Size presented;
    private bool enabled = true, imageChanged;

    internal PreviewArtworkChild(IntPtr parent, uint expectedPid, Bitmap image, Look look,
        string product)
    {
        this.parent = parent;
        this.look = look;
        this.image = image;
        uint actualPid;
        parentThread = GetWindowThreadProcessId(parent, out actualPid);
        if (parentThread == 0 || actualPid != expectedPid || GetAncestor(parent, 2) != parent)
            throw new ArgumentException("The verified source window is unavailable.");
        parentPid = expectedPid;
        dpiContext = GetWindowDpiAwarenessContext(parent);
        IntPtr previous = SetThreadDpiAwarenessContext(dpiContext);
        if (previous == IntPtr.Zero) throw new Win32Exception();
        try
        {
            // Create as a child from the outset. No SetParent call or changes to the host.
            CreateHandle(new CreateParams {
                Parent = parent, Caption = product + " artwork preview", Style = 0x40000000,
                ExStyle = 0x80000 | 0x20 | 0x08000000 | 0x4, // LAYERED, TRANSPARENT, NOACTIVATE, NOPARENTNOTIFY
                X = 0, Y = 0, Width = 1, Height = 1
            });
            if (!AreDpiAwarenessContextsEqual(dpiContext, GetWindowDpiAwarenessContext(Handle)))
                throw new InvalidOperationException("The child DPI context did not match.");
            if ((GetWindowLongPtrW(Handle, -20).ToInt64() & 0x80000) == 0)
                throw new NotSupportedException("The helper manifest does not enable layered child windows.");
            if (!RefreshSurface()) throw new InvalidOperationException("The source window closed during attachment.");
        }
        catch { Dispose(); throw; }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    internal void SetImage(Bitmap replacement)
    {
        image = replacement;
        imageChanged = true;
    }

    internal void SetLook(Look value)
    {
        if (look.SamePresent(value)) return;
        look = value;
        imageChanged = true;
    }

    internal void SetEnabled(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        if (Handle == IntPtr.Zero) return;
        if (!value) ShowWindow(Handle, 0);
    }

    internal bool RefreshSurface()
    {
        uint pid;
        if (Handle == IntPtr.Zero || GetWindowThreadProcessId(parent, out pid) != parentThread || pid != parentPid)
            return false;
        // Use the last valid viewport when a wallpaper changes while minimized.
        bool liveBounds = enabled && !IsIconic(parent) && IsWindowVisible(parent);
        if (!liveBounds && (presented.IsEmpty || (!enabled && !imageChanged))) return true;
        IntPtr previous = SetThreadDpiAwarenessContext(dpiContext);
        if (previous == IntPtr.Zero) throw new Win32Exception();
        try { return Present(liveBounds); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private bool Present(bool liveBounds)
    {
        Size size = presented;
        if (liveBounds)
        {
            Rect rect;
            if (!GetClientRect(parent, out rect) || rect.Right <= 0 || rect.Bottom <= 0)
                return true;
            size = new Size(rect.Right, rect.Bottom);
        }
        if (size != presented || imageChanged)
        {
            bool pixelPass = ArtworkLook.NeedsPixelPass(look);
            using (var frame = new Bitmap(size.Width, size.Height,
                pixelPass ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppRgb))
            {
                RectangleF crop; Rectangle dst; InterpolationMode mode;
                ArtworkLook.FramePlan(image.Size, size, look, out crop, out dst, out mode);
                using (var g = Graphics.FromImage(frame))
                {
                    g.InterpolationMode = mode;
                    if (mode == InterpolationMode.NearestNeighbor) g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(image, dst, crop, GraphicsUnit.Pixel);
                }
                IntPtr bitmap = pixelPass
                    ? ArtworkLook.AlphaDib(ArtworkLook.AlphaPixels(frame, look), size.Width, size.Height)
                    : frame.GetHbitmap();
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                IntPtr old = IntPtr.Zero;
                try
                {
                    if (dc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception();
                    old = SelectObject(dc, bitmap);
                    if (old == IntPtr.Zero || old == new IntPtr(-1)) throw new Win32Exception();
                    var from = new NativePoint();
                    var nativeSize = new NativeSize { W = size.Width, H = size.Height };
                    var blend = new Blend { Alpha = look.Alpha, Format = pixelPass ? (byte)1 : (byte)0 };
                    // Null destination preserves the original child-relative (0,0) across parent moves.
                    if (!UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref nativeSize, dc,
                        ref from, 0, ref blend, 2)) throw new Win32Exception();
                }
                finally
                {
                    if (old != IntPtr.Zero && old != new IntPtr(-1)) SelectObject(dc, old);
                    if (dc != IntPtr.Zero) DeleteDC(dc);
                    if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                }
            }
            presented = size;
            imageChanged = false;
        }
        // Read this child's own visibility bit. A hidden parent otherwise appears
        // to hide every child, even when its thumbnail surface is ready.
        bool shown = (GetWindowLongPtrW(Handle, -16).ToInt64() & 0x10000000) != 0;
        if (enabled && (!shown || (liveBounds && GetWindow(parent, 5) != Handle)))
        {
            // HWND_TOP acts only among siblings. Retain the position and Z-order while minimized.
            uint flags = 0x0040 | 0x0010 | 0x0001;
            if (!liveBounds) flags |= 0x0004 | 0x0002;
            if (!SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, flags))
                throw new Win32Exception();
        }
        return true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; }
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }

    public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int W,H; }
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint which);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window,uint kind);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr window,int index);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr window, IntPtr insert, int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destDC, IntPtr dest, ref NativeSize size, IntPtr sourceDC, ref NativePoint source, uint key, ref Blend blend, uint flags);
}

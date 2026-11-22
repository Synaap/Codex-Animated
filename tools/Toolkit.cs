using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

// Read-only window helpers for the test tools. Nothing here takes focus, sends keys or clicks,
// or moves, resizes or shows another program's window.
public static class TK
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
    delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    static TK() { SetProcessDPIAware(); }

    public class Win
    {
        public long Handle, Owner, Style, ExStyle;
        public string Title, Class, Rect;
        public uint Pid;
        public bool Visible, Minimized, Topmost;
        public override string ToString()
        {
            return string.Format("hwnd={0} pid={1} vis={2} top={3} rect={4} class={5} title='{6}'", Handle, Pid, Visible, Topmost, Rect, Class, Title);
        }
    }

    static Win Describe(IntPtr window)
    {
        var title = new StringBuilder(256); GetWindowText(window, title, 256);
        var cls = new StringBuilder(256); GetClassName(window, cls, 256);
        uint pid; GetWindowThreadProcessId(window, out pid);
        RECT r; GetWindowRect(window, out r);
        long ex = GetWindowLongPtr(window, -20).ToInt64();
        return new Win {
            Handle = window.ToInt64(), Owner = GetWindow(window, 4).ToInt64(), Style = GetWindowLongPtr(window, -16).ToInt64(), ExStyle = ex,
            Title = title.ToString(), Class = cls.ToString(), Pid = pid, Visible = IsWindowVisible(window), Minimized = IsIconic(window),
            Topmost = (ex & 8) != 0, Rect = r.L + "," + r.T + "," + r.R + "," + r.B
        };
    }

    public static List<Win> TopLevel()
    {
        var list = new List<Win>();
        EnumWindows(delegate(IntPtr h, IntPtr d) { list.Add(Describe(h)); return true; }, IntPtr.Zero);
        return list;
    }

    public static List<Win> Children(long parent)
    {
        var list = new List<Win>();
        EnumChildWindows(new IntPtr(parent), delegate(IntPtr h, IntPtr d) { list.Add(Describe(h)); return true; }, IntPtr.Zero);
        return list;
    }

    // PrintWindow with PW_RENDERFULLCONTENT: captures the window without bringing it forward.
    public static void Capture(long hwnd, string path)
    {
        var window = new IntPtr(hwnd);
        RECT r; GetWindowRect(window, out r);
        using (var bitmap = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), PixelFormat.Format32bppRgb))
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                IntPtr dc = g.GetHdc();
                try { PrintWindow(window, dc, 2); } finally { g.ReleaseHdc(dc); }
            }
            bitmap.Save(path, ImageFormat.Png);
        }
    }

    public static void Screen(int x, int y, int w, int h, string path)
    {
        using (var bitmap = new Bitmap(w, h, PixelFormat.Format32bppRgb))
        {
            using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            bitmap.Save(path, ImageFormat.Png);
        }
    }

    public static string MeanRgb(string path, int x, int y, int w, int h)
    {
        using (var bitmap = new Bitmap(path))
        {
            long r = 0, g = 0, b = 0; int n = 0;
            for (int j = y; j < Math.Min(bitmap.Height, y + h); j++)
                for (int i = x; i < Math.Min(bitmap.Width, x + w); i++) { Color c = bitmap.GetPixel(i, j); r += c.R; g += c.G; b += c.B; n++; }
            return string.Format("{0:0.0},{1:0.0},{2:0.0} (n={3})", (double)r / n, (double)g / n, (double)b / n, n);
        }
    }

    public static string Client(long hwnd)
    {
        var window = new IntPtr(hwnd); RECT r; GetClientRect(window, out r);
        POINT p = new POINT(); ClientToScreen(window, ref p);
        return p.X + "," + p.Y + "," + (p.X + r.R) + "," + (p.Y + r.B);
    }

    public static long Foreground() { return GetForegroundWindow().ToInt64(); }

    // SHQueryUserNotificationState, 0 on failure.
    public static int UserState()
    {
        int state;
        try { return SHQueryUserNotificationState(out state) == 0 ? state : 0; }
        catch (EntryPointNotFoundException) { return 0; }
        catch (DllNotFoundException) { return 0; }
    }

    // The same test as QuietTime.IsFullScreen, on the window in front.
    public static bool FrontIsFullScreen()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
        var cls = new StringBuilder(64); GetClassName(window, cls, 64);
        if (cls.ToString() == "Progman" || cls.ToString() == "WorkerW") return false;
        var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
        RECT bounds;
        IntPtr monitor = MonitorFromWindow(window, 2);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(window, out bounds)) return false;
        return bounds.L <= info.Monitor.L && bounds.T <= info.Monitor.T && bounds.R >= info.Monitor.R && bounds.B >= info.Monitor.B;
    }
}

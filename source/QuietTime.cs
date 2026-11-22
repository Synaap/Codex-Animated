using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// Whether the startup animation should stay away: Windows reports a full-screen game or
// presentation mode, or a full-screen window of another program is in front.
internal static class QuietTime
{
    internal const int Busy = 2, DirectXFullScreen = 3, PresentationMode = 4;

    // The rule. state is what SHQueryUserNotificationState reports, 0 when unknown.
    // Windows also reports Busy for the app's own full-screen window, so Busy only counts
    // when the window in front covers its monitor and is not the app's.
    internal static string Reason(int state, bool frontIsFullScreen, bool frontIsApp)
    {
        if (state == DirectXFullScreen) return "Windows reports a full-screen game in front";
        if (state == PresentationMode) return "Windows reports presentation mode";
        if (state == Busy && frontIsFullScreen && !frontIsApp) return "a full-screen window of another program is in front";
        return null;
    }

    // Null when the animation may play. ownsProcess tells whether a process id belongs to the app.
    internal static string Check(Func<uint, bool> ownsProcess)
    {
        int state = State();
        if (state != Busy) return Reason(state, false, false);
        IntPtr front = GetForegroundWindow();
        uint pid = 0;
        bool app = front != IntPtr.Zero && GetWindowThreadProcessId(front, out pid) != 0 && ownsProcess(pid);
        return Reason(state, IsFullScreen(front), app);
    }

    internal static int State()
    {
        int value;
        string forced = Environment.GetEnvironmentVariable("ANIMATED_USER_STATE"); // Test switch.
        if (!string.IsNullOrEmpty(forced) && int.TryParse(forced, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return value;
        try { return SHQueryUserNotificationState(out value) == 0 ? value : 0; }
        catch (EntryPointNotFoundException) { return 0; }
        catch (DllNotFoundException) { return 0; }
    }

    // A visible window that covers its whole monitor. The desktop itself does not count.
    internal static bool IsFullScreen(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        string cls = name.ToString();
        if (cls == "Progman" || cls == "WorkerW") return false;
        Rect bounds;
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        IntPtr monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(window, out bounds)) return false;
        return bounds.Left <= info.Monitor.Left && bounds.Top <= info.Monitor.Top &&
            bounds.Right >= info.Monitor.Right && bounds.Bottom >= info.Monitor.Bottom;
    }

    // The app's own window in real full screen (F11, a video, a presentation). A
    // maximized window also covers a monitor that has no taskbar or an auto-hiding
    // one, but it is still a normal window: Chromium and most apps leave the
    // maximized state before they go full screen, so IsZoomed tells them apart.
    internal static bool IsAppFullScreen(IntPtr window)
    {
        return window != IntPtr.Zero && !IsZoomed(window) && IsFullScreen(window);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal int Size; internal Rect Monitor, Work; internal uint Flags; }
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

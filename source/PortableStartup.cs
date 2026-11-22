using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class PortableStartup
{
    private static readonly string[] runtimeFiles = {
        "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll",
        "web/index.html", "web/style.css", "web/animation.js", "web/assets/artwork.jpg",
        "web/assets/avatar.jpg", "web/assets/contours.js",
        "notices/LICENSE.txt", "notices/NOTICE.txt"
    };
    internal static string BundleDirectory { get; private set; }

    // The app this copy stands for: its file name, or --app <id> for the neutral
    // AnimatedLauncher.exe. The shared helper (--artwork) has no app and uses the
    // shared folder for its bundle and log.
    internal static App ResolveApp(string[] args)
    {
        App app = AppTable.ByExeName(Path.GetFileName(Application.ExecutablePath));
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--app")
            {
                App named = AppTable.ById(args[i + 1]);
                if (named != null) app = named;
            }
        return app;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        App app = ResolveApp(args);
        string root = app != null ? app.SettingsDirectory : ThemeLibrary.SharedRoot;
        AppLog.Start(root, Mode(args));
        try
        {
            BundleDirectory = EmbeddedBundle.Prepare(typeof(PortableStartup).Assembly, root, "AnimatedLauncher.Bundle.", runtimeFiles);
            EmbeddedBundle.ResolveWebViewFrom(BundleDirectory);
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception ||
                ex is System.Security.SecurityException)) throw;
            AppLog.Write("start", "could not prepare the launcher files: " + ex.Message);
            MessageBox.Show("Windows could not prepare the launcher files.\n\n" + ex.Message,
                app != null ? app.Product : "Animated Launchers", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return RunLauncher(args);
    }

    // One word for what this process does, for the log. Kept here so Main touches no WebView2 type.
    private static string Mode(string[] args)
    {
        if (args.Length == 0) return "launcher";
        switch (args[0])
        {
            case "--artwork": return "helper";
            case "--splash": return "animation";
            case "--preview": return "preview";
        }
        return "other";
    }

    // Keep WebView2 references outside this entry point until the resolver is ready.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunLauncher(string[] args) { return Launcher.Run(args); }
}

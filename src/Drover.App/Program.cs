using Drover.App.Services;
using Velopack;

namespace Drover.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack must run before any UI work — when the app is launched by the
        // installer/updater bootstrapper with --veloapp-* args, this handles them
        // and exits without showing a window. In normal launches it returns
        // immediately and we proceed to start WPF.
        VelopackApp.Build()
            .SetLogger(AppLog.VelopackLogger)
            .Run();

        // `drover .` / Explorer "Open in Drover": if Drover is already running, hand the
        // folder to it and exit instead of starting a second window.
        var folder = FolderLaunch.ParseFolderArg(args);
        if (folder is not null && FolderLaunch.TryHandOff(folder)) return 0;

        var app = new App { StartupFolder = folder };
        app.InitializeComponent();
        return app.Run();
    }
}

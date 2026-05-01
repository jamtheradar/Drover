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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

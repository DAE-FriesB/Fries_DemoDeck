using Velopack;

namespace Ex.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: when the installer or an update starts the exe with a Velopack command, this handles it
        // (shortcuts, uninstall, restart after update) and exits. On a normal start it returns immediately.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

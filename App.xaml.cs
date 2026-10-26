using System.Windows;

namespace PdfTranslator;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless mode for scripted use and testing: see Cli.Usage.
        if (e.Args.Length > 0 && e.Args[0].StartsWith("--"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await Cli.RunAsync(e.Args);
            Shutdown(code);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0 && File.Exists(e.Args[0])) window.OpenFile(e.Args[0]);
    }
}

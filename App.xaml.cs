using System.Windows;

namespace FastImageViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? startupPath = e.Args.Length > 0 ? e.Args[0] : null;
        var mainWindow = new MainWindow(startupPath);
        mainWindow.Show();
    }
}

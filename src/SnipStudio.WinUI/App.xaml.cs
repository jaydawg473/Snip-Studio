using Microsoft.UI.Xaml;

namespace SnipStudio.WinUI;

public partial class App : Application
{
    private MainWindow? mainWindow;
    private bool startupCompleted;

    public App()
    {
        StartupDiagnostics.Stage("App.constructor.begin");
        UnhandledException += (_, args) =>
        {
            // Observation only. Do not mark the failure handled or continue after
            // an unknown XAML/runtime fault.
            if (!startupCompleted)
                StartupDiagnostics.Failure("Application.UnhandledException", args.Exception);
        };
        try
        {
            StartupDiagnostics.Stage("App.InitializeComponent.begin");
            InitializeComponent();
            StartupDiagnostics.Stage("App.InitializeComponent.completed");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Failure("App.InitializeComponent.failed", exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Deliberately does not call the legacy Program, preferences, taskbar,
        // shortcut broker, startup registration, updater or notification-area code.
        try
        {
            StartupDiagnostics.Stage("OnLaunched.MainWindow.constructor.begin");
            mainWindow = new MainWindow();
            StartupDiagnostics.Stage("OnLaunched.MainWindow.constructor.completed");
            mainWindow.Activate();
            StartupDiagnostics.Stage("OnLaunched.MainWindow.Activate.completed");
            startupCompleted = true;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Failure("OnLaunched.failed", exception);
            throw;
        }
    }
}

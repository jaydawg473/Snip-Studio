using Microsoft.UI.Xaml;

namespace SnipStudio.WinUI;

public partial class App : Application
{
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
        // WinUI adapters own an isolated identity and settings. The legacy
        // Program and its stable-channel integrations are never initialized.
        try
        {
            StartupDiagnostics.Stage("OnLaunched.MainWindow.constructor.begin");
            var mainWindow = new MainWindow();
            StartupDiagnostics.Stage("OnLaunched.MainWindow.constructor.completed");
            mainWindow.Activate();
            StartupDiagnostics.Stage("OnLaunched.MainWindow.Activate.completed");
            startupCompleted = true;
            // File arguments are consumed only after native XAML activation.
            // The editor validates the file before replacing its current work.
            string[] arguments = Environment.GetCommandLineArgs();
            if (arguments.Contains("--background", StringComparer.OrdinalIgnoreCase))
                mainWindow.TryStartInBackground();
            if (arguments.Length > 1 && !arguments[1].StartsWith("-", StringComparison.Ordinal))
                _ = mainWindow.OpenInitialPathAsync(arguments[1]);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Failure("OnLaunched.failed", exception);
            throw;
        }
    }
}

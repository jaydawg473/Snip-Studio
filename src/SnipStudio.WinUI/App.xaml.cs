using Microsoft.UI.Xaml;
using SnipStudio.WinUI.Backend;

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
        // Initialize the original Snip Studio shell identity through the WinUI adapters.
        try
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (DesktopJumpList.IsCloseAllWithoutSavingRequest(arguments))
            {
                startupCompleted = true;
                _ = DispatchTaskbarCloseRequestAsync();
                return;
            }
            DesktopJumpList.Initialize();
            StartupDiagnostics.Stage("OnLaunched.MainWindow.constructor.begin");
            var mainWindow = new MainWindow();
            StartupDiagnostics.Stage("OnLaunched.MainWindow.constructor.completed");
            mainWindow.Activate();
            StartupDiagnostics.Stage("OnLaunched.MainWindow.Activate.completed");
            startupCompleted = true;
            // File arguments are consumed only after native XAML activation.
            // The editor validates the file before replacing its current work.
            if (DesktopJumpList.TryGetCaptureDelay(arguments, out int seconds))
                _ = mainWindow.CaptureFromTaskbarAsync(seconds);
            else if (arguments.Contains("--background", StringComparer.OrdinalIgnoreCase))
                mainWindow.TryStartInBackground();
            else if (arguments.Length > 1 && !arguments[1].StartsWith("-", StringComparison.Ordinal))
                _ = mainWindow.OpenInitialPathAsync(arguments[1]);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Failure("OnLaunched.failed", exception);
            throw;
        }
    }

    private async Task DispatchTaskbarCloseRequestAsync()
    {
        try { await Task.Run(TaskbarEditorCloseRequest.NotifyRunningEditors); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            StartupDiagnostics.Failure("Taskbar close request failed", exception);
        }
        finally { Exit(); }
    }
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using SnipStudio.WinUI;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckOfflinePrivacyBoundaries()
    {
        var start = new ProcessStartInfo(@"C:\Fixture\Bundled Runtime\node.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add(@"C:\Fixture\Bundled Runtime\worker.cjs");
        string[] inheritedKeys =
        [
            "NODE_OPTIONS", "NODE_PATH", "NODE_V8_COVERAGE", "NODE_COMPILE_CACHE",
            "NODE_REDIRECT_WARNINGS", "NODE_EXTRA_CA_CERTS", "OPENSSL_CONF",
            "HTTPS_PROXY", "SNIPSTUDIO_TEST_TOKEN", "TEMP"
        ];
        foreach (string key in inheritedKeys)
            start.Environment[key] = "Synthetic inherited value";
        start.Environment["SystemRoot"] = @"Q:\WrongWindows";
        start.Environment["PATH"] = @"Q:\OutsideBundle";

        OfflineWorkerEnvironment.Configure(start);

        Check(inheritedKeys.All(key => !start.Environment.ContainsKey(key)) && start.Environment.Count == 3,
            "Bundled offline workers exclude inherited Node, proxy, diagnostics and unrelated variables");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Check(start.Environment["SystemRoot"] == windows && start.Environment["WINDIR"] == windows &&
            start.Environment["PATH"] == Environment.SystemDirectory + Path.PathSeparator + windows,
            "The offline worker receives Windows-derived runtime directories only");
        Check(start.FileName == @"C:\Fixture\Bundled Runtime\node.exe" &&
            start.ArgumentList.SequenceEqual([@"C:\Fixture\Bundled Runtime\worker.cjs"]) &&
            start.RedirectStandardInput && !start.UseShellExecute,
            "Worker isolation preserves the reviewed executable, command argument, and pipe options");

        const string privatePath = @"Q:\FixtureCustomer\example-document.png";
        const string privateMessage = "fixture transcription content";
        const string stage = "OnLaunched.failed";
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Exception failure = CapturePrivacyFixtureFailure(privatePath, privateMessage);
        string entry = StartupDiagnostics.FormatEntry(timestamp, stage, failure);
        Check(entry.StartsWith(timestamp.ToString("O") + " " + stage + Environment.NewLine, StringComparison.Ordinal) &&
            entry.Contains("type=System.InvalidOperationException hresult=0x", StringComparison.Ordinal) &&
            entry.Contains("type=System.IO.FileNotFoundException hresult=0x", StringComparison.Ordinal) &&
            entry.Contains(nameof(ThrowPrivacyFixtureFailure), StringComparison.Ordinal),
            "Startup diagnostics retain the stage, exception categories, error codes and method names");
        Check(!entry.Contains(privatePath, StringComparison.Ordinal) &&
            !entry.Contains(privateMessage, StringComparison.Ordinal) &&
            !entry.Contains("PrivacyDiagnosticsChecks.cs", StringComparison.Ordinal) &&
            !entry.Contains("message=", StringComparison.Ordinal),
            "Startup logs omit free-form exception text, external paths and stack source files");
        Check(StartupDiagnostics.FormatEntry(timestamp, "App.constructor.begin", null) ==
            timestamp.ToString("O") + " App.constructor.begin" + Environment.NewLine,
            "Successful startup stages record a timestamp and fixed label alone");
    }

    private static Exception CapturePrivacyFixtureFailure(string privatePath, string privateMessage)
    {
        try
        {
            ThrowPrivacyFixtureFailure(privatePath, privateMessage);
        }
        catch (FileNotFoundException failure)
        {
            return new InvalidOperationException(privateMessage, failure);
        }
        throw new InvalidOperationException("The diagnostic fixture must throw.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrivacyFixtureFailure(string privatePath, string privateMessage) =>
        throw new FileNotFoundException(privateMessage, privatePath);
}

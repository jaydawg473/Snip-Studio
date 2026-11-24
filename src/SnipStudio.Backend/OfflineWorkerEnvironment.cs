using System;
using System.Diagnostics;
using System.IO;

namespace SnipStudio.WinUI.Backend;

internal static class OfflineWorkerEnvironment
{
    internal static void Configure(ProcessStartInfo start)
    {
        // Both reviewed workers use bundled code and models. A minimal child
        // environment prevents parent Node caches, diagnostic files and module
        // search paths from changing the packaged runtime's behavior.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system = Environment.SystemDirectory;
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["PATH"] = system + Path.PathSeparator + windows;
    }
}

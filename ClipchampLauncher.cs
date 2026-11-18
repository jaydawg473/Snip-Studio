using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace SnipStudio
{
    // Use the installed application's registered file contract. Do not change
    // default video associations or construct undocumented file-import URIs.
    internal static class ClipchampLauncher
    {
        internal static bool Open(string path, IntPtr owner)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException("path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("This recording file is no longer available.");
            string fileClass = FindFileClass(Path.GetExtension(fullPath));
            if (fileClass != null)
            {
                ShellExecuteInfo request = BuildFileActivation(fullPath, fileClass, owner);
                if (ShellExecuteEx(ref request)) return true;
                // Preserve the OS error instead of silently opening an unrelated
                // default video player or reporting that an import succeeded.
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not send the recording to Clipchamp. Open Clipchamp and use Import media instead.");
            }
            if (!HasAppProtocol())
                throw new InvalidOperationException("Microsoft Clipchamp is not installed or registered for this user. Install or repair Clipchamp, then try again.");
            Process.Start(new ProcessStartInfo("ms-clipchamp:") { UseShellExecute = true });
            return false;
        }

        internal static string FindFileClass(string extension)
        {
            if (String.IsNullOrEmpty(extension) || extension[0] != '.' || extension.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0) return null;
            try
            {
                using (RegistryKey candidates = Registry.ClassesRoot.OpenSubKey(extension + "\\OpenWithProgids"))
                {
                    if (candidates == null) return null;
                    foreach (string candidate in candidates.GetValueNames())
                    {
                        if (String.IsNullOrEmpty(candidate) || candidate.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0) continue;
                        using (RegistryKey application = Registry.ClassesRoot.OpenSubKey(candidate + "\\Application"))
                        {
                            if (application == null || !IsClipchampApplication(application.GetValue("AppUserModelID") as string)) continue;
                            using (RegistryKey open = Registry.ClassesRoot.OpenSubKey(candidate + "\\shell\\open"))
                                if (open != null) return candidate;
                        }
                    }
                }
            }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return null;
        }

        internal static bool IsClipchampApplication(string appId)
        {
            return !String.IsNullOrEmpty(appId) && appId.StartsWith("Clipchamp.Clipchamp_", StringComparison.OrdinalIgnoreCase) &&
                appId.EndsWith("!App", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAppProtocol()
        {
            try
            {
                using (RegistryKey protocol = Registry.ClassesRoot.OpenSubKey("ms-clipchamp"))
                    return protocol != null && protocol.GetValue("URL Protocol") != null;
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        internal static ShellExecuteInfo BuildFileActivation(string path, string fileClass, IntPtr owner)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException("path");
            if (String.IsNullOrWhiteSpace(fileClass) || fileClass.IndexOfAny(new char[] { '\\', '/', ':' }) >= 0)
                throw new ArgumentException("Use a registered file class.", "fileClass");
            ShellExecuteInfo request = new ShellExecuteInfo();
            request.Size = Marshal.SizeOf(typeof(ShellExecuteInfo));
            request.Mask = 0x00000001 | 0x00000400 | 0x04000000; // CLASSNAME, NO_UI, LOG_USAGE
            request.Owner = owner;
            request.Verb = "open";
            request.File = Path.GetFullPath(path);
            request.Class = fileClass;
            request.Show = 1; // SW_SHOWNORMAL; the target decides how to present its project.
            return request;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ShellExecuteInfo
        {
            internal int Size;
            internal uint Mask;
            internal IntPtr Owner;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Verb;
            [MarshalAs(UnmanagedType.LPWStr)] internal string File;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Parameters;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Directory;
            internal int Show;
            internal IntPtr Instance;
            internal IntPtr IdList;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Class;
            internal IntPtr ClassKey;
            internal uint HotKey;
            internal IntPtr IconOrMonitor;
            internal IntPtr Process;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellExecuteEx(ref ShellExecuteInfo request);
    }
}

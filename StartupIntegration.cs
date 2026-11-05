using System;
using System.IO;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnipStudio
{
    public static class StartupIntegration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Snip Studio - Automation Forge";

        // The quoted executable is run directly. No command interpreter is involved.
        public static string BuildCommandLine(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
                throw new ArgumentException("Choose an absolute path to Snip Studio.exe.", "executablePath");
            if (executablePath.IndexOf('"') >= 0 || executablePath.IndexOf('\r') >= 0 || executablePath.IndexOf('\n') >= 0)
                throw new ArgumentException("The application path contains invalid characters.", "executablePath");
            string fullPath = Path.GetFullPath(executablePath);
            if (!String.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The startup application must be an .exe file.", "executablePath");
            string command = "\"" + fullPath + "\" --background";
            if (command.Length > 260)
                throw new InvalidOperationException("This application path is too long for a Windows startup entry. Move Snip Studio to a shorter path, then enable startup again.");
            return command;
        }

        // Reports this app's configured Run entry. Windows Startup Apps can separately
        // disable or delay an entry; a matching value is not proof of the next logon.
        public static bool GetEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return key != null && String.Equals(key.GetValue(ValueName) as string,
                        BuildCommandLine(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
            }
            catch (SecurityException ex) { throw new InvalidOperationException("Windows did not allow Snip Studio to read its startup setting.", ex); }
            catch (UnauthorizedAccessException ex) { throw new InvalidOperationException("Windows did not allow Snip Studio to read its startup setting.", ex); }
        }

        // Call only when the user saves a changed startup preference, never merely
        // because the app starts or reads its settings.
        public static void SetEnabled(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    if (!File.Exists(Application.ExecutablePath))
                        throw new InvalidOperationException("The Snip Studio application file is unavailable. Startup was not changed.");
                    string command = BuildCommandLine(Application.ExecutablePath);
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (key == null) throw new InvalidOperationException("The Windows startup key could not be opened.");
                        key.SetValue(ValueName, command, RegistryValueKind.String);
                        if (!String.Equals(key.GetValue(ValueName) as string, command, StringComparison.Ordinal))
                            throw new InvalidOperationException("Windows did not confirm the new startup setting.");
                    }
                }
                else
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (key == null) return;
                        key.DeleteValue(ValueName, false);
                        if (key.GetValue(ValueName) != null)
                            throw new InvalidOperationException("Windows did not confirm removal of the startup setting.");
                    }
                }
            }
            catch (SecurityException ex) { throw new InvalidOperationException("Windows did not allow Snip Studio to change its startup entry. Your other startup applications were not changed.", ex); }
            catch (UnauthorizedAccessException ex) { throw new InvalidOperationException("Windows did not allow Snip Studio to change its startup entry. Your other startup applications were not changed.", ex); }
            catch (IOException ex) { throw new InvalidOperationException("The startup setting could not be saved. Please try again.", ex); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SnipStudio
{
    public sealed class InstalledEditor
    {
        public string Name { get; set; }
        public string ExecutablePath { get; set; }
        public override string ToString() { return Name ?? "Editing application"; }
    }

    // Read-only discovery: registrations and a small set of known install folders.
    // Nothing found here is executed, and arbitrary registry command lines are rejected.
    public static class InstalledEditorFinder
    {
        private sealed class EditorFamily
        {
            internal string Name;
            internal string[] Executables;
            internal EditorFamily(string name, params string[] executables) { Name = name; Executables = executables; }
        }

        private static readonly EditorFamily[] Families = {
            new EditorFamily("Paint", "mspaint.exe", "PaintApp.exe"),
            new EditorFamily("Adobe Photoshop", "Photoshop.exe"),
            new EditorFamily("Adobe Photoshop Elements", "PhotoshopElementsEditor.exe"),
            new EditorFamily("GIMP", "gimp-3.exe", "gimp-3.0.exe", "gimp-2.10.exe", "gimp-2.8.exe", "gimp.exe"),
            new EditorFamily("Paint.NET", "paintdotnet.exe"),
            new EditorFamily("Krita", "krita.exe"),
            new EditorFamily("Affinity Photo", "Photo.exe", "AffinityPhoto.exe", "AffinityPhoto2.exe"),
            new EditorFamily("Affinity", "Affinity.exe"),
            new EditorFamily("Corel PaintShop Pro", "Corel PaintShop Pro.exe", "PaintShop Pro.exe"),
            new EditorFamily("Corel PHOTO-PAINT", "CorelPP.exe")
        };

        public static Task<IList<InstalledEditor>> DiscoverAsync()
        {
            return Task.Run<IList<InstalledEditor>>(delegate { return Discover(); });
        }

        public static IList<InstalledEditor> Discover()
        {
            Dictionary<string, InstalledEditor> editors = new Dictionary<string, InstalledEditor>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryHive hive in new RegistryHive[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (RegistryView view in new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadRegistrations(editors, hive, view);
            }
            AddPaint(editors);
            AddUserAliases(editors);
            ReadKnownFolders(editors);
            List<InstalledEditor> result = new List<InstalledEditor>(editors.Values);
            result.Sort(delegate(InstalledEditor first, InstalledEditor second)
            {
                if (first.Name == "Paint" && second.Name != "Paint") return -1;
                if (second.Name == "Paint" && first.Name != "Paint") return 1;
                int name = StringComparer.CurrentCultureIgnoreCase.Compare(first.Name, second.Name);
                return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(first.ExecutablePath, second.ExecutablePath);
            });
            DisambiguateNames(result);
            return result.AsReadOnly();
        }

        private static void ReadRegistrations(Dictionary<string, InstalledEditor> editors, RegistryHive hive, RegistryView view)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                {
                    using (RegistryKey paths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", false))
                    {
                        if (paths != null)
                            foreach (EditorFamily family in Families)
                            foreach (string executable in family.Executables)
                            {
                                try
                                {
                                    using (RegistryKey app = paths.OpenSubKey(executable, false))
                                        if (app != null) Add(editors, app.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, family, false);
                                }
                                catch (Exception) { }
                            }
                    }
                    using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
                    {
                        if (uninstall == null) return;
                        string[] keys = uninstall.GetSubKeyNames();
                        int limit = Math.Min(keys.Length, 4096);
                        for (int index = 0; index < limit; index++)
                        {
                            try
                            {
                                using (RegistryKey item = uninstall.OpenSubKey(keys[index], false))
                                {
                                    if (item == null) continue;
                                    EditorFamily family = FamilyFromDisplayName(item.GetValue("DisplayName") as string);
                                    if (family == null) continue;
                                    Add(editors, item.GetValue("DisplayIcon", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, family, true);
                                    string directory = NormalizeLocalDirectory(item.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                                    if (directory != null) AddInstallDirectory(editors, directory, family);
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        private static EditorFamily FamilyFromDisplayName(string displayName)
        {
            if (String.IsNullOrWhiteSpace(displayName)) return null;
            string name = displayName.Trim();
            if (name.IndexOf("Photoshop Elements", StringComparison.OrdinalIgnoreCase) >= 0) return Families[2];
            if (name.IndexOf("Photoshop", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Adobe", StringComparison.OrdinalIgnoreCase) >= 0) return Families[1];
            if (Regex.IsMatch(name, @"\bGIMP\b", RegexOptions.IgnoreCase)) return Families[3];
            if (name.IndexOf("paint.net", StringComparison.OrdinalIgnoreCase) >= 0) return Families[4];
            if (Regex.IsMatch(name, @"\bKrita\b", RegexOptions.IgnoreCase)) return Families[5];
            if (name.IndexOf("Affinity Photo", StringComparison.OrdinalIgnoreCase) >= 0) return Families[6];
            if (String.Equals(name, "Affinity", StringComparison.OrdinalIgnoreCase)) return Families[7];
            if (name.IndexOf("PaintShop Pro", StringComparison.OrdinalIgnoreCase) >= 0) return Families[8];
            if (name.IndexOf("PHOTO-PAINT", StringComparison.OrdinalIgnoreCase) >= 0) return Families[9];
            return null;
        }

        private static void AddPaint(Dictionary<string, InstalledEditor> editors)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!String.IsNullOrEmpty(system)) Add(editors, Path.Combine(system, "mspaint.exe"), Families[0], false);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrEmpty(local)) Add(editors, Path.Combine(local, @"Microsoft\WindowsApps\mspaint.exe"), Families[0], false);
        }

        private static void AddUserAliases(Dictionary<string, InstalledEditor> editors)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrEmpty(local)) return;
            foreach (EditorFamily family in Families)
                foreach (string executable in family.Executables)
                    Add(editors, Path.Combine(local, @"Microsoft\WindowsApps", executable), family, false);
        }

        private static void ReadKnownFolders(Dictionary<string, InstalledEditor> editors)
        {
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                string root = NormalizeLocalDirectory(value);
                if (root == null || !roots.Add(root)) continue;
                ProbeChildDirectories(editors, Path.Combine(root, "Adobe"), "Adobe Photoshop*", Families[1]);
                ProbeChildDirectories(editors, Path.Combine(root, "Adobe"), "Photoshop Elements*", Families[2]);
                ProbeChildDirectories(editors, Path.Combine(root, "Adobe"), "Adobe Photoshop Elements*", Families[2]);
                ProbeChildDirectories(editors, root, "GIMP*", Families[3]);
                AddInstallDirectory(editors, Path.Combine(root, "paint.net"), Families[4]);
                ProbeChildDirectories(editors, root, "Krita*", Families[5]);
                AddInstallDirectory(editors, Path.Combine(root, @"Affinity\Photo"), Families[6]);
                AddInstallDirectory(editors, Path.Combine(root, @"Affinity\Photo 2"), Families[6]);
                AddInstallDirectory(editors, Path.Combine(root, @"Affinity\Affinity"), Families[7]);
                ProbeChildDirectories(editors, Path.Combine(root, "Corel"), "Corel PaintShop Pro*", Families[8]);
            }
        }

        private static void ProbeChildDirectories(Dictionary<string, InstalledEditor> editors, string root, string pattern, EditorFamily family)
        {
            try
            {
                if (!IsLocalDrive(root) || !HasLocalDirectoryChain(root, true) || !Directory.Exists(root)) return;
                string[] directories = Directory.GetDirectories(root, pattern, SearchOption.TopDirectoryOnly);
                int limit = Math.Min(directories.Length, 40);
                for (int i = 0; i < limit; i++)
                {
                    if ((File.GetAttributes(directories[i]) & FileAttributes.ReparsePoint) != 0) continue;
                    AddInstallDirectory(editors, directories[i], family);
                }
            }
            catch (Exception) { }
        }

        private static void AddInstallDirectory(Dictionary<string, InstalledEditor> editors, string directory, EditorFamily family)
        {
            foreach (string subdirectory in new string[] { "", "bin", @"bin\64", "Programs", "App" })
            foreach (string executable in family.Executables)
            {
                try { Add(editors, Path.Combine(directory, subdirectory, executable), family, false); }
                catch (Exception) { }
            }
        }

        private static void Add(Dictionary<string, InstalledEditor> editors, string raw, EditorFamily family, bool iconValue)
        {
            string path = ExtractLocalExecutablePath(raw, iconValue);
            if (path == null || !IsLocalDrive(path) || !HasLocalDirectoryChain(path, false) || !File.Exists(path)) return;
            string fileName = Path.GetFileName(path);
            bool recognized = false;
            foreach (string name in family.Executables)
                if (String.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) { recognized = true; break; }
            if (!recognized || editors.ContainsKey(path)) return;
            // Paint's registered launcher is preferred; avoid a duplicate alias.
            if (family == Families[0])
                foreach (InstalledEditor editor in editors.Values) if (editor.Name == family.Name) return;
            editors.Add(path, new InstalledEditor { Name = family.Name, ExecutablePath = path });
        }

        internal static string ExtractLocalExecutablePath(string raw, bool iconValue)
        {
            if (String.IsNullOrWhiteSpace(raw) || raw.Length > 32767) return null;
            if (raw.IndexOf('\0') >= 0 || raw.IndexOf('\r') >= 0 || raw.IndexOf('\n') >= 0) return null;
            try
            {
                string value = Environment.ExpandEnvironmentVariables(raw.Trim());
                if (value.IndexOf('\0') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return null;
                string path;
                string tail = "";
                if (value[0] == '"')
                {
                    int close = value.IndexOf('"', 1);
                    if (close < 0) return null;
                    path = value.Substring(1, close - 1);
                    tail = value.Substring(close + 1).Trim();
                }
                else
                {
                    int end = value.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (end < 0) { path = value; }
                    else { path = value.Substring(0, end + 4); tail = value.Substring(end + 4).Trim(); }
                }
                if (tail.Length > 0 && (!iconValue || !Regex.IsMatch(tail, @"^,\s*-?\d+\s*$"))) return null;
                if (path.IndexOf('"') >= 0 || !IsLocalAbsolutePath(path)) return null;
                if (!String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (Path.GetExtension(path).Length != 0) return null;
                    path += ".exe";
                }
                path = Path.GetFullPath(path);
                return IsLocalAbsolutePath(path) ? path : null;
            }
            catch (Exception) { return null; }
        }

        private static string NormalizeLocalDirectory(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                string value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (!IsLocalAbsolutePath(value) || value.IndexOf('"') >= 0 || value.IndexOf('\0') >= 0) return null;
                return Path.GetFullPath(value);
            }
            catch (Exception) { return null; }
        }

        private static bool IsLocalAbsolutePath(string path)
        {
            return path != null && path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/');
        }

        private static bool IsLocalDrive(string path)
        {
            try
            {
                DriveType type = new DriveInfo(Path.GetPathRoot(path)).DriveType;
                return type == DriveType.Fixed || type == DriveType.Removable || type == DriveType.CDRom || type == DriveType.Ram;
            }
            catch (Exception) { return false; }
        }

        private static bool HasLocalDirectoryChain(string path, bool directory)
        {
            try
            {
                string full = directory ? Path.GetFullPath(path) : Path.GetDirectoryName(path);
                string root = Path.GetPathRoot(full);
                string current = root;
                foreach (string part in full.Substring(root.Length).Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void DisambiguateNames(List<InstalledEditor> editors)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (InstalledEditor editor in editors) { int count; counts.TryGetValue(editor.Name, out count); counts[editor.Name] = count + 1; }
            foreach (InstalledEditor editor in editors)
                if (counts[editor.Name] > 1)
                {
                    string directory = Path.GetFileName(Path.GetDirectoryName(editor.ExecutablePath));
                    if (String.Equals(directory, "bin", StringComparison.OrdinalIgnoreCase)) directory = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(editor.ExecutablePath)));
                    editor.Name += " — " + directory;
                }
        }
    }
}

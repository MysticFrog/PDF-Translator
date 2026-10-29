using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PdfTranslator.Setup
{
    /// <summary>Who the application is being installed for.</summary>
    public enum Scope
    {
        /// <summary>This user only. No elevation, and the default.</summary>
        CurrentUser,

        /// <summary>Every user of this PC. Needs administrator rights.</summary>
        AllUsers
    }

    /// <summary>
    /// Putting the application on a PC, and taking it off again.
    /// </summary>
    /// <remarks>
    /// Per-user by default and deliberately: an installer that demands a password
    /// before it will do anything is an installer that does not get run. Nothing
    /// about a per-user install needs administrator rights - it writes under the
    /// user's own profile and registers under HKCU - so it does not ask for them.
    /// All-users is offered for several people sharing one PC.
    /// </remarks>
    internal sealed class Installer
    {
        public const string ProductName = "PDF Translator";
        public const string Publisher = "Andrew Jones";
        public const string ExecutableName = "PdfTranslator.exe";
        public const string Description = "Translates PDFs keeping their layout, with OCR and a side-by-side preview";

        /// <summary>The key both scopes register under, in their own hive.</summary>
        private const string UninstallKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PdfTranslator";

        /// <summary>Registry key reserved for the application (its settings live in %APPDATA%\PdfTranslator).</summary>
        private const string SettingsKey = @"Software\PdfTranslator";

        public Scope Scope { get; set; }
        public string Directory { get; set; }
        public bool DesktopShortcut { get; set; }

        /// <summary>Files only (and a private .NET when needed): no shortcuts, registry entry or uninstaller.</summary>
        public bool Portable { get; set; }

        /// <summary>Progress, from 0 to 1, with the file being written.</summary>
        public Action<double, string> Progress { get; set; }

        public Installer()
        {
            Scope = Scope.CurrentUser;
            Directory = DefaultDirectory(Scope.CurrentUser);
            DesktopShortcut = true;
        }

        // --- where things go --------------------------------------------------

        public static string DefaultDirectory(Scope scope)
        {
            if (scope == Scope.AllUsers)
            {
                // A 64-bit installer process sees the 64-bit Program Files here.
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);
            }

            // Where a per-user application belongs, and where Windows expects to find
            // one: not AppData\Roaming, which would follow the user onto other PCs.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", ProductName);
        }

        private static RegistryKey Hive(Scope scope)
        {
            return scope == Scope.AllUsers
                ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        }

        private static string StartMenuFolder(Scope scope)
        {
            return Environment.GetFolderPath(scope == Scope.AllUsers
                ? Environment.SpecialFolder.CommonStartMenu
                : Environment.SpecialFolder.StartMenu);
        }

        private static string DesktopFolder(Scope scope)
        {
            return Environment.GetFolderPath(scope == Scope.AllUsers
                ? Environment.SpecialFolder.CommonDesktopDirectory
                : Environment.SpecialFolder.DesktopDirectory);
        }

        private static string StartMenuLink(Scope scope)
        {
            return Path.Combine(StartMenuFolder(scope), "Programs", ProductName + ".lnk");
        }

        private static string DesktopLink(Scope scope)
        {
            return Path.Combine(DesktopFolder(scope), ProductName + ".lnk");
        }

        // --- what is already here ---------------------------------------------

        /// <summary>An install already on this PC, or null.</summary>
        public static Installer Existing(Scope scope)
        {
            try
            {
                using (RegistryKey hive = Hive(scope))
                using (RegistryKey key = hive.OpenSubKey(UninstallKey))
                {
                    if (key == null) return null;

                    string location = key.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(location)) return null;

                    Installer found = new Installer();
                    found.Scope = scope;
                    found.Directory = location;
                    return found;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string InstalledVersion(Scope scope)
        {
            try
            {
                using (RegistryKey hive = Hive(scope))
                using (RegistryKey key = hive.OpenSubKey(UninstallKey))
                    return key == null ? null : key.GetValue("DisplayVersion") as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True when a copy of the application is running from the folder being
        /// installed into. Replacing a running executable fails halfway through and
        /// leaves an install that is neither the old version nor the new one.
        /// </summary>
        public static bool IsRunning(string directory)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(
                             Path.GetFileNameWithoutExtension(ExecutableName)))
                {
                    using (p)
                    {
                        try
                        {
                            string path = p.MainModule == null ? null : p.MainModule.FileName;

                            if (path != null && directory != null &&
                                path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        catch (Exception)
                        {
                            // A process this account cannot inspect is not one of ours.
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        // --- installing --------------------------------------------------------

        public void Install()
        {
            System.IO.Directory.CreateDirectory(Directory);

            // The small installer's application needs .NET: fetched first, so a PC without internet gets a
            // clear message and nothing half-installed. The bar: runtime 0 .. 0.4, files up to 0.9.
            string channel = DotNetRuntime.Channel;
            double start = 0;
            if (channel != null && !DotNetRuntime.IsPresent(channel, Directory))
            {
                DotNetRuntime.Ensure(channel, Scope, Directory, delegate (double f, string what) { Report(0.4 * f, what); });
                start = 0.4;
            }

            IList<string> files = Payload.Names();
            Payload.ExtractAll(Directory, delegate (int i, int count, string name)
            {
                Report(start + (0.9 - start) * i / Math.Max(1, count), name);
            });
            RemoveLeftovers(files);
            if (Portable)
            {
                Report(1, "Done");
                return;
            }

            Report(0.92, "Shortcuts");
            WriteShortcuts();

            Report(0.95, "Registering");
            CopySelf();
            Register();

            Report(1, "Done");
        }

        /// <summary>The list of files the last install put in the program folder.</summary>
        private string ManifestPath { get { return Path.Combine(Directory, "install-files.txt"); } }

        /// <summary>
        /// Removes files an earlier version installed that this one doesn't carry, then records this one's.
        /// Matters most when a self-contained install is replaced by the small one: a runtime left beside the
        /// program (hostfxr.dll and the rest) would make it start as self-contained, with parts missing.
        /// Installs from before the list existed: the program folder's top-level files not in this payload.
        /// </summary>
        private void RemoveLeftovers(IList<string> files)
        {
            HashSet<string> current = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> previous;
            if (File.Exists(ManifestPath))
                previous = File.ReadAllLines(ManifestPath);
            else
            {
                List<string> top = new List<string>();
                foreach (string path in System.IO.Directory.GetFiles(Directory)) top.Add(Path.GetFileName(path));
                previous = top;
            }

            foreach (string name in previous)
            {
                if (name.Length == 0 || current.Contains(name)) continue;
                if (string.Equals(name, "Uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, Path.GetFileName(ManifestPath), StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(DotNetRuntime.PrivateFolder + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                Delete(Path.Combine(Directory, name));
            }
            RemoveEmptyFolders(Directory);
            File.WriteAllLines(ManifestPath, files);
        }

        private void Report(double fraction, string what)
        {
            Action<double, string> progress = Progress;
            if (progress != null) progress(fraction, what);
        }

        private string ExecutablePath { get { return Path.Combine(Directory, ExecutableName); } }

        private string UninstallerPath { get { return Path.Combine(Directory, "Uninstall.exe"); } }

        private void WriteShortcuts()
        {
            Shortcut.Create(StartMenuLink(Scope), ExecutablePath, null, Description, ExecutablePath);

            string desktop = DesktopLink(Scope);

            if (DesktopShortcut)
                Shortcut.Create(desktop, ExecutablePath, null, Description, ExecutablePath);
            else if (File.Exists(desktop))
                File.Delete(desktop);
        }

        /// <summary>
        /// Leaves a copy of this installer behind as the uninstaller. It already
        /// knows how to remove everything it wrote, so a second executable would be
        /// a second thing to keep in step with the first.
        /// </summary>
        private void CopySelf()
        {
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;

            if (string.Equals(self, UninstallerPath, StringComparison.OrdinalIgnoreCase)) return;

            File.Copy(self, UninstallerPath, true);
        }

        private void Register()
        {
            string version = VersionOf(ExecutablePath);

            using (RegistryKey hive = Hive(Scope))
            using (RegistryKey key = hive.CreateSubKey(UninstallKey))
            {
                if (key == null) return;

                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", Publisher);
                key.SetValue("DisplayIcon", ExecutablePath);
                key.SetValue("InstallLocation", Directory);
                key.SetValue("UninstallString", Quote(UninstallerPath) + " /uninstall");
                key.SetValue("QuietUninstallString", Quote(UninstallerPath) + " /uninstall /S");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(Payload.TotalBytes() / 1024), RegistryValueKind.DWord);
                key.SetValue("SystemComponent", 0, RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// This installer's version, which is the version it is about to install.
        /// </summary>
        public static string ProductVersion
        {
            get { return VersionOf(System.Reflection.Assembly.GetExecutingAssembly().Location); }
        }

        public static string VersionOf(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrEmpty(info.FileVersion)) return info.FileVersion;
                }
            }
            catch (Exception)
            {
            }

            return "1.0.0";
        }

        private static string Quote(string path) { return "\"" + path + "\""; }

        // --- removing ----------------------------------------------------------

        /// <summary>
        /// Takes the application off. Settings (service keys and choices) and the
        /// downloaded OCR models are kept unless <paramref name="removeData"/> says
        /// otherwise. Project files are never touched: they belong to the user,
        /// wherever they were saved.
        /// </summary>
        public void Uninstall(bool removeData)
        {
            Delete(StartMenuLink(Scope));
            Delete(DesktopLink(Scope));

            try
            {
                using (RegistryKey hive = Hive(Scope))
                    hive.DeleteSubKeyTree(UninstallKey, false);
            }
            catch (Exception)
            {
            }

            foreach (string name in Payload.Names()) Delete(Path.Combine(Directory, name));
            Delete(ManifestPath);
            // A runtime unpacked beside the program belongs to it; a machine-wide one stays (others may use it).
            DotNetRuntime.RemovePrivate(Directory);
            RemoveEmptyFolders(Directory);

            if (removeData)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false);

                    // Settings (%APPDATA%) and downloaded OCR models (%LOCALAPPDATA%).
                    foreach (Environment.SpecialFolder root in new[] { Environment.SpecialFolder.ApplicationData,
                                                                       Environment.SpecialFolder.LocalApplicationData })
                    {
                        string data = Path.Combine(Environment.GetFolderPath(root), "PdfTranslator");
                        if (System.IO.Directory.Exists(data)) System.IO.Directory.Delete(data, true);
                    }
                }
                catch (Exception)
                {
                }
            }

            ScheduleSelfDelete();
        }

        private static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        /// <summary>
        /// Removes the plugin folders the payload created, deepest first, but only
        /// when they are empty - anything the user saved there (say a project in
        /// samples) stays.
        /// </summary>
        private static void RemoveEmptyFolders(string root)
        {
            try
            {
                if (!System.IO.Directory.Exists(root)) return;

                List<string> folders = new List<string>(
                    System.IO.Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
                folders.Sort((a, b) => b.Length.CompareTo(a.Length));

                foreach (string folder in folders)
                {
                    try
                    {
                        if (System.IO.Directory.GetFileSystemEntries(folder).Length == 0)
                            System.IO.Directory.Delete(folder);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Removes the uninstaller and the folder once this process has ended. A
        /// running executable cannot delete itself, so a detached command waits for
        /// this process to go. The folder is only removed if it is empty by then.
        /// </summary>
        private void ScheduleSelfDelete()
        {
            try
            {
                string self = System.Reflection.Assembly.GetExecutingAssembly().Location;

                string command = string.Format(
                    "/c ping 127.0.0.1 -n 3 > nul & del /q \"{0}\" & rmdir \"{1}\"",
                    self, Directory.TrimEnd('\\'));

                ProcessStartInfo start = new ProcessStartInfo("cmd.exe", command);
                start.CreateNoWindow = true;
                start.UseShellExecute = false;
                Process.Start(start);
            }
            catch (Exception)
            {
            }
        }
    }
}

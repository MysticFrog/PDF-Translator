using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace PdfTranslator.Setup
{
    /// <summary>Running as administrator, and asking to.</summary>
    internal static class Elevation
    {
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(identity)
                            .IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Starts this installer again with administrator rights, carrying the
        /// choices already made.
        /// </summary>
        /// <remarks>
        /// The manifest asks for asInvoker, so the installer itself never prompts - a
        /// per-user install, the default, needs no rights at all. Elevation is asked
        /// for here instead, once, after the person has chosen to install for
        /// everyone. Returns false when the prompt is refused or cancelled.
        /// </remarks>
        public static bool Relaunch(bool uninstalling, string directory, bool desktopShortcut)
        {
            try
            {
                string arguments = (uninstalling ? "/uninstall " : "") + "/allusers" +
                                   (desktopShortcut ? "" : " /nodesktop") +
                                   " /D=\"" + directory + "\"";

                ProcessStartInfo start = new ProcessStartInfo(
                    System.Reflection.Assembly.GetExecutingAssembly().Location, arguments);

                start.UseShellExecute = true;
                start.Verb = "runas";

                Process.Start(start);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The installer's entry point.
    /// </summary>
    /// <remarks>
    /// Command line, for deployment by whatever is already in use:
    ///
    ///   PdfTranslatorSetup.exe                     install for this user, with the window
    ///   PdfTranslatorSetup.exe /allusers           install for everyone (asks to elevate)
    ///   PdfTranslatorSetup.exe /S                  silent, current user
    ///   PdfTranslatorSetup.exe /S /allusers        silent, all users - must already be elevated
    ///   PdfTranslatorSetup.exe /D="C:\Path"        install somewhere else
    ///   PdfTranslatorSetup.exe /nodesktop          no desktop shortcut
    ///   PdfTranslatorSetup.exe /uninstall [/S]     remove it
    ///
    /// A silent all-users install that is not already elevated fails with a message
    /// rather than putting a prompt on screen: a deployment script has nobody there
    /// to answer it.
    /// </remarks>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = Has(args, "/S");
            bool uninstalling = Has(args, "/uninstall");
            bool noDesktop = Has(args, "/nodesktop");

            Scope scope = Has(args, "/allusers") ? Scope.AllUsers : Scope.CurrentUser;
            string directory = Directory(args);

            // Uninstalling from Add or Remove Programs: the entry says which scope
            // wrote it, and the folder comes from the registry rather than a guess.
            if (uninstalling && directory == null)
            {
                Installer existing = Installer.Existing(scope) ??
                                     Installer.Existing(Scope.AllUsers) ??
                                     Installer.Existing(Scope.CurrentUser);

                if (existing != null)
                {
                    scope = existing.Scope;
                    directory = existing.Directory;
                }
            }

            if (scope == Scope.AllUsers && !Elevation.IsElevated)
            {
                if (silent)
                {
                    Console.Error.WriteLine(
                        "Installing for all users needs administrator rights. Run this " +
                        "from an elevated command prompt, or leave off /allusers.");
                    return 5;
                }

                // The window would only ask again, so go straight to the prompt.
                if (Elevation.Relaunch(uninstalling, directory ??
                                       Installer.DefaultDirectory(scope), !noDesktop))
                    return 0;

                scope = Scope.CurrentUser;
                directory = null;
            }

            if (silent) return Silent(uninstalling, scope, directory, !noDesktop);

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

            SetupWindow window = new SetupWindow(uninstalling, scope, directory);
            return app.Run(window);
        }

        private static int Silent(bool uninstalling, Scope scope, string directory,
                                  bool desktopShortcut)
        {
            try
            {
                Installer installer = new Installer();
                installer.Scope = scope;
                installer.Directory = directory ?? Installer.DefaultDirectory(scope);
                installer.DesktopShortcut = desktopShortcut;

                if (Installer.IsRunning(installer.Directory))
                {
                    Console.Error.WriteLine(Installer.ProductName +
                                            " is running from " + installer.Directory);
                    return 5;
                }

                if (uninstalling) installer.Uninstall(false);
                else installer.Install();

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static bool Has(string[] args, string flag)
        {
            foreach (string arg in args)
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>The /D=path argument, in the form installers have always used it.</summary>
        private static string Directory(string[] args)
        {
            foreach (string arg in args)
            {
                if (!arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) continue;

                string path = arg.Substring(3).Trim().Trim('"');
                if (path.Length > 0) return path;
            }

            return null;
        }
    }
}

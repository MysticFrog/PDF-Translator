using System;
using System.Runtime.InteropServices;

namespace PdfTranslator.Setup
{
    /// <summary>
    /// Creating .lnk files, through the shell interfaces that own the format.
    /// </summary>
    /// <remarks>
    /// Declared here rather than referenced from the Windows Script Host type
    /// library: an interop assembly would be another file to ship with a single-file
    /// installer, for four calls.
    /// </remarks>
    internal static class Shortcut
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
                         int maxPath, IntPtr findData, int flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name,
                                int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory,
                                     int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments,
                              int maxArguments);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon,
                                 int maxPath, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr window, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid classId);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName,
                      [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }

        /// <summary>Writes a shortcut, replacing one already there.</summary>
        public static void Create(string linkPath, string target, string arguments,
                                  string description, string iconPath)
        {
            string directory = System.IO.Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);

            IShellLinkW link = (IShellLinkW)new ShellLink();

            link.SetPath(target);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target));

            if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
            if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
            if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);

            ((IPersistFile)link).Save(linkPath, true);
            Marshal.ReleaseComObject(link);
        }
    }
}

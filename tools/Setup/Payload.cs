using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace PdfTranslator.Setup
{
    /// <summary>
    /// The application, carried inside this executable.
    /// </summary>
    /// <remarks>
    /// One file to hand someone, and nothing to unpack first. The application is a
    /// few hundred files in several folders (the .NET runtime, OCR models), so it
    /// travels as one compressed archive, "Payload.zip", written by build.ps1 from the
    /// deployed folder. Single files the installer itself needs before anything is
    /// installed - the icon, an agreement - are embedded beside it as
    /// "Payload.&lt;name&gt;".
    /// </remarks>
    internal static class Payload
    {
        public const string Prefix = "Payload.";
        public const string ArchiveName = "Payload.zip";

        /// <summary>A single embedded file (icon, agreement), or null.</summary>
        public static Stream Open(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name);
        }

        private static ZipArchive Archive()
        {
            Stream stream = Open(ArchiveName);
            if (stream == null) throw new FileNotFoundException("This installer carries no application.");
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }

        /// <summary>The files carried, as paths relative to the install folder.</summary>
        public static IList<string> Names()
        {
            List<string> names = new List<string>();

            try
            {
                using (ZipArchive zip = Archive())
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                        if (!string.IsNullOrEmpty(entry.Name)) names.Add(Local(entry.FullName));
                }
            }
            catch (FileNotFoundException)
            {
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Total installed size, for the window and Add or Remove Programs.</summary>
        public static long TotalBytes()
        {
            long total = 0;

            try
            {
                using (ZipArchive zip = Archive())
                    foreach (ZipArchiveEntry entry in zip.Entries) total += entry.Length;
            }
            catch (FileNotFoundException)
            {
            }

            return total;
        }

        /// <summary>
        /// Writes every carried file into a folder, reporting each one.
        /// </summary>
        public static void ExtractAll(string directory, Action<int, int, string> progress)
        {
            using (ZipArchive zip = Archive())
            {
                List<ZipArchiveEntry> files = new List<ZipArchiveEntry>();
                foreach (ZipArchiveEntry entry in zip.Entries)
                    if (!string.IsNullOrEmpty(entry.Name)) files.Add(entry);

                for (int i = 0; i < files.Count; i++)
                {
                    string name = Local(files[i].FullName);
                    if (progress != null) progress(i, files.Count, name);
                    Extract(files[i], Path.Combine(directory, name));
                }
            }
        }

        private static void Extract(ZipArchiveEntry entry, string target)
        {
            string parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // Written to a temporary name and moved into place, so a failure part
            // way through cannot leave a half-written executable that looks
            // installed. Replacing an in-use file still fails, which is why the
            // running application is checked for first.
            string staging = target + ".new";

            using (Stream source = entry.Open())
            using (FileStream destination = File.Create(staging))
                source.CopyTo(destination);

            if (File.Exists(target)) File.Delete(target);
            File.Move(staging, target);
        }

        private static string Local(string zipPath)
        {
            return zipPath.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}

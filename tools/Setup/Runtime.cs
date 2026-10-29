using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace PdfTranslator.Setup
{
    /// <summary>
    /// The .NET Desktop Runtime the application needs, when this installer does not carry it.
    /// </summary>
    /// <remarks>
    /// The small installer carries the application without .NET (build.ps1 puts the channel, "10.0", in
    /// Payload.runtime.txt). When the PC has no runtime of that channel, it is fetched from Microsoft at
    /// install time - the newest release, as Microsoft's release list names it, checked against the SHA-512
    /// that list gives:
    ///   all users  - Microsoft's own runtime installer, run quietly (setup is already elevated). Machine-wide,
    ///                and Microsoft Update keeps it patched.
    ///   just me    - Microsoft's zip, unpacked into a "dotnet" folder beside the program. No administrator
    ///                rights; the program's launcher looks there before the machine-wide places.
    /// The offline installer carries a self-contained application and has no runtime.txt: nothing to do.
    /// </remarks>
    internal static class DotNetRuntime
    {
        /// <summary>Folder beside the program for a private runtime (matches AppHostRelativeDotNet).</summary>
        public const string PrivateFolder = "dotnet";

        private const string ReleasesUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/{0}/releases.json";

        /// <summary>The .NET channel the carried application needs ("10.0"), or null when it brings its own.</summary>
        public static string Channel
        {
            get
            {
                using (Stream stream = Payload.Open("runtime.txt"))
                {
                    if (stream == null) return null;
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string channel = reader.ReadToEnd().Trim();
                        return channel.Length == 0 ? null : channel;
                    }
                }
            }
        }

        /// <summary>A Desktop Runtime of the channel is beside the program or installed machine-wide.</summary>
        public static bool IsPresent(string channel, string programDirectory)
        {
            if (Has(Path.Combine(programDirectory, PrivateFolder), channel)) return true;
            foreach (string root in MachineRoots())
                if (Has(root, channel)) return true;
            return false;
        }

        private static IEnumerable<string> MachineRoots()
        {
            // Where the .NET installers record it (in the 32-bit view, whatever the architecture), then the default.
            string registered = null;
            try
            {
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (RegistryKey key = hive.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
                    if (key != null) registered = key.GetValue("InstallLocation") as string;
            }
            catch (Exception)
            {
            }
            if (!string.IsNullOrEmpty(registered)) yield return registered;
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }

        private static bool Has(string root, string channel)
        {
            return HasFramework(root, "Microsoft.NETCore.App", channel) &&
                   HasFramework(root, "Microsoft.WindowsDesktop.App", channel);
        }

        private static bool HasFramework(string root, string name, string channel)
        {
            try
            {
                string folder = Path.Combine(root, "shared", name);
                if (!Directory.Exists(folder)) return false;
                foreach (string version in Directory.GetDirectories(folder))
                    if (Path.GetFileName(version).StartsWith(channel + ".", StringComparison.Ordinal)) return true;
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// Makes sure the runtime is there, fetching it when it is not. <paramref name="report"/> gets 0..1.
        /// </summary>
        public static void Ensure(string channel, Scope scope, string programDirectory, Action<double, string> report)
        {
            if (IsPresent(channel, programDirectory)) return;

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string version;
            List<KeyValuePair<string, string>> downloads;   // url, SHA-512

            report(0, "Finding the .NET " + channel + " Desktop Runtime");
            try
            {
                // All users: Microsoft's Desktop Runtime installer, which brings the base runtime with it.
                // Just me: the zips, which don't - the base runtime (with the launcher's hostfxr) and the
                // Desktop part, unpacked into one folder.
                downloads = scope == Scope.AllUsers
                    ? Latest(channel, new[] { "windowsdesktop|windowsdesktop-runtime-win-x64.exe" }, out version)
                    : Latest(channel, new[] { "runtime|dotnet-runtime-win-x64.zip", "windowsdesktop|windowsdesktop-runtime-win-x64.zip" }, out version);
            }
            catch (Exception ex)
            {
                throw Offline("couldn't reach Microsoft's download site (" + ex.Message + ")");
            }

            string work = Path.Combine(Path.GetTempPath(), "PdfTranslatorSetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                List<string> files = new List<string>();
                for (int i = 0; i < downloads.Count; i++)
                {
                    string url = downloads[i].Key;
                    string file = Path.Combine(work, Path.GetFileName(new Uri(url).LocalPath));
                    int part = i;
                    try
                    {
                        Download(url, file, delegate (double f)
                        {
                            report(0.8 * (part + f) / downloads.Count, "Downloading .NET " + version + " from Microsoft");
                        });
                    }
                    catch (Exception ex)
                    {
                        throw Offline("the download failed (" + ex.Message + ")");
                    }

                    if (!string.Equals(Sha512(file), downloads[i].Value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A downloaded .NET file did not match Microsoft's checksum, " +
                                                            "so it was not installed. Run setup again.");
                    files.Add(file);
                }

                if (scope == Scope.AllUsers)
                {
                    report(0.85, "Installing .NET " + version + " for all users");
                    RunInstaller(files[0]);
                }
                else
                {
                    report(0.85, "Unpacking .NET " + version + " beside the program");
                    Unpack(files, Path.Combine(programDirectory, PrivateFolder));
                }
                report(1, ".NET " + version + " is ready");
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (Exception) { }
            }
        }

        private static Exception Offline(string why)
        {
            return new InvalidOperationException(
                Installer.ProductName + " needs the Microsoft .NET Desktop Runtime, which isn't on this PC, and " + why + ".\n\n" +
                "Connect to the internet and run setup again, or use the offline installer " +
                "(PdfTranslatorSetup-<version>-offline.exe), which has everything inside.");
        }

        /// <summary>
        /// The newest release of the channel: for each "component|file" wanted ("runtime|dotnet-runtime-win-x64.zip"),
        /// its download and SHA-512. By exact name: the runtime list has other win-x64 zips too (the apphost pack).
        /// </summary>
        private static List<KeyValuePair<string, string>> Latest(string channel, string[] wanted, out string version)
        {
            string json;
            using (WebClient web = new WebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = "PdfTranslatorSetup";
                web.Encoding = System.Text.Encoding.UTF8;
                json = web.DownloadString(string.Format(ReleasesUrl, channel));
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> root = (Dictionary<string, object>)serializer.DeserializeObject(json);
            version = (string)root["latest-release"];

            foreach (object item in (IEnumerable)root["releases"])
            {
                Dictionary<string, object> release = (Dictionary<string, object>)item;
                if ((string)release["release-version"] != version) continue;

                List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();
                foreach (string want in wanted)
                {
                    string[] parts = want.Split('|');
                    int before = found.Count;
                    Dictionary<string, object> component = (Dictionary<string, object>)release[parts[0]];
                    foreach (object f in (IEnumerable)component["files"])
                    {
                        Dictionary<string, object> entry = (Dictionary<string, object>)f;
                        string link = entry["url"] as string ?? "";
                        if (string.Equals(entry["name"] as string, parts[1], StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(new KeyValuePair<string, string>(link, (string)entry["hash"]));
                            break;
                        }
                    }
                    if (found.Count == before)
                        throw new InvalidOperationException("no win-x64 " + want + " in Microsoft's release list for .NET " + version);
                }
                return found;
            }
            throw new InvalidOperationException(".NET " + version + " is missing from Microsoft's release list");
        }

        private static void Download(string url, string file, Action<double> progress)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "PdfTranslatorSetup";
            using (WebResponse response = request.GetResponse())
            using (Stream source = response.GetResponseStream())
            using (FileStream target = File.Create(file))
            {
                long total = response.ContentLength, done = 0;
                byte[] buffer = new byte[1 << 16];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, read);
                    done += read;
                    if (total > 0) progress((double)done / total);
                }
            }
        }

        private static string Sha512(string file)
        {
            using (SHA512 sha = SHA512.Create())
            using (FileStream stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Microsoft's runtime installer, quietly. 3010 = done, restart pending; 1638 = newer already there.</summary>
        private static void RunInstaller(string exe)
        {
            ProcessStartInfo start = new ProcessStartInfo(exe, "/install /quiet /norestart");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                if (process.ExitCode != 0 && process.ExitCode != 3010 && process.ExitCode != 1638)
                    throw new InvalidOperationException("Microsoft's .NET installer stopped with code " + process.ExitCode + ".");
            }
        }

        /// <summary>
        /// The zips into one fresh folder (both carry the same LICENSE files), then swapped in, so a failed
        /// unpack never leaves half a runtime.
        /// </summary>
        private static void Unpack(List<string> zips, string folder)
        {
            string staging = folder + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (string zip in zips)
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never outside it
                        if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(staging, folder);
        }

        /// <summary>Removes a runtime this installer unpacked beside the program.</summary>
        public static void RemovePrivate(string programDirectory)
        {
            try
            {
                string folder = Path.Combine(programDirectory, PrivateFolder);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception)
            {
            }
        }
    }
}

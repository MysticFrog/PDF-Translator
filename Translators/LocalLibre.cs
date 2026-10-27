using System.Diagnostics;
using System.Text.Json.Nodes;
using PdfTranslator.Core;

namespace PdfTranslator.Translators;

/// <summary>
/// LibreTranslate (https://github.com/LibreTranslate/LibreTranslate) installed and run on this PC, for offline
/// translation. It lives in %LOCALAPPDATA%\PdfTranslator\libretranslate: a private Python 3.11 set up with uv
/// (LibreTranslate's engine has no packages for the newest Python), the libretranslate package, and its models.
/// The server is started on 127.0.0.1 when needed with only the languages in use, and stopped when the app exits.
/// </summary>
public static class LocalLibre
{
    public const string ProjectUrl = "https://github.com/LibreTranslate/LibreTranslate";
    const string UvUrl = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";

    static Process? _server;
    static string _serverLanguages = "";
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Where start-up progress goes when the caller passes none (the main window's status bar).</summary>
    public static IProgress<string>? Reporter { get; set; }

    /// <summary>Every language asked for this session: a restart for a new language keeps the earlier ones.</summary>
    static readonly HashSet<string> Wanted = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfTranslator", "libretranslate");

    public static string Executable => Path.Combine(Folder, "venv", "Scripts", "libretranslate.exe");

    public static bool IsInstalled => File.Exists(Executable);

    /// <summary>Not LibreTranslate's usual 5000, so it doesn't clash with a server started some other way.</summary>
    public static int Port { get; set; } = 5099;

    public static string Url => $"http://127.0.0.1:{Port}";

    public static string LogFile => Path.Combine(Folder, "server.log");

    /// <summary>Writes the install script and returns its path (run it in a visible terminal).</summary>
    public static string WriteInstallScript()
    {
        Directory.CreateDirectory(Folder);
        string script = Path.Combine(Folder, "install.ps1");
        File.WriteAllText(script, $$"""
            # Installs LibreTranslate for PDF Translator (offline translation). Safe to run again to repair/update.
            $ErrorActionPreference = 'Stop'
            $dir = '{{Folder}}'
            Set-Location $dir
            Write-Host 'PDF Translator - installing offline translation (LibreTranslate)' -ForegroundColor Cyan
            Write-Host "Folder: $dir`n"

            if (-not (Test-Path "$dir\uv\uv.exe")) {
                Write-Host '1/3  Downloading uv (Python installer by Astral)...'
                Invoke-WebRequest '{{UvUrl}}' -OutFile "$dir\uv.zip" -UseBasicParsing
                Expand-Archive "$dir\uv.zip" "$dir\uv" -Force
                Remove-Item "$dir\uv.zip"
            }
            $env:UV_PYTHON_INSTALL_DIR = "$dir\python"
            $env:UV_CACHE_DIR = "$dir\cache"

            Write-Host '2/3  Creating a private Python 3.11...'
            & "$dir\uv\uv.exe" venv --python 3.11 --allow-existing "$dir\venv"
            if ($LASTEXITCODE -ne 0) { throw 'Could not create the Python environment.' }

            Write-Host '3/3  Installing LibreTranslate (about 1 GB, takes a few minutes)...'
            & "$dir\uv\uv.exe" pip install --python "$dir\venv\Scripts\python.exe" libretranslate
            if ($LASTEXITCODE -ne 0) { throw 'Installing LibreTranslate failed.' }

            Write-Host "`nDone. PDF Translator starts LibreTranslate itself; the first translation in a new" -ForegroundColor Green
            Write-Host 'language pair downloads its model (about 100 MB) and may take a minute.' -ForegroundColor Green
            """);
        return script;
    }

    /// <summary>Starts the install in a terminal window so its progress is visible.</summary>
    public static void StartInstall()
    {
        string script = WriteInstallScript();
        PdfTranslator.Launch.Terminal("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
    }

    /// <summary>LibreTranslate codes to try for a language (newer releases use zh-Hans / zh-Hant).</summary>
    public static string[] Codes(string code) => code switch
    {
        "zh-CN" => new[] { "zh-Hans", "zh" },
        "zh-TW" => new[] { "zh-Hant", "zt" },
        "nb" => new[] { "nb", "no" },
        _ => new[] { code },
    };

    /// <summary>Codes the running server offers, or null when nothing answers on the port.</summary>
    public static async Task<HashSet<string>?> RunningLanguagesAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var body = await http.GetStringAsync(Url + "/languages", ct);
            return JsonNode.Parse(body)!.AsArray().Select(n => n!["code"]!.GetValue<string>()).ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure a local server offering both languages is running, starting (or restarting) one if needed.
    /// The first start of a language pair downloads its models, which can take a few minutes.
    /// </summary>
    /// <param name="extra">More languages to load, e.g. the one detected in an "Auto" source.</param>
    public static async Task EnsureRunningAsync(string from, string to, IProgress<string>? log, CancellationToken ct,
        params string[] extra)
    {
        if (!IsInstalled) throw new LocalLibreMissingException();
        log ??= Reporter;
        // One start at a time: pages translated together must not start servers over each other.
        await Gate.WaitAsync(ct);
        try { await StartAsync(from, to, extra, log, ct); }
        finally { Gate.Release(); }
    }

    static async Task StartAsync(string from, string to, string[] extra, IProgress<string>? log, CancellationToken ct)
    {
        lock (Wanted)
            foreach (var c in extra.Append(from).Append(to).Append("en"))
                if (!string.IsNullOrWhiteSpace(c) && c != Languages.Auto) Wanted.Add(c);
        List<string> needed;
        lock (Wanted) needed = Wanted.ToList();

        // LibreTranslate downloads models by itself only on its very first start; any later language would be
        // "loaded" without a model ("ko is not supported"), so fetch missing ones first.
        bool installed = await InstallModelsAsync(needed, log, ct);

        var running = await RunningLanguagesAsync(ct);
        if (!installed && running != null && needed.All(c => Codes(c).Any(running.Contains))) return;
        if (running != null && _server == null)
        {
            // Left running by an earlier session of this app (or another window): ours to restart, keeping its languages.
            var leftovers = OwnServers();
            if (leftovers.Count == 0)
                throw new InvalidOperationException($"Another program's LibreTranslate is using port {Port} without the languages needed. Stop it or change the port.");
            lock (Wanted) foreach (var c in running) Wanted.Add(c);
            lock (Wanted) needed = Wanted.ToList();
            foreach (var p in leftovers) { try { p.Kill(true); p.WaitForExit(5000); } catch { } }
        }
        Stop();

        // Try the code spellings in order: an unknown code makes the server exit straight away.
        foreach (int variant in new[] { 0, 1 })
        {
            string load = string.Join(",", needed.Select(c => Codes(c)[Math.Min(variant, Codes(c).Length - 1)]).Distinct());
            log?.Report($"Starting offline LibreTranslate ({load}). The first start downloads language models; this can take a few minutes…");
            var psi = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Folder,
            };
            foreach (var a in new[] { "--host", "127.0.0.1", "--port", Port.ToString(), "--load-only", load, "--disable-web-ui" })
                psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start LibreTranslate.");
            KillOnExitJob.Add(proc);
            var logWriter = OpenLog();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (logWriter) logWriter.WriteLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (logWriter) logWriter.WriteLine(e.Data); };
            proc.Exited += (_, _) => { lock (logWriter) logWriter.Dispose(); };
            proc.EnableRaisingEvents = true;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _server = proc;
            _serverLanguages = load;

            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromMinutes(20))
            {
                ct.ThrowIfCancellationRequested();
                if (proc.HasExited) break;
                KillOnExitJob.AddTree(proc.Id);   // the launcher's Python children may predate joining the job
                if (await RunningLanguagesAsync(ct) != null) { log?.Report("Offline LibreTranslate is ready."); return; }
                await Task.Delay(1500, ct);
                int secs = (int)(DateTime.UtcNow - started).TotalSeconds;
                if (secs % 15 < 2) log?.Report($"Starting offline LibreTranslate… {secs}s (downloading/loading models)");
            }
            if (!proc.HasExited) { Stop(); throw new TimeoutException("Offline LibreTranslate did not start within 20 minutes. See " + LogFile); }
            _server = null;
        }
        lock (Wanted) Wanted.Clear();   // a language it can't load must not block later starts
        throw new InvalidOperationException($"Offline LibreTranslate could not start with {string.Join(", ", needed)} " +
            "(a language it doesn't offer, or a damaged install: Settings › Offline translation › Repair). Details are in " + LogFile);
    }

    /// <summary>Argos Translate's code for a language (its model packages are named translate-en_zh etc.).</summary>
    static string ArgosCode(string c) => c switch
    {
        "zh-CN" or "zh-Hans" => "zh",
        "zh-TW" or "zh-Hant" => "zt",
        _ => c,
    };

    static string Name(string argos) => Languages.NameOf(argos switch { "zh" => "zh-CN", "zt" => "zh-TW", _ => argos });

    static string PackagesFolder => Environment.GetEnvironmentVariable("ARGOS_PACKAGES_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "argos-translate", "packages");

    /// <summary>LibreTranslate's own downloads are named translate-en_zh-1_9; argospm's are plain en_ko.</summary>
    static bool HasModel(string from, string to) =>
        Directory.Exists(PackagesFolder) && new[] { $"translate-{from}_{to}-*", $"translate-{from}_{to}", $"{from}_{to}" }
            .Any(pattern => Directory.EnumerateDirectories(PackagesFolder, pattern).Any());

    /// <summary>
    /// Downloads the models for languages that have none yet: each language to and from English, since
    /// LibreTranslate translates other pairs through English. Returns true when something was installed.
    /// </summary>
    static async Task<bool> InstallModelsAsync(IEnumerable<string> languages, IProgress<string>? log, CancellationToken ct)
    {
        var missing = languages.Select(ArgosCode).Where(c => c != "en").Distinct()
            .SelectMany(c => new[] { ("en", c), (c, "en") })
            .Where(pair => !HasModel(pair.Item1, pair.Item2)).ToList();
        if (missing.Count == 0) return false;

        string argospm = Path.Combine(Folder, "venv", "Scripts", "argospm.exe");
        log?.Report("Offline LibreTranslate: downloading the language list…");
        await RunAsync(argospm, ct, "update");
        var failed = new List<string>();
        foreach (var (from, to) in missing)
        {
            log?.Report($"Offline LibreTranslate: downloading the {Name(from)} → {Name(to)} model (about 100 MB, once)…");
            var (code, output) = await RunAsync(argospm, ct, "install", $"translate-{from}_{to}");
            if (code != 0 || !HasModel(from, to)) failed.Add($"{Name(from)} → {Name(to)}: {output.Trim().Split('\n').LastOrDefault()}");
        }
        if (failed.Count == missing.Count)
            throw new InvalidOperationException("Offline LibreTranslate could not download the language models it needs " +
                "(an internet connection is needed once per new language):\n" + string.Join("\n", failed));
        return true;
    }

    static async Task<(int code, string output)> RunAsync(string exe, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Folder,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not run " + exe);
        KillOnExitJob.Add(p);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await stdout + await stderr);
    }

    /// <summary>LibreTranslate servers started from this app's install (by any session of it).</summary>
    static List<Process> OwnServers()
    {
        var own = new List<Process>();
        foreach (var p in Process.GetProcessesByName("libretranslate"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase)) own.Add(p);
            }
            catch { }   // exited, or not ours to inspect
        }
        return own;
    }

    /// <summary>The server's log; a log still held by a server that is shutting down never stops a start.</summary>
    static TextWriter OpenLog()
    {
        try
        {
            var stream = new FileStream(LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream) { AutoFlush = true };
        }
        catch (IOException) { return TextWriter.Null; }
        catch (UnauthorizedAccessException) { return TextWriter.Null; }
    }

    /// <summary>A language the server refused ("ko is not supported"), or null for any other error.</summary>
    public static string? UnsupportedLanguage(Exception ex)
    {
        var m = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""error""\s*:\s*""([A-Za-z-]{2,10}) is not supported""");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Stops the server this app started (a server started elsewhere is left alone).</summary>
    public static void Stop()
    {
        try
        {
            if (_server is { HasExited: false } p)
            {
                p.Kill(true);
                p.WaitForExit(5000);   // lets go of the port and the log before a restart
            }
        }
        catch { }
        _server = null;
        _serverLanguages = "";
    }

    public static string Status()
    {
        if (!IsInstalled) return "Not installed";
        return _server is { HasExited: false } ? $"Installed · running ({_serverLanguages})" : "Installed · starts automatically when used";
    }
}

/// <summary>The offline translator was chosen but is not installed; the UI offers to install it.</summary>
public sealed class LocalLibreMissingException() : InvalidOperationException("Offline LibreTranslate is not installed on this PC.");

/// <summary>Translates with the local LibreTranslate server, starting it when needed.</summary>
public sealed class LocalLibreTranslator : ITranslator
{
    public string Id => "libre-local";
    public string DisplayName => "LibreTranslate (offline)";

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        // Only the languages in use are loaded, so an "Auto" source needs a language to load: guess it from the
        // script (Hangul, Kana, Cyrillic…), and if the server still meets one it hasn't loaded, load it and retry.
        if (from == Languages.Auto && TextTools.GuessLanguage(string.Concat(texts)) is { } guess && guess != to) from = guess;
        await LocalLibre.EnsureRunningAsync(from, to, null, ct);
        try
        {
            return await new LibreTranslator(LocalLibre.Url, "").TranslateAsync(texts, from, to, ct);
        }
        catch (HttpRequestException ex) when (LocalLibre.UnsupportedLanguage(ex) is { } missing)
        {
            await LocalLibre.EnsureRunningAsync(from, to, null, ct, missing);
            return await new LibreTranslator(LocalLibre.Url, "").TranslateAsync(texts, from, to, ct);
        }
    }
}

/// <summary>
/// A Windows job object that ends its processes when this app ends, however it ends (closed, crashed, killed),
/// so a LibreTranslate server is never left behind holding the port.
/// </summary>
static class KillOnExitJob
{
    static readonly IntPtr Handle = Create();

    public static void Add(Process p)
    {
        if (Handle != IntPtr.Zero) AssignProcessToJobObject(Handle, p.Handle);
    }

    /// <summary>Adds a process's descendants (processes started after joining inherit the job by themselves).</summary>
    public static void AddTree(int rootId)
    {
        if (Handle == IntPtr.Zero) return;
        var parents = new Dictionary<int, List<int>>();
        foreach (var (id, parent) in Snapshot())
            (parents.TryGetValue(parent, out var kids) ? kids : parents[parent] = new()).Add(id);
        var todo = new Stack<int>(parents.GetValueOrDefault(rootId) ?? new());
        while (todo.Count > 0)
        {
            int id = todo.Pop();
            try { using var p = Process.GetProcessById(id); AssignProcessToJobObject(Handle, p.Handle); } catch { }
            foreach (var k in parents.GetValueOrDefault(id) ?? new()) todo.Push(k);
        }
    }

    static IEnumerable<(int id, int parent)> Snapshot()
    {
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) yield break;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                yield return ((int)e.th32ProcessID, (int)e.th32ParentProcessID);
        }
        finally { CloseHandle(snap); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32FirstW")]
    static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32NextW")]
    static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = System.Runtime.InteropServices.Marshal.SizeOf(info);
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size)) return IntPtr.Zero;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        return job;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

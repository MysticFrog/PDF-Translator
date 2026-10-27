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
    public static async Task EnsureRunningAsync(string from, string to, IProgress<string>? log, CancellationToken ct)
    {
        if (!IsInstalled) throw new LocalLibreMissingException();
        var needed = new[] { from, to, "en" }.Where(c => c != Languages.Auto).Distinct().ToList();

        var running = await RunningLanguagesAsync(ct);
        if (running != null && needed.All(c => Codes(c).Any(running.Contains))) return;
        if (running != null && _server == null)
            throw new InvalidOperationException($"Another LibreTranslate is using port {Port} without the languages needed. Stop it or change the port.");
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
            var logWriter = new StreamWriter(LogFile, false) { AutoFlush = true };
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
                if (await RunningLanguagesAsync(ct) != null) { log?.Report("Offline LibreTranslate is ready."); return; }
                await Task.Delay(1500, ct);
                int secs = (int)(DateTime.UtcNow - started).TotalSeconds;
                if (secs % 15 < 2) log?.Report($"Starting offline LibreTranslate… {secs}s (downloading/loading models)");
            }
            if (!proc.HasExited) { Stop(); throw new TimeoutException("Offline LibreTranslate did not start within 20 minutes. See " + LogFile); }
            _server = null;
        }
        throw new InvalidOperationException("Offline LibreTranslate stopped while starting. Details are in " + LogFile);
    }

    /// <summary>Stops the server this app started (a server started elsewhere is left alone).</summary>
    public static void Stop()
    {
        try { if (_server is { HasExited: false }) _server.Kill(true); } catch { }
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
        await LocalLibre.EnsureRunningAsync(from, to, null, ct);
        return await new LibreTranslator(LocalLibre.Url, "").TranslateAsync(texts, from, to, ct);
    }
}

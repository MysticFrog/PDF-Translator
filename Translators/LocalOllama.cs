using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfTranslator.Core;

namespace PdfTranslator.Translators;

/// <summary>
/// Ollama (https://ollama.com) on this PC, for offline AI translation with a small local language model. An Ollama
/// already installed or running on the PC is used as it is; otherwise the app keeps a private copy in
/// %LOCALAPPDATA%\PdfTranslator\ollama (with its own model folder), started when needed on 127.0.0.1 and ended
/// with the app.
/// </summary>
public static class LocalOllama
{
    public const string ProjectUrl = "https://ollama.com";
    public const string LibraryUrl = "https://ollama.com/library";
    const string ZipUrl = "https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip";

    /// <summary>A small model that runs on an ordinary laptop and is strong in Chinese/Japanese/Korean.</summary>
    public const string SuggestedModel = "qwen3:4b";

    /// <summary>Ollama's usual address, used when an Ollama is already running there.</summary>
    const int SharedPort = 11434;
    /// <summary>Where the app runs its own server, so it never clashes with one the user starts later.</summary>
    const int PrivatePort = 11435;

    static Process? _server;
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfTranslator", "ollama");

    static string PrivateExe => Path.Combine(Folder, "ollama.exe");

    /// <summary>An installed Ollama (PATH or its usual per-user location) before the app's private copy.</summary>
    public static string? Executable
    {
        get
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try { var p = Path.Combine(dir.Trim(), "ollama.exe"); if (File.Exists(p)) return p; }
                catch (ArgumentException) { }
            }
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
            if (File.Exists(programs)) return programs;
            return File.Exists(PrivateExe) ? PrivateExe : null;
        }
    }

    public static bool IsInstalled => Executable != null;

    /// <summary>The app's own copy is (or, before installing, will be) the one in use.</summary>
    static bool IsPrivate => Executable is not { } exe || string.Equals(exe, PrivateExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>The private copy keeps its models beside it; an installed Ollama uses its own model folder.</summary>
    static string? ModelsFolder => IsPrivate ? Path.Combine(Folder, "models") : null;

    /// <summary>The server in use (set once one answers).</summary>
    public static string Url { get; private set; } = $"http://127.0.0.1:{PrivatePort}";

    public static IProgress<string>? Reporter { get; set; }

    static async Task<bool> AnswersAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var body = await http.GetStringAsync(url + "/api/version", ct);
            return body.Contains("version");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Makes sure an Ollama server answers: one already running, or the app's own, started now.</summary>
    public static async Task EnsureRunningAsync(IProgress<string>? log, CancellationToken ct)
    {
        log ??= Reporter;
        await Gate.WaitAsync(ct);
        try
        {
            if (_server is { HasExited: false } && await AnswersAsync(Url, ct)) return;
            string shared = $"http://127.0.0.1:{SharedPort}";
            if (await AnswersAsync(shared, ct)) { Url = shared; return; }
            var exe = Executable ?? throw new OllamaMissingException();

            Url = $"http://127.0.0.1:{PrivatePort}";
            if (await AnswersAsync(Url, ct)) return;   // left by another window of this app
            log?.Report("Starting offline AI (Ollama)…");
            var psi = new ProcessStartInfo(exe, "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var (k, v) in ServerEnvironment()) psi.Environment[k] = v;
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Ollama.");
            KillOnExitJob.Add(proc);
            proc.OutputDataReceived += (_, _) => { };
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _server = proc;

            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(60))
            {
                ct.ThrowIfCancellationRequested();
                if (proc.HasExited) break;
                if (await AnswersAsync(Url, ct)) return;
                await Task.Delay(500, ct);
            }
            Stop();
            throw new InvalidOperationException("Offline AI (Ollama) did not start. Settings › Offline AI › Install/Repair may fix it.");
        }
        finally { Gate.Release(); }
    }

    /// <summary>Environment for the app's own server (and for the install script, so both agree).</summary>
    static IEnumerable<(string, string)> ServerEnvironment()
    {
        yield return ("OLLAMA_HOST", $"127.0.0.1:{PrivatePort}");
        if (ModelsFolder is { } models) yield return ("OLLAMA_MODELS", models);
    }

    /// <summary>Ends the server this app started (an Ollama the user runs is left alone).</summary>
    public static void Stop()
    {
        try
        {
            if (_server is { HasExited: false } p) { p.Kill(true); p.WaitForExit(5000); }
        }
        catch { }
        _server = null;
    }

    /// <summary>The models downloaded to the Ollama in use, as it lists them; null when it can't be reached.</summary>
    public static async Task<List<ModelChoice>?> ModelsAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureRunningAsync(null, ct);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var node = JsonNode.Parse(await http.GetStringAsync(Url + "/api/tags", ct));
            return node!["models"]!.AsArray().Select(m =>
            {
                string name = m!["name"]!.GetValue<string>();
                double gb = (m["size"]?.GetValue<long>() ?? 0) / 1e9;
                string detail = m["details"]?["parameter_size"]?.GetValue<string>() ?? "";
                return new ModelChoice(name, $"{name} · {detail} · {gb:0.0} GB".Replace(" ·  ·", " ·"));
            }).OrderBy(m => m.Id).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public static string Status()
    {
        var exe = Executable;
        if (exe == null) return "Not installed";
        string where = IsPrivate ? "installed for PDF Translator" : "using the Ollama installed on this PC";
        return _server is { HasExited: false } ? $"Running · {where}" : $"Ready · {where}";
    }

    /// <summary>
    /// Writes the script that installs Ollama when it is missing (private copy; GPU libraries this PC can't use
    /// are removed) and downloads <paramref name="model"/>. Safe to run again: it only fetches what is missing.
    /// </summary>
    public static string WriteScript(string model)
    {
        Directory.CreateDirectory(Folder);
        string script = Path.Combine(Folder, "install.ps1");
        var env = new StringBuilder();
        foreach (var (k, v) in ServerEnvironment()) env.AppendLine($"$env:{k} = '{v}'");
        string exe = Executable ?? PrivateExe;
        File.WriteAllText(script, $$"""
            # Installs offline AI translation for PDF Translator: Ollama (https://ollama.com) and a language model.
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $dir = '{{Folder}}'
            $exe = '{{exe}}'
            $model = '{{model}}'
            Write-Host 'PDF Translator - offline AI translation (Ollama)' -ForegroundColor Cyan

            if (-not (Test-Path $exe)) {
                Write-Host "`n1/2  Downloading Ollama (about 1.6 GB)..."
                New-Item -ItemType Directory -Force $dir | Out-Null
                Invoke-WebRequest '{{ZipUrl}}' -OutFile "$dir\ollama.zip" -UseBasicParsing
                Write-Host '     Unpacking...'
                Expand-Archive "$dir\ollama.zip" $dir -Force
                Remove-Item "$dir\ollama.zip"
                # Graphics-card libraries for cards this PC doesn't have only take disk space.
                $gpus = (Get-CimInstance Win32_VideoController).Name -join ' '
                if ($gpus -notmatch 'NVIDIA') { Get-ChildItem "$dir\lib\ollama" -Directory -Filter 'cuda*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force }
                if ($gpus -notmatch 'AMD|Radeon') { Get-ChildItem "$dir\lib\ollama" -Directory -Filter 'rocm*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force }
            } else {
                Write-Host "`n1/2  Ollama is already installed: $exe"
            }

            {{env}}
            $server = $null
            try { Invoke-RestMethod "http://$env:OLLAMA_HOST/api/version" -TimeoutSec 3 | Out-Null }
            catch {
                $server = Start-Process $exe -ArgumentList 'serve' -WindowStyle Hidden -PassThru
                for ($i = 0; $i -lt 60; $i++) {
                    Start-Sleep 1
                    try { Invoke-RestMethod "http://$env:OLLAMA_HOST/api/version" -TimeoutSec 3 | Out-Null; break } catch { }
                }
            }
            try {
                Write-Host "`n2/2  Downloading the model $model (several GB, once)..."
                & $exe pull $model
                if ($LASTEXITCODE -ne 0) { throw "Downloading $model failed. Check the name at {{LibraryUrl}}" }
            } finally {
                if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
            }
            Write-Host "`nDone. Choose 'Offline AI (Ollama, on this PC)' as the service and $model as its model." -ForegroundColor Green
            """, new UTF8Encoding(true));
        return script;
    }

    /// <summary>Installs Ollama if needed and downloads the model, in a terminal window that shows progress.</summary>
    public static void StartInstall(string model)
    {
        Stop();   // the script starts its own server while downloading
        string script = WriteScript(string.IsNullOrWhiteSpace(model) ? SuggestedModel : model.Trim());
        PdfTranslator.Launch.Terminal("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
    }
}

/// <summary>Offline AI was chosen but Ollama is not on this PC; the UI offers to install it.</summary>
public sealed class OllamaMissingException() : InvalidOperationException("Offline AI (Ollama) is not installed on this PC.");

/// <summary>The chosen model hasn't been downloaded to Ollama yet.</summary>
public sealed class OllamaModelMissingException(string model)
    : InvalidOperationException($"The model “{model}” is not downloaded yet.")
{
    public string Model { get; } = model;
}

/// <summary>Translates a page at a time with a local language model through Ollama's chat API.</summary>
public sealed class OllamaTranslator(string model) : ITranslator
{
    public string Id => "ollama";
    public string DisplayName => $"Offline AI ({_model})";

    readonly string _model = string.IsNullOrWhiteSpace(model) ? LocalOllama.SuggestedModel : model.Trim();

    /// <summary>Running on a laptop processor a batch can take minutes; replies are length-capped, so this is only a backstop.</summary>
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// Models that can't translate a page in one request (translation-only models such as translategemma copy
    /// it back): they get one block per request from then on.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> OneAtATime = new();

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        await LocalOllama.EnsureRunningAsync(null, ct);
        // Named, not "detect it": told to detect, models tend to take the text as translated already.
        if (from == Languages.Auto) from = TextTools.GuessLanguage(string.Concat(texts)) ?? Languages.Auto;
        var results = new string[texts.Count];
        // Small models keep track of fewer items at once than Claude: smaller batches stay aligned.
        var batch = new List<int>();
        int chars = 0;
        async Task Flush()
        {
            if (batch.Count == 0) return;
            var items = batch.Select(i => texts[i]).ToList();
            var outItems = OneAtATime.ContainsKey(_model) ? null : await RunAsync(items, from, to, ct);
            if (outItems == null || outItems.Count != items.Count || Untranslated(items, outItems, to))
            {
                // Misaligned or copied back: one block per request, so every translation lands on its own block.
                if (outItems != null && outItems.Count == items.Count) OneAtATime[_model] = true;
                outItems = new List<string>();
                foreach (var item in items)
                    outItems.Add(await RunOneAsync(item, from, to, ct) ?? item);
            }
            for (int k = 0; k < batch.Count; k++) results[batch[k]] = outItems[k];
            batch.Clear();
            chars = 0;
        }
        for (int i = 0; i < texts.Count; i++)
        {
            if (batch.Count >= 30 || chars + texts[i].Length > 3000) await Flush();
            batch.Add(i);
            chars += texts[i].Length;
        }
        await Flush();
        return results;
    }

    /// <summary>
    /// Most of the blocks that are clearly in another script than the target came back unchanged: the model
    /// copied the page instead of translating it.
    /// </summary>
    static bool Untranslated(List<string> items, List<string> outItems, string to)
    {
        var target = TextTools.ScriptOfLanguage(to);
        bool Foreign(string s) => TextTools.CountScripts(s).Keys.Any(k => k != target && !(target == Script.Kana && k == Script.Han));
        var foreign = Enumerable.Range(0, items.Count).Where(i => Foreign(items[i])).ToList();
        int copied = foreign.Count(i => Foreign(outItems[i]) &&
            string.Equals(Squash(items[i]), Squash(outItems[i]), StringComparison.OrdinalIgnoreCase));
        return foreign.Count > 0 && copied * 2 >= foreign.Count;
    }

    static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// One block, asked the way translation models are trained to be asked; the JSON reply keeps chat models
    /// from thinking aloud or explaining.
    /// </summary>
    async Task<string?> RunOneAsync(string text, string from, string to, CancellationToken ct)
    {
        string target = $"{Languages.NameOf(to)} ({to})";
        string source = from == Languages.Auto ? "source-language" : $"{Languages.NameOf(from)} ({from})";
        string prompt =
            $"You are a professional {source} to {target} translator. Produce only the {Languages.NameOf(to)} translation, " +
            "without any additional explanations or commentary, as JSON {\"translation\": \"...\"}. Keep numbers, codes and " +
            $"placeholders like {{{{1}}}} unchanged. Please translate the following text into {Languages.NameOf(to)}:\n\n\n{text}";
        var request = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["think"] = false,
            ["format"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["translation"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("translation"),
            },
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_predict"] = text.Length * 3 + 100 },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };
        var reply = await ReplyAsync(request, ct);
        try { return JsonNode.Parse(reply)?["translation"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    async Task<List<string>?> RunAsync(List<string> items, string from, string to, CancellationToken ct)
    {
        string source = from == Languages.Auto ? "the source language (detect it)" : Languages.NameOf(from);
        string system =
            $"You translate text extracted from a PDF page from {source} into {Languages.NameOf(to)}. " +
            "The user message is a JSON array of text blocks from one page, in reading order. Translate every block " +
            "faithfully and naturally, using the neighbouring blocks as context. Keep numbers, units, part numbers, codes, " +
            "URLs and proper names unchanged. Keep each translation about as long as its source, because it must fit the " +
            "original layout box. Placeholders like {{1}} stand for icons inside the sentence: keep each exactly once, at " +
            "the matching place. If a block is already in the target language or is not translatable, return it unchanged. " +
            $"Reply with JSON {{\"translations\": [...]}}: one {Languages.NameOf(to)} string per numbered block, in order.";
        // A numbered list, not a JSON array: given an array, small models tend to copy it back untranslated.
        string input = $"Translate these {items.Count} blocks into {Languages.NameOf(to)}:\n" +
            string.Join("\n", items.Select((t, i) => $"{i + 1}. {t.ReplaceLineEndings(" ")}"));
        // Room for the instructions, the page and its translation (Ollama's default context is too small).
        int context = Math.Clamp((input.Length * 3 + 6000) / 1024 * 1024, 8192, 32768);

        var request = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["think"] = false,   // translation needs no reasoning pass; much faster on a laptop
            ["format"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["translations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                },
                ["required"] = new JsonArray("translations"),
            },
            // The reply is capped near the input's length, so a model stuck repeating itself stops instead of running for hours.
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = context, ["num_predict"] = input.Length * 3 + 200 },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = input }),
        };

        string reply = await ReplyAsync(request, ct);
        try
        {
            return JsonNode.Parse(reply)?["translations"]?.AsArray()
                .Select(t => t?.GetValue<string>() ?? "").ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>The model's reply text; a missing model or a server error is thrown.</summary>
    async Task<string> ReplyAsync(JsonObject request, CancellationToken ct)
    {
        string body = await PostAsync(request, ct);
        if (body.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase))
        {
            request.Remove("think");
            body = await PostAsync(request, ct);
        }
        var node = JsonNode.Parse(body);
        if (node?["error"]?.GetValue<string>() is { } error)
        {
            if (error.Contains("not found", StringComparison.OrdinalIgnoreCase)) throw new OllamaModelMissingException(_model);
            throw new InvalidOperationException("Offline AI (Ollama): " + error);
        }
        return node?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    static async Task<string> PostAsync(JsonObject request, CancellationToken ct)
    {
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(LocalOllama.Url + "/api/chat", content, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }
}

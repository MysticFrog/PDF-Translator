using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfTranslator.Core;

namespace PdfTranslator.Translators;

/// <summary>Claude Code is missing or not signed in; the UI offers buttons to fix it.</summary>
public sealed class ClaudeCodeSetupException(string message, string? exe, bool tokenRejected) : InvalidOperationException(message)
{
    /// <summary>The CLI that was found, or null when Claude Code is not installed.</summary>
    public string? Exe { get; } = exe;
    public bool TokenRejected { get; } = tokenRejected;
}

/// <summary>
/// Translation through the Claude Code CLI in headless mode (<c>claude -p</c>), using the user's own Claude sign-in
/// instead of an API key. Each page's blocks go in as a JSON array on stdin; the reply is a JSON array of the same
/// length. MCP servers and tools are switched off for these calls, so the CLI only translates.
/// </summary>
public sealed class ClaudeCodeTranslator : ITranslator
{
    readonly string _exe;
    readonly string _model;
    readonly string _token;

    public ClaudeCodeTranslator(string? configuredPath, string model, string token = "")
    {
        _token = token.Trim();
        _exe = Locate(configuredPath) ?? throw new ClaudeCodeSetupException(
            "Claude Code was not found. Install it, or set its path in Settings.", null, false);
        _model = model.Trim();
    }

    public string Id => "claude-code";
    public string DisplayName => "Claude (Claude Code)";

    /// <summary>Configured path, then PATH, then the usual install locations (incl. the copy bundled with Claude Desktop).</summary>
    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim())) return configured.Trim();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code");
        if (Directory.Exists(bundled))
            return Directory.EnumerateFiles(bundled, "claude.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return null;
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        var results = new string[texts.Count];
        var batch = new List<int>();
        int chars = 0;
        async Task Flush()
        {
            if (batch.Count == 0) return;
            var items = batch.Select(i => texts[i]).ToList();
            var outItems = await RunAsync(items, from, to, ct);
            if (outItems == null || outItems.Count != items.Count)
            {
                // Misaligned reply: one block per call so every translation lands on its own block.
                outItems = new List<string>();
                foreach (var item in items)
                {
                    var one = await RunAsync(new List<string> { item }, from, to, ct);
                    outItems.Add(one is { Count: 1 } ? one[0] : item);
                }
            }
            for (int k = 0; k < batch.Count; k++) results[batch[k]] = outItems[k];
            batch.Clear();
            chars = 0;
        }
        for (int i = 0; i < texts.Count; i++)
        {
            if (batch.Count >= 80 || chars + texts[i].Length > 15000) await Flush();
            batch.Add(i);
            chars += texts[i].Length;
        }
        await Flush();
        return results;
    }

    async Task<List<string>?> RunAsync(List<string> items, string from, string to, CancellationToken ct)
    {
        string source = from == Languages.Auto ? "the source language (detect it)" : Languages.NameOf(from);
        string prompt =
            $"Translate text extracted from a PDF page from {source} into {Languages.NameOf(to)}. " +
            "Standard input is a JSON array of text blocks from one page, in reading order. Translate every block " +
            "faithfully, using neighbouring blocks as context. Keep numbers, units, part numbers, codes, URLs and proper " +
            "names unchanged. Keep each translation about as long as its source, because it must fit the original layout box. " +
            "If a block is already in the target language or is not translatable, return it unchanged. " +
            "Reply with only a JSON array of strings, the same length and order as the input, and nothing else.";

        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in new[] { "-p", prompt, "--output-format", "json", "--strict-mcp-config", "--tools", "" })
            psi.ArgumentList.Add(a);
        if (_model.Length > 0) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(_model); }
        // When this app was itself started from a Claude session, don't let the child think it is nested in it.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE_CODE_") || k == "CLAUDECODE" || k.StartsWith("CLAUDE_AGENT_SDK")).ToList())
            psi.Environment.Remove(key);
        // A token from "claude setup-token" signs the CLI in without a saved /login.
        if (_token.Length > 0) psi.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = _token;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude Code.");
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
        await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(items));
        proc.StandardInput.Close();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(true); } catch { }
                throw new TimeoutException("Claude Code did not answer within 10 minutes.");
            }
        }
        ct.ThrowIfCancellationRequested();
        string stdout = await stdoutTask, stderr = await stderrTask;

        JsonNode? node = null;
        try { node = JsonNode.Parse(stdout); } catch (JsonException) { }
        string result = node?["result"]?.GetValue<string>() ?? "";
        bool isError = node?["is_error"]?.GetValue<bool>() ?? proc.ExitCode != 0;
        if (isError)
        {
            string message = result.Length > 0 ? result : stderr.Trim();
            if (message.Contains("login", StringComparison.OrdinalIgnoreCase) || message.Contains("logged in", StringComparison.OrdinalIgnoreCase))
                throw new ClaudeCodeSetupException(_token.Length > 0
                    ? "Claude Code rejected the saved token. Create a new one and paste it in Settings › Claude Code token."
                    : "Claude Code is not signed in for PDF Translator.", _exe, _token.Length > 0);
            throw new InvalidOperationException("Claude Code failed: " + (message.Length > 400 ? message[..400] + "…" : message));
        }

        int a0 = result.IndexOf('['), b0 = result.LastIndexOf(']');
        if (a0 < 0 || b0 <= a0) return null;
        try { return JsonSerializer.Deserialize<List<string>>(result[a0..(b0 + 1)]); }
        catch (JsonException) { return null; }
    }
}
